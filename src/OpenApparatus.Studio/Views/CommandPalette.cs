using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OpenApparatus.Studio.Themes;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Command palette overlay (Ctrl+K). Fuzzy-matches a registry of named
/// commands and runs the chosen one. Keyboard-driven: arrow keys move
/// the highlight, Enter executes, Esc dismisses.
///
/// Built dynamically rather than via XAML so the registry can change
/// based on the current scene state (e.g. "Open recent" subitems).
/// </summary>
public sealed class CommandPalette : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<CommandPalette, bool>(nameof(IsOpen));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    record CommandEntry(string Title, string Category, string? Hotkey, Action Run, Func<bool>? CanRun = null);

    readonly List<CommandEntry> _registry = new();
    List<CommandEntry> _filtered = new();
    int _highlight;

    TextBox? _input;
    StackPanel? _list;
    Border? _root;

    MainWindowViewModel? _vm;

    public CommandPalette()
    {
        IsHitTestVisible = false;
        Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        IsVisible = false;

        IsOpenProperty.Changed.AddClassHandler<CommandPalette>(
            (x, _) => x.OnOpenChanged());

        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as MainWindowViewModel;
            BuildRegistry();
        };
        AttachedToVisualTree += (_, _) => BuildOverlay();

        // Keyboard handling — capture at the tunnel level so Tab/Arrows
        // don't escape into the underlying editor.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    void BuildOverlay()
    {
        if (_root is not null) return;

        _input = new TextBox
        {
            Watermark = "Type a command…",
            Background = Tokens.SurfaceSecondary,
            BorderBrush = Brushes.Transparent,
            FontSize = 15,
            Padding = new Thickness(14, 12),
            CornerRadius = new Avalonia.CornerRadius(0),
        };
        _input.KeyDown += (_, e) =>
        {
            // Enter on input runs top hit; Down moves selection.
            if (e.Key == Key.Enter) { RunHighlighted(); e.Handled = true; }
            else if (e.Key == Key.Down) { Move(+1); e.Handled = true; }
            else if (e.Key == Key.Up)   { Move(-1); e.Handled = true; }
        };
        _input.GetObservable(TextBox.TextProperty).Subscribe(new ActionObserver<string?>(t =>
        {
            ApplyFilter(t ?? "");
        }));

        _list = new StackPanel { Margin = new Thickness(0) };

        var inner = new StackPanel { Spacing = 0 };
        inner.Children.Add(_input);
        inner.Children.Add(new Border
        {
            Height = 1,
            Background = Tokens.SurfacePressed,
        });
        inner.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        });

        _root = new Border
        {
            Background = Tokens.SurfaceRaised,
            BorderBrush = Tokens.SurfacePressed,
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Width = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 100, 0, 0),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 18 40 0 #66000000"),
            Child = inner,
        };
        Content = _root;

        // Click backdrop to dismiss.
        AddHandler(PointerPressedEvent, (sender, e) =>
        {
            if (!IsOpen) return;
            var pos = e.GetPosition(this);
            if (_root is not null)
            {
                var r = _root.Bounds;
                if (pos.X < r.X || pos.X > r.X + r.Width
                 || pos.Y < r.Y || pos.Y > r.Y + r.Height)
                {
                    Close();
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Bubble);
    }

    void OnOpenChanged()
    {
        BuildOverlay();
        IsVisible = IsOpen;
        IsHitTestVisible = IsOpen;
        if (IsOpen)
        {
            BuildRegistry();
            if (_input is not null)
            {
                _input.Text = "";
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _input.Focus());
            }
            ApplyFilter("");
        }
    }

    public void Toggle() => IsOpen = !IsOpen;
    public void Close() => IsOpen = false;

    void BuildRegistry()
    {
        _registry.Clear();
        if (_vm is null) return;

        void Add(string title, string category, string? hotkey, Action run, Func<bool>? canRun = null)
            => _registry.Add(new CommandEntry(title, category, hotkey, run, canRun));

        // File
        Add("New",              "File", "Ctrl+N", () => _vm.NewProjectCommand.Execute(null));
        Add("Open…",            "File", "Ctrl+O", () => _vm.OpenProjectCommand.Execute(GetWindow()));
        Add("Save",             "File", "Ctrl+S", () => _vm.SaveProjectCommand.Execute(GetWindow()));
        Add("Save As…",         "File", "Ctrl+Shift+S", () => _vm.SaveProjectAsCommand.Execute(GetWindow()));
        Add("Export glTF…",     "File", null,     () => _vm.ExportGltfCommand.Execute(GetWindow()));
        Add("Export Spec (JSON)…", "File", null,  () => _vm.ExportJsonCommand.Execute(GetWindow()));

        // Edit
        Add("Undo",             "Edit", "Ctrl+Z", () => _vm.UndoCommand.Execute(null), () => _vm.CanUndo);
        Add("Redo",             "Edit", "Ctrl+Y", () => _vm.RedoCommand.Execute(null), () => _vm.CanRedo);
        Add("Clear selection",  "Edit", "Esc",    () => _vm.ClearSelectionCommand.Execute(null));
        Add("Empty project",    "Edit", null,     () => _vm.ResetAllCommand.Execute(null));
        Add("Auto-layout",      "Edit", null,     () => _vm.RandomFillCommand.Execute(null), () => _vm.IsLayoutMode);

        // View
        Add("Switch to 2D",     "View", null,     () => _vm.SetTopViewCommand.Execute(null));
        Add("Switch to 3D",     "View", null,     () => _vm.Set3DViewCommand.Execute(null));
        Add("Frame selection",  "View", "F",      () => _vm.FrameSelectionCommand.Execute(null));
        Add("Fit",              "View", "Ctrl+1", () => _vm.ResetViewCommand.Execute(null));
        Add("Zoom 100%",        "View", "Ctrl+0", () => _vm.ZoomActualSizeCommand.Execute(null));

        // Mode
        Add("Layout mode",      "Mode", null,     () => _vm.EditMode = MainWindowViewModel.EditModeKind.Layout);
        Add("Objects mode",     "Mode", null,     () => _vm.EditMode = MainWindowViewModel.EditModeKind.Object);

        // Help
        Add("Shortcuts",        "Help", "F1",     () => _vm.ToggleShortcutOverlayCommand.Execute(null));
    }

    Window? GetWindow() => Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Window>(this);

    void ApplyFilter(string query)
    {
        if (_list is null) return;
        _filtered = string.IsNullOrWhiteSpace(query)
            ? _registry.Where(e => e.CanRun?.Invoke() != false).ToList()
            : _registry
                .Where(e => e.CanRun?.Invoke() != false)
                .Select(e => (Entry: e, Score: FuzzyScore(e.Title + " " + e.Category, query)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Entry)
                .ToList();
        _highlight = 0;
        RenderList();
    }

    void RenderList()
    {
        if (_list is null) return;
        _list.Children.Clear();
        for (int i = 0; i < _filtered.Count; i++)
        {
            var e = _filtered[i];
            int idx = i;
            var row = new Border
            {
                Padding = new Thickness(14, 8),
                Background = i == _highlight
                    ? Tokens.SurfaceHover
                    : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            };
            var title = new TextBlock
            {
                Text = e.Title,
                FontSize = 13,
                Foreground = Tokens.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);
            var cat = new TextBlock
            {
                Text = e.Category,
                FontSize = 10.5,
                Foreground = Tokens.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };
            Grid.SetColumn(cat, 1);
            grid.Children.Add(cat);
            if (e.Hotkey is not null)
            {
                var hk = new Border
                {
                    Background = Tokens.SurfacePrimary,
                    BorderBrush = Tokens.SurfacePressed,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Padding = new Thickness(6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = e.Hotkey,
                        FontFamily = new FontFamily("Consolas, 'Courier New', monospace"),
                        FontSize = 10,
                        Foreground = Tokens.TextSecondary,
                    },
                };
                Grid.SetColumn(hk, 2);
                grid.Children.Add(hk);
            }
            row.Child = grid;
            row.PointerEntered += (_, _) => { _highlight = idx; RenderList(); };
            row.PointerPressed += (_, _) => { _highlight = idx; RunHighlighted(); };
            _list.Children.Add(row);
        }
        if (_filtered.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No matching commands",
                Margin = new Thickness(14, 14),
                FontSize = 12,
                Foreground = Tokens.TextMuted,
            });
        }
    }

    void Move(int delta)
    {
        if (_filtered.Count == 0) return;
        _highlight = ((_highlight + delta) % _filtered.Count + _filtered.Count) % _filtered.Count;
        RenderList();
    }

    void RunHighlighted()
    {
        if (_highlight < 0 || _highlight >= _filtered.Count) return;
        var e = _filtered[_highlight];
        Close();
        try { e.Run(); }
        catch { /* command errors surface via toasts; palette stays calm */ }
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (!IsOpen) return;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    /// <summary>Cheap subsequence-match score: 0 if no match, otherwise
    /// rewards adjacent letters + penalises gaps. Good enough for command
    /// palettes; not as sophisticated as fzf.</summary>
    static int FuzzyScore(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 1;
        haystack = haystack.ToLowerInvariant();
        needle = needle.ToLowerInvariant();
        int score = 0, hi = 0, ni = 0, lastHit = -2;
        while (hi < haystack.Length && ni < needle.Length)
        {
            if (haystack[hi] == needle[ni])
            {
                score += hi == lastHit + 1 ? 5 : 1;
                lastHit = hi;
                ni++;
            }
            hi++;
        }
        if (ni < needle.Length) return 0;
        // Bonus for prefix match.
        if (haystack.StartsWith(needle)) score += 10;
        return score;
    }

    sealed class ActionObserver<T> : IObserver<T>
    {
        readonly Action<T> _act;
        public ActionObserver(Action<T> a) => _act = a;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => _act(value);
    }
}
