using Cysharp.Threading.Tasks;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GK2.CoopKit
{
    /// <summary>
    /// Adds a "Co-op Kit" entry to the main menu.
    ///
    /// The technique — clone an existing LazyButton, clear its id and callbacks, then re-run
    /// the gamepad wiring — is taken from Tomb Many Keepers (MIT, ZlordHUN and Zonda001).
    /// Two details there are not obvious and are the reason this works at all:
    /// LazyUIElementId must be cleared because this menu has no id registry, and the layout
    /// needs RefreshContentFitterAndDisable after a button is added.
    /// </summary>
    [HarmonyPatch(typeof(UIMainMenuWindow))]
    internal static class MainMenuPatch
    {
        private const string ButtonName = "Co-op Kit";

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UIMainMenuWindow.Init))]
        private static void AddButton(UIMainMenuWindow __instance, LazyButton ___gameSettingsButton)
        {
            if (___gameSettingsButton == null) return;
            if (___gameSettingsButton.transform.parent.Find(ButtonName) != null) return;

            var button = Object.Instantiate(___gameSettingsButton, ___gameSettingsButton.transform.parent);
            button.transform.SetSiblingIndex(___gameSettingsButton.transform.GetSiblingIndex() + 1);
            button.LazyUIElementId = string.Empty;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(new UnityAction(CoopDialog.Show));
            button.SetCallbacksIntoGamepadNavigationItem();

            var label = button.GetComponentInChildren<LocalizedLabel>(true);
            if (label != null)
            {
                label.IgnoreLocalize = true;
                label.GetComponent<TMP_Text>().text = ButtonName;
            }

            button.name = ButtonName;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UIMainMenuWindow.Open))]
        private static void RefreshLayout(UIMainMenuWindow __instance, LazyButton ___gameSettingsButton)
        {
            var ours = ___gameSettingsButton?.transform.parent.Find(ButtonName);
            // Child text-style components re-apply their own style on activation.
            ours?.GetComponent<LazyButton>()?.SetKeepPressed(false);
            ((RectTransform)__instance.transform).RefreshContentFitterAndDisable();
        }
    }

    /// <summary>
    /// Unblocks the game's own client entry path.
    ///
    /// LobbyHelper.Client_StartGame starts with LazyUI.GetWindow&lt;UILobbyWindow&gt;().Close(),
    /// and that prefab is not in the build, so the call throws there — before
    /// PrepareGameForNetwork and LoadGameScene, which are the parts that matter. Everything
    /// after that first line is intact, so this replacement runs it without the window.
    ///
    /// This is the one patch that changes game behaviour rather than observing it.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientStartGamePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LobbyHelper), nameof(LobbyHelper.Client_StartGame))]
        private static bool SkipMissingLobbyWindow()
        {
            var log = CoopKitPlugin.Instance.Log;
            log.LogInfo("Client_StartGame intercepted — running it without the missing lobby window.");

            try
            {
                int clientId = (int)Unity.Netcode.NetworkManager.Singleton.LocalClient.ClientId;
                var save = MainGame.Instance.GameSave;

                if (!save.GetClient(clientId, out var clientPlayer))
                {
                    log.LogError($"The host's save has no client {clientId}. Did the world transfer finish?");
                    return false;
                }

                MainGame.Instance.PrepareGameForNetwork(save, clientPlayer, isHost: false);
                log.LogInfo("PrepareGameForNetwork done. Loading the host's world.");

                // Go through the loading overlay rather than a bare LoadGameScene, or the menu
                // stays on screen while the world changes behind it.
                var overlay = LazyUI.Get<UILoadingOverlay>();
                if (overlay != null)
                    overlay.Draw(new LoadingWindowData(MainGame.EntrySceneToLoad,
                        () => MainGame.Instance.LoadGameScene().Forget()));
                else
                    MainGame.Instance.LoadGameScene().Forget();
            }
            catch (System.Exception e)
            {
                log.LogError($"Client entry failed: {e.GetType().Name}: {e.Message}");
            }

            return false;
        }
    }
}
