﻿using GTA;
using GTA.Math;
using GTA.Native;
using RageCoop.Client.Menus;
using RageCoop.Client.Scripting;
using RageCoop.Core;
using SHVDN;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Console = GTA.Console;
using Script = GTA.Script;

namespace RageCoop.Client
{
    /// <summary>
    /// Don't use it!
    /// </summary>
    [ScriptAttributes(Author = "RageCoop", NoDefaultInstance = false, SupportURL = "https://github.com/RAGECOOP/RAGECOOP-V")]
    internal class Main : Script
    {
        private static bool _gameLoaded = false;
        internal static Version Version = typeof(Main).Assembly.GetName().Version;

        internal static int LocalPlayerID = 0;

        internal static RelationshipGroup SyncedPedsGroup;

        internal static new Settings Settings = null;

#if !NON_INTERACTIVE
#endif
        internal static Chat MainChat = null;
        internal static Stopwatch Counter = new Stopwatch();
        internal static Logger Logger = null;
        internal static ulong Ticked = 0;
        internal static Vector3 PlayerPosition;
        internal static Resources Resources = null;
        private static readonly ConcurrentQueue<Action> TaskQueue = new ConcurrentQueue<Action>();
        public static string LogPath => $"{Settings.DataDirectory}\\RageCoop.Client.log";
        /// <summary>
        /// Don't use it!
        /// </summary>
        public Main()
        {
            Util.StartUpCheck();
            Console.Info($"Starting {typeof(Main).FullName}, domain: {AppDomain.CurrentDomain.Id} {AppDomain.CurrentDomain.FriendlyName}");
            try
            {
                Settings = Util.ReadSettings();
                if (Settings.DataDirectory.StartsWith("Scripts"))
                {
                    var defaultDir = new Settings().DataDirectory;
                    Console.Warning("Data directory must be outside scripts folder, migrating to default direcoty: " + defaultDir);
                    if (Directory.Exists(Settings.DataDirectory))
                    {
                        CoreUtils.CopyFilesRecursively(new DirectoryInfo(Settings.DataDirectory), new DirectoryInfo(defaultDir));
                        Directory.Delete(Settings.DataDirectory, true);
                    }
                    Settings.DataDirectory = defaultDir;
                    Util.SaveSettings();
                }
            }
            catch
            {
                // GTA.UI.Notification.Show("Malformed configuration, overwriting with default values...");
                Settings = new Settings();
                Util.SaveSettings();
            }
            Directory.CreateDirectory(Settings.DataDirectory);
            Logger = new Logger()
            {
                Writers = new List<StreamWriter> { CoreUtils.OpenWriter(LogPath) },
#if DEBUG
                LogLevel = 0,
#else
                LogLevel = Settings.LogLevel,
#endif
            };
            Logger.OnFlush += (line, formatted) =>
            {
                switch (line.LogLevel)
                {
#if DEBUG
                    // case LogLevel.Trace:
                    case LogLevel.Debug:
                        Console.Info(line.Message);
                        break;
#endif
                    case LogLevel.Info:
                        Console.Info(line.Message);
                        break;
                    case LogLevel.Warning:
                        Console.Warning(line.Message);
                        break;
                    case LogLevel.Error:
                        Console.Error(line.Message);
                        break;
                }
            };
            ScriptDomain.CurrentDomain.Tick += DomainTick;
            Resources = new Resources();
            if (Game.Version < GameVersion.v1_0_1290_1_Steam)
            {
                Tick += (object sender, EventArgs e) =>
                {
                    if (Game.IsLoading)
                    {
                        return;
                    }
                    if (!_gameLoaded)
                    {
                        GTA.UI.Notification.Show("~r~Please update your GTA5 to v1.0.1290 or newer!", true);
                        _gameLoaded = true;
                    }
                };
                return;
            }
            BaseScript.OnStart();
            SyncedPedsGroup = World.AddRelationshipGroup("SYNCPED");
            Game.Player.Character.RelationshipGroup.SetRelationshipBetweenGroups(SyncedPedsGroup, Relationship.Neutral, true);
#if !NON_INTERACTIVE
#endif
            MainChat = new Chat();
            Aborted += OnAborted;
            Tick += OnTick;
            KeyDown += OnKeyDown;

            Util.NativeMemory();
            Counter.Restart();

        }

        private static void OnAborted(object sender, EventArgs e)
        {
            try
            {
                WorldThread.Instance?.Abort();
                DevTool.Instance?.Abort();
                ScriptDomain.CurrentDomain.Tick -= DomainTick;
                Disconnected("Abort");
                WorldThread.DoQueuedActions();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private static void DomainTick()
        {
            while (TaskQueue.TryDequeue(out var task))
            {
                try
                {
                    task.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }

            if (Networking.IsOnServer)
            {
                try
                {
                    EntityPool.DoSync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }
        }

        /// <summary>
        /// Queue an action to main thread and wait for execution to complete, must be called from script thread.
        /// </summary>
        /// <param name="task"></param>
        internal static void QueueToMainThreadAndWait(Action task)
        {
            Exception e = null;
            TaskQueue.Enqueue(() => { try { task(); } catch (Exception ex) { e = ex; } });
            Yield();
            if (e != null) { throw e; }
        }

        public static Ped P;
        public static float FPS;
        private static bool _lastDead;
        private static void OnTick(object sender, EventArgs e)
        {
            P = Game.Player.Character;
            PlayerPosition = P.ReadPosition();
            FPS = Game.FPS;
            if (Game.IsLoading)
            {
                return;
            }
            else if (!_gameLoaded && (_gameLoaded = true))
            {
#if !NON_INTERACTIVE
                GTA.UI.Notification.Show(GTA.UI.NotificationIcon.AllPlayersConf, "RAGECOOP", "Welcome!", $"Press ~g~{Main.Settings.MenuKey}~s~ to open the menu.");
#endif
            }

#if !NON_INTERACTIVE
            CoopMenu.MenuPool.Process();
#endif


            if (!Networking.IsOnServer)
            {
                return;
            }
            if (Game.TimeScale != 1)
            {
                Game.TimeScale = 1;
            }

            if (Networking.ShowNetworkInfo)
            {
                new LemonUI.Elements.ScaledText(new PointF(Screen.PrimaryScreen.Bounds.Width / 2, 0), $"L: {Networking.Latency * 1000:N0}ms", 0.5f) { Alignment = GTA.UI.Alignment.Center }.Draw();
                new LemonUI.Elements.ScaledText(new PointF(Screen.PrimaryScreen.Bounds.Width / 2, 30), $"R: {Lidgren.Network.NetUtility.ToHumanReadable(Statistics.BytesDownPerSecond)}/s", 0.5f) { Alignment = GTA.UI.Alignment.Center }.Draw();
                new LemonUI.Elements.ScaledText(new PointF(Screen.PrimaryScreen.Bounds.Width / 2, 60), $"S: {Lidgren.Network.NetUtility.ToHumanReadable(Statistics.BytesUpPerSecond)}/s", 0.5f) { Alignment = GTA.UI.Alignment.Center }.Draw();
            }

            MainChat.Tick();
            PlayerList.Tick();
            if (!API.Config.EnableAutoRespawn)
            {
                Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, true);
                Function.Call(Hash.IGNORE_NEXT_RESTART, true);
                Function.Call(Hash.FORCE_GAME_STATE_PLAYING);
                Function.Call(Hash.TERMINATE_ALL_SCRIPTS_WITH_THIS_NAME, "respawn_controller");
                if (P.IsDead)
                {
                    Function.Call(Hash.SET_FADE_OUT_AFTER_DEATH, false);

                    if (P.Health != 1)
                    {
                        P.Health = 1;
                        Game.Player.WantedLevel = 0;
                        Logger.Debug("Player died.");
                        API.Events.InvokePlayerDied();
                    }
                    GTA.UI.Screen.StopEffects();
                }
                else
                {
                    Function.Call(Hash.DISPLAY_HUD, true);
                }
            }
            else if (P.IsDead && !_lastDead)
            {
                API.Events.InvokePlayerDied();
            }

            _lastDead = P.IsDead;
            Ticked++;
        }
        private static void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (MainChat.Focused)
            {
                MainChat.OnKeyDown(e.KeyCode);
                return;
            }
            if (Networking.IsOnServer)
            {
                if (Voice.WasInitialized())
                {
                    if (Game.IsControlPressed(GTA.Control.PushToTalk))
                    {
                        Voice.StartRecording();
                        return;
                    }
                    else if (Voice.IsRecording())
                    {
                        Voice.StopRecording();
                        return;
                    }
                }

                if (Game.IsControlPressed(GTA.Control.FrontendPause))
                {
                    Function.Call(Hash.ACTIVATE_FRONTEND_MENU, Function.Call<int>(Hash.GET_HASH_KEY, "FE_MENU_VERSION_SP_PAUSE"), false, 0);
                    return;
                }
                if (Game.IsControlPressed(GTA.Control.FrontendPauseAlternate) && Settings.DisableAlternatePause)
                {
                    Function.Call(Hash.ACTIVATE_FRONTEND_MENU, Function.Call<int>(Hash.GET_HASH_KEY, "FE_MENU_VERSION_SP_PAUSE"), false, 0);
                    return;
                }
            }
            if (e.KeyCode == Settings.MenuKey)
            {
                if (CoopMenu.MenuPool.AreAnyVisible)
                {
                    CoopMenu.MenuPool.ForEach<LemonUI.Menus.NativeMenu>(x =>
                    {
                        if (x.Visible)
                        {
                            CoopMenu.LastMenu = x;
                            x.Visible = false;
                        }
                    });
                }
                else
                {
                    CoopMenu.LastMenu.Visible = true;
                }
            }
            else if (Game.IsControlJustPressed(GTA.Control.MpTextChatAll))
            {
                if (Networking.IsOnServer)
                {
                    MainChat.Focused = true;
                }
            }
            else if (MainChat.Focused) { return; }
            else if (Game.IsControlJustPressed(GTA.Control.MultiplayerInfo))
            {
                if (Networking.IsOnServer)
                {
                    ulong currentTimestamp = Util.GetTickCount64();
                    PlayerList.Pressed = (currentTimestamp - PlayerList.Pressed) < 5000 ? (currentTimestamp - 6000) : currentTimestamp;
                }
            }
            else if (e.KeyCode == Settings.PassengerKey)
            {
                var P = Game.Player.Character;

                if (!P.IsInVehicle())
                {
                    if (P.IsTaskActive(TaskType.CTaskEnterVehicle))
                    {
                        P.Task.ClearAll();
                    }
                    else
                    {
                        var V = World.GetClosestVehicle(P.ReadPosition(), 50);

                        if (V != null)
                        {
                            var seat = P.GetNearestSeat(V);
                            var p = V.GetPedOnSeat(seat);
                            if (p != null && !p.IsDead)
                            {
                                for (int i = -1; i < V.PassengerCapacity; i++)
                                {
                                    seat = (VehicleSeat)i;
                                    p = V.GetPedOnSeat(seat);
                                    if (p == null || p.IsDead)
                                    {
                                        break;
                                    }
                                }
                            }
                            P.Task.EnterVehicle(V, seat, -1, 5, EnterVehicleFlags.None);
                        }
                    }
                }
            }
        }
        internal static void Connected()
        {
            Memory.ApplyPatches();
            if (Settings.Voice && !Voice.WasInitialized())
            {
                Voice.Init();
            }
            API.QueueAction(() =>
            {
                WorldThread.Traffic(!Settings.DisableTraffic);
                Function.Call(Hash.SET_ENABLE_VEHICLE_SLIPSTREAMING, true);
                CoopMenu.ConnectedMenuSetting();
                MainChat.Init();
                GTA.UI.Notification.Show("~g~Connected!");
            });

            Logger.Info(">> Connected <<");
        }
        public static void Disconnected(string reason)
        {


            Logger.Info($">> Disconnected << reason: {reason}");
            API.QueueAction(() =>
            {
                if (MainChat.Focused)
                {
                    MainChat.Focused = false;
                }
                PlayerList.Cleanup();
                MainChat.Clear();
                EntityPool.Cleanup();
                WorldThread.Traffic(true);
                Function.Call(Hash.SET_ENABLE_VEHICLE_SLIPSTREAMING, false);
                CoopMenu.DisconnectedMenuSetting();
                GTA.UI.Notification.Show("~r~Disconnected: " + reason);
                LocalPlayerID = default;
                Resources.Unload();
            });
            Memory.RestorePatches();
            DownloadManager.Cleanup();
            Voice.ClearAll();
        }


    }
}
