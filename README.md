# OpenApparatus Studio

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Cross-platform desktop app for authoring, previewing, and exporting OpenApparatus floor plans. Built with [Avalonia 11](https://avaloniaui.net/) on .NET 8.

## What it does (v0.1)

- **Live 2D top-down preview** of a generated floor plan, redrawing on every parameter change.
- **Parameter panel** — width / height / tile size, rectangle-room count, wall thickness / height, door width / height, seed, outer-entrance toggle.
- **Save / Load** of `.floorplan.json` files (parameter spec; the floor plan itself is regenerated deterministically from the seed).
- **Export OBJ** of the assembled 3D geometry, one OBJ object per room, with named groups for floor / walls / ceiling.

## Install

Pre-built installers are published on the [Releases page](https://github.com/OpenApparatus/studio/releases):

- **Windows** — `OpenApparatusStudio-win-Setup.exe`
- **macOS (Apple Silicon)** — `OpenApparatusStudio-osx-arm64-Setup.pkg`
- **macOS (Intel)** — `OpenApparatusStudio-osx-x64-Setup.pkg`

The installer bundles the .NET 8 runtime, so no other prerequisites are required. Updates are delivered automatically via [Velopack](https://velopack.io).

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

## Building an installer

CI builds installers for all platforms automatically when a `v*.*.*` tag is pushed (see [.github/workflows/release.yml](.github/workflows/release.yml)). To cut a release:

```bash
git tag v0.1.0
git push --tags
```

To build an installer locally:

```bash
# Windows
./scripts/build-installer.ps1 -Version 0.1.0

# macOS / Linux
./scripts/build-installer.sh 0.1.0
```

Output lands in `./releases/`.

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
