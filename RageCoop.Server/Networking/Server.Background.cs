﻿using ICSharpCode.SharpZipLib.Zip;
using Lidgren.Network;
using Newtonsoft.Json;
using RageCoop.Core;
using RageCoop.Server.Scripting;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RageCoop.Server
{
    public partial class Server
    {
        private const string _versionURL = "https://raw.githubusercontent.com/RAGECOOP/RAGECOOP-V/main/RageCoop.Server/Properties/AssemblyInfo.cs";
        private void SendPlayerUpdate()
        {
            foreach (var c in ClientsByNetHandle.Values.ToArray())
            {
                try
                {
                    NetOutgoingMessage outgoingMessage = MainNetServer.CreateMessage();
                    new Packets.PlayerInfoUpdate()
                    {
                        PedID = c.Player.ID,
                        Username = c.Username,
                        Latency = c.Latency,
                        Position = c.Player.Position,
                        IsHost = c == _hostClient
                    }.Pack(outgoingMessage);
                    MainNetServer.SendToAll(outgoingMessage, NetDeliveryMethod.ReliableSequenced, (byte)ConnectionChannel.Default);
                }
                catch (Exception ex)
                {
                    Logger?.Error(ex);
                }
            }
        }
        private IpInfo IpInfo = null;
        private bool CanAnnounce = false;
        private void Announce()
        {
            HttpResponseMessage response = null;
            HttpClient httpClient = new();
            if (IpInfo == null)
            {
                try
                {
                    // TLS only
                    ServicePointManager.Expect100Continue = true;
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13 |
                                                           SecurityProtocolType.Tls12 |
                                                           SecurityProtocolType.Tls11 |
                                                           SecurityProtocolType.Tls;
                    ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

                    try
                    {
                        IpInfo = CoreUtils.GetIPInfo();
                        Logger?.Info($"Your public IP is {IpInfo.Address}, announcing to master server...");
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error(ex.InnerException?.Message ?? ex.Message);
                        return;
                    }
                }
                catch (HttpRequestException ex)
                {
                    Logger?.Error($"MasterServer: {ex.InnerException.Message}");
                }
                catch (Exception ex)
                {
                    Logger?.Error($"MasterServer: {ex.Message}");
                }
            }
            if (!CanAnnounce)
            {
                var existing = JsonConvert.DeserializeObject<List<ServerInfo>>(HttpHelper.DownloadString(Util.GetFinalRedirect(Settings.MasterServer))).Where(x => x.address == IpInfo.Address).FirstOrDefault();
                if(existing != null)
                {
                    Logger.Warning("Server info already present in master server, waiting for 10 seconds...");
                    return;
                }
                else
                {
                    CanAnnounce = true;
                }
            }
            try
            {
                Security.GetPublicKey(out var pModulus, out var pExpoenet);
                var serverInfo = new ServerInfo
                {
                    address = IpInfo.Address,
                    port = Settings.Port.ToString(),
                    country = IpInfo.Country,
                    name = Settings.Name,
                    version = Version.ToString(),
                    players = MainNetServer.ConnectionsCount.ToString(),
                    maxPlayers = Settings.MaxPlayers.ToString(),
                    description = Settings.Description,
                    website = Settings.Website,
                    gameMode = Settings.GameMode,
                    language = Settings.Language,
                    useP2P = Settings.UseP2P,
                    useZT = Settings.UseZeroTier,
                    ztID = Settings.UseZeroTier ? Settings.ZeroTierNetworkID : "",
                    ztAddress = Settings.UseZeroTier ? ZeroTierHelper.Networks[Settings.ZeroTierNetworkID].Addresses.Where(x => !x.Contains(":")).First() : "0.0.0.0",
                    publicKeyModulus = Convert.ToBase64String(pModulus),
                    publicKeyExponent = Convert.ToBase64String(pExpoenet)
                };
                string msg = JsonConvert.SerializeObject(serverInfo);

                var realUrl = Util.GetFinalRedirect(Settings.MasterServer);
                response = httpClient.PostAsync(realUrl, new StringContent(msg, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger?.Error($"MasterServer: {ex.Message}");
                return;
            }

            if (response == null)
            {
                Logger?.Error("MasterServer: Something went wrong!");
            }
            else if (response.StatusCode != HttpStatusCode.OK)
            {
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    string requestContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Logger?.Error($"MasterServer: [{(int)response.StatusCode}], {requestContent}");
                }
                else
                {
                    Logger?.Error($"MasterServer: [{(int)response.StatusCode}]");
                    Logger?.Error($"MasterServer: [{response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}]");
                }
            }
        }
        private void CheckUpdate()
        {
            try
            {
                var versionLine = HttpHelper.DownloadString(_versionURL).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Contains("[assembly: AssemblyVersion(")).First();
                var start = versionLine.IndexOf('\"') + 1;
                var end = versionLine.LastIndexOf('\"');
                var latest = Version.Parse(versionLine.AsSpan(start, end - start));
                if (latest <= Version) { return; }

                // wait ten minutes for the build to complete
                API.SendChatMessage($"New server version found: {latest}, server will update in 10 minutes");
                Thread.Sleep(10 * 60 * 1000);

                API.SendChatMessage("downloading update...");
                var downloadURL = $"https://github.com/RAGECOOP/RAGECOOP-V/releases/download/nightly/RageCoop.Server-{CoreUtils.GetInvariantRID()}.zip";
                if (Directory.Exists("Update")) { Directory.Delete("Update", true); }
                HttpHelper.DownloadFile(downloadURL, "Update.zip", null);
                Logger?.Info("Installing update");
                Directory.CreateDirectory("Update");
                new FastZip().ExtractZip("Update.zip", "Update", FastZip.Overwrite.Always, null, null, null, true);
                MainNetServer.Shutdown("Server updating");
                Logger.Info("Server shutting down!");
                Logger.Flush();
                Process.Start(Path.Combine("Update", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "RageCoop.Server.exe" : "RageCoop.Server"), "update \"" + AppDomain.CurrentDomain.BaseDirectory[0..^1] + "\"");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Logger?.Error("Update", ex);
            }
        }

        private void KickAssholes()
        {
            foreach (var c in ClientsByNetHandle.Values.ToArray())
            {
                if (c.EntitiesCount > Settings.SpamLimit && Settings.KickSpamming)
                {
                    c.Kick("Bye bye asshole: spamming");
                    API.SendChatMessage($"Asshole {c.Username} was kicked: Spamming");
                }
                else if (Settings.KickGodMode && c.Player.IsInvincible)
                {
                    c.Kick("Bye bye asshole: godmode");
                    API.SendChatMessage($"Asshole {c.Username} was kicked: GodMode");
                }
            }
        }
    }
}
