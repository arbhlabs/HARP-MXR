using System.Text.Json;
namespace MixDock.Settings;

public sealed class HotCueStore
{
    private Dictionary<string,double[]> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    public HotCueStore(string? path=null) { _path=path??Path.Combine(Path.GetDirectoryName(AppConfig.FilePath)!,"hotcues.json"); }
    public void Load()
    {
        try { if(File.Exists(_path))_tracks=new(JsonSerializer.Deserialize<Dictionary<string,double[]>>(File.ReadAllText(_path))??new(),StringComparer.OrdinalIgnoreCase); }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){Log.Write("Hot cues: "+ex.Message);}
    }
    public double? Get(string path,int slot)
    {
        if(slot<0||slot>=16||!_tracks.TryGetValue(Path.GetFullPath(path),out var cues)||cues==null||slot>=cues.Length)return null;
        double value=cues[slot];return double.IsFinite(value)&&value>=0&&value<TimeSpan.MaxValue.TotalSeconds?value:null;
    }
    public void Set(string path,int slot,double seconds)
    {
        if(slot<0||slot>=16||!double.IsFinite(seconds)||seconds<0||seconds>=TimeSpan.MaxValue.TotalSeconds)return;
        path=Path.GetFullPath(path);
        if(!_tracks.TryGetValue(path,out var cues)||cues==null||cues.Length!=16){cues=Enumerable.Repeat(-1d,16).ToArray();_tracks[path]=cues;}
        cues[slot]=seconds;
    }
    public void Save()
    {
        try {Directory.CreateDirectory(Path.GetDirectoryName(_path)!);File.WriteAllText(_path+".tmp",JsonSerializer.Serialize(_tracks));File.Move(_path+".tmp",_path,true);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Log.Write("Hot cues could not save: "+ex.Message);}
    }
}
