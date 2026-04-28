using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenApparatus.Studio.Views;

public partial class Png2DExportDialog : Window
{
    public bool Confirmed { get; private set; }

    public bool Ribbons       { get; private set; }
    public bool RoomLabels    { get; private set; }
    public bool RoomDims      { get; private set; }
    public bool FloorArea     { get; private set; }
    public bool OpeningSizes  { get; private set; }
    public bool ObjectDist    { get; private set; }
    public bool DoorAngles    { get; private set; }
    public bool DoorDist      { get; private set; }

    public Png2DExportDialog()
    {
        InitializeComponent();
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void Configure(
        bool ribbons, bool roomLabels,
        bool roomDims, bool floorArea, bool openingSizes,
        bool objectDist, bool doorAngles, bool doorDist)
    {
        Set("ChkRibbons",    ribbons);
        Set("ChkRoomLabels", roomLabels);
        Set("ChkRoomDims",   roomDims);
        Set("ChkFloorArea",  floorArea);
        Set("ChkOpenings",   openingSizes);
        Set("ChkObjectDist", objectDist);
        Set("ChkDoorAngles", doorAngles);
        Set("ChkDoorDist",   doorDist);
    }

    void Set(string name, bool v)
    {
        var cb = this.FindControl<CheckBox>(name);
        if (cb != null) cb.IsChecked = v;
    }

    bool Get(string name) => this.FindControl<CheckBox>(name)?.IsChecked == true;

    void SetAll(bool v)
    {
        foreach (var n in new[] {
            "ChkRibbons", "ChkRoomLabels", "ChkRoomDims", "ChkFloorArea",
            "ChkOpenings", "ChkObjectDist", "ChkDoorAngles", "ChkDoorDist" })
            Set(n, v);
    }

    void OnAllOn(object? sender, Avalonia.Interactivity.RoutedEventArgs e)  => SetAll(true);
    void OnAllOff(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetAll(false);

    void OnMatchCurrent(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Re-applies whatever Configure was called with — held in InitialState
        // so the user can revert after toggling things in the dialog.
        if (_initial is { } s)
            Configure(s.Ribbons, s.RoomLabels, s.RoomDims, s.FloorArea,
                      s.OpeningSizes, s.ObjectDist, s.DoorAngles, s.DoorDist);
    }

    record InitialState(
        bool Ribbons, bool RoomLabels, bool RoomDims, bool FloorArea,
        bool OpeningSizes, bool ObjectDist, bool DoorAngles, bool DoorDist);

    InitialState? _initial;

    public void RememberInitial(
        bool ribbons, bool roomLabels,
        bool roomDims, bool floorArea, bool openingSizes,
        bool objectDist, bool doorAngles, bool doorDist)
    {
        _initial = new InitialState(
            ribbons, roomLabels, roomDims, floorArea,
            openingSizes, objectDist, doorAngles, doorDist);
    }

    void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Ribbons      = Get("ChkRibbons");
        RoomLabels   = Get("ChkRoomLabels");
        RoomDims     = Get("ChkRoomDims");
        FloorArea    = Get("ChkFloorArea");
        OpeningSizes = Get("ChkOpenings");
        ObjectDist   = Get("ChkObjectDist");
        DoorAngles   = Get("ChkDoorAngles");
        DoorDist     = Get("ChkDoorDist");
        Confirmed = true;
        Close();
    }

    void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
