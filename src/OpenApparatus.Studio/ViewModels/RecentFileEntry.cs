using System.IO;

namespace OpenApparatus.Studio.ViewModels;

/// <summary>Display row for a recent project file — name + folder for
/// the welcome screen / File menu. Wraps a path so callers only need to
/// bind one record per row.</summary>
public sealed record RecentFileEntry(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder
    {
        get
        {
            try { return System.IO.Path.GetDirectoryName(Path) ?? Path; }
            catch { return Path; }
        }
    }
    public string FullPath => Path;
}
