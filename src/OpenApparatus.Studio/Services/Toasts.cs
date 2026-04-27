using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Lightweight in-app toast / snackbar service. Toasts are small
/// dismissable cards that appear at the bottom-right of the window for
/// transient confirmations (with optional Undo) and lightweight
/// success / warning / error messaging.
///
/// Replaces the previous "single status-bar message that auto-clears in
/// 6 s" pattern, which buried important feedback in the chrome.
///
/// Use:
///     Toasts.Default.Show("Saved scene");
///     Toasts.Default.Show("Reset 3 rooms", undo: () =&gt; vm.Undo());
///     Toasts.Default.ShowError("Couldn't load file: {msg}");
/// </summary>
public sealed class Toasts
{
    public static readonly Toasts Default = new();

    public ObservableCollection<Toast> Active { get; } = new();

    public void Show(string message, ToastSeverity severity = ToastSeverity.Info,
                     Action? undo = null, TimeSpan? duration = null)
    {
        var toast = new Toast
        {
            Message = message,
            Severity = severity,
            UndoAction = undo,
            CreatedAt = DateTime.UtcNow,
        };
        Dispatcher.UIThread.Post(() =>
        {
            Active.Add(toast);
            // Auto-dismiss after the toast's lifetime (default 5 s for
            // info/success, 8 s when an undo is offered, 0 = sticky for
            // errors so the user notices them).
            var life = duration ?? severity switch
            {
                ToastSeverity.Error => TimeSpan.FromSeconds(12),
                _ when undo is not null => TimeSpan.FromSeconds(8),
                _ => TimeSpan.FromSeconds(5),
            };
            DispatcherTimer.RunOnce(() =>
            {
                if (toast.Dismissed) return;
                Dismiss(toast);
            }, life);
        });
    }

    public void ShowSuccess(string message, Action? undo = null)
        => Show(message, ToastSeverity.Success, undo);
    public void ShowWarning(string message)
        => Show(message, ToastSeverity.Warning);
    public void ShowError(string message)
        => Show(message, ToastSeverity.Error);

    public void Dismiss(Toast toast)
    {
        toast.Dismissed = true;
        Dispatcher.UIThread.Post(() => Active.Remove(toast));
    }
}

public enum ToastSeverity { Info, Success, Warning, Error }

public sealed class Toast
{
    public string Message { get; init; } = "";
    public ToastSeverity Severity { get; init; }
    public Action? UndoAction { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool Dismissed { get; set; }

    public bool HasUndo => UndoAction is not null;
}
