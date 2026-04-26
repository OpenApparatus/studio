# OpenApparatus Studio

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Cross-platform desktop app for authoring, previewing, and exporting OpenApparatus floor plans. Built with [Avalonia](https://avaloniaui.net/) on .NET 8.

> 🚧 **Pre-scaffold.** This repo is a placeholder until milestone **B0** lands. The Avalonia project, ViewModel scaffolding, and 2D top-down viewer are coming next.

## Planned scope (v0.1)

- 2D top-down viewer rendering [`OpenApparatus.Core`](https://github.com/OpenApparatus/core) `FloorPlan` objects via Avalonia's `DrawingContext`
- Parameter panel with live regeneration on edit
- Save / load `.floorplan.json` files
- Export geometry to OBJ for handing meshes to other tools

## Why standalone

The core library generates engine-agnostic environments. Studio lets researchers author and inspect those environments without any game engine in the loop — useful for replication, methods-section figures, or pipelines that hand the output to non-Unity consumers.

## Related repos

- **[OpenApparatus/core](https://github.com/OpenApparatus/core)** — the engine-agnostic .NET library Studio is built on
- **[OpenApparatus/unity](https://github.com/OpenApparatus/unity)** — Unity package consuming the same core library

## License

MIT — see [LICENSE](LICENSE).
