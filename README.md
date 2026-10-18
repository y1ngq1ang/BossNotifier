# BossNotifier for SPT 5.0

A client mod that notifies you which bosses are present in your raid.

## Features

- 🔔 Notification at raid start showing which bosses spawned
- 📍 Shows boss spawn locations (if Intel Center unlocked)
- ✓ Real-time detection when you get near a boss
- 🌙 Cultist notifications only appear at night (7 PM - 7 AM)
- ⌨️ Press 'O' to show boss notifications again (can change by F12 menu)
  - Press F12 in-game to access BepInEx configuration:
    - Keyboard shortcut (default: O)
    - Show notifications on raid start
    - Intel Center level requirements
- 🎯 On-screen markers above bosses (symbol, name, distance, per-group color)
- 🧩 Custom boss groups loaded from `custom_bosses.json` (RUAF, UNTAR, Black Division)
- 🆕 SPT 5.0 roles: The Wedge, Black Division, Russian Military, Shadow of Tagilla
  (Labyrinth), Vengeful Killa and Zryachiy's revenge event

## Installation

1. Download the latest release from [Releases](https://github.com/y1ngq1ang/BossNotifier/releases)
2. Extract the zip into your SPT 5.0 root folder (where `EscapeFromTarkov.exe` is)
3. Start the game

## Build Requirements

SPT 5.0 is IL2CPP only, so this is a BepInEx 6 plugin targeting `net10.0` that links
against the interop assemblies BepInEx generates on the first game launch.

1. Launch SPT 5.0 once so `BepInEx/interop/` is generated.
2. Set the install root (defaults to `C:\SPT-5.0-BE`):
   ```
   dotnet build -c Release
   dotnet build -c Release -p:SptRoot=D:\SPT
   ```
3. The build deploys into `BepInEx/plugins/BossNotifier/` and writes
   `BossNotifier-<version>.zip`. Pass `-p:DeployOnBuild=false` to only produce the zip.

Referenced from the install: `BepInEx/core` (`BepInEx.Core`, `BepInEx.Unity.IL2CPP`,
`0Harmony`, `Il2CppInterop.Runtime`), `BepInEx/plugins/sptushonka/SPTushonka.Reflection.dll`
and `BepInEx/interop/` (Assembly-CSharp, Comfort, Il2Cppmscorlib,
UnityEngine.CoreModule, UnityEngine.PhysicsModule, UnityEngine.TextRenderingModule).

## Notes for the 5.0 port

- `BasePlugin` with `Load()` replaces `BaseUnityPlugin.Awake()`.
- The runtime MonoBehaviours are registered with
  `Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<T>()`; injected
  MonoBehaviour classes need a `(IntPtr ptr) : base(ptr)` constructor.
- `UnityEngine.TextMesh` has no registered IL2CPP class pointer in this build, so markers
  are drawn as IMGUI labels from `OnGUI` (`GUI.Label` + `Camera.WorldToScreenPoint`) - the
  same approach as flir.enemymarkers. `AddComponent<T>()` also only works for types IL2CPP
  already instantiated, and a failed generic method store stays poisoned for later calls.
- `BepInEx.Unity.IL2CPP.Configuration.KeyboardShortcut` is the IL2CPP flavour of the
  shortcut config entry; `IsDown()` matches the old `GetKeyDown` behaviour.
- Events on IL2CPP types accept managed delegates (`Action<IPlayer>`); use
  `DelegateSupport.ConvertDelegate<T>()` only when the accessor wants
  `Il2CppSystem.Action<T>`.
- IL2CPP turns instance fields into properties, so patches read `__instance.Prop`
  instead of injected `___field` values.
- Boss locations come from `BotOwner.SpawnBotZone`; `BossLocationSpawn.BossZone` only
  holds the list of zones a boss may use.
- BepInEx rejects `=` (and a few other characters) in section/key names, so the Chinese
  config section reads `4 表示禁用` rather than `4 = 禁用`.

## Credits

- Original mod by [Mattdokn](https://github.com/m-barneto/BossNotifier)
- Updated for SPT 4.1.3, now ported to SPT 5.0

## License

MIT License
