# Building from source

## Toolchain

- .NET SDK 6.0 or newer (the mod targets `netstandard2.1`, the server `net6.0`).
- A KSP2 install with Redux + SpaceWarp 2 — the mod compiles against the game's assemblies in
  `<KSP2>\KSP2_x64_Data\Managed`. **These DLLs are not in this repository and must not be
  committed.**
- PowerShell 5.1+ for the helper scripts (optional; plain `dotnet` commands work too).

## Point the build at your game

The mod project reads the `KSP2DIR` property. Set it once as an environment variable:

```powershell
$env:KSP2DIR = "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2"
```

or pass it per build: `dotnet build -p:KSP2DIR="..."`. If unset it defaults to the standard Steam
path.

## Client mod

```powershell
cd KSP2MultiplayerRedux
.\build_and_deploy.ps1            # build, assemble dist\, deploy to <KSP2>\mods, zip
.\build_and_deploy.ps1 -NoDeploy  # build + zip only
```

Or manually: `dotnet build KSP2MultiplayerRedux.csproj -c Release`.

**Release builds merge LiteNetLib into the mod DLL** via `ILRepack.targets` (the game's runtime
won't load a sibling DLL). Debug builds skip the merge and won't work in-game — always test a
Release build. A correct output is a single ~230 KB `KSP2MultiplayerRedux.dll` with no
`LiteNetLib` assembly reference.

Close KSP2 before deploying; the game locks the DLL.

## Dedicated server

```powershell
cd KSP2MultiplayerServer
.\build_server.ps1                 # publish win-x64 to dist\ and zip
.\build_server.ps1 -Runtime linux-x64
```

Or `dotnet publish KSP2MultiplayerServer.csproj -c Release -r win-x64 --self-contained false -o dist`.

## Repository layout

```text
KSP2MultiplayerRedux/          client mod
  src/Networking/              transport, packets, connection & session management
  src/Sync/                    vessel / time / campaign synchronisation
  src/UI/                      in-game windows
  Assets/KSP2MultiplayerRedux/UI/  UXML assets shipped with the mod
  lib/LiteNetLib.dll           merged into the mod at build time
  swinfo.json                  SpaceWarp 2 manifest (bump "version" with each release)
  ILRepack.targets             merge step
KSP2MultiplayerServer/         dedicated server
docs/                          this documentation
```

## Cutting a release

1. Bump `VERSION` in **both** `Constants.cs` files and `version` in `swinfo.json`.
2. If any packet layout changed, bump `PROTOCOL_VERSION` in **both** `Constants.cs` files.
3. `.\build_and_deploy.ps1 -NoDeploy` and `.\build_server.ps1`.
4. Attach `KSP2MultiplayerRedux_Release.zip` and `KSP2MultiplayerServer_Release.zip` to the
   GitHub release; update `CHANGELOG.md`.

## Keep the game DLLs out of the project folder

Do **not** drop copies of the game's `Managed` DLLs inside the mod folder (e.g. from a Unity
project setup). MSBuild resolves references from project-folder files *before* the HintPath, so a
stale copy would silently be compiled against instead of your real install — code that compiles
but breaks at runtime after a game update. The csproj excludes the usual Unity folders, but the
safe rule is: game assemblies live only under `$(KSP2DIR)`.

## Inspecting the game API

Redux's API is largely undocumented. A reliable way to discover signatures without launching the
game is `System.Reflection.MetadataLoadContext` pointed at `Assembly-CSharp.dll` in the Managed
folder; all APIs used here were confirmed that way.
