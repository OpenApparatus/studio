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

<!-- BEGIN TEMPORARY: Preview / unsigned-build instructions. Remove this section once SSL.com EV cert and Apple Developer ID are provisioned and code-signing is enabled in .github/workflows/release.yml. -->

## Preview builds (unsigned — temporary)

Until our code-signing certificates are provisioned (SSL.com EV for Windows, Apple Developer ID for macOS), preview builds are **unsigned**. They install and run normally, but the operating system will warn the user that the publisher is unverified. The instructions below cover both how to produce a preview build and how a recipient installs it.

### For engineers — producing a preview build to share

Cut a pre-release tag. The release workflow builds installers for all three targets (`win-x64`, `osx-x64`, `osx-arm64`) and attaches them to a GitHub Release.

```bash
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

When the workflow finishes, find the artifacts on the [Releases page](https://github.com/OpenApparatus/studio/releases). Download the relevant `.pkg` (macOS) or `.exe` (Windows) and send it to your tester via Slack, email, or a private link.

> **Tip:** ask the recipient which Mac they have. Apple Silicon (M1 / M2 / M3 / M4) needs `osx-arm64`. Older Intel Macs need `osx-x64`. If unsure, they can check **Apple menu → About This Mac** — "Apple M…" means arm64, "Intel" means x64.

### For macOS recipients — installing an unsigned preview build

When you double-click the `.pkg` file you'll see one of these warnings:

> *"OpenApparatusStudio-osx-arm64-Setup.pkg" cannot be opened because Apple cannot check it for malicious software.*

This is expected — the build is unsigned during preview. To install:

**Option 1 — GUI (easiest)**

1. Double-click the `.pkg`. macOS blocks it. Click **Done**.
2. Open **System Settings → Privacy & Security**.
3. Scroll to the **Security** section near the bottom. You'll see a message like *"OpenApparatusStudio-osx-arm64-Setup.pkg was blocked because it is not from an identified developer."*
4. Click **Open Anyway**.
5. Authenticate with your Mac password / Touch ID when prompted.
6. The installer launches normally.

**Option 2 — Terminal (fastest)**

If you're comfortable with the command line:

```bash
xattr -cr ~/Downloads/OpenApparatusStudio-osx-arm64-Setup.pkg
open ~/Downloads/OpenApparatusStudio-osx-arm64-Setup.pkg
```

The `xattr` command strips the quarantine attribute that triggers Gatekeeper. The installer then runs normally.

> **After installing**, if macOS blocks the OpenApparatus Studio app itself on first launch, repeat the same workaround — System Settings → Privacy & Security → Open Anyway, **or** right-click the app in Applications → **Open** → **Open** in the confirmation dialog.

### For Windows recipients — installing an unsigned preview build

When you double-click `OpenApparatusStudio-win-Setup.exe` you'll see:

> *Windows protected your PC — Microsoft Defender SmartScreen prevented an unrecognized app from starting.*

To install:

1. Click **More info** in the warning dialog.
2. Click **Run anyway**.
3. The installer proceeds normally.

This warning will disappear once the build is signed with our SSL.com EV certificate.

<!-- END TEMPORARY -->

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
