using System;
using System.Collections.Generic;
using System.IO;
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
/// Three-state choice for the starting (entrance) room type. NoPreference maps to
/// PassageAssigner.PreferEntranceRoomType = null; Square / Rectangle map to the
/// corresponding RoomType.
/// </summary>
public enum StartingRoomTypeChoice
{
    NoPreference,
    Square,
    Rectangle,
}

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] int _floorWidthCells = 4;
    [ObservableProperty] int _floorLengthCells = 4;
    [ObservableProperty] int _rectangleRoomCount = 0;
    [ObservableProperty] RectangleOrientation _rectangleOrientation = RectangleOrientation.Random;
    [ObservableProperty] float _tileSize = 3.5f;
    [ObservableProperty] float _wallThickness = 0.2f;
    [ObservableProperty] float _wallHeight = 3f;
    [ObservableProperty] int _seed = 42;
    [ObservableProperty] bool _includeOuterEntrance = true;
    [ObservableProperty] StartingRoomTypeChoice _startingRoomType = StartingRoomTypeChoice.NoPreference;
    [ObservableProperty] float _doorWidth = 1.2f;
    [ObservableProperty] float _doorHeight = 2.2f;

    [ObservableProperty] MultiRoomEnvironment? _currentEnvironment;
    [ObservableProperty] string _statusMessage = "Ready.";

    /// <summary>Backing list for the orientation ComboBox.</summary>
    public IReadOnlyList<RectangleOrientation> RectangleOrientationOptions { get; } =
        new[] { RectangleOrientation.Random, RectangleOrientation.LengthWise, RectangleOrientation.WidthWise };

    /// <summary>Backing list for the starting-room ComboBox.</summary>
    public IReadOnlyList<StartingRoomTypeChoice> StartingRoomTypeOptions { get; } =
        new[] { StartingRoomTypeChoice.NoPreference, StartingRoomTypeChoice.Square, StartingRoomTypeChoice.Rectangle };

    public MainWindowViewModel()
    {
        Regenerate();
    }

    [RelayCommand]
    void Regenerate()
    {
        try
        {
            var gen = new GridDominoGenerator
            {
                FloorWidthCells = FloorWidthCells,
                FloorLengthCells = FloorLengthCells,
                RectangleRoomCount = RectangleRoomCount,
                TileSize = TileSize,
                Orientation = RectangleOrientation,
            };
            var plan = gen.Generate(new SeededRandom(Seed));
            new SpanningTreePassageAssigner
            {
                IncludeOuterEntrance = IncludeOuterEntrance,
                DoorWidth = DoorWidth,
                DoorHeight = DoorHeight,
                PreferEntranceRoomType = StartingRoomType switch
                {
                    StartingRoomTypeChoice.Square => RoomType.Square,
                    StartingRoomTypeChoice.Rectangle => RoomType.Rectangle,
                    _ => null,
                },
            }.Assign(plan, new SeededRandom(Seed));
            CurrentEnvironment = plan;
            StatusMessage = $"Generated {plan.Rooms.Count} rooms, {plan.Adjacencies.Count} adjacencies (seed {Seed}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Generation failed: {ex.Message}";
            CurrentEnvironment = null;
        }
    }

    [RelayCommand]
    void Reseed()
    {
        Seed = new Random().Next(int.MaxValue);
    }

    [RelayCommand]
    async Task SaveAsync(Window? owner)
    {
        if (owner is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save floor plan parameters",
            SuggestedFileName = $"floorplan-{Seed}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("MultiRoomEnvironment JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;

        try
        {
            var spec = MultiRoomEnvironmentSpec.From(this);
            var json = MultiRoomEnvironmentJsonSerializer.Serialize(spec);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            StatusMessage = $"Saved → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    async Task LoadAsync(Window? owner)
    {
        if (owner is null) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load floor plan parameters",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("MultiRoomEnvironment JSON") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var spec = MultiRoomEnvironmentJsonSerializer.Deserialize(json);
            spec.ApplyTo(this);
            Regenerate();
            StatusMessage = $"Loaded ← {files[0].Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    async Task ExportObjAsync(Window? owner)
    {
        if (owner is null || CurrentEnvironment is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export geometry as OBJ",
            SuggestedFileName = $"floorplan-{Seed}.obj",
            DefaultExtension = "obj",
            FileTypeChoices = new[] { new FilePickerFileType("Wavefront OBJ") { Patterns = new[] { "*.obj" } } },
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            ObjExporter.Export(writer, CurrentEnvironment, WallThickness, WallHeight);
            StatusMessage = $"Exported OBJ → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // Auto-regenerate on any parameter change.
    partial void OnFloorWidthCellsChanged(int value) => Regenerate();
    partial void OnFloorLengthCellsChanged(int value) => Regenerate();
    partial void OnRectangleRoomCountChanged(int value) => Regenerate();
    partial void OnRectangleOrientationChanged(RectangleOrientation value) => Regenerate();
    partial void OnTileSizeChanged(float value) => Regenerate();
    partial void OnSeedChanged(int value) => Regenerate();
    partial void OnIncludeOuterEntranceChanged(bool value) => Regenerate();
    partial void OnStartingRoomTypeChanged(StartingRoomTypeChoice value) => Regenerate();
    partial void OnDoorWidthChanged(float value) => Regenerate();
    partial void OnDoorHeightChanged(float value) => Regenerate();
}
