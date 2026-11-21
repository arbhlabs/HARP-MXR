namespace MixDock;

/// <summary>Thread-safe file log at %LOCALAPPDATA%\MixDock\mixdock.log (rolls at 2 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixDock");
    public static readonly string PathName = Path.Combine(Dir, "mixdock.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(PathName, PathName + ".old", true);
                File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break audio */ }
    }
}
