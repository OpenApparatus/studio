using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// First-run welcome screen — shown over an empty editor surface when
/// no project file is open and the scene has no rooms. Surfaces three
/// primary actions (Start a new scene, Open a file, browse Recents) and
/// a hint at the keyboard shortcuts overlay (F1) for new users. Hides
/// itself as soon as the user does anything that creates state.
/// </summary>
public sealed class WelcomePanel : ContentControl
{
    MainWindowViewModel? _vm;

    public WelcomePanel()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.RefreshRecentFiles();
            }
            Build();
        };
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ShowWelcomePanel)
                          or nameof(MainWindowViewModel.HasNoRooms)
                          or nameof(MainWindowViewModel.HasProjectFilePath))
        {
            IsVisible = _vm?.ShowWelcomePanel == true;
            if (IsVisible) Build();
        }
    }

    Button? _primaryAction;

    void Build()
    {
        if (_vm is null) return;
        IsVisible = _vm.ShowWelcomePanel;
        if (!IsVisible) return;
        _primaryAction = null;

        // Vector-only "hero" mark — three nested rounded squares forming
        // a stylised floor plan. Cheap, on-brand, no external assets.
        var hero = new Canvas { Width = 96, Height = 96 };
        for (int i = 0; i < 3; i++)
        {
            var rect = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 92 - i * 24, Height = 92 - i * 24,
                StrokeThickness = 1.6,
                Stroke = new SolidColorBrush(Color.FromArgb((byte)(180 - i * 35), 0x1F, 0x6F, 0xEB)),
                RadiusX = 7, RadiusY = 7,
                Fill = i == 0 ? new SolidColorBrush(Color.FromArgb(20, 0x1F, 0x6F, 0xEB)) : null,
            };
            Canvas.SetLeft(rect, 2 + i * 12);
            Canvas.SetTop(rect, 2 + i * 12);
            hero.Children.Add(rect);
        }

        // Time-of-day greeting (a small touch but signals "the app
        // pays attention to context"). UTC hour is fine — we're going
        // for warmth, not strict locale hour boundaries.
        int hour = DateTime.Now.Hour;
        string greet = hour switch
        {
            < 5  => "Working late",
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            < 22 => "Good evening",
            _    => "Working late",
        };
        var greeting = new TextBlock
        {
            Text = greet,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 2),
        };

        var title = new TextBlock
        {
            Text = "OpenApparatus Studio",
            FontSize = 22, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var subtitle = new TextBlock
        {
            Text = "Author multi-room navigation environments — rooms, walls, openings, objects.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Margin = new Thickness(0, 0, 0, 24),
        };

        // Three large primary action buttons.
        var actionRow = new UniformGrid
        {
            Columns = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 28),
        };
        actionRow.Children.Add(MakeAction("New scene", "Start blank — drag tiles, press R.",
            "Plus", () => _vm.NewProjectCommand.Execute(null), primary: true));
        actionRow.Children.Add(MakeAction("Open scene", "Load an .oapp project file.",
            "Folder", () => _vm.OpenProjectCommand.Execute(GetWindow())));
        actionRow.Children.Add(MakeAction("Keyboard help", "Browse every shortcut (F1).",
            "Help", () => _vm.ToggleShortcutOverlayCommand.Execute(null)));

        // Recent files list.
        var recents = new StackPanel { Spacing = 4 };
        if (_vm.RecentFiles.Count == 0)
        {
            recents.Children.Add(new TextBlock
            {
                Text = "No recent scenes yet — your saved projects will appear here.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
        else
        {
            recents.Children.Add(new TextBlock
            {
                Text = "Recent",
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
                Margin = new Thickness(2, 0, 0, 6),
            });
            foreach (var entry in _vm.RecentFiles)
            {
                var path = entry.FullPath;
                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 8),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEC)),
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Margin = new Thickness(0, 0, 0, 4),
                };
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var leftStack = new StackPanel();
                leftStack.Children.Add(new TextBlock
                {
                    Text = entry.Name,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
                });
                leftStack.Children.Add(new TextBlock
                {
                    Text = entry.Folder,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                Grid.SetColumn(leftStack, 0);
                rowGrid.Children.Add(leftStack);
                btn.Content = rowGrid;
                btn.Click += (_, _) => _vm.OpenProjectFromPath(path);
                recents.Children.Add(btn);
            }
        }

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(14),
            Padding = new Thickness(36, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 620,
            BoxShadow = BoxShadows.Parse("0 12 36 0 #44000000"),
            // Entrance animation: fade in + slide up. Set initial state
            // here, animate after layout via the dispatcher post below.
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, 12),
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(280),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
                new Avalonia.Animation.TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(320),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            },
            Child = new StackPanel
            {
                Children =
                {
                    new ContentControl { Content = hero, HorizontalAlignment = HorizontalAlignment.Center },
                    greeting,
                    title,
                    subtitle,
                    actionRow,
                    recents,
                    new TextBlock
                    {
                        Text = "Press F1 anytime · Ctrl+K opens the command palette",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xBB)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 16, 0, 0),
                    },
                    BuildSkipLink(),
                },
            },
        };

        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            Child = card,
        };

        Content = backdrop;

        // Run the entrance animation + auto-focus once the card has
        // landed in the visual tree. Both posted at Render priority so
        // they happen on the next frame after layout.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            card.RenderTransform = new TranslateTransform(0, 0);
            _primaryAction?.Focus();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    Control MakeAction(string title, string subtitle, string iconSymbol, Action onClick, bool primary = false)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 12),
            Margin = new Thickness(6, 0),
            CornerRadius = new Avalonia.CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = primary
                ? new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = primary
                ? new SolidColorBrush(Color.FromRgb(0x1C, 0x5F, 0xC7))
                : new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEC)),
            // Primary action accepts Enter — same pattern as a default
            // dialog button. Combined with auto-focus on Build it means
            // the user can hit Enter immediately to start a new scene.
            IsDefault = primary,
        };
        Avalonia.Automation.AutomationProperties.SetName(btn, $"{title}: {subtitle}");
        if (primary) _primaryAction = btn;
        var sp = new StackPanel
        {
            Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
        };
        sp.Children.Add(new Icon
        {
            Symbol = iconSymbol, Size = 22,
            Foreground = primary
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
        });
        sp.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = primary
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = primary
                ? new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160,
        });
        btn.Content = sp;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>"Skip" link rendered as a borderless centred button so the
    /// user can dismiss the welcome screen without picking an action.</summary>
    Control BuildSkipLink()
    {
        var btn = new Button
        {
            Content = "Skip — open the empty editor",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
            FontSize = 11,
            Padding = new Thickness(4, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        btn.Click += (_, _) => _vm?.DismissWelcomeCommand.Execute(null);
        return btn;
    }

    Window? GetWindow() => Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Window>(this);
}
