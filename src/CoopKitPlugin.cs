using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GK2.CoopKit
{
    /// <summary>
    /// Everything learned on 2026-09-26, in one plugin.
    ///
    /// Graveyard Keeper 2 ships a complete Unity Netcode stack with no way to reach it: the
    /// menu buttons were removed and the lobby prefabs are not in the build. The code is all
    /// there, though, and four calls are enough to host. This plugin makes those four calls
    /// behind one key, and measures what the game would actually put on the wire.
    ///
    ///   F6  host now      — LazyNetwork.Init, StartHostGame, and attach the local player
    ///   F7  stop hosting  — NetworkManager.Shutdown
    ///   F9  state dump    — what exists right now, written to BepInEx/coopkit/
    ///   F1  traffic spy   — toggle; writes a report when switched off
    ///
    /// Read-only where it can be. The only thing it changes is the network layer, which the
    /// game leaves switched off.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class CoopKitPlugin : BaseUnityPlugin
    {
        public const string Guid = "gk2.coopkit";
        public const string Name = "GK2 Co-op Kit";
        public const string Version = "1.2.0";

        private const ushort Port = 7777;

        internal static CoopKitPlugin Instance;
        internal static Assembly GameAssembly;

        private Harmony harmony;
        private BepInEx.Configuration.ConfigEntry<string> hostAddress;

        internal string HostAddressValue => hostAddress?.Value?.Trim();
        internal BepInEx.Logging.ManualLogSource Log => Logger;

        private void Awake()
        {
            Instance = this;
            GameAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            hostAddress = Config.Bind("Join", "HostAddress", "",
                "The host's address, as the joining player sees it. Over Radmin VPN this is the host's 26.x.x.x address; on a LAN it is their 192.168.x.x one. Only the joining side needs this.");

            Logger.LogInfo($"{Name} {Version}");
            Logger.LogInfo("F4 opens the Co-op Kit screen. Keys still work: F6 host, F8 join, F5 send world, F7 stop, F9 dump, F1 traffic spy.");

            if (GameAssembly == null)
            {
                Logger.LogError("Assembly-CSharp not found — nothing will work.");
                return;
            }

            harmony = new Harmony(Guid);
            TrafficSpy.Install(harmony, Logger);

            try
            {
                harmony.PatchAll(typeof(MainMenuPatch));
                harmony.PatchAll(typeof(ClientStartGamePatch));
                Logger.LogInfo("Main menu button and client-entry fix installed.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"UI patches failed, keys still work: {e.GetType().Name}: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            if (TrafficSpy.Active) TrafficSpy.WriteReport(Logger);
            harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6)) HostNow();
            if (Input.GetKeyDown(KeyCode.F8)) Join();
            if (Input.GetKeyDown(KeyCode.F5)) SyncSavesToClients();
            if (Input.GetKeyDown(KeyCode.F7)) StopHosting();
            if (Input.GetKeyDown(KeyCode.F9)) WriteStateDump();
            if (Input.GetKeyDown(KeyCode.F4)) CoopDialog.Show();
            if (Input.GetKeyDown(KeyCode.F1)) ToggleSpy();
        }

        // ---------- F6: the four calls ----------

        internal void HostNow()
        {
            Logger.LogInfo("===== F6: host now =====");

            if (Reflect.LoadedGameScene() == null)
            {
                Logger.LogError("Load a campaign through the menu first, then press F6 inside the game.");
                return;
            }

            var singleton = Reflect.NetcodeSingleton();
            if (Equals(Reflect.Get(singleton, "IsListening"), true))
            {
                Logger.LogWarning("Already hosting — not calling StartHostGame again (a second call clears IsCoopGame).");
                WriteStateDump();
                return;
            }

            // 1. wake the layer the game never wakes
            if (!EnsureLayerAwake()) return;

            // 2. bind on every interface; the game's own helper picks the Radmin adapter
            foreach (var address in Reflect.LocalAddresses())
                Logger.LogInfo($"      local address: {address}{(address.StartsWith("26.") ? "  (Radmin VPN — give this one to a remote player)" : "  (LAN)")}");

            var uManagerType = Reflect.GameType("UNetworkManager");
            var uManager = Resources.FindObjectsOfTypeAll(uManagerType).FirstOrDefault();
            try
            {
                var started = uManagerType.GetMethod("StartHostGame", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(uManager, new object[] { "0.0.0.0", Port });
                Logger.LogInfo($"2/4 StartHostGame(\"0.0.0.0\", {Port}) returned {started}");
                if (Equals(started, false))
                {
                    Logger.LogError("StartHostGame refused. Is something already bound to the port?");
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"2/4 StartHostGame failed: {Reflect.Describe(e)}");
                return;
            }

            // 3 and 4. the networking half of PrepareGameForNetwork, and nothing else:
            // calling the world-data half twice is what hangs the loading screen forever.
            var mainGame = Reflect.MainGame();
            var gameSave = Reflect.Get(mainGame, "GameSave");
            var playerData = Reflect.Get(gameSave, "playerData");
            if (gameSave == null || playerData == null)
            {
                Logger.LogError("3/4 no GameSave / playerData — the host is up but no player is attached.");
                return;
            }

            try
            {
                var clientId = Convert.ToInt32(Reflect.Get(Reflect.NetcodeSingleton(), "LocalClientId"));
                var networkPlayer = Activator.CreateInstance(
                    Reflect.GameType("NetworkPlayer"), new[] { (object)clientId, playerData });

                gameSave.GetType().GetField("hostPlayer", BindingFlags.Public | BindingFlags.Instance)
                    ?.SetValue(gameSave, networkPlayer);
                Logger.LogInfo($"3/4 GameSave.hostPlayer set (clientId {clientId})");

                var holder = Reflect.Get(mainGame, "PlayerUniqueCommandHolder");
                holder?.GetType().GetMethod("RegisterData", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(holder, new[] { networkPlayer });
                Logger.LogInfo("4/4 PlayerUniqueCommandHolder.RegisterData — player now feeds the command pipeline");
            }
            catch (Exception e)
            {
                Logger.LogError($"3-4/4 attach failed: {Reflect.Describe(e)}");
                return;
            }

            Logger.LogInfo($"Hosting. A second player joins at <your address>:{Port}.");
            WriteStateDump();
        }

        // ---------- F8: the other side ----------

        /// <summary>
        /// The joining player. Same shape as hosting: load a campaign normally, then attach.
        ///
        /// The game's own client path cannot be used as shipped. LobbyHelper.Client_StartGame
        /// begins with LazyUI.GetWindow&lt;UILobbyWindow&gt;().Close(), and that prefab is not in
        /// the build — the call throws before it ever reaches PrepareGameForNetwork. So the
        /// lobby handshake (Host_SyncGameSaves → ConfirmGameSave → ForceStartGame) is a dead
        /// end until someone rebuilds that window.
        ///
        /// What this does instead is connect the transport, which is what a first test needs:
        /// does the link establish across Radmin, does the host see the client, does traffic
        /// start flowing.
        /// </summary>
        internal void Join()
        {
            Logger.LogInfo("===== F8: join =====");

            var address = hostAddress.Value?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                Logger.LogError("No host address configured. Put the host's address in BepInEx/config/gk2.coopkit.cfg under [Join] HostAddress, then restart the game.");
                return;
            }

            if (Reflect.LoadedGameScene() == null)
            {
                Logger.LogError("Load a campaign through the menu first, then press F8 inside the game.");
                return;
            }

            if (!EnsureLayerAwake()) return;

            var uManagerType = Reflect.GameType("UNetworkManager");
            var uManager = Resources.FindObjectsOfTypeAll(uManagerType).FirstOrDefault();
            try
            {
                var connect = uManagerType.GetMethod("ConnectToHost", BindingFlags.Public | BindingFlags.Instance);
                var result = connect.Invoke(uManager, new object[] { address, Port });
                Logger.LogInfo($"ConnectToHost(\"{address}\", {Port}) returned {result}");
            }
            catch (Exception e)
            {
                Logger.LogError($"ConnectToHost failed: {Reflect.Describe(e)}");
                return;
            }

            Logger.LogInfo("If the link comes up, the host's client count goes to 2. Press F9 on both machines to compare.");
            WriteStateDump();
        }

        // ---------- F5: push the world to whoever is connected ----------

        /// <summary>
        /// Host side. Publishes the serialized GameSave to every connected client, which is
        /// the game's own late-join mechanism. Worth trying once a link is up: it is the one
        /// piece of the lobby flow that does not depend on the missing window prefabs.
        /// </summary>
        internal void SyncSavesToClients()
        {
            Logger.LogInfo("===== F5: Host_SyncGameSaves =====");

            var clients = Reflect.Count(Reflect.Get(
                Resources.FindObjectsOfTypeAll(Reflect.GameType("UNetworkManager")).FirstOrDefault(), "OtherClients"));
            if (clients <= 0)
            {
                Logger.LogError("No other clients connected — nothing to send.");
                return;
            }

            try
            {
                Reflect.GameType("LobbyHelper")
                    .GetMethod("Host_SyncGameSaves", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, null);
                Logger.LogInfo($"Save published to {clients} client(s). Watch the other machine's log for Client_InitGameSave.");
            }
            catch (Exception e)
            {
                Logger.LogError($"Host_SyncGameSaves failed: {Reflect.Describe(e)}");
            }
        }

        private bool EnsureLayerAwake()
        {
            var lazyNetworkType = Reflect.GameType("LazyNetwork");
            if (!Equals(Reflect.GetStatic(lazyNetworkType, "IsInitialized"), false))
            {
                Logger.LogInfo("LazyNetwork already initialised");
                return true;
            }

            var lazyNetwork = Resources.FindObjectsOfTypeAll(lazyNetworkType).FirstOrDefault();
            var manager = Resources.FindObjectsOfTypeAll(Reflect.GameType("UNetworkManager")).FirstOrDefault();
            var channelType = Reflect.GameType("UNetworkMessageChannelManager");
            if (lazyNetwork == null || manager == null || channelType == null)
            {
                Logger.LogError("LazyNetwork / UNetworkManager / UNetworkMessageChannelManager not all present.");
                return false;
            }

            try
            {
                var channels = Activator.CreateInstance(channelType);
                lazyNetworkType.GetMethod("Init", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(lazyNetwork, new[] { manager, channels });
                Logger.LogInfo("LazyNetwork.Init — done");
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"LazyNetwork.Init failed: {Reflect.Describe(e)}");
                return false;
            }
        }

        // ---------- F7 ----------

        internal void StopHosting()
        {
            var singleton = Reflect.NetcodeSingleton();
            if (singleton == null)
            {
                Logger.LogWarning("No NetworkManager — nothing to stop.");
                return;
            }

            try
            {
                var shutdown = singleton.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Shutdown");
                shutdown.Invoke(singleton, shutdown.GetParameters().Length == 0 ? null : new object[] { false });
                Logger.LogInfo("Shutdown called. Expect a NullReferenceException from the game's own teardown — it is harmless.");
            }
            catch (Exception e)
            {
                Logger.LogError($"Shutdown failed: {Reflect.Describe(e)}");
            }
        }

        // ---------- F9 ----------

        internal void WriteStateDump()
        {
            var report = new StringBuilder();
            void Line(string text = "")
            {
                report.AppendLine(text);
                Logger.LogInfo(text);
            }

            var singleton = Reflect.NetcodeSingleton();
            var uManager = Resources.FindObjectsOfTypeAll(Reflect.GameType("UNetworkManager")).FirstOrDefault();
            var connectionManager = Reflect.GetStatic(Reflect.GameType("LazyNetwork"), "ConnectionManager");

            Line($"===== Co-op Kit state — {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            Line($"scene                : {Reflect.LoadedGameScene() ?? "menu / none"}");
            Line($"LazyNetwork ready    : {Reflect.GetStatic(Reflect.GameType("LazyNetwork"), "IsInitialized")}");
            Line($"IsListening          : {Reflect.Get(singleton, "IsListening")}");
            Line($"IsHost / IsServer    : {Reflect.Get(singleton, "IsHost")} / {Reflect.Get(singleton, "IsServer")}");
            Line($"IsCoopGame           : {Reflect.Get(uManager, "IsCoopGame")}");
            Line($"connected clients    : {Reflect.Count(Reflect.Get(singleton, "ConnectedClientsIds"))}");
            Line($"other clients        : {Reflect.Count(Reflect.Get(uManager, "OtherClients"))}");
            Line($"connection state     : {Reflect.TypeNameOf(Reflect.Get(connectionManager, "CurrentState"))}");
            Line($"GameSave             : {(Reflect.Get(Reflect.MainGame(), "GameSave") == null ? "null" : "present")}");
            Line($"traffic spy          : {(TrafficSpy.Active ? "on" : "off")}");
            Line();
            Line(TrafficSpy.Summary());

            Files.Write("state", report.ToString(), Logger);
        }

        // ---------- F1 ----------

        internal void ToggleSpy()
        {
            if (TrafficSpy.Active)
            {
                TrafficSpy.Active = false;
                TrafficSpy.WriteReport(Logger);
                Logger.LogInfo("Traffic spy off. Report written.");
            }
            else
            {
                TrafficSpy.Reset();
                TrafficSpy.Active = true;
                Logger.LogInfo("Traffic spy on. Play normally — chop, mine, build, cook, talk, sleep, fight — then press F1 again.");
            }
        }
    }
}
