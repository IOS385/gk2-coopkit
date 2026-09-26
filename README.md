# GK2 Co-op Kit

Graveyard Keeper 2 shipped as a single-player game. It also shipped, compiled into the
release build, with a complete Unity Netcode stack: transport, a host-authoritative command
system, save synchronisation, and three lobby windows. What was removed were the menu
buttons and the window prefabs. The code stayed.

This plugin makes the calls the game no longer makes, and measures what it would actually
put on the wire.

**Status: experimental.** Hosting works and the session stays playable. Two real players
have not been tested yet — see [What is not proven](#what-is-not-proven).

## What was found

| | |
|---|---|
| Runtime | Mono, `Assembly-CSharp.dll` unobfuscated, 2773 classes |
| Shipped libraries | `Unity.Netcode.Runtime.dll`, `Unity.Networking.Transport.dll` |
| Live in every session | `NetworkManager` and a `UnityTransport` on a `UNetworkManager` object in `DontDestroyOnLoad`, created when a save loads |
| Never called | `LazyNetwork.Init(INetworkManager, BaseNetworkMessageChannelManager)` — public, and nothing in the game invokes it |
| Missing | The prefabs for `UIStartHostGameWindow`, `UIConnectToHostGameWindow` and `UILobbyWindow`. The classes are in the assembly; asking `LazyUI` for them throws |
| Replicating methods | **3**, marked `[NetworkMethod]`: `WgoData.ApplyTool`, `WorldData.AddWgoDataToGameScene`, `WorldData.RemoveWgoDataFromGameScene` |

Verified from outside the game while hosting:

```
PS> Get-NetUDPEndpoint -LocalPort 7777
LocalAddress  LocalPort  OwningProcess
0.0.0.0            7777           9308   (GraveyardKeeper2)
```

### The minimal sequence to host

1. Load a campaign through the normal menu.
2. `LazyNetwork.Init(<UNetworkManager in the scene>, new UNetworkMessageChannelManager())`
3. `UNetworkManager.StartHostGame("0.0.0.0", 7777)` — **exactly once**
4. `new NetworkPlayer((int)NetworkManager.Singleton.LocalClientId, gameSave.playerData)` →
   assign to `GameSave.hostPlayer` → `MainGame.Instance.PlayerUniqueCommandHolder.RegisterData(...)`

Step 4 is the networking half of `MainGame.PrepareGameForNetwork`, without the world-data
half. That distinction matters: see bug 2 below.

### Three bugs in the shipped code

1. **`StartHostGame` is not re-entrant.** It assigns `isCoopGame = NetworkManager.Singleton.StartHost()`.
   A second call returns `false` because a host already listens, which clears `IsCoopGame`
   while the server keeps running.
2. **`PrepareForGame` is not idempotent.** `NPCLifeSimulatorData.PrepareForGame` does
   `cachedPoints.Add(id, …)` without clearing first, so a second pass throws
   `An item with the same key has already been added` mid-`LoadingPipeline` and loading
   never completes. `LobbyHelper.Host_Init` + `MainGame.ContinueGame` hits exactly this.
3. **Dirty teardown.** On shutdown, `OnlineState.Exit()` unregisters message channels that
   were never registered: `NullReferenceException` in `UNetworkMessageChannel<T>.Unregister`.

Two smaller ones: `LobbyHelper.Host_Init` needs `MainGame.Instance.gameSceneConfigs`
populated (it only fills after a campaign has loaded once, otherwise it throws in
`GdPointsData.LinkNextGdPointsData`), and `LobbyHelper.GetLocalIpAddress()` returns the
first IPv4 from `Dns.GetHostEntry`, which on a machine with Radmin VPN is the 26.x adapter
rather than the LAN one.

## What the plugin does

| Key | |
|---|---|
| **F4** | Opens the Co-op Kit screen — also reachable from a button in the main menu |
| **F6** | Host: the four calls above |
| **F8** | Join the address in `BepInEx/config/gk2.coopkit.cfg` under `[Join] HostAddress` |
| **F5** | Host: `LobbyHelper.Host_SyncGameSaves()` |
| **F7** | Stop hosting |
| **F9** | State dump to `BepInEx/coopkit/` |
| **F1** | Traffic spy on/off; writes a report when switched off |

### The traffic spy

The guard that drops traffic lives *inside* `OnlineState.AddCommand`:

```csharp
if (command != null && (!IsHost || OtherClients.Count != 0)) commands.Add(command);
```

A solo host therefore walks the whole path and discards at the last step, which means one
player can measure what a second player would have received. The report also counts the
three `[NetworkMethod]` actions directly, so a silent report can be told apart from one
where those actions never happened.

First measurement, 3.3 minutes of ordinary play: zero world commands, and
`PlayerUniqueCommandHolder` attempted ~11 times per second. Player position, facing and
animation are the only continuously flowing channel.

## What is not proven

Everything about a second player. The client path also needs the patch in this plugin:
`LobbyHelper.Client_StartGame` begins with `LazyUI.GetWindow<UILobbyWindow>().Close()`,
which throws because the prefab is gone, so it never reaches `PrepareGameForNetwork`. The
patch skips that line and runs the rest — untested against a real client.

## Build

.NET SDK 6 or newer, and BepInEx 5 x64 installed in the game folder.

```bash
dotnet build -c Release                  # default Steam path
dotnet build -c Release -p:Deploy=true   # also copies to BepInEx/plugins
```

Copy `build/Local.props.example` to `build/Local.props` to point at another install.

## Install

Grab the release zip, extract it into the game folder (the one with
`GraveyardKeeper2.exe`), and it brings BepInEx with it. `LEIAME.txt` inside has the
instructions in Portuguese, including the Radmin VPN setup.

Use a save you do not mind losing, and consider turning Steam Cloud off for the game while
experimenting.

## Credits

The UI techniques come from
[Tomb Many Keepers](https://github.com/ZlordHUN/GYK2-Tomb-Many-Keepers) (MIT, ZlordHUN and
Zonda001): cloning a `LazyButton` into the main menu, and building screens out of the
native `UIDialogWindow` instead of prefabs that are not in the build. If you want to
actually play co-op today, that is the project to use — it implements its own networking
and covers far more of the game than the three replicating methods found here.

MIT licensed. Not affiliated with Lazy Bear Games or tinyBuild.
