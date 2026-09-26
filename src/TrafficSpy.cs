using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace GK2.CoopKit
{
    /// <summary>
    /// Counts what the game tries to put on the wire while you play.
    ///
    /// The interesting patch target is OnlineState.AddCommand, because the guard that drops
    /// traffic lives *inside* it:
    ///
    ///     if (command != null &amp;&amp; (!IsHost || OtherClients.Count != 0)) commands.Add(command);
    ///
    /// So a solo host still walks the whole path and discards at the last step — which means a
    /// single player can measure exactly what a second player would have received, without a
    /// second player existing. Anything counted as "dropped (no client)" is real traffic that
    /// a co-op session would carry.
    ///
    /// AddNotRepeatableCommand is the same story for the per-tick holders (player position,
    /// facing, animation state).
    /// </summary>
    internal static class TrafficSpy
    {
        internal static bool Active;

        private static readonly Dictionary<string, int> Commands = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> Dropped = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> Holders = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> HoldersQueued = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> Actions = new Dictionary<string, int>();
        private static int packages;
        private static int handleCommandCalls;
        private static DateTime startedAt;

        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            Patch(harmony, log, "OnlineState", "AddCommand", nameof(AddCommandPrefix));
            Patch(harmony, log, "OnlineState", "AddNotRepeatableCommand", nameof(AddHolderPrefix), nameof(AddHolderPostfix));
            Patch(harmony, log, "NetworkPackageSystem", "CreateCommandPackage", nameof(CreatePackagePrefix));
            Patch(harmony, log, "Command", "HandleCommand", nameof(HandleCommandPrefix));

            // The three methods the game marks with [NetworkMethod] — the entire set of game
            // actions that know how to replicate. Counting them separately tells a silent
            // report apart from one where the actions never happened.
            Patch(harmony, log, "WgoData", "ApplyTool", nameof(ApplyToolPrefix));
            Patch(harmony, log, "WorldData", "AddWgoDataToGameScene", nameof(AddWgoPrefix));
            Patch(harmony, log, "WorldData", "RemoveWgoDataFromGameScene", nameof(RemoveWgoPrefix));
        }

        private static void Patch(Harmony harmony, ManualLogSource log, string typeName, string methodName, string prefixName, string postfixName = null)
        {
            try
            {
                var type = CoopKitPlugin.GameAssembly.GetType(typeName, throwOnError: false);
                var target = type == null ? null : AccessTools.Method(type, methodName);
                if (target == null)
                {
                    log.LogWarning($"spy: {typeName}.{methodName} not found — that measurement will be missing.");
                    return;
                }

                var prefix = typeof(TrafficSpy).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
                var postfix = postfixName == null
                    ? null
                    : new HarmonyMethod(typeof(TrafficSpy).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(target, new HarmonyMethod(prefix), postfix);
                log.LogInfo($"spy: watching {typeName}.{methodName}");
            }
            catch (Exception e)
            {
                log.LogWarning($"spy: could not patch {typeName}.{methodName}: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---------- patches ----------

        private static void AddCommandPrefix(object command)
        {
            if (!Active || command == null) return;

            var name = command.GetType().Name;
            Bump(Commands, name);
            if (SoloHost()) Bump(Dropped, name);
        }

        // Counted on the way in, before the guard inside the method decides. A holder that is
        // counted here but not in HoldersQueued was dropped for lack of a connected client.
        private static void AddHolderPrefix(object commandHolder)
        {
            if (!Active || commandHolder == null) return;
            Bump(Holders, commandHolder.GetType().Name);
        }

        private static void AddHolderPostfix(object commandHolder)
        {
            if (!Active || commandHolder == null) return;
            if (Equals(Reflect.Get(commandHolder, "isQueued"), true))
                Bump(HoldersQueued, commandHolder.GetType().Name);
        }

        private static void ApplyToolPrefix() { if (Active) Bump(Actions, "WgoData.ApplyTool"); }
        private static void AddWgoPrefix() { if (Active) Bump(Actions, "WorldData.AddWgoDataToGameScene"); }
        private static void RemoveWgoPrefix() { if (Active) Bump(Actions, "WorldData.RemoveWgoDataFromGameScene"); }

        private static void CreatePackagePrefix(object commands)
        {
            if (!Active) return;
            packages++;
        }

        private static void HandleCommandPrefix(object command)
        {
            if (!Active) return;
            handleCommandCalls++;
        }

        // ---------- reporting ----------

        internal static void Reset()
        {
            Commands.Clear();
            Dropped.Clear();
            Holders.Clear();
            HoldersQueued.Clear();
            Actions.Clear();
            packages = 0;
            handleCommandCalls = 0;
            startedAt = DateTime.Now;
        }

        internal static string Summary()
        {
            if (startedAt == default) return "traffic spy: never started";

            var elapsed = DateTime.Now - startedAt;
            var report = new StringBuilder();
            report.AppendLine($"--- traffic, {elapsed.TotalMinutes:F1} min of play ---");
            report.AppendLine($"Command.HandleCommand calls : {handleCommandCalls}");
            report.AppendLine($"packages built              : {packages}");
            report.AppendLine();

            if (Commands.Count == 0)
            {
                report.AppendLine("No commands were produced. Nothing you did in this session would have");
                report.AppendLine("reached a second player.");
            }
            else
            {
                report.AppendLine("commands the game produced (what a second player would receive):");
                foreach (var row in Commands.OrderByDescending(r => r.Value))
                {
                    Dropped.TryGetValue(row.Key, out int dropped);
                    var note = dropped == row.Value ? "all dropped: no client connected" : $"{dropped} dropped";
                    report.AppendLine($"  {row.Key,-32} {row.Value,6}   ({note})");
                }
            }

            report.AppendLine();
            if (Actions.Count > 0)
            {
                report.AppendLine("replicating actions performed ([NetworkMethod] methods):");
                foreach (var row in Actions.OrderByDescending(r => r.Value))
                    report.AppendLine($"  {row.Key,-38} {row.Value,6}");
            }
            else
            {
                report.AppendLine("replicating actions performed: NONE.");
                report.AppendLine("The three methods the game can replicate — WgoData.ApplyTool,");
                report.AppendLine("WorldData.AddWgoDataToGameScene, WorldData.RemoveWgoDataFromGameScene —");
                report.AppendLine("were never reached. Either those actions were not performed, or the");
                report.AppendLine("systems behind them do not route through the network path at all.");
            }

            if (Holders.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("per-tick holders (position, facing, animation):");
                foreach (var row in Holders.OrderByDescending(r => r.Value))
                {
                    HoldersQueued.TryGetValue(row.Key, out int queued);
                    double perSecond = elapsed.TotalSeconds > 0 ? row.Value / elapsed.TotalSeconds : 0;
                    report.AppendLine($"  {row.Key,-32} {row.Value,6} attempts, {queued} queued  (~{perSecond:F1}/s)");
                }
                report.AppendLine("  attempts minus queued = dropped because no client is connected.");
            }
            else
            {
                report.AppendLine();
                report.AppendLine("per-tick holders: none queued. Expected while hosting alone — the game");
                report.AppendLine("skips them entirely when OtherClients is empty, so player movement");
                report.AppendLine("cannot be measured without a second player.");
            }

            return report.ToString();
        }

        internal static void WriteReport(ManualLogSource log)
        {
            var report = new StringBuilder();
            report.AppendLine($"===== Co-op Kit traffic report — {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            report.AppendLine();
            report.AppendLine(Summary());
            report.AppendLine();
            report.AppendLine("How to read this: every command counted here is a piece of game state the");
            report.AppendLine("shipped netcode knows how to replicate. Systems missing from the list are the");
            report.AppendLine("ones a co-op mod would still have to wire up by hand.");

            Files.Write("traffic", report.ToString(), log);
            log.LogInfo(Summary());
        }

        // ---------- helpers ----------

        private static void Bump(Dictionary<string, int> counter, string key)
        {
            counter.TryGetValue(key, out int current);
            counter[key] = current + 1;
        }

        private static bool SoloHost()
        {
            try
            {
                var uManager = UnityEngine.Resources
                    .FindObjectsOfTypeAll(Reflect.GameType("UNetworkManager")).FirstOrDefault();
                if (uManager == null) return true;
                return Equals(Reflect.Get(uManager, "IsHost"), true) && Reflect.Count(Reflect.Get(uManager, "OtherClients")) == 0;
            }
            catch
            {
                return true;
            }
        }
    }
}
