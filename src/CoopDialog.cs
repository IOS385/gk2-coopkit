using System.Collections.Generic;
using System.Linq;
using System.Text;
using LazyBearTechnology;
using UnityEngine;

namespace GK2.CoopKit
{
    /// <summary>
    /// The kit's own screen, built out of the game's dialog window.
    ///
    /// The lobby prefabs (UIStartHostGameWindow, UIConnectToHostGameWindow, UILobbyWindow)
    /// were cut from the release build, so asking LazyUI for them throws. UIDialogWindow did
    /// ship, and a dialog is enough for what this needs: some state text and a few buttons.
    ///
    /// The approach is borrowed from Tomb Many Keepers (MIT, ZlordHUN and Zonda001), which
    /// builds its whole multiplayer UI — lobby, server browser, campaign picker — this way.
    /// </summary>
    internal static class CoopDialog
    {
        internal static void Show()
        {
            var window = LazyUI.GetWindow<UIDialogWindow>();
            if (window == null)
            {
                CoopKitPlugin.Instance.Log.LogError("UIDialogWindow is not available in this build.");
                return;
            }

            if (window.IsShown) window.CloseWithoutCallback();
            window.Open(new UIDialogWindowData("Co-op Kit", BuildText(), BuildButtons(window))
            {
                ShowCloseButton = true
            });
        }

        private static string BuildText()
        {
            var singleton = Reflect.NetcodeSingleton();
            var uManager = Resources.FindObjectsOfTypeAll(Reflect.GameType("UNetworkManager")).FirstOrDefault();
            bool listening = Equals(Reflect.Get(singleton, "IsListening"), true);
            bool inGame = Reflect.LoadedGameScene() != null;

            var text = new StringBuilder();

            if (listening)
            {
                int others = Reflect.Count(Reflect.Get(uManager, "OtherClients"));
                text.AppendLine($"Hosting on port 7777 — {(others > 0 ? $"{others} player(s) connected" : "waiting for a player")}");
                text.AppendLine($"Co-op flag: {Reflect.Get(uManager, "IsCoopGame")}");
                text.AppendLine();
                text.AppendLine("Give your friend one of these addresses:");
                foreach (var address in Reflect.LocalAddresses())
                    text.AppendLine($"   {address}{(address.StartsWith("26.") ? "   (Radmin VPN)" : "   (LAN)")}");
            }
            else if (inGame)
            {
                text.AppendLine("Not hosting.");
                text.AppendLine();
                text.AppendLine("The multiplayer layer ships inside Graveyard Keeper 2 but is");
                text.AppendLine("switched off. Host now turns it on for this session.");
            }
            else
            {
                text.AppendLine("Load a campaign first.");
                text.AppendLine();
                text.AppendLine("Hosting and joining both attach to a running game, so the world");
                text.AppendLine("has to be loaded before either one can do anything.");
            }

            var configured = CoopKitPlugin.Instance.HostAddressValue;
            text.AppendLine();
            text.AppendLine(string.IsNullOrEmpty(configured)
                ? "To join someone: set HostAddress in BepInEx/config/gk2.coopkit.cfg"
                : $"Configured host to join: {configured}");

            if (TrafficSpy.Active)
            {
                text.AppendLine();
                text.AppendLine("Traffic spy is recording.");
            }

            return text.ToString();
        }

        private static List<UIDialogWindowData.ButtonData> BuildButtons(UIDialogWindow window)
        {
            var plugin = CoopKitPlugin.Instance;
            var singleton = Reflect.NetcodeSingleton();
            bool listening = Equals(Reflect.Get(singleton, "IsListening"), true);
            bool inGame = Reflect.LoadedGameScene() != null;
            bool hasHostAddress = !string.IsNullOrEmpty(plugin.HostAddressValue);

            var buttons = new List<UIDialogWindowData.ButtonData>();

            bool first = true;

            if (inGame && !listening)
            {
                buttons.Add(Action(() => { window.CloseWithoutCallback(); plugin.HostNow(); Show(); }, "Host now", ref first));

                if (hasHostAddress)
                    buttons.Add(Action(() => { window.CloseWithoutCallback(); plugin.Join(); Show(); }, "Join", ref first));
            }

            if (listening)
            {
                buttons.Add(Action(() => { window.CloseWithoutCallback(); plugin.SyncSavesToClients(); Show(); },
                    "Send world", ref first,
                    () => Reflect.Count(Reflect.Get(
                        Resources.FindObjectsOfTypeAll(Reflect.GameType("UNetworkManager")).FirstOrDefault(),
                        "OtherClients")) > 0));

                buttons.Add(Action(() => { window.CloseWithoutCallback(); plugin.StopHosting(); Show(); }, "Stop hosting", ref first));
            }

            buttons.Add(Action(() => { plugin.ToggleSpy(); window.CloseWithoutCallback(); Show(); },
                TrafficSpy.Active ? "Stop measuring" : "Measure traffic", ref first));

            buttons.Add(new UIDialogWindowData.ButtonData(window.Close, "Close",
                replaceForGamepad: true, keyToReplace: GameKey.Back));
            return buttons;
        }

        /// <summary>
        /// Every button needs a GameKey. UIDialogWindowButton.Update reads keyToReplace.value
        /// before it checks replaceForGamepad, so a ButtonData built without one throws a
        /// NullReferenceException on every frame the dialog is open.
        ///
        /// Only the first action claims the gamepad's Select key; the rest opt out of the
        /// gamepad binding but still carry the key, which is what the game expects.
        /// </summary>
        private static UIDialogWindowData.ButtonData Action(System.Action onPressed, string text,
            ref bool first, System.Func<bool> available = null)
        {
            var data = new UIDialogWindowData.ButtonData(onPressed, text, available,
                replaceForGamepad: first, keyToReplace: GameKey.Select);
            first = false;
            return data;
        }
    }
}
