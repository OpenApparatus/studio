using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenApparatus.Studio.Services;
using OpenApparatus.Studio.Themes;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Overlay control that subscribes to <see cref="Toasts.Default"/> and
/// renders one card per active toast in the bottom-right corner of the
/// window. Each card fades + slides in on Add and fades out on Dismiss.
/// </summary>
public sealed class ToastsLayer : ContentControl
{
    static readonly Color SuccessColor = Color.FromRgb(0x28, 0xA0, 0x5A);
    static readonly Color WarningColor = Color.FromRgb(0xE6, 0x9C, 0x18);
    static readonly Color ErrorColor   = Color.FromRgb(0xC8, 0x28, 0x28);
    // Info severity tracks the AR HUD accent so it stays in sync with
    // the brand emerald — resolved per-toast so it picks up the active
    // theme variant.
    static Color InfoColor => Tokens.AccentColor;

    readonly StackPanel _stack;

    public ToastsLayer()
    {
        IsHitTestVisible = true;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment   = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 18, 24);
        _stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Content = _stack;

        Toasts.Default.Active.CollectionChanged += OnToastsChanged;
    }

    void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null) break;
                foreach (var item in e.NewItems)
                    if (item is Toast t) _stack.Children.Add(BuildCard(t));
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is null) break;
                foreach (var item in e.OldItems)
                    if (item is Toast t) RemoveCard(t);
                break;
            case NotifyCollectionChangedAction.Reset:
                _stack.Children.Clear();
                break;
        }
    }

    Control BuildCard(Toast toast)
    {
        var col = toast.Severity switch
        {
            ToastSeverity.Success => SuccessColor,
            ToastSeverity.Warning => WarningColor,
            ToastSeverity.Error   => ErrorColor,
            _ => InfoColor,
        };

        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(col),
        };
        var msg = new TextBlock
        {
            Text = toast.Message,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Tokens.TextOnDark,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(dot);
        stack.Children.Add(msg);

        if (toast.HasUndo)
        {
            var undoBtn = new Button
            {
                Content = "Undo",
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Foreground = Tokens.TextOnDark,
                Padding = new Thickness(8, 3),
                MinHeight = 0,
                CornerRadius = new Avalonia.CornerRadius(3),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            undoBtn.Click += (_, _) =>
            {
                toast.UndoAction?.Invoke();
                Toasts.Default.Dismiss(toast);
            };
            stack.Children.Add(undoBtn);
        }

        var card = new Border
        {
            Background = Tokens.SurfaceInk,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Thickness(14, 10),
            BoxShadow = BoxShadows.Parse("0 4 14 0 #44000000"),
            Tag = toast,
            Child = stack,
            // Initial state for the slide-in animation.
            Opacity = 0,
            RenderTransform = new TranslateTransform(20, 0),
            Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220) },
            },
        };
        // Animate in on the next layout cycle.
        Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            card.RenderTransform = new TranslateTransform(0, 0);
        }, Avalonia.Threading.DispatcherPriority.Render);

        return card;
    }

    void RemoveCard(Toast toast)
    {
        for (int i = _stack.Children.Count - 1; i >= 0; i--)
        {
            if (_stack.Children[i] is Border b && ReferenceEquals(b.Tag, toast))
            {
                // Fade out, then remove from tree.
                b.Opacity = 0;
                b.RenderTransform = new TranslateTransform(20, 0);
                var captured = b;
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    if (_stack.Children.Contains(captured))
                        _stack.Children.Remove(captured);
                }, TimeSpan.FromMilliseconds(220));
                return;
            }
        }
    }
}
