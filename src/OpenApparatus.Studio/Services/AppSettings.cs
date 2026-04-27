using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Per-user app settings persisted to %APPDATA%/OpenApparatus/Studio/
/// (or the equivalent platform path). Covers window geometry,
/// recent-file list, and theme preferences. Designed to fail silently
/// if the file is missing or malformed — settings should never block
/// app startup.
/// </summary>
public sealed class AppSettings
{
    public double WindowX  { get; set; } = double.NaN;
    public double WindowY  { get; set; } = double.NaN;
    public double WindowWidth  { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool   WindowMaximized { get; set; }
    public string ThemeVariant { get; set; } = "Light"; // "Light" | "Dark"
    public List<string> RecentFiles { get; set; } = new();
    public string? LastOpenedFile { get; set; }
    public string? LastExportFolder { get; set; }

    public const int MaxRecent = 8;

    static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenApparatus", "Studio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings LoadOrDefault()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, s_options) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, s_options));
        }
        catch { /* settings persistence is best-effort */ }
    }

    public void RecordRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > MaxRecent) RecentFiles.RemoveAt(RecentFiles.Count - 1);
        LastOpenedFile = path;
        Save();
    }

    public IReadOnlyList<string> ExistingRecentFiles
        => RecentFiles.Where(File.Exists).ToList();
}
