using MixDock.Audio;
using Windows.Storage;

namespace MixDock;

public sealed partial class MainForm
{
    private readonly List<string> _recentFiles = [];
    private readonly Dictionary<string, (DateTime Modified, long Bytes, string Artist, TrackAnalysis Analysis)> _libraryAnalysis =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _libraryScan;

    private TrackAnalysis? DeckAnalysis(int deck)
    {
        var source = Source(deck);
        if (!source.IsFile)
            return _memory[deck]?.Current?.AnalysisAt(_mixHost!.PlayheadFrame(deck));
        var wave = _tracks[deck];
        if (wave?.Analysis == null || !string.Equals(wave.Path, source.FilePath, StringComparison.OrdinalIgnoreCase))
            return null;
        // Seeking changes file time without rewinding the absolute input frame counter.
        return wave.Analysis.Shifted(source.History.Written / 44100d - source.Position.TotalSeconds);
    }

    private static async Task<string> ReadArtistAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            return (await file.Properties.GetMusicPropertiesAsync()).Artist.Trim();
        }
        catch (Exception) { return ""; } // Tags are optional, including on removable media.
    }

    private async Task PickNextFromLibraryAsync()
    {
        if (_preview || _closing || _library == null || _autoMix == null || _mixHost == null) return;
        if (_libraryScan != null) { SetMessage("Already comparing library tracks."); return; }
        var paths = _library.TrackPaths();
        if (paths.Length == 0) { SetMessage("Add tracks to the library first."); return; }
        int main = _mixHost.Crossfader < .5 ? 0 : _mixHost.Crossfader > .5 ? 1 : _mixHost.Level(0) >= _mixHost.Level(1) ? 0 : 1;
        var outgoing = DeckAnalysis(main);
        var sec = _mixHost.PlayheadSec(main);
        if (outgoing == null && double.IsFinite(sec))
            outgoing = _autoMix.Map(main).LiveAnalysis(sec, _mixHost.Grid(main), _autoMix.Preferences.Genre);
        if (outgoing == null) { SetMessage("Play a deck so Auto Mix can compare its tempo, key and energy."); return; }

        using var scan = new CancellationTokenSource();
        _libraryScan = scan;
        try
        {
            SetMessage("Comparing library tracks…");
            string? sourcePath = Source(main).FilePath;
            string artist = sourcePath == null ? _memory[main]?.Current?.Match.Track.Metadata?.Artist ?? "" : await ReadArtistAsync(sourcePath);
            var entries = new List<(string Path, string Artist, TrackAnalysis Analysis)>();
            foreach (var path in paths)
            {
                scan.Token.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                try
                {
                    if (!_libraryAnalysis.TryGetValue(path, out var entry) || entry.Modified != file.LastWriteTimeUtc || entry.Bytes != file.Length)
                    {
                        var info = _tracks.FirstOrDefault(w => string.Equals(w?.Path, path, StringComparison.OrdinalIgnoreCase))?.Analysis;
                        info ??= await Task.Run(() => TrackWaveform.Analyze(path, scan.Token).Analysis, scan.Token);
                        if (info == null) continue;
                        entry = (file.LastWriteTimeUtc, file.Length, await ReadArtistAsync(path), info);
                        _libraryAnalysis[path] = entry;
                    }
                    entries.Add((path, entry.Artist, entry.Analysis));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Write($"Library analysis skipped {file.Name}: {ex.Message}");
                }
            }
            scan.Token.ThrowIfCancellationRequested();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var deck in new[] { Source(0), Source(1) })
                if (deck.FilePath is string path) loaded.Add(path);
            var pick = TransitionPlanner.PickNext(outgoing, _autoMix.Preferences, _autoMix.Mixes, entries, _recentFiles, artist, loaded);
            if (pick == null) { SetMessage("No other playable track in the library."); return; }
            _library.SelectTrack(pick);
            SetMessage($"Next pick: {Path.GetFileNameWithoutExtension(pick)} · select LOAD A or LOAD B to use it.");
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested) { }
        finally { _libraryScan = null; }
    }
}
