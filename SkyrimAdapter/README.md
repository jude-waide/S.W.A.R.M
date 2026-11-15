# Skyrim adapter

This SKSE/CommonLibSSE-NG plugin connects Skyrim Special Edition to the
game-scoped `/api/plugin/skyrim` protocol while leaving the Minecraft adapter
and routes unchanged. It starts when an exterior save loads, spawns five
temporary references using Skyrim's own Bee base object, samples landscape
height into the protocol's local cell grid, and moves the references toward
accepted planner destinations.

The adapter intentionally does not extract or package Minecraft assets. It
does not yet sample buildings, trees, or other non-landscape collision geometry;
use an open exterior area for this prototype. Interior cells are rejected rather
than represented as invented terrain.

CommonLibSSE-NG is GPL-3.0-or-later. Review its license and any applicable
obligations before distributing a build.

## Build

Requirements: Visual Studio 2022 C++ tools, CMake, Git, and vcpkg.
CommonLibSSE-NG is pinned to `v16.5.0`; its release binary is preferred, with a
source build as fallback.

```powershell
$env:VCPKG_ROOT = "C:\path\to\vcpkg"
cmake -S .\SkyrimAdapter -B .\SkyrimAdapter\build -G "Visual Studio 17 2022" -A x64 `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT\scripts\buildsystems\vcpkg.cmake" `
  -DCOMMONLIB_PREBUILT_MULTICONFIG=ON `
  -DVCPKG_TARGET_TRIPLET=x64-windows-vs2022 `
  -DVCPKG_OVERLAY_TRIPLETS="$PWD\SkyrimAdapter\triplets"
cmake --build .\SkyrimAdapter\build --config Release
```

The resulting DLL is written to
`SkyrimAdapter\build\Data\SKSE\Plugins\DroneSwarmSkyrim.dll`.

## Local setup

1. Install SKSE for the installed Steam runtime and launch Skyrim through
   `skse64_loader.exe`.
2. Copy the built `DroneSwarmSkyrim.dll` to `Data\SKSE\Plugins\`.
3. Copy `DroneSwarmSkyrim.ini.example` to
   `Data\SKSE\Plugins\DroneSwarmSkyrim.ini`; set `BaseUrl` and the same
   `PluginKey` configured by the web backend. Never commit the real key.
4. Build the dashboard/backend, load a save in an exterior area, and open the
   dashboard's Skyrim session.
5. End or reset any existing Skyrim mission before loading the save. The plugin
   will not replace an active mission automatically.

The Skyrim game build can be validated locally, but an in-game smoke test also
requires SKSE and Address Library installed for the exact game runtime. The
adapter currently supports exterior landscape sensing and is an early prototype,
not a drop-in replacement for the Minecraft entity/obstacle model.

`TargetX` and `TargetZ` are offsets from the exterior cell's lower-left corner
(0-127); `TargetY` is an offset above the first air cell over the target terrain
(0-31).
