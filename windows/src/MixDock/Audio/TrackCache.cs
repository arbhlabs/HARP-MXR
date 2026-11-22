using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixDock.Audio;

public sealed record TrackMetadata(string Title, string Artist, string Album, double DurationSec)
{
    public bool SameSong(TrackMetadata other) => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(other.Title) &&
        Equal(Title, other.Title) && (string.IsNullOrWhiteSpace(Artist) || string.IsNullOrWhiteSpace(other.Artist) || Equal(Artist, other.Artist));

    internal double Affinity(TrackMetadata? other) => other == null ? 0 :
        (SameSong(other) ? .05 : 0) + (!string.IsNullOrWhiteSpace(Album) && Equal(Album, other.Album) ? .015 : 0);

    private static bool Equal(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record WaveformLevel(int ColumnsPerSlice, WaveColumn[] Columns);

/// <summary>Only derived measurements are accepted by the cache. Its format contains no signal samples.</summary>
public sealed record TrackCacheEntry(Guid Id, TrackMetadata? Metadata, double DurationSec, WaveColumn[] Columns,
    FingerprintPoint[] Fingerprint, TrackAnalysis? Analysis, bool Complete)
{
    public WaveformLevel[] Overview { get; init; } = [];
}

public sealed record TrackMatch(TrackCacheEntry Track, int OffsetTicks, double Confidence, bool Partial)
{
    /// <summary>Add this signed offset to the live capture clock to locate the cached playhead.</summary>
    public double OffsetSec => OffsetTicks / AcousticFingerprint.TicksPerSecond;
}

/// <summary>
/// Local byte-capped cache. Fingerprints are indexed in memory and waveform / analysis files are read
/// on demand. An atomic replacement commits each track, and file timestamps persist the LRU across runs.
/// </summary>
public sealed class TrackCache
{
    private const string Extension = ".hmxc";
    private static readonly byte[] Magic = "HMXRCHE1"u8.ToArray();
    private const int MaxWaveColumns = WaveformAnalyzer.ColumnsPerSecond * 60 * 120;
    private const int MaxFingerprints = MaxWaveColumns * 5;
    private const int MaxAnalysisBytes = 2 * 1024 * 1024;
    private static readonly int[] Resolutions = [5, 25];
    private static readonly JsonSerializerOptions AnalysisJson = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public const long DefaultMaxBytes = 64 * 1024 * 1024;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HARP-MXR", "trackcache");

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Item> _items = new();
    private bool _loaded;

    private sealed class Item(string path, long bytes, long touched, TrackMetadata? metadata, FingerprintPoint[] fingerprint)
    {
        internal readonly string Path = path;
        internal readonly long Bytes = bytes;
        internal readonly TrackMetadata? Metadata = metadata;
        internal readonly FingerprintAlignment Index = new(fingerprint);
        internal long Touched = touched;
        internal WeakReference<TrackCacheEntry>? LastRead;
    }

    public TrackCache(string? directory = null, long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _directory = directory ?? DefaultDirectory;
        _maxBytes = maxBytes;
    }

    public bool Put(TrackCacheEntry entry)
    {
        if (entry.Id == Guid.Empty || entry.Columns.Length > MaxWaveColumns || entry.Fingerprint.Length > MaxFingerprints ||
            !double.IsFinite(entry.DurationSec) || entry.DurationSec <= 0 ||
            entry.Fingerprint.Any(static p => p.Tick < 0 || p.Hash == 0)) return false;
        lock (_gate)
        {
            EnsureLoaded();
            string path = Path.Combine(_directory, entry.Id.ToString("N") + Extension);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] analysis = entry.Analysis == null ? [] : JsonSerializer.SerializeToUtf8Bytes(entry.Analysis, AnalysisJson);
                if (analysis.Length > MaxAnalysisBytes) return false;
                var overview = Resolutions.Select(r => new WaveformLevel(r, Aggregate(entry.Columns, r))).ToArray();
                Directory.CreateDirectory(_directory);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    WriteMetadata(writer, entry.Metadata);
                    writer.Write(entry.DurationSec);
                    writer.Write(entry.Complete);
                    writer.Write(entry.Fingerprint.Length);
                    foreach (var point in entry.Fingerprint) { writer.Write(point.Tick); writer.Write(point.Hash); }
                    WriteWaveform(writer, entry.Columns);
                    foreach (var level in overview) WriteWaveform(writer, level.Columns);
                    writer.Write(analysis.Length);
                    writer.Write(analysis);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                long length = new FileInfo(temp).Length;
                if (length > _maxBytes) return false;
                var now = DateTime.UtcNow.Ticks;
                var item = new Item(path, length, now, entry.Metadata, entry.Fingerprint)
                {
                    LastRead = new(entry with { Overview = overview })
                };
                File.Move(temp, path, overwrite: true);
                _items[entry.Id] = item;
                Touch(item);
                Evict();
                return _items.ContainsKey(entry.Id);
            }
            catch (Exception ex) when (IsCacheFailure(ex)) { return false; }
            finally { DeleteFile(temp); }
        }
    }

    public TrackCacheEntry? TryGet(Guid id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(id, out var item)) return null;
            return ReadEntry(id, item, touch: true);
        }
    }

    public TrackMatch? Find(ReadOnlySpan<FingerprintPoint> query, TrackMetadata? metadata = null, double? liveDurationSec = null)
    {
        KeyValuePair<Guid, Item>[] snapshot;
        lock (_gate)
        {
            EnsureLoaded();
            snapshot = _items.ToArray();
        }
        var ranked = new List<(Guid Id, Item Item, FingerprintAlignment.Hit Hit, double Score)>();
        foreach (var (id, item) in snapshot)
        {
            if (item.Index.Find(query) is not { } hit) continue;
            double score = hit.Confidence * (.65 + .35 * hit.Coverage) + (item.Metadata?.Affinity(metadata) ?? 0);
            ranked.Add((id, item, hit, score));
        }

        lock (_gate)
        {
            foreach (var (id, item, hit, _) in ranked.OrderByDescending(v => v.Score))
            {
                if (!_items.TryGetValue(id, out var current) || current != item || ReadEntry(id, item, touch: true) is not { } entry) continue;
                return ToMatch(entry, hit, metadata, liveDurationSec);
            }
        }
        return null;
    }

    /// <summary>Confirm that a recent window still agrees with an existing playhead alignment.</summary>
    public TrackMatch? Recheck(TrackMatch candidate, ReadOnlySpan<FingerprintPoint> query, TrackMetadata? metadata = null)
    {
        Item item;
        lock (_gate)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(candidate.Track.Id, out var stored)) return null;
            item = stored;
        }
        if (item.Index.Find(query, candidate.OffsetTicks) is not { } hit) return null;
        lock (_gate)
        {
            if (!_items.TryGetValue(candidate.Track.Id, out var current) || current != item) return null;
            if (ReadEntry(candidate.Track.Id, item, touch: true) is not { } entry) return null;
            var result = ToMatch(entry, hit, metadata, null);
            return result with { Partial = candidate.Partial || result.Partial };
        }
    }

    public bool Forget(Guid id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(id, out var item) || !DeleteFile(item.Path)) return false;
            return _items.Remove(id);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            EnsureLoaded();
            foreach (var (id, item) in _items.ToArray())
                if (DeleteFile(item.Path)) _items.Remove(id);
        }
    }

    private static TrackMatch ToMatch(TrackCacheEntry entry, FingerprintAlignment.Hit hit, TrackMetadata? metadata, double? liveDurationSec)
    {
        double duration = liveDurationSec ?? metadata?.DurationSec ?? 0;
        bool durationDiffers = duration > 0 && Math.Abs(entry.DurationSec - duration) > 2;
        bool metadataDiffers = metadata != null && entry.Metadata != null &&
            !string.IsNullOrWhiteSpace(metadata.Title) && !string.IsNullOrWhiteSpace(entry.Metadata.Title) && !entry.Metadata.SameSong(metadata);
        bool unverifiedStart = metadata == null && hit.OffsetTicks > 50;
        return new TrackMatch(entry, hit.OffsetTicks, hit.Confidence,
            !entry.Complete || durationDiffers || metadataDiffers || unverifiedStart || hit.Coverage < .75);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!Directory.Exists(_directory)) return;
            // Load the newest files first. Keep the persistent cache within the configured budget
            // even after importing a cache from a larger budget or interrupting a prior save.
            long retained = 0;
            var files = new DirectoryInfo(_directory).EnumerateFiles("*" + Extension)
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            foreach (var file in files)
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out var id) || id == Guid.Empty) continue;
                if (file.Length > _maxBytes - retained) { DeleteFile(file.FullName); continue; }
                try
                {
                    using var reader = Open(file.FullName);
                    var metadata = ReadMetadata(reader);
                    double duration = reader.ReadDouble();
                    if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Invalid duration");
                    reader.ReadBoolean();
                    var points = ReadFingerprints(reader);
                    _items.Add(id, new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, metadata, points));
                    retained += file.Length;
                }
                catch (Exception ex) when (IsCacheFailure(ex)) { DeleteFile(file.FullName); }
            }
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*" + Extension + ".*.tmp"))
            {
                var parts = file.Name.Split('.');
                if (parts.Length == 4 && Guid.TryParseExact(parts[0], "N", out _) && Guid.TryParseExact(parts[2], "N", out _) &&
                    file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)) DeleteFile(file.FullName);
            }
        }
        catch (Exception ex) when (IsCacheFailure(ex)) { }
    }

    private TrackCacheEntry? ReadEntry(Guid id, Item item, bool touch)
    {
        if (item.LastRead?.TryGetTarget(out var already) == true)
        {
            if (touch) Touch(item);
            return already;
        }
        try
        {
            using var reader = Open(item.Path);
            var metadata = ReadMetadata(reader);
            double duration = reader.ReadDouble();
            bool complete = reader.ReadBoolean();
            var points = ReadFingerprints(reader);
            var columns = ReadWaveform(reader);
            var overview = Resolutions.Select(r => new WaveformLevel(r, ReadWaveform(reader))).ToArray();
            int analysisSize = ReadLength(reader, MaxAnalysisBytes, 1);
            var analysis = analysisSize == 0 ? null : JsonSerializer.Deserialize<TrackAnalysis>(reader.ReadBytes(analysisSize), AnalysisJson);
            var entry = new TrackCacheEntry(id, metadata, duration, columns, points, analysis, complete) { Overview = overview };
            item.LastRead = new(entry);
            if (touch) Touch(item);
            return entry;
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            if (DeleteFile(item.Path)) _items.Remove(id);
            return null;
        }
    }

    private void Evict()
    {
        long size = _items.Values.Sum(i => i.Bytes);
        foreach (var (id, item) in _items.OrderBy(p => p.Value.Touched).ToArray())
        {
            if (size <= _maxBytes) break;
            if (!DeleteFile(item.Path)) continue;
            _items.Remove(id);
            size -= item.Bytes;
        }
    }

    private static void Touch(Item item)
    {
        item.Touched = DateTime.UtcNow.Ticks;
        try { File.SetLastWriteTimeUtc(item.Path, new DateTime(item.Touched, DateTimeKind.Utc)); }
        catch (Exception ex) when (IsCacheFailure(ex)) { }
    }

    private static BinaryReader Open(string path)
    {
        var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Unsupported track cache format");
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    private static void WriteMetadata(BinaryWriter writer, TrackMetadata? metadata)
    {
        writer.Write(metadata != null);
        if (metadata == null) return;
        WriteString(metadata.Title);
        WriteString(metadata.Artist);
        WriteString(metadata.Album);
        writer.Write(double.IsFinite(metadata.DurationSec) ? metadata.DurationSec : 0);
        void WriteString(string? text)
        {
            int length = Math.Min(text?.Length ?? 0, 1024);
            byte[] utf8 = length == 0 ? [] : Encoding.UTF8.GetBytes(text![..length]);
            writer.Write(utf8.Length);
            writer.Write(utf8);
        }
    }

    private static TrackMetadata? ReadMetadata(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        string ReadString() => Encoding.UTF8.GetString(reader.ReadBytes(ReadLength(reader, 4096, 1)));
        return new TrackMetadata(ReadString(), ReadString(), ReadString(), reader.ReadDouble());
    }

    private static int ReadLength(BinaryReader reader, int maxCount, int bytesPerItem)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maxCount || (long)length * bytesPerItem > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid track cache length");
        return length;
    }

    private static FingerprintPoint[] ReadFingerprints(BinaryReader reader)
    {
        var points = new FingerprintPoint[ReadLength(reader, MaxFingerprints, 8)];
        for (int i = 0; i < points.Length; i++)
        {
            int tick = reader.ReadInt32();
            uint hash = reader.ReadUInt32();
            if (tick < 0 || hash == 0) throw new InvalidDataException("Invalid landmark");
            points[i] = new FingerprintPoint(tick, hash);
        }
        return points;
    }

    private static void WriteWaveform(BinaryWriter writer, WaveColumn[] columns)
    {
        writer.Write(columns.Length);
        foreach (var c in columns)
        {
            writer.Write(Level(c.Low)); writer.Write(Level(c.Mid)); writer.Write(Level(c.High)); writer.Write(Level(c.Peak));
            writer.Write(Level(c.Rms)); writer.Write(Level(c.LowRms)); writer.Write(Level(c.MidRms)); writer.Write(Level(c.HighRms));
        }
        static byte Level(float v) => float.IsFinite(v) ? (byte)Math.Round(Math.Clamp(v, 0, 1) * 255) : (byte)0;
    }

    private static WaveColumn[] ReadWaveform(BinaryReader reader)
    {
        var columns = new WaveColumn[ReadLength(reader, MaxWaveColumns, 8)];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new WaveColumn(Level(), Level(), Level(), Level(), Level(), Level(), Level(), Level());
        return columns;
        float Level() => reader.ReadByte() / 255f;
    }

    private static WaveColumn[] Aggregate(WaveColumn[] source, int factor)
    {
        var output = new WaveColumn[(source.Length + factor - 1) / factor];
        for (int i = 0; i < output.Length; i++)
        {
            float low = 0, mid = 0, high = 0, peak = 0;
            double rms2 = 0, low2 = 0, mid2 = 0, high2 = 0;
            int start = i * factor, stop = Math.Min(source.Length, start + factor);
            for (int j = start; j < stop; j++)
            {
                var c = source[j];
                low = Math.Max(low, c.Low); mid = Math.Max(mid, c.Mid); high = Math.Max(high, c.High); peak = Math.Max(peak, c.Peak);
                rms2 += c.Rms * c.Rms; low2 += c.LowRms * c.LowRms; mid2 += c.MidRms * c.MidRms; high2 += c.HighRms * c.HighRms;
            }
            double n = stop - start;
            output[i] = new WaveColumn(low, mid, high, peak, (float)Math.Sqrt(rms2 / n), (float)Math.Sqrt(low2 / n),
                (float)Math.Sqrt(mid2 / n), (float)Math.Sqrt(high2 / n));
        }
        return output;
    }

    private static bool DeleteFile(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (IsCacheFailure(ex)) { return false; }
    }

    private static bool IsCacheFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;
}
