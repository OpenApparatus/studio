using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenApparatus;
using OpenApparatus.Studio.Services;
using OpenApparatus.Topology;
using OpenApparatus.Topology.Assigners;
using OpenApparatus.Topology.Generators;

namespace OpenApparatus.Studio.ViewModels;

/// <summary>
/// View-model for the multi-room environment editor. Owns the authored tile grid
/// (which tile belongs to which room, or -1 for empty) plus visual / generation
/// parameters. The materialized <see cref="MultiRoomEnvironment"/> is rebuilt on
/// every grid change.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    // ---- Grid + authoring state ----

    [ObservableProperty] int _gridWidth = 8;
    [ObservableProperty] int _gridLength = 6;
    [ObservableProperty] float _tileSize = 3.5f;

    /// <summary>
    /// roomId per tile; -1 = empty. Indexing: <c>RoomGrid[x, z]</c>. Always
    /// <c>GridWidth × GridLength</c> in size.
    /// </summary>
    public int[,] RoomGrid { get; private set; }

    /// <summary>Tiles the user has selected but not yet committed to a room.</summary>
    public HashSet<(int x, int z)> SelectedTiles { get; } = new();

    int _nextRoomId = 0;

    /// <summary>
    /// Per-wall passage overrides remembered across grid rebuilds. Without this,
    /// every Rebuild() resets all walls to Closed (since FromGrid produces a fresh
    /// MultiRoomEnvironment with default passages), losing user-placed doorways.
    ///
    /// Keys identify a wall by its segment midpoint, quantized to millimeters.
    /// This means a door survives outer→internal transitions: when a new room is
    /// placed on the far side of an existing outer wall, the wall's physical
    /// location stays the same and the override applies to the new internal
    /// adjacency. Room-pair-based keys would lose the door in that case.
    /// </summary>
    /// <summary>
    /// Per-wall passage overrides remembered across grid rebuilds. The Start
    /// position is captured alongside the passage so that when the wall's
    /// segment direction flips on a rebuild (which happens whenever the lower-
    /// id RoomA changes — e.g. after a renumber), each opening's offset can
    /// be flipped to keep its world position fixed.
    /// </summary>
    readonly Dictionary<(int, int), (Passage Passage, System.Numerics.Vector2 Start)> _passageOverrides = new();

    /// <summary>
    /// Per-wall color overrides keyed by (roomId, segment-midpoint-mm). A wall
    /// shared between two rooms can carry two distinct colors — one per side.
    /// Stored as RGB in 0..1; default gray is used when no override is set.
    /// </summary>
    readonly Dictionary<(int RoomId, int MidX, int MidZ), System.Numerics.Vector3> _wallColors = new();

    static (int, int) PassageKey(Adjacency adj)
    {
        var mid = adj.SharedSegment.Midpoint;
        return (
            (int)System.Math.Round(mid.X * 1000),
            (int)System.Math.Round(mid.Y * 1000));
    }

    void RememberPassage(Adjacency adj)
        => _passageOverrides[PassageKey(adj)] = (adj.Passage, adj.SharedSegment.Start);

    /// <summary>Read-only access to per-wall color overrides (for view rendering).</summary>
    public IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), System.Numerics.Vector3> WallColors => _wallColors;

    /// <summary>
    /// Adjacencies the currently selected room participates in, in build order.
    /// Empty when no room is selected (or when CurrentEnvironment hasn't been
    /// rebuilt for the selected id).
    /// </summary>
    public IReadOnlyList<Adjacency> SelectedRoomAdjacencies
    {
        get
        {
            var list = new List<Adjacency>();
            if (CurrentEnvironment is null || SelectedRoomId < 0) return list;
            foreach (var adj in CurrentEnvironment.Adjacencies)
                if (adj.RoomA.Id == SelectedRoomId || (adj.RoomB?.Id == SelectedRoomId))
                    list.Add(adj);
            return list;
        }
    }

    /// <summary>Returns the index of the opening on <paramref name="adj"/> whose span
    /// contains <paramref name="alongMeters"/>, or -1 if none.</summary>
    static int FindOpeningAt(Adjacency adj, float alongMeters)
    {
        if (adj.Passage is not Passage.Doorway dw) return -1;
        const float EPS = 1e-3f;
        for (int i = 0; i < dw.Openings.Count; i++)
        {
            var op = dw.Openings[i];
            if (alongMeters >= op.OffsetAlongEdge - EPS &&
                alongMeters <= op.OffsetAlongEdge + op.Width + EPS)
                return i;
        }
        return -1;
    }

    /// <summary>Replace the opening at <see cref="SelectedOpeningIndex"/> with the
    /// result of <paramref name="transform"/>. No-op when no opening is selected.</summary>
    public void UpdateSelectedOpening(System.Func<Opening, Opening> transform)
    {
        if (!HasSelectedOpening || SelectedAdjacency is null) return;
        var dw = (Passage.Doorway)SelectedAdjacency.Passage;
        var openings = new List<Opening>(dw.Openings);
        var newOp = transform(openings[SelectedOpeningIndex]);
        // Keep within wall length.
        var seg = SelectedAdjacency.SharedSegment;
        if (newOp.OffsetAlongEdge + newOp.Width > seg.Length + 1e-3f)
        {
            StatusMessage = $"Can't fit a {newOp.Width:F2}m opening — wall is only {seg.Length:F2}m.";
            return;
        }
        PushUndo();
        openings[SelectedOpeningIndex] = newOp;
        SelectedAdjacency.Passage = new Passage.Doorway(openings);
        RememberPassage(SelectedAdjacency);
        EditVersion++;
        OnPropertyChanged(nameof(SelectedOpening));
    }

    /// <summary>Effective wall color for a room's side of an adjacency, accounting for
    /// the room's multi-color toggle. Falls back to the default neutral when nothing
    /// has been set.</summary>
    public System.Numerics.Vector3 EffectiveWallColor(int roomId, Adjacency adj)
    {
        var key = GltfExporter.WallColorKey(roomId, adj);
        if (IsRoomMultiColor(roomId) && _wallColors.TryGetValue(key, out var perWall))
            return perWall;
        if (_roomSingleWallColors.TryGetValue(roomId, out var single))
            return single;
        return new System.Numerics.Vector3(0.78f, 0.78f, 0.80f);
    }

    /// <summary>Sets the color for one room's side of the given wall. RGB in 0..1.</summary>
    public void SetWallColor(int roomId, Adjacency adj, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _wallColors[GltfExporter.WallColorKey(roomId, adj)] = rgb;
        EditVersion++;
    }

    /// <summary>Removes any color override for one room's side of the given wall.</summary>
    public void ClearWallColor(int roomId, Adjacency adj)
    {
        PushUndo();
        _wallColors.Remove(GltfExporter.WallColorKey(roomId, adj));
        EditVersion++;
    }

    // ---- Visual / generation parameters ----

    [ObservableProperty] float _wallThickness = 0.2f;
    [ObservableProperty] float _wallHeight = 3f;
    [ObservableProperty] float _doorWidth = 1.2f;
    [ObservableProperty] float _doorHeight = 2.2f;
    [ObservableProperty] float _windowWidth = 1.2f;
    [ObservableProperty] float _windowHeight = 2.2f;
    [ObservableProperty] float _windowSillHeight = 1.0f;

    /// <summary>
    /// id of the currently selected room (-1 = none). Set when the user clicks a
    /// tile that already belongs to a room, or right after a fresh CreateRoom.
    /// Drives the right-side room editor panel.
    /// </summary>
    [ObservableProperty] int _selectedRoomId = -1;

    /// <summary>
    /// Index of the currently selected opening within <see cref="SelectedAdjacency"/>'s
    /// passage (-1 = none). Set when the user clicks a wall anchor that lands inside
    /// an existing opening, or right after toggling a new door / window in. Drives
    /// the right-side opening editor panel — when this is &gt;=0 the panel shows
    /// per-opening size + hinge / swing controls instead of room appearance.
    /// </summary>
    [ObservableProperty] int _selectedOpeningIndex = -1;

    public bool HasSelectedOpening =>
        SelectedOpeningIndex >= 0 && SelectedAdjacency?.Passage is Passage.Doorway dw
        && SelectedOpeningIndex < dw.Openings.Count;

    public Opening? SelectedOpening
        => HasSelectedOpening
            ? ((Passage.Doorway)SelectedAdjacency!.Passage).Openings[SelectedOpeningIndex]
            : null;

    partial void OnSelectedOpeningIndexChanged(int value)
        => OnPropertyChanged(nameof(HasSelectedOpening));

    /// <summary>Defaults applied to every new room's floor / ceiling. Editing them
    /// does NOT retroactively change existing rooms — those keep whatever colors
    /// they had at creation (or after manual override).</summary>
    [ObservableProperty] System.Numerics.Vector3 _defaultFloorColor   = new(0.92f, 0.92f, 0.93f);
    [ObservableProperty] System.Numerics.Vector3 _defaultCeilingColor = new(0.92f, 0.92f, 0.90f);
    /// <summary>Default colour applied to newly-created rooms' walls.
    /// Stored as the per-room single-wall colour so the visual matches
    /// what RoomEditorPanel's Wall colour row shows. The value can
    /// still be overridden per-wall later. Ignored when
    /// <see cref="UseRandomDefaultWallColor"/> is on.</summary>
    [ObservableProperty] System.Numerics.Vector3 _defaultWallColor    = new(0.84f, 0.85f, 0.87f);

    /// <summary>When on, each newly-created room is assigned a random
    /// HSV-rotated wall colour instead of <see cref="DefaultWallColor"/>.
    /// Surfaced in the UI as a rainbow-gradient swatch. Defaults to
    /// true so a fresh project visually distinguishes adjacent rooms
    /// without the user touching settings.</summary>
    [ObservableProperty] bool _useRandomDefaultWallColor = true;

    /// <summary>Brush displayed in the wall-default swatch — either a
    /// solid colour or a rainbow gradient when random-mode is on.
    /// Notified whenever either underlying property changes.</summary>
    public Avalonia.Media.IBrush WallDefaultBrush =>
        UseRandomDefaultWallColor
            ? BuildRainbowBrush()
            : (Avalonia.Media.IBrush)new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(
                    (byte)(DefaultWallColor.X * 255),
                    (byte)(DefaultWallColor.Y * 255),
                    (byte)(DefaultWallColor.Z * 255)));

    static Avalonia.Media.IBrush BuildRainbowBrush()
    {
        var b = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
            EndPoint   = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
        };
        // Hue sweep 0 → 360 in 7 stops, taken at S=0.55, V=0.85 so the
        // rainbow signals "any colour" without screaming saturation.
        var stops = new[] {
            (0.00, 0xE2, 0x74, 0x74),  // red
            (0.16, 0xE2, 0xB6, 0x74),  // orange
            (0.33, 0xE2, 0xDC, 0x74),  // yellow
            (0.50, 0x8E, 0xC8, 0x6E),  // green
            (0.66, 0x6E, 0xB1, 0xCC),  // cyan
            (0.83, 0x82, 0x7C, 0xD0),  // indigo
            (1.00, 0xCB, 0x7B, 0xC4),  // magenta
        };
        foreach (var (off, r, g, bb) in stops)
            b.GradientStops.Add(new Avalonia.Media.GradientStop(
                Avalonia.Media.Color.FromRgb((byte)r, (byte)g, (byte)bb), off));
        return b;
    }

    partial void OnDefaultWallColorChanged(System.Numerics.Vector3 value)
        => OnPropertyChanged(nameof(WallDefaultBrush));
    partial void OnUseRandomDefaultWallColorChanged(bool value)
        => OnPropertyChanged(nameof(WallDefaultBrush));

    /// <summary>Hue-rotated random Vector3 colour at S=0.55, V=0.82.
    /// Bright enough to be distinguishable but desaturated enough not to
    /// clash with floor / ceiling palettes.</summary>
    static System.Numerics.Vector3 PickRandomWallColor()
    {
        // Use a deterministic-seeded RNG that rolls forward each call so
        // adjacent rooms get visibly different hues.
        s_wallRng ??= new System.Random();
        float h = (float)s_wallRng.NextDouble();
        return HsvToRgbVec3(h, 0.55f, 0.82f);
    }
    static System.Random? s_wallRng;
    static System.Numerics.Vector3 HsvToRgbVec3(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f;
        float c = v * s;
        float x = c * (1f - System.MathF.Abs((h * 6f) % 2f - 1f));
        float m = v - c;
        float r = 0, g = 0, b = 0;
        int seg = (int)System.Math.Floor(h * 6f);
        switch (seg) {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }
        return new System.Numerics.Vector3(r + m, g + m, b + m);
    }

    /// <summary>The user-facing edit mode. Layout mode is for authoring the
    /// floor plan (rooms, walls, doors, windows). Object mode is for placing
    /// objects in those rooms. Each mode swaps the left panel and the
    /// editor's input semantics.</summary>
    public enum EditModeKind { Layout, Object }
    [ObservableProperty] EditModeKind _editMode = EditModeKind.Layout;

    partial void OnEditModeChanged(EditModeKind value)
    {
        // Mode switch resets the contextual selection so the right panel
        // doesn't dangle on something irrelevant in the new mode.
        SelectedAdjacency = null;
        SelectedOpeningIndex = -1;
        SelectedSubCell = null;
        SelectedObjectIndex = -1;
        SelectedTiles.Clear();
        RefreshSelectionState();
        EditVersion++;
        OnPropertyChanged(nameof(IsObjectsMode));
        OnPropertyChanged(nameof(IsLayoutMode));
        OnPropertyChanged(nameof(ShowFloorCeilingTabs));
        OnPropertyChanged(nameof(SceneSummary));
    }

    public bool IsObjectsMode => EditMode == EditModeKind.Object;
    public bool IsLayoutMode => EditMode == EditModeKind.Layout;

    /// <summary>Whether the keyboard-shortcut help overlay is showing. F1
    /// toggles, Esc dismisses (handled in MainWindow code-behind).</summary>
    [ObservableProperty] bool _isShortcutOverlayVisible;

    [RelayCommand]
    void ToggleShortcutOverlay() => IsShortcutOverlayVisible = !IsShortcutOverlayVisible;

    [RelayCommand]
    void DismissShortcutOverlay() => IsShortcutOverlayVisible = false;

    /// <summary>Whether the command palette (Ctrl+K) is open.</summary>
    [ObservableProperty] bool _isCommandPaletteOpen;

    [RelayCommand]
    void OpenCommandPalette() => IsCommandPaletteOpen = true;

    /// <summary>Tile-colour view used while in Layout mode. Floor shows each
    /// room's floor colour, Ceiling shows the ceiling colour. Has no effect in
    /// Object mode (always renders floor colours).</summary>
    public enum ViewSurface { Floor, Ceiling }
    [ObservableProperty] ViewSurface _viewMode = ViewSurface.Floor;

    /// <summary>Camera projection used by the editor. TopDown is the
    /// authoritative editing view; Iso is a real 3D viewer with orbit /
    /// pan / zoom — read-only so users switch back to TopDown to edit.</summary>
    public enum CameraKind { TopDown, Iso }
    [ObservableProperty] CameraKind _cameraView = CameraKind.TopDown;
    public bool IsTopDownView => CameraView == CameraKind.TopDown;
    public bool IsIsoView      => CameraView == CameraKind.Iso;
    /// <summary>True when the Floor/Ceiling surface pills should appear in
    /// the toolbar (top-down + Layout mode).</summary>
    public bool ShowFloorCeilingTabs => IsTopDownView && IsLayoutMode;
    /// <summary>True when the Opacity slider should appear (top-down +
    /// Borders enabled).</summary>
    public bool ShowOpacitySlider   => IsTopDownView && ShowWallBorders;
    /// <summary>True when LAYOUT measurements section is meaningful (only
    /// in top-down — they're 2D-canvas overlays).</summary>
    public bool ShowLayoutMeasurements => IsTopDownView;
    /// <summary>True when OBJECT measurements section is meaningful.</summary>
    public bool ShowObjectMeasurements => IsTopDownView;
    /// <summary>True when the PATHS section is meaningful (paths overlay
    /// is currently 2D-only; future 3D-pass would relax this).</summary>
    public bool ShowPathsSection => IsTopDownView;
    partial void OnCameraViewChanged(CameraKind value)
    {
        OnPropertyChanged(nameof(IsTopDownView));
        OnPropertyChanged(nameof(IsIsoView));
        OnPropertyChanged(nameof(ShowFloorCeilingTabs));
        OnPropertyChanged(nameof(ShowOpacitySlider));
        OnPropertyChanged(nameof(ShowLayoutMeasurements));
        OnPropertyChanged(nameof(ShowObjectMeasurements));
        OnPropertyChanged(nameof(ShowPathsSection));
        OnPropertyChanged(nameof(SceneSummary));
        EditVersion++;
    }

    // ── 3D camera state (active when CameraView == Iso) ──
    // Spherical orbit around an XZ-plane pivot. Yaw rotates around the
    // world Y axis, pitch tilts the camera toward / away from straight
    // down, distance is the camera's distance from the pivot.

    /// <summary>Orbit yaw in radians (rotation about world Y).</summary>
    [ObservableProperty] float _isoYaw = (float)(System.Math.PI * 0.25);
    /// <summary>Orbit pitch in radians, measured up from the XZ plane.
    /// Clamped on input to (0, π/2) so we never flip past straight-down.</summary>
    [ObservableProperty] float _isoPitch = (float)(System.Math.PI * 0.30);
    /// <summary>Distance from camera to pivot, in world metres.</summary>
    [ObservableProperty] float _isoDistance = 28f;
    /// <summary>Pivot world X (the point the camera looks at).</summary>
    [ObservableProperty] float _isoPivotX;
    /// <summary>Pivot world Z.</summary>
    [ObservableProperty] float _isoPivotZ;
    /// <summary>Whether the iso camera has been initialised yet — flag is
    /// flipped once on first render so the pivot lands at the grid centre
    /// and the distance auto-fits the room layout.</summary>
    public bool IsoCameraInitialised { get; set; }

    [RelayCommand]
    void SetTopView() => CameraView = CameraKind.TopDown;

    [RelayCommand]
    void Set3DView() => CameraView = CameraKind.Iso;

    /// <summary>App-wide theme variant. Light is the default; Dark
    /// flips Application.RequestedThemeVariant which cascades through
    /// the DynamicResource bindings on every Token.axaml entry.
    /// Persisted across sessions via AppSettings.</summary>
    public enum ThemeVariantKind { Light, Dark }
    [ObservableProperty] ThemeVariantKind _themeVariant = ThemeVariantKind.Light;

    public bool IsLightTheme => ThemeVariant == ThemeVariantKind.Light;
    public bool IsDarkTheme  => ThemeVariant == ThemeVariantKind.Dark;

    partial void OnThemeVariantChanged(ThemeVariantKind value)
    {
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        ApplyThemeVariant();
        // Persist so it survives next launch.
        var settings = OpenApparatus.Studio.Services.AppSettings.LoadOrDefault();
        settings.ThemeVariant = value.ToString();
        settings.Save();
    }

    public void ApplyThemeVariant()
    {
        var app = Avalonia.Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = ThemeVariant == ThemeVariantKind.Dark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }

    [RelayCommand]
    void SetLightTheme() => ThemeVariant = ThemeVariantKind.Light;

    [RelayCommand]
    void SetDarkTheme() => ThemeVariant = ThemeVariantKind.Dark;

    [RelayCommand]
    void ToggleTheme() => ThemeVariant =
        ThemeVariant == ThemeVariantKind.Light
            ? ThemeVariantKind.Dark
            : ThemeVariantKind.Light;

    [RelayCommand]
    void ResetIsoCamera()
    {
        IsoYaw = (float)(System.Math.PI * 0.25);
        IsoPitch = (float)(System.Math.PI * 0.30);
        IsoCameraInitialised = false;  // forces re-fit on next render
        EditVersion++;
    }

    /// <summary>Global subdivision: every tile is divided into N×N sub-cells
    /// when placing or snapping objects. 1 = no subdivision (objects sit at
    /// tile centres), 2 = 2×2 per tile, …, up to 8.</summary>
    [ObservableProperty] int _gridSubdivision = 1;
    partial void OnGridSubdivisionChanged(int value) => EditVersion++;

    /// <summary>Default Y coordinate (metres above the floor) used when an
    /// object is first placed. The 3D viewer will let users edit Y per-object;
    /// for now, every new object starts here.</summary>
    [ObservableProperty] float _defaultObjectY = 0.5f;

    /// <summary>The selected sub-cell in Objects mode, or null. fineX / fineZ
    /// run 0..GridSubdivision-1 within the parent tile.</summary>
    [ObservableProperty] (int TileX, int TileZ, int FineX, int FineZ)? _selectedSubCell;
    partial void OnSelectedSubCellChanged((int TileX, int TileZ, int FineX, int FineZ)? value) => EditVersion++;

    /// <summary>Index of the currently selected RoomObject in <see cref="Objects"/>,
    /// or -1 when nothing is selected.</summary>
    [ObservableProperty] int _selectedObjectIndex = -1;
    partial void OnSelectedObjectIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedObject));
        EditVersion++;
    }

    public RoomObject? SelectedObject =>
        SelectedObjectIndex >= 0 && SelectedObjectIndex < _objects.Count
            ? _objects[SelectedObjectIndex] : null;

    readonly List<RoomObject> _objects = new();
    public IReadOnlyList<RoomObject> Objects => _objects;

    /// <summary>
    /// Editable object types. The user starts with one default and uses
    /// <see cref="AddObjectTypeCommand"/> to grow the list. Hotkey 1..N
    /// places an instance of the matching type.
    /// </summary>
    readonly System.Collections.ObjectModel.ObservableCollection<ObjectType> _objectTypes = new();
    public System.Collections.ObjectModel.ObservableCollection<ObjectType> ObjectTypes => _objectTypes;

    /// <summary>1-based lookup matching <see cref="RoomObject.Slot"/>. Returns
    /// null when the slot is out of range (e.g. hotkey 7 was pressed but only
    /// 4 types exist).</summary>
    public ObjectType? GetObjectType(int slot)
    {
        int idx = slot - 1;
        if (idx < 0 || idx >= _objectTypes.Count) return null;
        return _objectTypes[idx];
    }

    [RelayCommand]
    void AddObjectType()
    {
        PushUndo();
        int id = _objectTypes.Count + 1;
        var shape = (ObjectShape)((id - 1) % System.Enum.GetValues<ObjectShape>().Length);
        // Pick a perceptually-distinct hue so consecutive Add presses don't
        // produce visually similar swatches. Same HSV walk used for room hues.
        var rgb = HsvToRgb((id * 137.5) % 360, 0.55f, 0.92f);
        _objectTypes.Add(new ObjectType
        {
            Name = $"Object {id}",
            Shape = shape,
            Color = rgb,
            Size = 0.30f,
        });
        StatusMessage = $"Added object type {id}.";
        EditVersion++;
    }

    /// <summary>Remove the object type at index <paramref name="index"/> (0-based).
    /// Refuses to remove the last remaining type. Any RoomObject pointing at the
    /// removed type is dropped; later types shift down by one slot.</summary>
    public void RemoveObjectType(int index)
    {
        if (index < 0 || index >= _objectTypes.Count) return;
        if (_objectTypes.Count <= 1)
        {
            StatusMessage = "Keep at least one type.";
            return;
        }
        PushUndo();
        int removedSlot = index + 1;
        _objectTypes.RemoveAt(index);
        // Drop instances using the removed type, decrement instances above it.
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i].Slot == removedSlot) _objects.RemoveAt(i);
            else if (_objects[i].Slot > removedSlot) _objects[i].Slot--;
        }
        if (SelectedObjectIndex >= _objects.Count) SelectedObjectIndex = -1;
        StatusMessage = $"Removed object type {removedSlot}.";
        EditVersion++;
    }

    /// <summary>Called from the inspector after editing a type's name / shape /
    /// color in place. Repaints the editor view to reflect the change.</summary>
    public void OnEditedObjectType()
    {
        EditVersion++;
    }

    /// <summary>Sub-cell side length in metres at the current subdivision.</summary>
    public float SubCellSize => TileSize / System.Math.Max(1, GridSubdivision);

    /// <summary>World position of the centre of a sub-cell.</summary>
    public System.Numerics.Vector2 SubCellCenter(int tileX, int tileZ, int fineX, int fineZ)
    {
        float s = SubCellSize;
        return new System.Numerics.Vector2(
            tileX * TileSize + (fineX + 0.5f) * s,
            tileZ * TileSize + (fineZ + 0.5f) * s);
    }

    /// <summary>The room id at <paramref name="worldPos"/>, or -1 if no tile
    /// owns that point. Used to assign owning rooms when objects are placed
    /// or snapped.</summary>
    public int RoomIdAtWorld(System.Numerics.Vector2 worldPos)
    {
        int tx = (int)System.Math.Floor(worldPos.X / TileSize);
        int tz = (int)System.Math.Floor(worldPos.Y / TileSize);
        if (tx < 0 || tx >= GridWidth || tz < 0 || tz >= GridLength) return -1;
        return RoomGrid[tx, tz];
    }

    /// <summary>
    /// Place an object of <paramref name="slot"/> at the centre of
    /// <see cref="SelectedSubCell"/>, owned by the room that contains that
    /// sub-cell. No-ops when no sub-cell is selected, the slot is out of
    /// range, the cell sits in empty space, or an object of the same slot is
    /// already at this sub-cell centre.
    /// </summary>
    public void PlaceObjectAtSelectedSubCell(int slot)
    {
        var typeForPlacement = GetObjectType(slot);
        if (typeForPlacement is null) return;
        if (SelectedSubCell is not { } sc) return;
        var center2 = SubCellCenter(sc.TileX, sc.TileZ, sc.FineX, sc.FineZ);
        // RoomIdAtWorld returns -1 when the sub-cell sits on empty space; we
        // treat that as "outside any room" rather than refusing the placement
        // — empty tiles are still valid object territory and the JSON / glTF
        // exports group those objects under an Outside container.
        int roomId = RoomIdAtWorld(center2);
        // Single-object-per-sub-cell rule: refuse if ANY object (regardless
        // of slot) already occupies this sub-cell centre. Users delete first
        // to replace; replacing silently would be too easy to do by accident.
        const float EPS = 1e-3f;
        foreach (var o in _objects)
        {
            if (System.Math.Abs(o.Position.X - center2.X) < EPS &&
                System.Math.Abs(o.Position.Z - center2.Y) < EPS)
            {
                StatusMessage = "Sub-cell occupied.";
                return;
            }
        }
        PushUndo();
        var obj = new RoomObject
        {
            OwningRoomId = roomId,
            Slot = slot,
            Position = new System.Numerics.Vector3(center2.X, DefaultObjectY, center2.Y),
            Rotation = 0f,
        };
        _objects.Add(obj);
        SelectedObjectIndex = _objects.Count - 1;
        StatusMessage = roomId >= 0
            ? $"Placed {typeForPlacement.Name} in Room {roomId}."
            : $"Placed {typeForPlacement.Name} (outside any room).";
        EditVersion++;
    }

    /// <summary>Called from the inspector after an in-place mutation of the
    /// selected RoomObject (X/Y/Z/rotation spinners) so the editor view
    /// repaints. We can't use ObservableProperty on RoomObject because it's a
    /// reference type held by index in a list — bumping EditVersion is the
    /// signal everything else listens to.</summary>
    public void OnEditedSelectedObject()
    {
        EditVersion++;
        OnPropertyChanged(nameof(SelectedObject));
    }

    /// <summary>Delete the currently selected object.</summary>
    [RelayCommand]
    void DeleteSelectedObject()
    {
        if (SelectedObjectIndex < 0 || SelectedObjectIndex >= _objects.Count) return;
        PushUndo();
        _objects.RemoveAt(SelectedObjectIndex);
        SelectedObjectIndex = -1;
        StatusMessage = "Object deleted.";
        EditVersion++;
    }

    /// <summary>
    /// Snap every object's X/Z position to the centre of its containing
    /// sub-cell at the current <see cref="GridSubdivision"/>. Y is preserved
    /// — height is independent of the grid. Useful after changing the
    /// subdivision so older objects align with the new grain.
    /// </summary>
    [RelayCommand]
    void SnapObjectsToGrid()
    {
        if (_objects.Count == 0)
        {
            StatusMessage = "No objects to snap.";
            return;
        }
        PushUndo();
        float s = SubCellSize;
        int touched = 0;
        foreach (var o in _objects)
        {
            float fx = o.Position.X / s;
            float fz = o.Position.Z / s;
            float snappedX = (System.MathF.Floor(fx) + 0.5f) * s;
            float snappedZ = (System.MathF.Floor(fz) + 0.5f) * s;
            if (System.MathF.Abs(snappedX - o.Position.X) > 1e-4f ||
                System.MathF.Abs(snappedZ - o.Position.Z) > 1e-4f)
            {
                o.Position = new System.Numerics.Vector3(snappedX, o.Position.Y, snappedZ);
                touched++;
            }
            // Reassign owning room based on the new position. -1 = outside any
            // room, which is a valid state — the export groups these under an
            // Outside container.
            o.OwningRoomId = RoomIdAtWorld(new System.Numerics.Vector2(snappedX, snappedZ));
        }
        if (touched > 0)
        {
            OpenApparatus.Studio.Services.Toasts.Default.Show(
                $"Snapped {touched} object{(touched == 1 ? "" : "s")} to the {GridSubdivision}× sub-grid.",
                OpenApparatus.Studio.Services.ToastSeverity.Success,
                undo: () => UndoCommand.Execute(null));
        }
        else
        {
            OpenApparatus.Studio.Services.Toasts.Default.Show(
                "Objects already aligned to the current sub-grid.",
                OpenApparatus.Studio.Services.ToastSeverity.Info);
        }
        StatusMessage = touched > 0
            ? $"Snapped {touched} object(s) to the {GridSubdivision}× sub-grid."
            : "Objects already aligned to the current sub-grid.";
        EditVersion++;
    }

    /// <summary>When on, the editor draws a path overlay through every
    /// traversable passage (open or doorway), anchoring each leg to the
    /// centres of the tile cells on either side of the opening.</summary>
    [ObservableProperty] bool _showPaths;
    partial void OnShowPathsChanged(bool value) => EditVersion++;

    /// <summary>Colour used by the path overlay. Default is a green that reads
    /// against most floor / wall tones.</summary>
    [ObservableProperty] System.Numerics.Vector3 _pathColor = new(0.125f, 0.66f, 0.376f);
    partial void OnPathColorChanged(System.Numerics.Vector3 value) => EditVersion++;

    /// <summary>When true the path overlay animates from the start room
    /// outward, looping. When false the full overlay renders statically.</summary>
    [ObservableProperty] bool _isPathAnimating;

    /// <summary>0..1 progress through one animation cycle. Loops back to 0
    /// when it crosses 1.0 while <see cref="IsPathAnimating"/> is true.</summary>
    [ObservableProperty] double _pathAnimationProgress;

    Avalonia.Threading.DispatcherTimer? _pathTimer;
    System.DateTime _pathTimerLastTick;
    /// <summary>Seconds for one full BFS sweep at 1× speed. Combined with
    /// <see cref="PathAnimationSpeed"/> for the effective duration.</summary>
    const double PathAnimationBaseDuration = 6.0;
    /// <summary>Animation speed multiplier (0.25× — slow, 4× — fast).
    /// 1× takes <see cref="PathAnimationBaseDuration"/> seconds for a
    /// full cycle.</summary>
    [ObservableProperty] double _pathAnimationSpeed = 1.0;

    [RelayCommand]
    void TogglePathAnimation()
    {
        if (IsPathAnimating)
        {
            _pathTimer?.Stop();
            IsPathAnimating = false;
            EditVersion++;
            return;
        }

        // Switching on starts a fresh sweep.
        PathAnimationProgress = 0;
        if (_pathTimer is null)
        {
            _pathTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(33),
            };
            _pathTimer.Tick += OnPathTick;
        }
        _pathTimerLastTick = System.DateTime.UtcNow;
        _pathTimer.Start();
        IsPathAnimating = true;
        // Force ShowPaths on so the user sees something — otherwise pressing
        // Play with paths hidden does nothing visible.
        ShowPaths = true;
        EditVersion++;
    }

    void OnPathTick(object? sender, System.EventArgs e)
    {
        var now = System.DateTime.UtcNow;
        var dt = (now - _pathTimerLastTick).TotalSeconds;
        _pathTimerLastTick = now;
        double duration = PathAnimationBaseDuration / System.Math.Max(0.05, PathAnimationSpeed);
        var p = PathAnimationProgress + dt / duration;
        if (p >= 1.0) p -= 1.0; // loop
        PathAnimationProgress = p;
        EditVersion++;
    }

    /// <summary>Inter-object distance line + label.</summary>
    [ObservableProperty] bool _showObjectDistances;
    partial void OnShowObjectDistancesChanged(bool value) => EditVersion++;

    /// <summary>Door→object angle arc + |angle| label.</summary>
    [ObservableProperty] bool _showDoorAngles;
    partial void OnShowDoorAnglesChanged(bool value) => EditVersion++;

    /// <summary>Door→object distance line + label.</summary>
    [ObservableProperty] bool _showDoorDistances;
    partial void OnShowDoorDistancesChanged(bool value) => EditVersion++;

    /// <summary>True when any object-measurement layer is on. Used by the
    /// renderer to short-circuit early.</summary>
    public bool AnyObjectMeasurementsVisible
        => ShowObjectDistances || ShowDoorAngles || ShowDoorDistances;

    /// <summary>Active placement constraints. Stored as a single instance so
    /// the panel can mutate fields directly; mutations call
    /// <see cref="OnConstraintsChanged"/> to repaint and refresh the
    /// compliance summary.</summary>
    public PlacementConstraints Constraints { get; } = new();

    /// <summary>Notify the editor view + the inspector panel that a
    /// constraint has changed. Bumps EditVersion (view repaint) and raises
    /// PropertyChanged on the constraints handle (inspector listens for that).</summary>
    public void OnConstraintsChanged()
    {
        EditVersion++;
        OnPropertyChanged(nameof(Constraints));
        OnPropertyChanged(nameof(ConstraintViolations));
        OnPropertyChanged(nameof(ConstraintComplianceSummary));
    }

    /// <summary>The current set of constraint violations across every object /
    /// room. Recomputed on demand — cheap for typical apparatus sizes.</summary>
    public IReadOnlyList<ConstraintViolation> ConstraintViolations => EvaluateConstraints();

    /// <summary>Human-readable summary string ready to drop into the panel
    /// header (e.g. "7 / 9 compliant").</summary>
    public string ConstraintComplianceSummary
    {
        get
        {
            var list = EvaluateConstraints();
            int total = _objects.Count;
            int violators = 0;
            var seen = new HashSet<int>();
            foreach (var v in list)
                if (v.ObjectIndex is int idx && seen.Add(idx)) violators++;
            int compliant = total - violators;
            int roomViolations = 0;
            foreach (var v in list) if (v.RoomId is not null) roomViolations++;
            return roomViolations == 0
                ? $"{compliant} / {total} compliant"
                : $"{compliant} / {total} compliant • {roomViolations} room count violation(s)";
        }
    }

    /// <summary>Walks every object + room against the active constraints and
    /// returns a flat list of violations. Empty list = fully compliant.</summary>
    public IReadOnlyList<ConstraintViolation> EvaluateConstraints()
    {
        var c = Constraints;
        var list = new List<ConstraintViolation>();
        if (CurrentEnvironment is not { } env) return list;
        if (_objects.Count == 0 && !c.PerRoomCountsEnabled) return list;

        // Pre-build a roomId -> room lookup and a set of valid ids.
        var roomById = new Dictionary<int, OpenApparatus.Topology.Room>();
        foreach (var r in env.Rooms) roomById[r.Id] = r;

        // Pre-build a roomId -> doors-with-inward-normals lookup.
        var doorsByRoom = new Dictionary<int, List<(System.Numerics.Vector2 Pos, System.Numerics.Vector2 Inward)>>();
        foreach (var adj in env.Adjacencies)
        {
            if (adj.Passage is not Passage.Doorway dw) continue;
            void Add(OpenApparatus.Topology.Room? room, bool roomIsA)
            {
                if (room is null) return;
                if (!doorsByRoom.TryGetValue(room.Id, out var lst))
                    doorsByRoom[room.Id] = lst = new();
                var seg = adj.SharedSegment;
                var inward = roomIsA ? seg.Normal : -seg.Normal;
                foreach (var op in dw.Openings)
                {
                    if (op.IsWindow) continue;
                    var p = seg.Start + seg.Direction * (op.OffsetAlongEdge + op.Width * 0.5f);
                    lst.Add((p, inward));
                }
            }
            Add(adj.RoomA, true);
            Add(adj.RoomB, false);
        }

        // Pre-build a roomId -> outline segments (the room's wall segments) for
        // object-to-wall distance.
        var wallsByRoom = new Dictionary<int, List<(System.Numerics.Vector2 A, System.Numerics.Vector2 B)>>();
        foreach (var adj in env.Adjacencies)
        {
            void Add(OpenApparatus.Topology.Room? room)
            {
                if (room is null) return;
                if (!wallsByRoom.TryGetValue(room.Id, out var lst))
                    wallsByRoom[room.Id] = lst = new();
                lst.Add((adj.SharedSegment.Start, adj.SharedSegment.End));
            }
            Add(adj.RoomA);
            Add(adj.RoomB);
        }

        // Connected-room map for the "across connected rooms" toggle.
        var connected = new Dictionary<int, HashSet<int>>();
        if (c.ObjectToObjectAcrossConnectedRooms)
        {
            foreach (var adj in env.Adjacencies)
            {
                if (adj.IsOuter) continue;
                if (adj.Passage is Passage.Closed) continue;
                int a = adj.RoomA.Id, b = adj.RoomB!.Id;
                if (!connected.TryGetValue(a, out var sa)) connected[a] = sa = new();
                if (!connected.TryGetValue(b, out var sb)) connected[b] = sb = new();
                sa.Add(b); sb.Add(a);
            }
        }

        // -------- Per-object checks --------
        for (int i = 0; i < _objects.Count; i++)
        {
            var oi = _objects[i];
            var pi = new System.Numerics.Vector2(oi.Position.X, oi.Position.Z);

            // Door → Object
            if (c.DoorToObjectEnabled && roomById.ContainsKey(oi.OwningRoomId)
                && doorsByRoom.TryGetValue(oi.OwningRoomId, out var doors)
                && doors.Count > 0)
            {
                int satisfying = 0;
                int firstFailIdx = -1;
                string firstFailMsg = "";
                for (int d = 0; d < doors.Count; d++)
                {
                    var (dp, inw) = doors[d];
                    var v = pi - dp;
                    float dist = v.Length();
                    bool ok = true;
                    string msg = "";
                    if (c.DoorToObjectMin > 0 && dist < c.DoorToObjectMin)
                    { ok = false; msg = $"door distance {dist:0.00}m < min {c.DoorToObjectMin:0.00}m"; }
                    if (ok && c.DoorToObjectMax > 0 && dist > c.DoorToObjectMax)
                    { ok = false; msg = $"door distance {dist:0.00}m > max {c.DoorToObjectMax:0.00}m"; }
                    if (ok && c.DoorAngleBandEnabled)
                    {
                        double forward = v.X * inw.X + v.Y * inw.Y;
                        double perp = v.X * inw.Y - v.Y * inw.X;
                        double a = System.Math.Atan2(perp, forward) * 180.0 / System.Math.PI;
                        double abs = System.Math.Abs(a);
                        if (abs < c.DoorAngleMinDeg)
                        { ok = false; msg = $"door angle {abs:0}° < min {c.DoorAngleMinDeg:0}°"; }
                        else if (abs > c.DoorAngleMaxDeg)
                        { ok = false; msg = $"door angle {abs:0}° > max {c.DoorAngleMaxDeg:0}°"; }
                    }
                    if (ok) satisfying++;
                    else if (firstFailIdx < 0) { firstFailIdx = d; firstFailMsg = msg; }
                }
                bool passes = c.DoorAppliesToEveryDoor ? satisfying == doors.Count : satisfying > 0;
                if (!passes)
                    list.Add(new ConstraintViolation
                    {
                        ObjectIndex = i,
                        Message = c.DoorAppliesToEveryDoor
                            ? $"slot {oi.Slot}: {firstFailMsg}"
                            : $"slot {oi.Slot}: no door satisfies the band ({firstFailMsg})",
                    });
            }

            // Object → Wall
            if (c.ObjectToWallEnabled && c.ObjectToWallMin > 0
                && wallsByRoom.TryGetValue(oi.OwningRoomId, out var walls))
            {
                float minD = float.PositiveInfinity;
                foreach (var (a, b) in walls)
                {
                    float d = DistanceFromPointToSegment(pi,
                        new OpenApparatus.EdgeSegment(a, b));
                    if (d < minD) minD = d;
                }
                if (minD < c.ObjectToWallMin)
                    list.Add(new ConstraintViolation
                    {
                        ObjectIndex = i,
                        Message = $"slot {oi.Slot}: wall distance {minD:0.00}m < min {c.ObjectToWallMin:0.00}m",
                    });
            }

            // Object ↔ Object
            if (c.ObjectToObjectEnabled)
            {
                for (int j = i + 1; j < _objects.Count; j++)
                {
                    var oj = _objects[j];
                    bool sameRoom = oi.OwningRoomId == oj.OwningRoomId;
                    bool connectedPair = false;
                    if (c.ObjectToObjectAcrossConnectedRooms)
                    {
                        if (oi.OwningRoomId == oj.OwningRoomId) connectedPair = true;
                        else if (connected.TryGetValue(oi.OwningRoomId, out var s) && s.Contains(oj.OwningRoomId))
                            connectedPair = true;
                    }
                    bool inScope = c.ObjectToObjectAcrossConnectedRooms ? connectedPair : sameRoom;
                    if (!inScope) continue;
                    var pj = new System.Numerics.Vector2(oj.Position.X, oj.Position.Z);
                    float dist = (pj - pi).Length();
                    if (c.ObjectToObjectMin > 0 && dist < c.ObjectToObjectMin)
                    {
                        var msg = $"slots {oi.Slot}↔{oj.Slot}: distance {dist:0.00}m < min {c.ObjectToObjectMin:0.00}m";
                        list.Add(new ConstraintViolation { ObjectIndex = i, Message = msg });
                        list.Add(new ConstraintViolation { ObjectIndex = j, Message = msg });
                    }
                    else if (c.ObjectToObjectMax > 0 && dist > c.ObjectToObjectMax)
                    {
                        var msg = $"slots {oi.Slot}↔{oj.Slot}: distance {dist:0.00}m > max {c.ObjectToObjectMax:0.00}m";
                        list.Add(new ConstraintViolation { ObjectIndex = i, Message = msg });
                        list.Add(new ConstraintViolation { ObjectIndex = j, Message = msg });
                    }
                }
            }
        }

        // -------- Per-room count check --------
        if (c.PerRoomCountsEnabled)
        {
            var counts = new Dictionary<int, int>();
            foreach (var o in _objects)
            {
                if (!roomById.ContainsKey(o.OwningRoomId)) continue;
                counts.TryGetValue(o.OwningRoomId, out var n);
                counts[o.OwningRoomId] = n + 1;
            }
            foreach (var r in env.Rooms)
            {
                counts.TryGetValue(r.Id, out var n);
                if (c.PerRoomCountMin > 0 && n < c.PerRoomCountMin)
                    list.Add(new ConstraintViolation
                    {
                        RoomId = r.Id,
                        Message = $"Room {r.Id}: {n} objects < min {c.PerRoomCountMin}",
                    });
                if (c.PerRoomCountMax > 0 && n > c.PerRoomCountMax)
                    list.Add(new ConstraintViolation
                    {
                        RoomId = r.Id,
                        Message = $"Room {r.Id}: {n} objects > max {c.PerRoomCountMax}",
                    });
            }
        }

        return list;
    }

    /// <summary>Returns the indices of all objects with at least one violation.
    /// The editor view uses this to draw violator highlight rings.</summary>
    public HashSet<int> GetViolatingObjectIndices()
    {
        var s = new HashSet<int>();
        if (!Constraints.HighlightViolations) return s;
        foreach (var v in EvaluateConstraints())
            if (v.ObjectIndex is int i) s.Add(i);
        return s;
    }

    // ─── View options (cosmetic editor toggles, never affect export) ───
    [ObservableProperty] bool _showRoomLabels = true;
    partial void OnShowRoomLabelsChanged(bool value) => EditVersion++;

    [ObservableProperty] bool _showWallBorders = true;
    partial void OnShowWallBordersChanged(bool value)
    {
        EditVersion++;
        OnPropertyChanged(nameof(ShowOpacitySlider));
    }

    /// <summary>Ruler ticks + numeric grid-coordinate labels along the
    /// bottom and left edges of the editor. Default on; a PNG export can
    /// momentarily flip it off to produce a chrome-free image.</summary>
    [ObservableProperty] bool _showAxisMarkers = true;
    partial void OnShowAxisMarkersChanged(bool value) => EditVersion++;

    /// <summary>Constraint-derived placement overlays — valid-region
    /// shading, door annular wedges, exclusion discs. Default on so the
    /// editor behaves as before; PNG export gates this so the highlights
    /// don't bleed into a printed floorplan.</summary>
    [ObservableProperty] bool _showConstraintOverlays = true;
    partial void OnShowConstraintOverlaysChanged(bool value) => EditVersion++;

    /// <summary>0..1 multiplier on the saturation of room tile fills. 1 =
    /// full colour, 0 = greyscale. Useful in Object mode so the room hues
    /// don't compete with object icons.</summary>
    [ObservableProperty] double _tileSaturation = 1.0;
    partial void OnTileSaturationChanged(double value) => EditVersion++;

    // ─── Layout measurement toggles (all default off) ───
    [ObservableProperty] bool _showRoomDimensions;
    partial void OnShowRoomDimensionsChanged(bool value) => EditVersion++;

    [ObservableProperty] bool _showOpeningSizeLabels;
    partial void OnShowOpeningSizeLabelsChanged(bool value) => EditVersion++;

    [ObservableProperty] bool _showWallLengthLabels;
    partial void OnShowWallLengthLabelsChanged(bool value) => EditVersion++;

    [ObservableProperty] bool _showFloorAreaLabels;
    partial void OnShowFloorAreaLabelsChanged(bool value) => EditVersion++;

    /// <summary>Editor-only opacity multiplier for the interior wall borders.
    /// 1.0 = fully opaque (default), 0 = invisible. Does NOT alter the colours
    /// used by the glTF export — purely a viewport aid for spotting doors and
    /// windows behind the coloured ribbons.</summary>
    [ObservableProperty] double _wallBorderOpacity = 1.0;

    partial void OnWallBorderOpacityChanged(double value) => EditVersion++;

    /// <summary>Editor zoom factor. 1.0 = fit-to-view; >1 zooms in, &lt;1 zooms out.
    /// The viewport renders ZoomFactor× the auto-fit tile size.</summary>
    [ObservableProperty] double _zoomFactor = 1.0;
    /// <summary>Pan offset added to the viewport origin in screen pixels.</summary>
    [ObservableProperty] double _panOffsetX;
    [ObservableProperty] double _panOffsetY;

    public const double MinZoom = 0.5;
    public const double MaxZoom = 8.0;

    partial void OnZoomFactorChanged(double value) => EditVersion++;
    partial void OnPanOffsetXChanged(double value) => EditVersion++;
    partial void OnPanOffsetYChanged(double value) => EditVersion++;

    /// <summary>Reset zoom + pan to the default fit-to-view layout.</summary>
    [RelayCommand]
    void ResetView()
    {
        ZoomFactor = 1.0;
        PanOffsetX = 0;
        PanOffsetY = 0;
    }

    /// <summary>Zoom to 100% (1× — pixel-perfect tile rendering).</summary>
    [RelayCommand]
    void ZoomActualSize()
    {
        ZoomFactor = 1.0;
        PanOffsetX = 0;
        PanOffsetY = 0;
    }

    /// <summary>Frame the current selection (room or object). Falls back
    /// to fit-all when nothing is selected. Bound to the F key.</summary>
    [RelayCommand]
    void FrameSelection()
    {
        if (CameraView == CameraKind.Iso)
        {
            // 3D mode: pull the orbit pivot to the selection centroid and
            // tighten distance so the camera frames it.
            (float cx, float cz, float radius)? sel = SelectionCentre();
            if (sel is null) return;
            IsoPivotX = sel.Value.cx;
            IsoPivotZ = sel.Value.cz;
            IsoDistance = System.Math.Clamp(sel.Value.radius * 3f, 4f, 200f);
            IsoCameraInitialised = true;
            EditVersion++;
            return;
        }
        // Top-down: defer to FocusOnRoom for a selected room; otherwise
        // reset to fit-all.
        if (SelectedRoomId >= 0 && _lastViewportSize is { } vp)
            FocusOnRoom(SelectedRoomId, vp.W, vp.H);
        else ResetView();
    }

    /// <summary>Bounding centroid + approximate radius (in world m) of
    /// the current selection. Used by FrameSelection.</summary>
    (float cx, float cz, float radius)? SelectionCentre()
    {
        // Selected object — use its position.
        if (SelectedObjectIndex >= 0 && SelectedObjectIndex < _objects.Count)
        {
            var o = _objects[SelectedObjectIndex];
            float r = GetObjectType(o.Slot)?.Size ?? 0.5f;
            return (o.Position.X, o.Position.Z, r * 2f);
        }
        // Selected room — bbox of its tiles.
        if (SelectedRoomId >= 0)
        {
            int xMin = int.MaxValue, xMax = int.MinValue;
            int zMin = int.MaxValue, zMax = int.MinValue;
            bool any = false;
            for (int x = 0; x < GridWidth; x++)
                for (int z = 0; z < GridLength; z++)
                    if (RoomGrid[x, z] == SelectedRoomId)
                    {
                        if (x < xMin) xMin = x; if (x > xMax) xMax = x;
                        if (z < zMin) zMin = z; if (z > zMax) zMax = z;
                        any = true;
                    }
            if (any)
            {
                float cx = (xMin + xMax + 1) * 0.5f * TileSize;
                float cz = (zMin + zMax + 1) * 0.5f * TileSize;
                float r  = System.MathF.Max(xMax - xMin + 1, zMax - zMin + 1) * TileSize;
                return (cx, cz, r);
            }
        }
        // No selection — return entire grid.
        if (GridWidth > 0 && GridLength > 0)
        {
            return (
                GridWidth  * TileSize * 0.5f,
                GridLength * TileSize * 0.5f,
                System.MathF.Max(GridWidth, GridLength) * TileSize);
        }
        return null;
    }

    // Surface size cached by GridEditorView so ResetView / FrameSelection
    // can compute fits without a window-handle round-trip.
    (double W, double H)? _lastViewportSize;
    public void ReportViewportSize(double w, double h) => _lastViewportSize = (w, h);

    /// <summary>
    /// Compute zoom + pan so the given room fits centered in the viewport with a
    /// small empty buffer of tiles on each side. Width / height are the editor's
    /// available pixel area minus its padding (16 px on each side).
    /// </summary>
    public void FocusOnRoom(int roomId, double availW, double availH)
    {
        if (availW <= 0 || availH <= 0 || GridWidth <= 0 || GridLength <= 0) return;

        int xMin = int.MaxValue, xMax = int.MinValue, zMin = int.MaxValue, zMax = int.MinValue;
        bool any = false;
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                if (RoomGrid[x, z] == roomId)
                {
                    any = true;
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                    if (z < zMin) zMin = z;
                    if (z > zMax) zMax = z;
                }
        if (!any) return;

        const double Buffer = 1.5; // tiles of empty space around the room
        double bufferedW = (xMax - xMin + 1) + Buffer * 2;
        double bufferedD = (zMax - zMin + 1) + Buffer * 2;

        // Fit-to-view tile size for the WHOLE grid (matches ComputeLayout).
        double autoFit = System.Math.Min(availW / GridWidth, availH / GridLength);
        if (autoFit <= 0) return;

        // Tile size that makes the buffered room fit exactly.
        double targetTile = System.Math.Min(availW / bufferedW, availH / bufferedD);
        double zoom = System.Math.Clamp(targetTile / autoFit, MinZoom, MaxZoom);

        // Pan so the room's center sits in the viewport center. With zoom in
        // place, the actual tile size is autoFit * zoom; the math mirrors what
        // ComputeLayout does so the result is exactly the room-centered layout.
        double tile = autoFit * zoom;
        double cx = (xMin + xMax + 1) * 0.5;
        double cz = (zMin + zMax + 1) * 0.5;

        ZoomFactor = zoom;
        PanOffsetX = tile * (GridWidth * 0.5 - cx);
        PanOffsetY = tile * (cz - GridLength * 0.5);
    }

    partial void OnViewModeChanged(ViewSurface value) => EditVersion++;

    /// <summary>Auto-generated room color used as both the visible tile fill and
    /// the seed for the room's wall color when it is first created. The same
    /// HSV walk used by the editor view, exposed here so the VM can decide on
    /// a wall color before the view ever runs.</summary>
    public static System.Numerics.Vector3 RoomColorRgb(int roomId)
    {
        double h = (roomId * 137.5) % 360.0;
        return HsvToRgb(h, 0.45f, 0.92f);
    }

    static System.Numerics.Vector3 HsvToRgb(double h, float s, float v)
    {
        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1 - System.Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        if (hp < 1)      { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else             { r = c; b = x; }
        double m = v - c;
        return new System.Numerics.Vector3((float)(r + m), (float)(g + m), (float)(b + m));
    }

    public bool HasSelectedRoom => SelectedRoomId >= 0;
    public string SelectedRoomTitle => SelectedRoomId >= 0 ? $"Room {SelectedRoomId}" : "";

    partial void OnSelectedRoomIdChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedRoom));
        OnPropertyChanged(nameof(SelectedRoomTitle));
    }

    // ---- Per-room appearance state ----
    // Storage is keyed by roomId, never by Room object — rebuilds throw away the
    // Room instances but keep the same ids, so user selections survive grid edits.

    readonly Dictionary<int, string> _roomNames = new();
    public IReadOnlyDictionary<int, string> RoomNames => _roomNames;
    public string GetRoomName(int roomId)
        => _roomNames.TryGetValue(roomId, out var n) ? n : string.Empty;
    public void SetRoomName(int roomId, string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) _roomNames.Remove(roomId);
        else _roomNames[roomId] = trimmed;
        EditVersion++;
    }

    readonly Dictionary<int, System.Numerics.Vector3> _roomFloorColors = new();
    readonly Dictionary<int, System.Numerics.Vector3> _roomCeilingColors = new();
    readonly Dictionary<int, System.Numerics.Vector3> _roomSingleWallColors = new();
    readonly HashSet<int> _multiColorRoomIds = new();

    public IReadOnlyDictionary<int, System.Numerics.Vector3> RoomFloorColors => _roomFloorColors;
    public IReadOnlyDictionary<int, System.Numerics.Vector3> RoomCeilingColors => _roomCeilingColors;
    public IReadOnlyDictionary<int, System.Numerics.Vector3> RoomSingleWallColors => _roomSingleWallColors;
    public IReadOnlyCollection<int> MultiColorRoomIds => _multiColorRoomIds;

    public bool IsRoomMultiColor(int roomId) => _multiColorRoomIds.Contains(roomId);

    public void SetRoomMultiColor(int roomId, bool on)
    {
        PushUndo();
        if (on) _multiColorRoomIds.Add(roomId);
        else _multiColorRoomIds.Remove(roomId);
        EditVersion++;
    }

    public void SetRoomFloorColor(int roomId, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _roomFloorColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomFloorColor(int roomId)
    {
        PushUndo();
        _roomFloorColors.Remove(roomId); EditVersion++;
    }

    public void SetRoomCeilingColor(int roomId, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _roomCeilingColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomCeilingColor(int roomId)
    {
        PushUndo();
        _roomCeilingColors.Remove(roomId); EditVersion++;
    }

    public void SetRoomSingleWallColor(int roomId, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _roomSingleWallColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomSingleWallColor(int roomId)
    {
        PushUndo();
        _roomSingleWallColors.Remove(roomId); EditVersion++;
    }

    [ObservableProperty] MultiRoomEnvironment? _currentEnvironment;

    /// <summary>True before any rooms have been created. Drives the
    /// "How to use" first-run card so it stops occupying panel space
    /// once the user has built their first room.</summary>
    public bool HasNoRooms => CurrentEnvironment is null || CurrentEnvironment.Rooms.Count == 0;
    partial void OnCurrentEnvironmentChanged(MultiRoomEnvironment? value)
    {
        OnPropertyChanged(nameof(HasNoRooms));
        OnPropertyChanged(nameof(SceneSummary));
        OnPropertyChanged(nameof(ShowWelcomePanel));
    }
    [ObservableProperty] string _statusMessage = "Drag tiles to mark a footprint, then press R to create a room.";

    /// <summary>True when there's a non-empty status message to surface
    /// (controls the status bar's brand-blue indicator dot).</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Contextual hint shown in the centre of the status bar when
    /// no live message is active. Surfaces the next-best thing the user
    /// can do based on current selection / mode. Replaces what would
    /// otherwise be empty whitespace with quietly useful guidance.</summary>
    public string ContextualHint
    {
        get
        {
            if (HasNoRooms) return "Drag tiles to mark a footprint, then press R to create a room.";
            if (IsObjectsMode)
            {
                if (SelectedObjectIndex >= 0) return "Edit on the right · Delete to remove · Ctrl+Z to undo.";
                if (SelectedSubCell is not null) return "Press 1–9 to place an object in this sub-cell.";
                return "Click an object to edit it, or click a sub-cell to place one.";
            }
            // Layout mode
            if (SelectedAdjacency is not null) return "D = door · W = window · O = open · C = closed.";
            if (HasSelectedTiles) return "Press R to create a room from the selection.";
            if (SelectedRoomId >= 0) return "Edit colours on the right · click a wall to add a door / window.";
            return "Click + drag tiles to start a new room, or click an existing one to edit.";
        }
    }

    Avalonia.Threading.DispatcherTimer? _statusClearTimer;
    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
        // Auto-clear after a few seconds so transient confirmations
        // ("Created room #N", "Snapped 3 objects") don't sit forever.
        _statusClearTimer?.Stop();
        if (!string.IsNullOrWhiteSpace(value))
        {
            _statusClearTimer ??= new Avalonia.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromSeconds(6),
            };
            _statusClearTimer.Tick -= OnStatusClearTick;
            _statusClearTimer.Tick += OnStatusClearTick;
            _statusClearTimer.Start();
        }
    }
    void OnStatusClearTick(object? sender, System.EventArgs e)
    {
        _statusClearTimer?.Stop();
        StatusMessage = string.Empty;
    }

    /// <summary>Right-edge summary for the status bar — quick read of
    /// scene size + active mode without diving into menus.</summary>
    public string SceneSummary
    {
        get
        {
            int rooms = CurrentEnvironment?.Rooms.Count ?? 0;
            int objs = Objects.Count;
            string mode = IsLayoutMode ? "Layout" : "Objects";
            string view = IsTopDownView ? "2D" : "3D";
            return $"{rooms} room{(rooms == 1 ? "" : "s")}  ·  {objs} object{(objs == 1 ? "" : "s")}  ·  {mode} · {view}";
        }
    }

    /// <summary>Bumped after every grid mutation so views can re-render.</summary>
    [ObservableProperty] int _editVersion;
    /// <summary>External-facing way to bump EditVersion (the field-backed
    /// property setter is private to the source-generated partial). Used
    /// by the editor view to request a redraw after adjusting iso camera
    /// state.</summary>
    public void RaiseEditVersion() => EditVersion++;

    /// <summary>Auto-mark unsaved on any edit-version bump (every state
    /// mutation routes through that property). Skipped during project
    /// load via the field setter directly so a fresh load is "clean".</summary>
    partial void OnEditVersionChanged(int value)
    {
        // First load arrives with HasUnsavedChanges still false from the
        // RestoreFromProjectFile reset; subsequent edits flip it on.
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(ContextualHint));
    }

    /// <summary>Project title displayed in the top action bar. Defaults
    /// to "Untitled scene"; persistence + filename binding is a future
    /// follow-up.</summary>
    [ObservableProperty] string _projectTitle = "Untitled scene";

    /// <summary>Composed title for the OS window: "Project — App"
    /// with a leading "● " marker when there are unsaved changes
    /// (Notion / VS Code convention). Bound to Window.Title.</summary>
    public string WindowTitle =>
        (HasUnsavedChanges ? "● " : "")
        + (string.IsNullOrWhiteSpace(ProjectTitle) ? "Untitled" : ProjectTitle)
        + " — OpenApparatus Studio";

    partial void OnProjectTitleChanged(string value)
        => OnPropertyChanged(nameof(WindowTitle));
    partial void OnHasUnsavedChangesChanged(bool value)
        => OnPropertyChanged(nameof(WindowTitle));

    /// <summary>Last cursor world position (XZ, in metres). NaN when the
    /// cursor isn't over the editor canvas. Surfaced in the status bar.</summary>
    public float CursorWorldX { get; private set; } = float.NaN;
    public float CursorWorldZ { get; private set; } = float.NaN;
    public bool HasCursorWorldPos => !float.IsNaN(CursorWorldX);
    public string CursorWorldDisplay =>
        HasCursorWorldPos ? $"x {CursorWorldX:0.00} m   z {CursorWorldZ:0.00} m" : "";

    public void SetCursorWorldPos(float x, float z)
    {
        CursorWorldX = x; CursorWorldZ = z;
        OnPropertyChanged(nameof(CursorWorldX));
        OnPropertyChanged(nameof(CursorWorldZ));
        OnPropertyChanged(nameof(HasCursorWorldPos));
        OnPropertyChanged(nameof(CursorWorldDisplay));
    }
    public void ClearCursorWorldPos()
    {
        CursorWorldX = float.NaN; CursorWorldZ = float.NaN;
        OnPropertyChanged(nameof(HasCursorWorldPos));
        OnPropertyChanged(nameof(CursorWorldDisplay));
    }

    /// <summary>True while the in-memory scene has edits not yet exported
    /// or saved. The top bar's indicator dot lights brand-blue when set,
    /// transparent when clean. Toggled by the same code that bumps
    /// EditVersion, with a dedicated reset on export.</summary>
    [ObservableProperty] bool _hasUnsavedChanges;

    /// <summary>Path of the file the project was last saved to / loaded
    /// from. Null for unsaved sessions; drives Save vs Save As routing.</summary>
    [ObservableProperty] string? _projectFilePath;
    public bool HasProjectFilePath => !string.IsNullOrEmpty(ProjectFilePath);
    partial void OnProjectFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProjectFilePath));
        OnPropertyChanged(nameof(ShowWelcomePanel));
        RefreshRecentFiles();
    }

    [RelayCommand]
    void MarkSaved()
    {
        HasUnsavedChanges = false;
    }

    // ---- Undo / redo state. Snapshots are deep copies of authored state; we
    // push one before every user-initiated mutation. Selection / view state is
    // excluded so undo doesn't move the camera.
    readonly Stack<Snapshot> _undoStack = new();
    readonly Stack<Snapshot> _redoStack = new();
    const int UndoLimit = 100;
    bool _suppressUndoCapture;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Capture the current state into the undo stack and clear redo.
    /// Call this at the start of any user-initiated mutating action.</summary>
    public void PushUndo()
    {
        if (_suppressUndoCapture) return;
        _undoStack.Push(Snapshot.Capture(this));
        if (_undoStack.Count > UndoLimit)
        {
            // Drop oldest by reversing — Stack is LIFO so we pop+rebuild.
            var keep = new Snapshot[UndoLimit];
            for (int i = UndoLimit - 1; i >= 0; i--) keep[i] = _undoStack.Pop();
            _undoStack.Clear();
            for (int i = 0; i < UndoLimit; i++) _undoStack.Push(keep[i]);
        }
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(Snapshot.Capture(this));
        var snap = _undoStack.Pop();
        _suppressUndoCapture = true;
        try { snap.Restore(this); }
        finally { _suppressUndoCapture = false; }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = "Undo.";
    }

    [RelayCommand]
    void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(Snapshot.Capture(this));
        var snap = _redoStack.Pop();
        _suppressUndoCapture = true;
        try { snap.Restore(this); }
        finally { _suppressUndoCapture = false; }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = "Redo.";
    }

    // Internal raw accessors used by Snapshot. Keep these here rather than
    // exposing the underlying dictionaries so the public API stays clean.
    internal int NextRoomIdRaw => _nextRoomId;
    internal Dictionary<(int, int), (Passage, System.Numerics.Vector2)> PassageOverridesRaw => _passageOverrides;

    // ── Project file IO helpers ──
    // ProjectIO uses these to round-trip authored state to disk; the
    // dictionaries are kept private otherwise.

    /// <summary>Snapshots passage overrides into a flat list of entries
    /// keyed by world-mm Start coordinates. Used by ProjectIO.Save.</summary>
    public List<OpenApparatus.Studio.Services.PassageOverrideEntry> SerializePassageOverrides()
    {
        var list = new List<OpenApparatus.Studio.Services.PassageOverrideEntry>();
        foreach (var kv in _passageOverrides)
        {
            var (passage, start) = kv.Value;
            // Reconstruct end coords from the key (which is mid * 1000):
            // mid = (start + end) / 2, so end = 2 * mid - start.
            float midXm = kv.Key.Item1 / 1000f;
            float midZm = kv.Key.Item2 / 1000f;
            float endX = 2f * midXm - start.X;
            float endZ = 2f * midZm - start.Y;
            string kind = passage switch
            {
                Passage.Open => "Open",
                Passage.Closed => "Closed",
                Passage.Doorway => "Doorway",
                _ => "Closed",
            };
            var entry = new OpenApparatus.Studio.Services.PassageOverrideEntry
            {
                StartX = start.X, StartZ = start.Y,
                EndX = endX, EndZ = endZ,
                Kind = kind,
            };
            if (passage is Passage.Doorway d)
            {
                entry.Openings = d.Openings.Select(o =>
                    new OpenApparatus.Studio.Services.OpeningEntry
                    {
                        Offset = o.OffsetAlongEdge,
                        Width = o.Width,
                        Height = o.Height,
                        SillHeight = o.SillHeight,
                        HingeAtEnd = o.HingeAtEnd,
                        SwingNegative = o.SwingNegative,
                    }).ToList();
            }
            list.Add(entry);
        }
        return list;
    }

    /// <summary>Replaces the entire VM state with the contents of a
    /// project file. Used by ProjectIO.Load.</summary>
    public void RestoreFromProjectFile(OpenApparatus.Studio.Services.ProjectFile f)
    {
        // Push undo so the user can step back to whatever was open.
        PushUndo();

        // Simple props.
        ProjectTitle = f.Title ?? "Untitled scene";
        GridWidth = f.GridWidth;
        GridLength = f.GridLength;
        TileSize = f.TileSize;
        WallThickness = f.WallThickness;
        WallHeight = f.WallHeight;
        DoorWidth = f.DoorWidth;
        DoorHeight = f.DoorHeight;
        WindowWidth = f.WindowWidth;
        WindowHeight = f.WindowHeight;
        WindowSillHeight = f.WindowSillHeight;
        if (f.GridSubdivision > 0) GridSubdivision = f.GridSubdivision;
        DefaultObjectY = f.DefaultObjectY;
        if (f.DefaultFloorColor   is { Length: 3 } a) DefaultFloorColor   = new(a[0], a[1], a[2]);
        if (f.DefaultCeilingColor is { Length: 3 } b) DefaultCeilingColor = new(b[0], b[1], b[2]);

        // Grid ownership.
        ResetGrid();
        if (f.RoomGrid is { Length: > 0 } g)
        {
            int maxId = -1;
            for (int x = 0; x < f.GridWidth; x++)
                for (int z = 0; z < f.GridLength; z++)
                {
                    int id = g[x * f.GridLength + z];
                    RoomGrid[x, z] = id;
                    if (id > maxId) maxId = id;
                }
            _nextRoomId = maxId + 1;
        }

        // Per-room palettes / state.
        _roomFloorColors.Clear();
        if (f.RoomFloorColors is not null)
            foreach (var kv in f.RoomFloorColors)
                if (kv.Value is { Length: 3 })
                    _roomFloorColors[kv.Key] = new(kv.Value[0], kv.Value[1], kv.Value[2]);
        _roomCeilingColors.Clear();
        if (f.RoomCeilingColors is not null)
            foreach (var kv in f.RoomCeilingColors)
                if (kv.Value is { Length: 3 })
                    _roomCeilingColors[kv.Key] = new(kv.Value[0], kv.Value[1], kv.Value[2]);
        _roomSingleWallColors.Clear();
        if (f.RoomSingleWallColors is not null)
            foreach (var kv in f.RoomSingleWallColors)
                if (kv.Value is { Length: 3 })
                    _roomSingleWallColors[kv.Key] = new(kv.Value[0], kv.Value[1], kv.Value[2]);
        _roomNames.Clear();
        if (f.RoomNames is not null)
            foreach (var kv in f.RoomNames) _roomNames[kv.Key] = kv.Value;
        _multiColorRoomIds.Clear();
        if (f.MultiColorRoomIds is not null)
            foreach (var id in f.MultiColorRoomIds) _multiColorRoomIds.Add(id);
        _wallColors.Clear();
        if (f.WallColors is not null)
            foreach (var kv in f.WallColors)
            {
                var parts = kv.Key.Split('_');
                if (parts.Length != 3) continue;
                if (!int.TryParse(parts[0], out int rid)) continue;
                if (!int.TryParse(parts[1], out int mx))  continue;
                if (!int.TryParse(parts[2], out int mz))  continue;
                if (kv.Value is { Length: 3 } col)
                    _wallColors[(rid, mx, mz)] = new(col[0], col[1], col[2]);
            }

        // Object types + instances.
        _objectTypes.Clear();
        if (f.ObjectTypes is not null && f.ObjectTypes.Count > 0)
        {
            foreach (var t in f.ObjectTypes)
            {
                _objectTypes.Add(new ObjectType
                {
                    Name = t.Name,
                    Shape = System.Enum.TryParse<ObjectShape>(t.Shape, out var s) ? s : ObjectShape.Cube,
                    Color = (t.Color is { Length: 3 } cc)
                        ? new System.Numerics.Vector3(cc[0], cc[1], cc[2])
                        : new System.Numerics.Vector3(0.7f, 0.7f, 0.75f),
                    Size = t.Size > 0 ? t.Size : 0.3f,
                });
            }
        }
        else
        {
            // No types in file → seed at least one default so the UI is usable.
            _objectTypes.Add(new ObjectType { Name = "Object 1", Shape = ObjectShape.Cube,
                Color = new System.Numerics.Vector3(0.55f, 0.65f, 0.8f), Size = 0.3f });
        }
        _objects.Clear();
        if (f.Objects is not null)
        {
            foreach (var o in f.Objects)
                _objects.Add(new RoomObject
                {
                    Slot = o.Slot,
                    OwningRoomId = o.OwningRoomId,
                    Position = new System.Numerics.Vector3(o.X, o.Y, o.Z),
                    Rotation = o.Rotation,
                });
        }

        // Camera.
        if (f.CameraView is not null && System.Enum.TryParse<CameraKind>(f.CameraView, out var cam))
            CameraView = cam;
        if (f.ZoomFactor > 0) ZoomFactor = f.ZoomFactor;
        PanOffsetX = f.PanOffsetX;
        PanOffsetY = f.PanOffsetY;
        IsoYaw = f.IsoYaw;
        IsoPitch = f.IsoPitch;
        IsoDistance = f.IsoDistance > 0 ? f.IsoDistance : 28f;
        IsoPivotX = f.IsoPivotX;
        IsoPivotZ = f.IsoPivotZ;
        IsoCameraInitialised = true;

        // Constraints — copy field-by-field since the property is the
        // VM's own POCO instance (its identity matters for binding).
        if (f.Constraints is not null) Constraints.CopyFrom(f.Constraints);

        // Rebuild adjacencies from the room grid before applying passage
        // overrides — overrides need real Adjacency objects to attach to.
        Rebuild();

        // Passage overrides — match by Start coordinates against newly-
        // built adjacencies.
        _passageOverrides.Clear();
        if (f.PassageOverrides is not null && CurrentEnvironment is { } env)
        {
            foreach (var po in f.PassageOverrides)
            {
                Passage p = po.Kind switch
                {
                    "Open" => Passage.Open.Instance,
                    "Doorway" => new Passage.Doorway(
                        (po.Openings ?? new List<OpenApparatus.Studio.Services.OpeningEntry>())
                        .Select(o => new Opening(
                            offsetAlongEdge: o.Offset,
                            width: o.Width,
                            height: o.Height,
                            sillHeight: o.SillHeight,
                            hingeAtEnd: o.HingeAtEnd,
                            swingNegative: o.SwingNegative))
                        .ToList()),
                    _ => Passage.Closed.Instance,
                };
                // Find adjacency whose segment Start matches (within 1 mm).
                foreach (var adj in env.Adjacencies)
                {
                    var s = adj.SharedSegment.Start;
                    if (System.MathF.Abs(s.X - po.StartX) < 0.001f &&
                        System.MathF.Abs(s.Y - po.StartZ) < 0.001f)
                    {
                        adj.Passage = p;
                        _passageOverrides[PassageKey(adj)] = (p, adj.SharedSegment.Start);
                        break;
                    }
                }
            }
        }

        // Final refresh.
        SelectedRoomId = -1;
        SelectedAdjacency = null;
        SelectedOpeningIndex = -1;
        SelectedSubCell = null;
        SelectedObjectIndex = -1;
        EditVersion++;
        Rebuild();
        HasUnsavedChanges = false;
        OnPropertyChanged(nameof(ObjectTypes));
        OnPropertyChanged(nameof(Objects));
        OnPropertyChanged(nameof(SceneSummary));
    }

    /// <summary>Internal hook used by <see cref="Snapshot.Restore"/>. Do not
    /// call directly — go through Undo / Redo / push-and-mutate instead.</summary>
    internal void RestoreFromSnapshot(Snapshot s)
    {
        // Selection and transient state cleared so we don't dangle pointers
        // into a stale environment.
        SelectedAdjacency = null;
        SelectedOpeningIndex = -1;
        SelectedRoomId = -1;
        SelectedTiles.Clear();
        RefreshSelectionState();
        SelectedSubCell = null;
        SelectedObjectIndex = -1;

        // Resize the grid + replay tile assignments.
        RoomGrid = (int[,])s.RoomGrid.Clone();
        GridWidth = s.GridWidth;
        GridLength = s.GridLength;

        // Parameter state.
        TileSize = s.TileSize;
        WallThickness = s.WallThickness;
        WallHeight = s.WallHeight;
        DoorWidth = s.DoorWidth;
        DoorHeight = s.DoorHeight;
        WindowWidth = s.WindowWidth;
        WindowHeight = s.WindowHeight;
        WindowSillHeight = s.WindowSillHeight;
        GridSubdivision = s.GridSubdivision;
        DefaultObjectY = s.DefaultObjectY;
        DefaultFloorColor = s.DefaultFloorColor;
        DefaultCeilingColor = s.DefaultCeilingColor;
        _nextRoomId = s.NextRoomId;

        // Authored dictionaries.
        _passageOverrides.Clear();
        foreach (var kv in s.PassageOverrides) _passageOverrides[kv.Key] = kv.Value;
        _wallColors.Clear();
        foreach (var kv in s.WallColors) _wallColors[kv.Key] = kv.Value;
        _roomFloorColors.Clear();
        foreach (var kv in s.RoomFloorColors) _roomFloorColors[kv.Key] = kv.Value;
        _roomCeilingColors.Clear();
        foreach (var kv in s.RoomCeilingColors) _roomCeilingColors[kv.Key] = kv.Value;
        _roomSingleWallColors.Clear();
        foreach (var kv in s.RoomSingleWallColors) _roomSingleWallColors[kv.Key] = kv.Value;
        _multiColorRoomIds.Clear();
        foreach (var id in s.MultiColorRoomIds) _multiColorRoomIds.Add(id);
        _roomNames.Clear();
        foreach (var kv in s.RoomNames) _roomNames[kv.Key] = kv.Value;

        _objects.Clear();
        foreach (var o in s.Objects)
            _objects.Add(new RoomObject
            {
                OwningRoomId = o.OwningRoomId,
                Slot = o.Slot,
                Position = o.Position,
                Rotation = o.Rotation,
            });
        _objectTypes.Clear();
        foreach (var t in s.ObjectTypes)
            _objectTypes.Add(new ObjectType
            {
                Name = t.Name,
                Shape = t.Shape,
                Color = t.Color,
                Size = t.Size,
            });

        Rebuild();
        EditVersion++;
    }

    public MainWindowViewModel()
    {
        RoomGrid = new int[GridWidth, GridLength];
        ResetGrid();
        // Seed one default object type so Objects mode has something to place
        // out of the box. The user grows the list via 'Add object type'.
        _objectTypes.Add(new ObjectType
        {
            Name = "Object 1",
            Shape = ObjectShape.Cube,
            Color = new System.Numerics.Vector3(0.85f, 0.30f, 0.30f),
            Size = 0.30f,
        });
        Rebuild();
    }

    // ---- Grid management ----

    void ResetGrid()
    {
        RoomGrid = new int[GridWidth, GridLength];
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                RoomGrid[x, z] = -1;
        SelectedTiles.Clear();
        RefreshSelectionState();
        _nextRoomId = 0;
    }

    /// <summary>Multi-selection set for objects. Shadows
    /// SelectedObjectIndex (which stays the "primary" / inspector-shown
    /// item). Click sets {idx}; Ctrl/Shift+click toggles in place.</summary>
    public HashSet<int> SelectedObjectIndices { get; } = new();
    public int SelectedObjectsCount => SelectedObjectIndices.Count;
    public bool HasMultipleObjectsSelected => SelectedObjectIndices.Count > 1;

    /// <summary>Replaces the multi-selection with a single index. Use
    /// for plain (no-modifier) clicks.</summary>
    public void SetObjectSelection(int idx)
    {
        SelectedObjectIndices.Clear();
        if (idx >= 0) SelectedObjectIndices.Add(idx);
        SelectedObjectIndex = idx;
        OnPropertyChanged(nameof(SelectedObjectsCount));
        OnPropertyChanged(nameof(HasMultipleObjectsSelected));
    }
    /// <summary>Toggles a single index in / out of the multi-selection.
    /// Used for Ctrl-click. Updates SelectedObjectIndex to be the most
    /// recently added index (or -1 if the last entry was removed).</summary>
    public void ToggleObjectInSelection(int idx)
    {
        if (idx < 0) return;
        if (!SelectedObjectIndices.Add(idx))
            SelectedObjectIndices.Remove(idx);
        SelectedObjectIndex = SelectedObjectIndices.Contains(idx)
            ? idx
            : (SelectedObjectIndices.Count > 0 ? SelectedObjectIndices.GetEnumerator() is { } e && e.MoveNext() ? e.Current : -1 : -1);
        OnPropertyChanged(nameof(SelectedObjectsCount));
        OnPropertyChanged(nameof(HasMultipleObjectsSelected));
    }
    /// <summary>Ctrl+A — select every object on the canvas.</summary>
    [RelayCommand]
    void SelectAllObjects()
    {
        if (!IsObjectsMode) return;
        SelectedObjectIndices.Clear();
        for (int i = 0; i < _objects.Count; i++) SelectedObjectIndices.Add(i);
        SelectedObjectIndex = _objects.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedObjectsCount));
        OnPropertyChanged(nameof(HasMultipleObjectsSelected));
        EditVersion++;
    }

    /// <summary>Bulk delete — removes every object in the multi-select.
    /// Indices are removed in descending order so the underlying List
    /// stays consistent during iteration.</summary>
    [RelayCommand]
    void DeleteSelectedObjects()
    {
        if (SelectedObjectIndices.Count == 0) return;
        PushUndo();
        int removed = 0;
        foreach (var idx in SelectedObjectIndices.OrderByDescending(i => i))
        {
            if (idx < 0 || idx >= _objects.Count) continue;
            _objects.RemoveAt(idx);
            removed++;
        }
        SelectedObjectIndices.Clear();
        SelectedObjectIndex = -1;
        OnPropertyChanged(nameof(SelectedObjectsCount));
        OnPropertyChanged(nameof(HasMultipleObjectsSelected));
        EditVersion++;
        OpenApparatus.Studio.Services.Toasts.Default.Show(
            $"Deleted {removed} object{(removed == 1 ? "" : "s")}.",
            OpenApparatus.Studio.Services.ToastSeverity.Warning,
            undo: () => UndoCommand.Execute(null));
    }

    /// <summary>True when any empty tiles are currently selected. Drives
    /// the enabled state of "Create Room from Selection" so it grays
    /// out when nothing is selected.</summary>
    public bool HasSelectedTiles => SelectedTiles.Count > 0;
    /// <summary>Selection size — surfaced for the Actions card so the
    /// user can see how many tiles will turn into a room.</summary>
    public int SelectedTilesCount => SelectedTiles.Count;

    /// <summary>Re-broadcasts HasSelectedTiles / SelectedTilesCount.
    /// Call after any direct mutation of SelectedTiles.</summary>
    public void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(HasSelectedTiles));
        OnPropertyChanged(nameof(SelectedTilesCount));
    }

    /// <summary>Mutates the selection set; called by the editor view on user input.</summary>
    public void SetTileSelected(int x, int z, bool selected)
    {
        if (x < 0 || x >= GridWidth || z < 0 || z >= GridLength) return;
        // Can't select tiles already owned by a room.
        if (RoomGrid[x, z] >= 0) return;

        if (selected) SelectedTiles.Add((x, z));
        else SelectedTiles.Remove((x, z));
        RefreshSelectionState();
        EditVersion++;
    }

    /// <summary>Removes the currently-selected room. Tiles revert to
    /// empty; objects in the room move to "outside". Pushes undo +
    /// surfaces an undo toast so the user can roll back without
    /// hunting for Ctrl+Z.</summary>
    [RelayCommand]
    void DeleteRoom()
    {
        if (SelectedRoomId < 0) return;
        int rid = SelectedRoomId;
        PushUndo();
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                if (RoomGrid[x, z] == rid) RoomGrid[x, z] = -1;
        _roomFloorColors.Remove(rid);
        _roomCeilingColors.Remove(rid);
        _roomSingleWallColors.Remove(rid);
        _roomNames.Remove(rid);
        _multiColorRoomIds.Remove(rid);
        // Strip per-wall colour overrides that referenced this room.
        var staleKeys = new List<(int, int, int)>();
        foreach (var kv in _wallColors)
            if (kv.Key.RoomId == rid) staleKeys.Add(kv.Key);
        foreach (var k in staleKeys) _wallColors.Remove(k);
        // Reassign objects whose owning room was this one.
        foreach (var o in _objects)
            if (o.OwningRoomId == rid) o.OwningRoomId = -1;
        SelectedRoomId = -1;
        Rebuild();
        EditVersion++;
        OpenApparatus.Studio.Services.Toasts.Default.Show(
            $"Deleted room {rid}.",
            OpenApparatus.Studio.Services.ToastSeverity.Warning,
            undo: () => UndoCommand.Execute(null));
    }

    [RelayCommand]
    void CreateRoomFromSelection()
    {
        if (SelectedTiles.Count == 0)
        {
            StatusMessage = "Nothing selected. Click+drag empty tiles first.";
            return;
        }

        // Validate: selection must be a contiguous rectangle (v1 limitation).
        int xMin = int.MaxValue, xMax = int.MinValue, zMin = int.MaxValue, zMax = int.MinValue;
        foreach (var (x, z) in SelectedTiles)
        {
            if (x < xMin) xMin = x;
            if (x > xMax) xMax = x;
            if (z < zMin) zMin = z;
            if (z > zMax) zMax = z;
        }
        int bbox = (xMax - xMin + 1) * (zMax - zMin + 1);
        if (SelectedTiles.Count != bbox)
        {
            StatusMessage = "Selection must be a solid rectangle.";
            return;
        }

        PushUndo();
        int id = _nextRoomId++;
        foreach (var (x, z) in SelectedTiles)
            RoomGrid[x, z] = id;

        // Seed appearance from the auto-generated room hue (used as the wall
        // color so the room reads as a single colored block by default) and
        // the user-configured floor / ceiling defaults.
        _roomSingleWallColors[id] = RoomColorRgb(id);
        _roomFloorColors[id]      = DefaultFloorColor;
        _roomCeilingColors[id]    = DefaultCeilingColor;
        // Random-default mode rolls a fresh hue per room; otherwise use
        // the chosen flat colour.
        _roomSingleWallColors[id] = UseRandomDefaultWallColor
            ? PickRandomWallColor()
            : DefaultWallColor;

        SelectedTiles.Clear();
        RefreshSelectionState();
        SelectedRoomId = id; // auto-select the new room so its editor panel appears
        EditVersion++;
        Rebuild();
        StatusMessage = $"Created room #{id} ({xMax - xMin + 1}×{zMax - zMin + 1} tiles).";
    }

    /// <summary>
    /// Renumbers all rooms so the room with id <paramref name="newStartId"/>
    /// becomes id 0, with the rest filled in BFS order from there. Walking the
    /// adjacency graph this way keeps spatially-related rooms numerically
    /// adjacent, which is the most useful default for downstream tooling that
    /// reads room numbers as a navigation order. Disconnected rooms get
    /// trailing ids in their previous order. All per-room state (colours,
    /// multi-colour flag, per-wall overrides, current selection) is remapped
    /// so the user sees no apparent change beyond the new ids.
    /// </summary>
    [RelayCommand]
    void MarkSelectedRoomAsStart()
    {
        if (SelectedRoomId < 0)
        {
            StatusMessage = "Select a room first.";
            return;
        }
        if (CurrentEnvironment is null) return;
        if (SelectedRoomId == 0)
        {
            StatusMessage = "Room 0 is already start.";
            return;
        }
        PushUndo();
        int oldStart = SelectedRoomId;
        RenumberFromStart(oldStart);
        SelectedRoomId = 0;
        StatusMessage = $"Room {oldStart} is now Room 0 (start). Other rooms renumbered by BFS distance.";
    }

    void RenumberFromStart(int newStartOldId)
    {
        var env = CurrentEnvironment;
        if (env is null) return;

        // BFS through the adjacency graph; each room becomes the next id.
        // Multi-exit branches break ties by the neighbour's current id, which
        // is deterministic but otherwise arbitrary — the "best guess" the user
        // asked for.
        var oldToNew = new Dictionary<int, int>();
        int next = 0;
        oldToNew[newStartOldId] = next++;
        var queue = new Queue<int>();
        queue.Enqueue(newStartOldId);

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            var room = env.Rooms.FirstOrDefault(r => r.Id == cur);
            if (room is null) continue;

            var neighbours = new List<int>();
            foreach (var adj in env.Adjacencies)
            {
                if (!adj.IsInternal) continue;
                // Only follow traversable passages — closed walls don't form a
                // path between rooms and shouldn't drive the numbering order.
                if (adj.Passage is Passage.Closed) continue;
                int? other =
                    adj.RoomA.Id == cur ? adj.RoomB!.Id :
                    adj.RoomB!.Id == cur ? adj.RoomA.Id :
                    (int?)null;
                if (other is int nid && !oldToNew.ContainsKey(nid))
                    neighbours.Add(nid);
            }
            neighbours.Sort();
            foreach (var nid in neighbours)
            {
                if (oldToNew.ContainsKey(nid)) continue; // could've been picked up by a previous neighbour
                oldToNew[nid] = next++;
                queue.Enqueue(nid);
            }
        }

        // Disconnected rooms (graph-component-isolated) trail in id order.
        foreach (var room in env.Rooms.OrderBy(r => r.Id))
            if (!oldToNew.ContainsKey(room.Id))
                oldToNew[room.Id] = next++;

        // Identity mapping → no-op renumber.
        bool anyChange = false;
        foreach (var kvp in oldToNew)
            if (kvp.Key != kvp.Value) { anyChange = true; break; }
        if (!anyChange) return;

        // Apply mapping to every place a room id is stored.
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
            {
                int old = RoomGrid[x, z];
                if (old >= 0 && oldToNew.TryGetValue(old, out var nu))
                    RoomGrid[x, z] = nu;
            }
        RemapKeys(_roomFloorColors, oldToNew);
        RemapKeys(_roomCeilingColors, oldToNew);
        RemapKeys(_roomSingleWallColors, oldToNew);
        RemapKeys(_roomNames, oldToNew);

        var newMulti = new HashSet<int>();
        foreach (var id in _multiColorRoomIds)
            newMulti.Add(oldToNew.TryGetValue(id, out var nu) ? nu : id);
        _multiColorRoomIds.Clear();
        foreach (var id in newMulti) _multiColorRoomIds.Add(id);

        var newWallColors = new Dictionary<(int RoomId, int MidX, int MidZ), System.Numerics.Vector3>();
        foreach (var kvp in _wallColors)
        {
            int oldId = kvp.Key.RoomId;
            int mappedId = oldToNew.TryGetValue(oldId, out var nu) ? nu : oldId;
            newWallColors[(mappedId, kvp.Key.MidX, kvp.Key.MidZ)] = kvp.Value;
        }
        _wallColors.Clear();
        foreach (var kvp in newWallColors) _wallColors[kvp.Key] = kvp.Value;

        _nextRoomId = next;

        Rebuild();
        EditVersion++;
    }

    /// <summary>
    /// Walks every internal adjacency and rewrites each door opening so it
    /// swings into the room with the higher id (RoomB by Adjacency convention,
    /// hence SwingNegative=true). Outer adjacencies and window openings are
    /// left untouched.
    /// </summary>
    public void UpdateDoorSwingsToHigherRoom()
    {
        if (CurrentEnvironment is null) return;
        PushUndo();
        int touched = 0;
        foreach (var adj in CurrentEnvironment.Adjacencies)
        {
            if (!adj.IsInternal) continue;
            if (adj.Passage is not Passage.Doorway dw) continue;

            // Higher room is whichever of RoomA / RoomB has the larger id;
            // in this codebase RoomA is always lower, so target = -N (true).
            bool targetSwingNegative = adj.RoomB!.Id > adj.RoomA.Id;

            var openings = new List<Opening>(dw.Openings.Count);
            bool changed = false;
            foreach (var op in dw.Openings)
            {
                if (op.IsWindow) { openings.Add(op); continue; }
                if (op.SwingNegative != targetSwingNegative)
                {
                    openings.Add(op.With(swingNegative: targetSwingNegative));
                    changed = true;
                }
                else
                {
                    openings.Add(op);
                }
            }
            if (changed)
            {
                adj.Passage = new Passage.Doorway(openings);
                RememberPassage(adj);
                touched += openings.Count;
            }
        }
        if (touched > 0)
        {
            EditVersion++;
            StatusMessage = $"Updated swing on {touched} door opening(s).";
        }
        else
        {
            StatusMessage = "All swings already point outward.";
        }
    }

    static void RemapKeys<T>(Dictionary<int, T> dict, Dictionary<int, int> oldToNew)
    {
        var copy = new Dictionary<int, T>(dict);
        dict.Clear();
        foreach (var kvp in copy)
        {
            int newKey = oldToNew.TryGetValue(kvp.Key, out var nu) ? nu : kvp.Key;
            dict[newKey] = kvp.Value;
        }
    }

    /// <summary>
    /// Click handler for the editor view — if the tile belongs to a room, select
    /// that room (opens the right-side editor panel). Returns true if the click
    /// hit a room and should NOT fall through to drag-select.
    /// </summary>
    public bool TrySelectRoomAtTile(int x, int z)
    {
        if (x < 0 || x >= GridWidth || z < 0 || z >= GridLength) return false;
        int id = RoomGrid[x, z];
        if (id < 0) return false;
        SelectedRoomId = id;
        SelectedAdjacency = null;
        StatusMessage = $"Selected Room {id}.";
        return true;
    }

    [RelayCommand]
    void ClearSelection()
    {
        SelectedTiles.Clear();
        RefreshSelectionState();
        SelectedAdjacency = null;
        EditVersion++;
        StatusMessage = "Selection cleared.";
    }

    /// <summary>Internal scene reset shared by ResetAll (user-initiated)
    /// and NewProject (file menu / welcome). Returns to a blank canvas
    /// without toasting — callers add their own user-facing feedback.</summary>
    void ResetSceneState()
    {
        PushUndo();
        ResetGrid();
        _passageOverrides.Clear();
        _wallColors.Clear();
        _roomFloorColors.Clear();
        _roomCeilingColors.Clear();
        _roomSingleWallColors.Clear();
        _multiColorRoomIds.Clear();
        _roomNames.Clear();
        _objects.Clear();
        SelectedRoomId = -1;
        SelectedSubCell = null;
        SelectedObjectIndex = -1;
        SelectedObjectIndices.Clear();
        Rebuild();
        EditVersion++;
    }

    [RelayCommand]
    void ResetAll()
    {
        ResetSceneState();
        // Toast with Undo — the explicit Reset is destructive and should
        // be reversible at one click without diving into the menu.
        OpenApparatus.Studio.Services.Toasts.Default.Show(
            "Scene reset.",
            OpenApparatus.Studio.Services.ToastSeverity.Warning,
            undo: () => UndoCommand.Execute(null));
    }

    /// <summary>
    /// The wall the user has clicked on. Set via <see cref="TrySelectAdjacencyAtWorld"/>;
    /// hotkeys then mutate its passage. Cleared on grid changes (the materialized
    /// adjacency would point at a stale Adjacency object).
    /// </summary>
    [ObservableProperty] Adjacency? _selectedAdjacency;

    /// <summary>
    /// World-space distance along the selected wall (from segment Start) where the
    /// user clicked, snapped to the nearest valid door anchor (whole-wall midpoint
    /// or a tile-pair-seam center). Pressing D places the doorway centered here.
    /// Exposed so the view can highlight the active anchor.
    /// </summary>
    public float SelectedClickAlong => _selectedClickAlong;

    float _selectedClickAlong;

    /// <summary>
    /// Selects the wall closest to the given world XZ point, if within tolerance.
    /// Returns true if a wall was selected. Also records where along the wall the
    /// click landed so a subsequent door placement can snap to that spot.
    /// </summary>
    public bool TrySelectAdjacencyAtWorld(System.Numerics.Vector2 worldPos, float toleranceWorld)
    {
        if (CurrentEnvironment is null) return false;

        Adjacency? hit = null;
        float minDist = toleranceWorld;
        foreach (var adj in CurrentEnvironment.Adjacencies)
        {
            float d = DistanceFromPointToSegment(worldPos, adj.SharedSegment);
            if (d < minDist)
            {
                minDist = d;
                hit = adj;
            }
        }
        if (hit is null) return false;

        SelectedAdjacency = hit;
        // Project click onto wall direction → "along" coordinate in world units.
        var seg = hit.SharedSegment;
        var ap = worldPos - seg.Start;
        var ab = seg.End - seg.Start;
        float lenSq = ab.LengthSquared();
        float along = lenSq > 1e-6f
            ? System.Math.Clamp(System.Numerics.Vector2.Dot(ap, ab) / lenSq, 0f, 1f) * seg.Length
            : 0f;
        _selectedClickAlong = SnapAlongToCandidate(along, seg.Length, TileSize);

        // If the snapped anchor lands inside an existing opening, select it so
        // the right-side panel pops up with that opening's settings.
        SelectedOpeningIndex = FindOpeningAt(hit, _selectedClickAlong);
        OnPropertyChanged(nameof(HasSelectedOpening));
        OnPropertyChanged(nameof(SelectedOpening));

        EditVersion++;
        string b = hit.RoomB is null ? "outside" : $"#{hit.RoomB.Id}";
        StatusMessage = $"Selected wall #{hit.RoomA.Id} ↔ {b} ({hit.Passage.GetType().Name}). " +
                        "Press D for doorway, O for open, C for closed.";
        return true;
    }

    /// <summary>
    /// Toggle a doorway at the active anchor on the selected wall (D hotkey).
    /// If a door already exists at the active anchor, remove it. Otherwise, add
    /// one — preserving any other openings on the wall.
    /// </summary>
    [RelayCommand]
    void ToggleDoorOnSelectedWall() => ToggleOpeningOnSelectedWall(isWindow: false);

    /// <summary>
    /// Toggle a window at the active anchor on the selected wall (W hotkey).
    /// If a window already exists at the active anchor, remove it. Otherwise,
    /// add one and merge with any window directly adjacent on either side
    /// (matching sill / head heights) so consecutive windows form a single
    /// wider opening.
    /// </summary>
    [RelayCommand]
    void ToggleWindowOnSelectedWall() => ToggleOpeningOnSelectedWall(isWindow: true);

    void ToggleOpeningOnSelectedWall(bool isWindow)
    {
        if (SelectedAdjacency is null)
        {
            StatusMessage = "No wall selected. Click a wall first.";
            return;
        }
        PushUndo();

        var adj = SelectedAdjacency;
        float segLen = adj.SharedSegment.Length;
        float reqWidth = isWindow ? WindowWidth : DoorWidth;
        float reqHeight = isWindow ? WindowHeight : DoorHeight;
        float reqSill = isWindow ? WindowSillHeight : 0f;
        // Refuse to place an opening that won't fit at the selected anchor.
        // The previous behaviour clamped w = min(reqWidth, segLen), which on
        // walls shorter than DoorWidth produced a door spanning the entire
        // wall instead of one centered on the click — confusing.
        if (reqWidth > segLen + 1e-3f)
        {
            string label1 = isWindow ? "window" : "door";
            StatusMessage = $"Can't fit a {reqWidth:F2}m {label1} on this {segLen:F2}m wall. " +
                            $"Reduce the {label1} width or pick a longer wall.";
            return;
        }
        float w = reqWidth;
        float anchor = _selectedClickAlong > 0f ? _selectedClickAlong : segLen * 0.5f;
        string label = isWindow ? "window" : "door";

        var openings = adj.Passage is Passage.Doorway dw
            ? new List<Opening>(dw.Openings)
            : new List<Opening>();

        // Does an opening already cover the active anchor (i.e., the anchor sits
        // inside the opening's span)? Use containment rather than center-distance
        // so merged wide openings still toggle off correctly.
        const float ANCHOR_EPS = 1e-3f;
        int sameKindIdx = -1;
        int otherKindIdx = -1;
        for (int i = 0; i < openings.Count; i++)
        {
            var op = openings[i];
            if (anchor < op.OffsetAlongEdge - ANCHOR_EPS) continue;
            if (anchor > op.OffsetAlongEdge + op.Width + ANCHOR_EPS) continue;
            if (op.IsWindow == isWindow) sameKindIdx = i;
            else otherKindIdx = i;
        }

        if (sameKindIdx >= 0)
        {
            openings.RemoveAt(sameKindIdx);
            StatusMessage = openings.Count > 0
                ? $"Removed {label} — {openings.Count} opening(s) remaining."
                : $"Removed {label} — wall is now closed.";
        }
        else
        {
            float offset = System.Math.Clamp(anchor - w * 0.5f, 0f, segLen - w);
            float endOff = offset + w;

            // Reject if the new opening would clash with a *different-kind* opening
            // (a door cannot share space with a window). Same-kind clashes fall
            // through to the merge step below.
            bool blocked = false;
            foreach (var op in openings)
            {
                if (op.IsWindow == isWindow) continue;
                if (endOff > op.OffsetAlongEdge && offset < op.OffsetAlongEdge + op.Width)
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked)
            {
                StatusMessage = $"Can't add {label} here — would overlap a {(isWindow ? "door" : "window")}.";
                return;
            }

            // Default door swing: into the room with the higher id. RoomA is
            // always the lower id by convention, so internal walls swing -N
            // (into RoomB). Outer walls have RoomB = null → swing +N into the
            // only room. Windows ignore hinge / swing.
            bool defaultSwingNegative = !isWindow && adj.IsInternal;
            openings.Add(new Opening(offset, w, reqHeight, reqSill,
                hingeAtEnd: false,
                swingNegative: defaultSwingNegative));
            MergeOverlappingSameKind(openings);
            StatusMessage = $"Added {label} at offset {offset:F2}m. {openings.Count} opening(s) on this wall.";
        }

        adj.Passage = openings.Count > 0
            ? new Passage.Doorway(openings)
            : Passage.Closed.Instance;
        RememberPassage(adj);

        // After mutating, surface the opening at the active anchor (if any) to
        // the right-side panel so the user can immediately tweak its size /
        // hinge / swing.
        SelectedOpeningIndex = FindOpeningAt(adj, anchor);
        OnPropertyChanged(nameof(HasSelectedOpening));
        OnPropertyChanged(nameof(SelectedOpening));
        // Clear room selection so the panel switches to opening mode.
        if (HasSelectedOpening) SelectedRoomId = -1;

        EditVersion++;
    }

    /// <summary>
    /// Merge same-kind openings (door+door or window+window) that touch or
    /// overlap into a single wider opening. Heights / sill must match.
    /// Mutates <paramref name="openings"/> in place.
    /// </summary>
    static void MergeOverlappingSameKind(List<Opening> openings)
    {
        const float EPS = 1e-3f;
        bool merged;
        do
        {
            openings.Sort((a, b) => a.OffsetAlongEdge.CompareTo(b.OffsetAlongEdge));
            merged = false;
            for (int i = 0; i < openings.Count - 1; i++)
            {
                var a = openings[i];
                var b = openings[i + 1];
                if (a.IsWindow != b.IsWindow) continue;
                if (System.Math.Abs(a.Height - b.Height) > EPS) continue;
                if (System.Math.Abs(a.SillHeight - b.SillHeight) > EPS) continue;
                // Touching or overlapping: b starts at or before a's right edge.
                if (b.OffsetAlongEdge > a.OffsetAlongEdge + a.Width + EPS) continue;

                float newStart = a.OffsetAlongEdge;
                float newEnd = System.Math.Max(
                    a.OffsetAlongEdge + a.Width,
                    b.OffsetAlongEdge + b.Width);
                openings[i] = new Opening(
                    newStart,
                    newEnd - newStart,
                    a.Height,
                    a.SillHeight);
                openings.RemoveAt(i + 1);
                merged = true;
                break;
            }
        } while (merged);
    }

    /// <summary>Set the selected wall's passage to Open (no wall) — O hotkey.</summary>
    [RelayCommand]
    void OpenSelectedWall()
    {
        if (SelectedAdjacency is null)
        {
            StatusMessage = "No wall selected. Click a wall first.";
            return;
        }
        PushUndo();
        SelectedAdjacency.Passage = Passage.Open.Instance;
        RememberPassage(SelectedAdjacency);
        EditVersion++;
        StatusMessage = "Wall opened.";
    }

    /// <summary>Set the selected wall's passage to Closed (solid wall, no doors) — C hotkey.</summary>
    [RelayCommand]
    void CloseSelectedWall()
    {
        if (SelectedAdjacency is null)
        {
            StatusMessage = "No wall selected. Click a wall first.";
            return;
        }
        PushUndo();
        SelectedAdjacency.Passage = Passage.Closed.Instance;
        RememberPassage(SelectedAdjacency);
        EditVersion++;
        StatusMessage = "Wall closed.";
    }

    /// <summary>
    /// Snap a click position along a wall to the nearest valid door anchor. Anchors
    /// are at every tile center along the wall AND at every internal tile-seam
    /// (the boundary between two consecutive tiles, excluding the two corners).
    /// </summary>
    static float SnapAlongToCandidate(float clickAlong, float segLen, float tileSize)
    {
        float best = float.MaxValue;
        float bestDist = float.MaxValue;
        foreach (var anchor in DoorAnchorPositions(segLen, tileSize))
        {
            float d = System.Math.Abs(clickAlong - anchor);
            if (d < bestDist)
            {
                bestDist = d;
                best = anchor;
            }
        }
        return best;
    }

    /// <summary>
    /// All valid door anchors along the given wall, in world units along the segment.
    /// For an N-tile-long wall there are 2N-1 anchors:
    ///   • N tile centers — at (i + 0.5) × tileSize for i = 0..N-1
    ///   • N-1 internal tile-seam centers — at i × tileSize for i = 1..N-1
    ///     (corners at 0 and N×tileSize are excluded — those are wall ends)
    /// </summary>
    public static IEnumerable<float> DoorAnchorsAlongWall(EdgeSegment segment, float tileSize) =>
        DoorAnchorPositions(segment.Length, tileSize);

    static IEnumerable<float> DoorAnchorPositions(float segLen, float tileSize)
    {
        int n = (int)System.Math.Max(1, System.Math.Round(segLen / tileSize));
        for (int i = 0; i < n; i++)
            yield return (i + 0.5f) * tileSize;       // tile centers
        for (int i = 1; i < n; i++)
            yield return i * tileSize;                 // internal tile-seams
    }

    static float DistanceFromPointToSegment(System.Numerics.Vector2 p, EdgeSegment seg)
    {
        var ab = seg.End - seg.Start;
        var ap = p - seg.Start;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f) return (p - seg.Start).Length();
        float t = System.Math.Clamp(System.Numerics.Vector2.Dot(ap, ab) / abLenSq, 0f, 1f);
        var closest = seg.Start + ab * t;
        return (p - closest).Length();
    }

    /// <summary>
    /// Generates a random GridDomino layout into the authored grid as a starting point
    /// (D4: keeps the parametric generator available without coupling the UI to it).
    /// </summary>
    [RelayCommand]
    void RandomFill()
    {
        PushUndo();
        try
        {
            int seed = new Random().Next(int.MaxValue);
            var gen = new GridDominoGenerator
            {
                FloorWidthCells = GridWidth,
                FloorLengthCells = GridLength,
                RectangleRoomCount = (GridWidth * GridLength) / 6, // ~1 rectangle per 6 tiles
                TileSize = TileSize,
            };
            var env = gen.Generate(new SeededRandom(seed));

            // Reverse-engineer the grid from the generated env: each room has a position
            // (in world coords) and a RectangleShape; convert back to tile coordinates.
            ResetGrid();
            int maxId = 0;
            foreach (var room in env.Rooms)
            {
                int xMin = (int)MathF.Round(room.Position.X / TileSize);
                int zMin = (int)MathF.Round(room.Position.Y / TileSize);
                var rect = (RectangleShape)room.Shape;
                int w = (int)MathF.Round(rect.Width / TileSize);
                int d = (int)MathF.Round(rect.Depth / TileSize);
                for (int x = xMin; x < xMin + w; x++)
                    for (int z = zMin; z < zMin + d; z++)
                        if (x >= 0 && x < GridWidth && z >= 0 && z < GridLength)
                            RoomGrid[x, z] = room.Id;
                if (room.Id >= maxId) maxId = room.Id + 1;
                // Seed appearance for every random room so the editor view and the
                // glTF export both read consistently without forcing the user to
                // visit each room's panel.
                if (!_roomSingleWallColors.ContainsKey(room.Id))
                    _roomSingleWallColors[room.Id] = RoomColorRgb(room.Id);
                if (!_roomFloorColors.ContainsKey(room.Id))
                    _roomFloorColors[room.Id] = DefaultFloorColor;
                if (!_roomCeilingColors.ContainsKey(room.Id))
                    _roomCeilingColors[room.Id] = DefaultCeilingColor;
            }
            _nextRoomId = maxId;

            Rebuild();
            EditVersion++;
            StatusMessage = $"Random fill: {env.Rooms.Count} rooms (seed {seed}). Edit as needed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Random fill failed: {ex.Message}";
        }
    }

    /// <summary>Save the current scene to disk. Routes to SaveAs when no
    /// path is set yet; otherwise overwrites the existing file. Updates
    /// the recent-files list and clears the unsaved indicator on success.</summary>
    [RelayCommand]
    async Task SaveProjectAsync(Window? owner)
    {
        if (string.IsNullOrEmpty(ProjectFilePath))
        {
            await SaveProjectAsAsync(owner);
            return;
        }
        try
        {
            OpenApparatus.Studio.Services.ProjectIO.Save(ProjectFilePath, this);
            HasUnsavedChanges = false;
            OpenApparatus.Studio.Services.Toasts.Default.ShowSuccess(
                $"Saved → {System.IO.Path.GetFileName(ProjectFilePath)}");
        }
        catch (Exception ex)
        {
            OpenApparatus.Studio.Services.Toasts.Default.ShowError($"Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    async Task SaveProjectAsAsync(Window? owner)
    {
        if (owner is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save scene",
            SuggestedFileName = string.IsNullOrEmpty(ProjectTitle) ? "scene" : ProjectTitle,
            DefaultExtension = OpenApparatus.Studio.Services.ProjectIO.FileExtension.TrimStart('.'),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("OpenApparatus project (*.oapp)")
                    { Patterns = new[] { "*.oapp" } },
            },
        });
        if (file is null) return;

        try
        {
            string path = file.Path.LocalPath;
            OpenApparatus.Studio.Services.ProjectIO.Save(path, this);
            ProjectFilePath = path;
            ProjectTitle = System.IO.Path.GetFileNameWithoutExtension(path);
            HasUnsavedChanges = false;
            // Track in recent.
            var settings = OpenApparatus.Studio.Services.AppSettings.LoadOrDefault();
            settings.RecordRecent(path);
            OpenApparatus.Studio.Services.Toasts.Default.ShowSuccess($"Saved → {file.Name}");
        }
        catch (Exception ex)
        {
            OpenApparatus.Studio.Services.Toasts.Default.ShowError($"Save failed: {ex.Message}");
        }
    }

    /// <summary>Pick a project file and load it. Replaces the current
    /// scene; the user's previous in-memory scene goes onto the undo
    /// stack via RestoreFromProjectFile so they can step back.</summary>
    [RelayCommand]
    async Task OpenProjectAsync(Window? owner)
    {
        if (owner is null) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open scene",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("OpenApparatus project (*.oapp)")
                    { Patterns = new[] { "*.oapp" } },
            },
        });
        var file = files?.Count > 0 ? files[0] : null;
        if (file is null) return;
        try
        {
            string path = file.Path.LocalPath;
            OpenApparatus.Studio.Services.ProjectIO.Load(path, this);
            ProjectFilePath = path;
            ProjectTitle = System.IO.Path.GetFileNameWithoutExtension(path);
            var settings = OpenApparatus.Studio.Services.AppSettings.LoadOrDefault();
            settings.RecordRecent(path);
            OpenApparatus.Studio.Services.Toasts.Default.ShowSuccess($"Opened {file.Name}");
        }
        catch (Exception ex)
        {
            OpenApparatus.Studio.Services.Toasts.Default.ShowError($"Couldn't open {file.Name}: {ex.Message}");
        }
    }

    /// <summary>List of recent project file paths surfaced to the
    /// File → Recent menu and the welcome screen. Refreshed each time
    /// a new file is loaded or saved.</summary>
    public System.Collections.ObjectModel.ObservableCollection<RecentFileEntry> RecentFiles { get; } = new();

    public void RefreshRecentFiles()
    {
        var settings = OpenApparatus.Studio.Services.AppSettings.LoadOrDefault();
        RecentFiles.Clear();
        foreach (var p in settings.ExistingRecentFiles)
            RecentFiles.Add(new RecentFileEntry(p));
    }

    /// <summary>True once the user has explicitly dismissed the welcome
    /// (clicked New scene, Open scene, Skip, or finished creating a
    /// room). Resets to false on each fresh launch so the welcome
    /// re-appears for the next session if the scene is empty.</summary>
    bool _welcomeDismissed;

    /// <summary>Whether the welcome panel should be visible — true on
    /// startup with no rooms / no current file, false once the user
    /// has signalled they want to use the editor.</summary>
    public bool ShowWelcomePanel
        => !_welcomeDismissed && HasNoRooms && !HasProjectFilePath;

    [RelayCommand]
    void DismissWelcome()
    {
        _welcomeDismissed = true;
        OnPropertyChanged(nameof(ShowWelcomePanel));
    }

    /// <summary>Open a project file directly by path — used by recent
    /// files menu and by command-line / drag-drop hand-offs.</summary>
    public void OpenProjectFromPath(string path)
    {
        try
        {
            OpenApparatus.Studio.Services.ProjectIO.Load(path, this);
            ProjectFilePath = path;
            ProjectTitle = System.IO.Path.GetFileNameWithoutExtension(path);
            var settings = OpenApparatus.Studio.Services.AppSettings.LoadOrDefault();
            settings.RecordRecent(path);
            OpenApparatus.Studio.Services.Toasts.Default.ShowSuccess(
                $"Opened {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            OpenApparatus.Studio.Services.Toasts.Default.ShowError(
                $"Couldn't open {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>New empty scene. Resets without the destructive
    /// "Scene reset" toast (which would be confusing right after the
    /// user hit "New scene") and dismisses the welcome panel so they
    /// can start drawing immediately.</summary>
    [RelayCommand]
    void NewProject()
    {
        ResetSceneState();
        ProjectFilePath = null;
        ProjectTitle = "Untitled scene";
        HasUnsavedChanges = false;
        _welcomeDismissed = true;
        OnPropertyChanged(nameof(ShowWelcomePanel));
        OpenApparatus.Studio.Services.Toasts.Default.Show(
            "New scene — drag tiles and press R to create your first room.",
            OpenApparatus.Studio.Services.ToastSeverity.Info);
    }

    [RelayCommand]
    async Task ExportGltfAsync(Window? owner)
    {
        if (owner is null || CurrentEnvironment is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export geometry as glTF",
            SuggestedFileName = "environment.glb",
            DefaultExtension = "glb",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("glTF Binary (*.glb)") { Patterns = new[] { "*.glb" } },
                new FilePickerFileType("glTF JSON (*.gltf)")  { Patterns = new[] { "*.gltf" } },
            },
        });
        if (file is null) return;

        try
        {
            // SharpGLTF picks GLB vs glTF+bin from the extension on Save —
            // .glb is single-file binary; .gltf writes JSON + sidecar .bin.
            // glTF natively supports scene hierarchy, so the per-room nesting
            // (Root → Room_<id> → floor/ceiling/wall_<i>) survives import in
            // Unity, Blender, Three.js, or any glTF-aware tool.
            GltfExporter.Export(
                file.Path.LocalPath, CurrentEnvironment, WallThickness, WallHeight,
                _multiColorRoomIds,
                _roomFloorColors, _roomCeilingColors,
                _roomSingleWallColors, _wallColors,
                _objects, _objectTypes);
            StatusMessage = $"Exported glTF → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    async Task ExportJsonAsync(Window? owner)
    {
        if (owner is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export environment as JSON",
            SuggestedFileName = "environment.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;

        try
        {
            var doc = JsonExporter.BuildDocument(
                RoomGrid,
                TileSize,
                WallThickness, WallHeight,
                DoorWidth, DoorHeight,
                WindowWidth, WindowHeight, WindowSillHeight,
                CurrentEnvironment,
                GridSubdivision,
                DefaultObjectY,
                _objects,
                _objectTypes,
                Constraints);

            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            JsonExporter.Export(writer, doc);
            StatusMessage = $"Exported JSON → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"JSON export failed: {ex.Message}";
        }
    }

    void Rebuild()
    {
        try
        {
            var env = MultiRoomEnvironmentBuilder.FromGrid(RoomGrid, TileSize);

            // Re-apply any persisted passage choices. Doors / open passages set by
            // the user need to survive grid edits — including outer↔internal
            // transitions when new rooms are placed against existing walls.
            foreach (var adj in env.Adjacencies)
            {
                if (!_passageOverrides.TryGetValue(PassageKey(adj), out var stored))
                    continue;

                // If the new segment's Start is closer to the stored End than to
                // the stored Start, the wall direction flipped on rebuild
                // (typical after renumbering: RoomA is always the lower-id
                // room, so swapping ids can flip the segment). Re-orient the
                // openings so they keep their world positions.
                var newSeg = adj.SharedSegment;
                bool flipped = (newSeg.Start - stored.Start).LengthSquared()
                             > (newSeg.End - stored.Start).LengthSquared();

                if (stored.Passage is Passage.Doorway dw)
                {
                    var rebased = new List<Opening>(dw.Openings.Count);
                    foreach (var op in dw.Openings)
                    {
                        var op2 = flipped
                            ? op.With(
                                offsetAlongEdge: newSeg.Length - op.OffsetAlongEdge - op.Width,
                                hingeAtEnd: !op.HingeAtEnd,
                                swingNegative: !op.SwingNegative)
                            : op;
                        if (op2.OffsetAlongEdge >= -1e-3f &&
                            op2.OffsetAlongEdge + op2.Width <= newSeg.Length + 1e-3f)
                            rebased.Add(op2);
                    }
                    if (rebased.Count > 0)
                        adj.Passage = new Passage.Doorway(rebased);
                    // else: leave as default Closed — no openings fit the new segment.
                }
                else
                {
                    adj.Passage = stored.Passage;
                }
            }

            CurrentEnvironment = env;
            // The previous SelectedAdjacency points at a stale Adjacency from the previous
            // build; clear it so the editor doesn't try to mutate orphaned objects.
            SelectedAdjacency = null;
        }
        catch (Exception ex)
        {
            CurrentEnvironment = null;
            SelectedAdjacency = null;
            StatusMessage = $"Build failed: {ex.Message}";
        }
    }

    // Reactivity: when grid dimensions change, resize the underlying array.
    partial void OnGridWidthChanged(int value) => OnGridDimensionChanged();
    partial void OnGridLengthChanged(int value) => OnGridDimensionChanged();
    partial void OnTileSizeChanged(float value) => Rebuild();
    partial void OnWallThicknessChanged(float value) => Rebuild();
    partial void OnWallHeightChanged(float value) => Rebuild();
    partial void OnDoorWidthChanged(float value) => Rebuild();
    partial void OnDoorHeightChanged(float value) => Rebuild();
    partial void OnWindowWidthChanged(float value) => Rebuild();
    partial void OnWindowHeightChanged(float value) => Rebuild();
    partial void OnWindowSillHeightChanged(float value) => Rebuild();

    void OnGridDimensionChanged()
    {
        if (GridWidth < 1 || GridLength < 1) return;
        // Resize grid, preserving rooms that fit in the new bounds.
        var oldGrid = RoomGrid;
        int oldW = oldGrid.GetLength(0);
        int oldL = oldGrid.GetLength(1);
        var newGrid = new int[GridWidth, GridLength];
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                newGrid[x, z] = (x < oldW && z < oldL) ? oldGrid[x, z] : -1;
        RoomGrid = newGrid;
        // Drop any selection now off-grid.
        SelectedTiles.RemoveWhere(t => t.x >= GridWidth || t.z >= GridLength);
        EditVersion++;
        Rebuild();
    }
}
