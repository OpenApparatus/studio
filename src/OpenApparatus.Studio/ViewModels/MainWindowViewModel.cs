using System;
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

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] int _floorWidthCells = 4;
    [ObservableProperty] int _floorHeightCells = 4;
    [ObservableProperty] int _rectangleRoomCount = 0;
    [ObservableProperty] float _tileSize = 3.5f;
    [ObservableProperty] float _wallThickness = 0.2f;
    [ObservableProperty] float _wallHeight = 3f;
    [ObservableProperty] int _seed = 42;
    [ObservableProperty] bool _includeOuterEntrance = true;
    [ObservableProperty] float _doorWidth = 1.2f;
    [ObservableProperty] float _doorHeight = 2.2f;

    [ObservableProperty] FloorPlan? _currentPlan;
    [ObservableProperty] string _statusMessage = "Ready.";

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
                FloorHeightCells = FloorHeightCells,
                RectangleRoomCount = RectangleRoomCount,
                TileSize = TileSize,
            };
            var plan = gen.Generate(new SeededRandom(Seed));
            new SpanningTreePassageAssigner
            {
                IncludeOuterEntrance = IncludeOuterEntrance,
                DoorWidth = DoorWidth,
                DoorHeight = DoorHeight,
            }.Assign(plan, new SeededRandom(Seed));
            CurrentPlan = plan;
            StatusMessage = $"Generated {plan.Cells.Count} cells, {plan.Adjacencies.Count} adjacencies (seed {Seed}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Generation failed: {ex.Message}";
            CurrentPlan = null;
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
            FileTypeChoices = new[] { new FilePickerFileType("FloorPlan JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;

        try
        {
            var spec = FloorPlanSpec.From(this);
            var json = FloorPlanJsonSerializer.Serialize(spec);
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
            FileTypeFilter = new[] { new FilePickerFileType("FloorPlan JSON") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var spec = FloorPlanJsonSerializer.Deserialize(json);
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
        if (owner is null || CurrentPlan is null) return;
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
            ObjExporter.Export(writer, CurrentPlan, WallThickness, WallHeight);
            StatusMessage = $"Exported OBJ → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // Auto-regenerate on any parameter change.
    partial void OnFloorWidthCellsChanged(int value) => Regenerate();
    partial void OnFloorHeightCellsChanged(int value) => Regenerate();
    partial void OnRectangleRoomCountChanged(int value) => Regenerate();
    partial void OnTileSizeChanged(float value) => Regenerate();
    partial void OnSeedChanged(int value) => Regenerate();
    partial void OnIncludeOuterEntranceChanged(bool value) => Regenerate();
    partial void OnDoorWidthChanged(float value) => Regenerate();
    partial void OnDoorHeightChanged(float value) => Regenerate();
}
