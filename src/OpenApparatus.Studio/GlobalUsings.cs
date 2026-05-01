// Bring the OpenApparatus.IO library types into scope without touching every
// existing .cs file's `using` block. The exporter classes and the POCO types
// (PlacementConstraints, RoomObject, ObjectType, ProjectFile, ...) used to
// live in OpenApparatus.Studio.Services / .ViewModels namespaces and are now
// referenced unqualified throughout the codebase. After their move into the
// shared OpenApparatus.IO library, these globals re-establish those names
// without per-file edits.
global using OpenApparatus.IO;
global using OpenApparatus.IO.Exporters;
