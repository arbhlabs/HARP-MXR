namespace MixDock;

/// <summary>Where HARP//MXR keeps its settings and logs. The app was called MixDock up to 1.4: on the first start of the
/// renamed app the old folder's files are copied across once. The old folder is left untouched, so an older build still
/// finds its own settings.</summary>
public static class DataFolder
{
    public const string Name = "HARP-MXR", OldName = "MixDock";

    public static string Resolve(Environment.SpecialFolder root)
    {
        string baseDir = Environment.GetFolderPath(root), dir = Path.Combine(baseDir, Name);
        try
        {
            // only the app itself migrates; test runners and tools that share this code never touch the user's folders
            bool isApp = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), Name, StringComparison.OrdinalIgnoreCase);
            string old = Path.Combine(baseDir, OldName);
            if (isApp && Directory.Exists(old) && !File.Exists(Path.Combine(dir, ".migrated")))
            {
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".log.old", StringComparison.OrdinalIgnoreCase)) continue;
                    string to = Path.Combine(dir, Path.GetRelativePath(old, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (!File.Exists(to)) File.Copy(f, to);
                }
                File.WriteAllText(Path.Combine(dir, ".migrated"), $"copied from {OldName} {DateTime.Now:s}");
            }
        }
        catch { /* settings fall back to defaults; never block start-up */ }
        return dir;
    }
}
