# OpenApparatus Studio

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Cross-platform desktop app for authoring, previewing, and exporting OpenApparatus floor plans. Built with [Avalonia 11](https://avaloniaui.net/) on .NET 8.

## What it does (v0.1)

- **Live 2D top-down preview** of a generated floor plan, redrawing on every parameter change.
- **Parameter panel** — width / height / tile size, rectangle-room count, wall thickness / height, door width / height, seed, outer-entrance toggle.
- **Save / Load** of `.floorplan.json` files (parameter spec; the floor plan itself is regenerated deterministically from the seed).
- **Export OBJ** of the assembled 3D geometry, one OBJ object per room, with named groups for floor / walls / ceiling.

## Install

Pre-built zips are attached to the [Releases page](https://github.com/OpenApparatus/studio/releases):

- **Windows** — `OpenApparatusStudio-<version>-win-x64.zip`. Extract anywhere, run `OpenApparatus.Studio.exe`.
- **macOS (Apple Silicon)** — `OpenApparatusStudio-<version>-osx-arm64.zip`. Extract and double-click `OpenApparatusStudio.app`.
- **macOS (Intel)** — `OpenApparatusStudio-<version>-osx-x64.zip`. Same.

The build is self-contained (.NET 8 runtime included) — no separate runtime install needed.

Builds are unsigned. On first run, Windows SmartScreen will warn ("Don't run" / "More info" → "Run anyway") and macOS Gatekeeper will block (right-click the .app, choose Open, then Open again at the dialog). Once accepted, the OS remembers.

## Building from source

For contributors. Requires the .NET 8 SDK or newer.

```bash
# clone alongside openapparatus-core (sibling directory)
git clone https://github.com/OpenApparatus/core.git ../openapparatus-core

# then in this repo:
dotnet run --project src/OpenApparatus.Studio
```

The project references `OpenApparatus.Core` via a relative `ProjectReference` (default: `../../openapparatus-core/src/OpenApparatus.Core`). Override with the `OpenApparatusCoreRepo` MSBuild property if your layout differs:

```bash
dotnet build -p:OpenApparatusCoreRepo=/path/to/openapparatus-core/
```

When `OpenApparatus.Core` ships on NuGet, the local clone requirement goes away.

## Cutting a release

CI builds and zips for every supported platform on tag push, then attaches the zips to a GitHub Release (see [.github/workflows/release.yml](.github/workflows/release.yml)):

```bash
git tag v0.1.0
git push --tags
```

To build a release zip locally instead:

```bash
dotnet publish src/OpenApparatus.Studio -c Release -r win-x64 --self-contained true -o publish
# then zip the publish/ folder
```

Substitute `osx-x64` or `osx-arm64` for macOS. On macOS, wrap `publish/` inside an `.app` bundle before zipping if you want Finder-launchable output.

## Architecture

| Layer | Files | Responsibility |
|---|---|---|
| ViewModel | `ViewModels/MainWindowViewModel.cs` | parameters, commands, current plan |
| View | `Views/MainWindow.axaml`, `Views/FloorPlanView.cs` | layout, custom 2D renderer |
| Services | `Services/FloorPlanSpec.cs`, `Services/FloorPlanJsonSerializer.cs`, `Services/ObjExporter.cs` | I/O |

The actual floor-plan generation, mesh assembly, and topology data structures live in [`OpenApparatus.Core`](https://github.com/OpenApparatus/core). Studio is a thin GUI wrapper around it.

## Related repos

- **[OpenApparatus/core](https://github.com/OpenApparatus/core)** — the engine-agnostic .NET library
- **[OpenApparatus/unity](https://github.com/OpenApparatus/unity)** — Unity package consuming the same library

## License

MIT — see [LICENSE](LICENSE).
