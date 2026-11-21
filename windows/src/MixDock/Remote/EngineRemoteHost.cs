using System.Text.Json.Nodes;
using MixDock.Audio;

namespace MixDock.Remote;

/// <summary>Maps remote messages onto AudioEngine. App list / assignment come from delegates the main form supplies.</summary>
public sealed class EngineRemoteHost : IRemoteHost
{
    private readonly AudioEngine _engine;
    private readonly Func<IReadOnlyList<(string Id, string Name)>> _apps;
    private readonly Action<string, string> _assign;
    private readonly Dictionary<string, string> _names = new() { ["A"] = "", ["B"] = "" };

    public EngineRemoteHost(AudioEngine engine, Func<IReadOnlyList<(string Id, string Name)>> apps, Action<string, string> assign)
    { _engine = engine; _apps = apps; _assign = assign; }

    private DeckDsp Deck(string d) => d == "A" ? _engine.A : _engine.B;

    public void SetDeck(string deck, string param, double v)
    {
        var dsp = Deck(deck); var p = dsp.Parameters;
        dsp.Parameters = param switch
        {
            "low" => p with { LowDb = Math.Clamp(v, -12, 12) },
            "mid" => p with { MidDb = Math.Clamp(v, -12, 12) },
            "high" => p with { HighDb = Math.Clamp(v, -12, 12) },
            "filter" => p with { Filter = Math.Clamp(v, -1, 1) },
            "trim" => p with { Trim = Math.Clamp(v, 0, 1) },
            "mute" => p with { Mute = v > 0.5 },
            _ => p
        };
    }

    public void SetMaster(string param, double v)
    {
        if (param == "cross") _engine.Mixer.Crossfader = v;
        else if (param == "master") _engine.Mixer.Master = (float)v;
    }

    public IReadOnlyList<(string Id, string Name)> ListApps() => _apps();

    public void AssignApp(string deck, string id)
    {
        _assign(deck, id);
        _names[deck] = _apps().FirstOrDefault(a => a.Id == id).Name ?? "";
    }

    public (float A, float B) Peaks() => (_engine.A.Peak, _engine.B.Peak);

    public JsonObject GetState()
    {
        JsonObject D(string d)
        {
            var p = Deck(d).Parameters;
            return new JsonObject { ["app"] = _names[d], ["low"] = p.LowDb, ["mid"] = p.MidDb, ["high"] = p.HighDb,
                ["filter"] = p.Filter, ["trim"] = p.Trim, ["mute"] = p.Mute };
        }
        return new JsonObject { ["A"] = D("A"), ["B"] = D("B"), ["cross"] = _engine.Mixer.Crossfader, ["master"] = _engine.Mixer.Master };
    }
}
