namespace MixDock.Settings;

/// <summary>Captured pad slices persist next to config.json as small raw float files (one per deck and pad), so banks keep their samples between sessions.</summary>
public static class SliceStore
{
    private const uint Magic = 0x4D58534C;   // "MXSL"
    public static string Dir => Path.Combine(AppConfig.DirectoryPath, "slices");
    public static string FileFor(int deck, int pad) => Path.Combine(Dir, $"d{deck}p{pad:00}.f32");

    public static bool Save(int deck, int pad, float[] stereo, string? dir = null)
    {
        try
        {
            var d = dir ?? Dir; Directory.CreateDirectory(d);
            var path = Path.Combine(d, $"d{deck}p{pad:00}.f32"); var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(Magic); w.Write(stereo.Length / 2);
                var bytes = new byte[stereo.Length * 4]; Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length); w.Write(bytes);
            }
            File.Move(tmp, path, true); return true;
        }
        catch { return false; }
    }

    /// <summary>Returns the interleaved stereo samples and frame count, or null when missing / damaged.</summary>
    public static (float[] Data, int Frames)? Load(int deck, int pad, string? dir = null)
    {
        try
        {
            var path = Path.Combine(dir ?? Dir, $"d{deck}p{pad:00}.f32");
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic) return null;
            int frames = r.ReadInt32();
            if (frames <= 0 || frames > 44100 * 4) return null;
            var bytes = r.ReadBytes(frames * 8);
            if (bytes.Length != frames * 8) return null;
            var data = new float[frames * 2]; Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
            foreach (var v in data) if (!float.IsFinite(v)) return null;
            return (data, frames);
        }
        catch { return null; }
    }
}
