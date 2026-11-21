using System.Text;
using MixDock.Audio;
using MixDock.Linux;

const string Usage = """
MixDock Lite for Linux - two-deck terminal mixer with Xbox controller support.

  mixdock <trackA> [trackB]     load files (any format ffmpeg reads) and start the mixer
  mixdock --doctor              show which audio player, ffmpeg and controller were found
  mixdock --selftest [out.wav]  render a test mix without an audio device (CI); optional WAV output
  mixdock --help

Keys: Tab focus A/B · Space play/pause · c cue · Left/Right seek · Up/Down deck volume · z/x crossfader
      m centre crossfader · 1/2/3 kill low/mid/high · f/v filter · r reset deck · +/- master · q quit
""";

if (args.Length > 0 && args[0] is "--help" or "-h") { Console.Write(Usage); return 0; }
if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run(args.Length > 1 ? args[1] : null);
if (args.Length > 0 && args[0] == "--doctor") return Doctor();
return new App(args).Run();

static int Doctor()
{
    foreach (var (exe, _) in AudioOut.Candidates) Console.WriteLine($"{exe,-8} {AudioOut.FindOnPath(exe) ?? "not found"}");
    Console.WriteLine($"{"ffmpeg",-8} {AudioOut.FindOnPath("ffmpeg") ?? "not found (needed to load tracks)"}");
    var js = Enumerable.Range(0, 8).Select(i => $"/dev/input/js{i}").Where(File.Exists).ToList();
    Console.WriteLine($"{"joystick",-8} {(js.Count > 0 ? string.Join(", ", js) : "none (plug in the controller; xpad or xpadneo driver)")}");
    return 0;
}

sealed class App(string[] files)
{
    private readonly Mixer _mix = new();
    private readonly Joystick _js = new();
    private readonly double[] _filterLatch = new double[2];
    private int _focus;
    private string _message = "";
    private bool _quit;

    private Deck Focus => _focus == 0 ? _mix.A : _mix.B;
    private Deck DeckAt(int i) => i == 0 ? _mix.A : _mix.B;

    public int Run()
    {
        if (AudioOut.FindOnPath("ffmpeg") == null && files.Length > 0)
            _message = "ffmpeg not found: install it to load tracks (sudo apt install ffmpeg).";
        for (int i = 0; i < Math.Min(2, files.Length); i++) LoadAsync(DeckAt(i), files[i]);
        using var audio = new AudioOut(_mix);
        if (!audio.Start()) { Console.Error.WriteLine(audio.Error); return 2; }
        _js.Start();
        Console.Write("\x1b[?25l\x1b[2J");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _quit = true; };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!_quit)
            {
                double dt = Math.Clamp(clock.Elapsed.TotalSeconds, 0.001, 0.1); clock.Restart();
                while (Console.KeyAvailable) Key(Console.ReadKey(true));
                Pad(dt);
                Draw(audio);
                Thread.Sleep(16);
            }
        }
        finally { _js.Dispose(); Console.Write("\x1b[?25h\x1b[0m\n"); }
        return 0;
    }

    private void LoadAsync(Deck d, string path)
    {
        d.Status = "Decoding...";
        Task.Run(() =>
        {
            try { d.Load(Path.GetFileName(path), TrackDecoder.Decode(path)); }
            catch (Exception e) { d.Status = "Load failed: " + e.Message; }
        });
    }

    private void Edit(Func<DeckParameters, DeckParameters> f) => Focus.Update(f);

    private void Key(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Tab: _focus = 1 - _focus; return;
            case ConsoleKey.Spacebar: Focus.TogglePlay(); return;
            case ConsoleKey.LeftArrow: Focus.Seek(-4); return;
            case ConsoleKey.RightArrow: Focus.Seek(4); return;
            case ConsoleKey.UpArrow: Edit(p => p with { Trim = Math.Min(1, p.Trim + 0.05) }); return;
            case ConsoleKey.DownArrow: Edit(p => p with { Trim = Math.Max(0, p.Trim - 0.05) }); return;
        }
        switch (char.ToLowerInvariant(k.KeyChar))
        {
            case 'q': _quit = true; break;
            case 'c': Focus.Cue(); break;
            case 'z': _mix.Crossfader -= 0.05; break;
            case 'x': _mix.Crossfader += 0.05; break;
            case 'm': _mix.Crossfader = 0.5; break;
            case '1': Edit(p => p with { LowKill = !p.LowKill }); break;
            case '2': Edit(p => p with { MidKill = !p.MidKill }); break;
            case '3': Edit(p => p with { HighKill = !p.HighKill }); break;
            case 'f': _filterLatch[_focus] = Math.Min(1, _filterLatch[_focus] + 0.1); break;
            case 'v': _filterLatch[_focus] = Math.Max(-1, _filterLatch[_focus] - 0.1); break;
            case 'r': _filterLatch[_focus] = 0; Edit(p => new DeckParameters(Trim: p.Trim)); break;
            case '+' or '=': _mix.Master += 0.05; break;
            case '-': _mix.Master -= 0.05; break;
        }
    }

    /// <summary>Controller layout (see README): LB/RB focus, A play, B cue, X bass kill, Y reset, LT/RT crossfader,
    /// left stick up/down filter (springs back), right stick up/down deck volume, D-pad seek and master, L3 centre fader.</summary>
    private void Pad(double dt)
    {
        while (_js.TryDequeue(out var ev))
        {
            if (!ev.down) continue;
            switch (ev.button)
            {
                case Joystick.BtnLB: _focus = 0; break;
                case Joystick.BtnRB: _focus = 1; break;
                case Joystick.BtnA: Focus.TogglePlay(); break;
                case Joystick.BtnB: Focus.Cue(); break;
                case Joystick.BtnX: Edit(p => p with { LowKill = !p.LowKill }); break;
                case Joystick.BtnY: _filterLatch[_focus] = 0; Edit(p => new DeckParameters(Trim: p.Trim)); break;
                case Joystick.BtnL3: _mix.Crossfader = 0.5; break;
                case Joystick.BtnView or Joystick.BtnMenu when _js.Button(Joystick.BtnView) && _js.Button(Joystick.BtnMenu):
                    _mix.A.Playing = _mix.B.Playing = false; _message = "Panic: both decks stopped."; break;
            }
        }
        if (!_js.Connected) { Edit(p => p with { Filter = _filterLatch[_focus] }); return; }
        _mix.Crossfader += (_js.Trigger(Joystick.AxisRT) - _js.Trigger(Joystick.AxisLT)) * dt;   // full press crosses in ~1 s
        var (_, ly) = _js.Stick(Joystick.AxisLX, Joystick.AxisLY);
        var (_, ry) = _js.Stick(Joystick.AxisRX, Joystick.AxisRY);
        for (int i = 0; i < 2; i++)
        {
            double f = Math.Clamp(_filterLatch[i] + (i == _focus ? ly : 0), -1, 1);
            DeckAt(i).Update(p => p with { Filter = f });
        }
        if (ry != 0) Edit(p => p with { Trim = Math.Clamp(p.Trim + ry * 0.6 * dt, 0, 1) });
        short dx = _js.Axis(Joystick.AxisDX), dy = _js.Axis(Joystick.AxisDY);
        if (dx != 0) Focus.Seek(Math.Sign(dx) * 8 * dt);
        if (dy != 0) _mix.Master -= Math.Sign(dy) * 0.4 * dt;
    }

    private static string Bar(double v, int width, char fill = '#')
    {
        int n = (int)Math.Round(Math.Clamp(v, 0, 1) * width);
        return new string(fill, n) + new string('.', width - n);
    }

    private static string Time(long frames) => TimeSpan.FromSeconds(frames / (double)DeckDsp.SampleRate).ToString(@"m\:ss");

    private void Draw(AudioOut audio)
    {
        var sb = new StringBuilder("\x1b[H");
        void Line(string s) => sb.Append(s).Append("\x1b[K\n");
        Line("\x1b[1mMixDock Lite\x1b[0m  (Linux, file decks)   audio: " + audio.Backend
             + "   controller: " + (_js.Connected ? _js.Device : "not connected"));
        Line("");
        for (int i = 0; i < 2; i++)
        {
            var d = DeckAt(i); var p = d.Dsp.Parameters;
            string col = i == 0 ? "\x1b[36m" : "\x1b[33m";
            string mark = i == _focus ? "\x1b[7m FOCUS \x1b[0m" : "       ";
            long len = d.Frames, pos = d.Position;
            Line($"{col}\x1b[1mDECK {d.Name}\x1b[0m {mark} {(d.Playing ? "PLAYING" : "paused ")}  {Truncate(d.Title, 48)}");
            Line($"  {Time(pos)} / {Time(len)}  [{Bar(len > 0 ? pos / (double)len : 0, 40, '=')}]  {d.Status}");
            Line($"  vol [{Bar(p.Trim, 10)}]  filter {(p.Filter < -0.02 ? "LP" : p.Filter > 0.02 ? "HP" : "--")} {p.Filter,5:+0.00;-0.00}  "
                 + $"kills {(p.LowKill ? "LOW " : "low ")}{(p.MidKill ? "MID " : "mid ")}{(p.HighKill ? "HIGH" : "high")}  level [{Bar(d.Dsp.Peak, 12, '|')}]");
            d.Dsp.DecayPeak();
            Line("");
        }
        int xf = (int)Math.Round(_mix.Crossfader * 30);
        Line($"crossfader  A [{new string('-', xf)}O{new string('-', 30 - xf)}] B     master [{Bar(_mix.Master, 10)}]  out [{Bar(_mix.PeakOut, 12, '|')}]");
        Line("");
        Line("\x1b[2mPad: LB/RB focus · A play · B cue · X bass kill · Y reset · LT/RT crossfader · L-stick filter · R-stick volume");
        Line("     D-pad left/right seek, up/down master · L3 centre fader · View+Menu stop all\x1b[0m");
        Line("\x1b[2mKeys: Tab focus · Space play · c cue · arrows seek/volume · z/x fader · 1/2/3 kills · f/v filter · r reset · q quit\x1b[0m");
        Line(audio.Error ?? _message);
        Console.Write(sb.ToString());
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

static class SelfTest
{
    public static int Run(string? wavPath)
    {
        int passed = 0;
        void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); Console.WriteLine("PASS " + what); passed++; }
        try
        {
            var (ea, eb) = Mixer.EqualPower(0.5);
            Check(Math.Abs(ea * ea + eb * eb - 1) < 1e-9, "equal-power crossfader keeps power at centre");

            var mix = new Mixer();
            mix.A.Load("sine 220", Sine(220, 4)); mix.B.Load("sine 330", Sine(330, 4));
            mix.B.TogglePlay(); mix.Crossfader = 0;
            var block = new float[AudioOut.BlockFrames * 2];
            for (int i = 0; i < 20; i++) mix.Render(block);
            Check(block.All(v => Math.Abs(v) < 1e-4), "crossfader on A silences a playing B");

            mix.A.TogglePlay();
            var all = new List<float>();
            int blocks = 3 * DeckDsp.SampleRate / AudioOut.BlockFrames;
            for (int i = 0; i < blocks; i++)
            {
                mix.Crossfader = i / (double)blocks;
                mix.A.Update(p => p with { Filter = -i / (double)blocks });
                if (i == blocks / 2) mix.B.Update(p => p with { LowKill = true });
                mix.Render(block); all.AddRange(block);
            }
            Check(all.All(float.IsFinite), "mix has no NaN or infinity");
            Check(all.Max(Math.Abs) <= 0.9f, $"limiter keeps the peak under -1 dBFS (peak {all.Max(Math.Abs):0.000})");
            double rms = Math.Sqrt(all.Average(v => (double)v * v));
            Check(rms > 0.02, $"mix carries signal (rms {rms:0.000})");

            var js = new Joystick();
            js.Apply([0, 0, 0, 0, 1, 0, 1, Joystick.BtnA]);
            Check(js.Button(Joystick.BtnA) && js.TryDequeue(out var ev) && ev is { button: Joystick.BtnA, down: true }, "joystick button event parsed");
            js.Apply([0, 0, 0, 0, 0xFF, 0x7F, 2, Joystick.AxisLY]);
            Check(js.Stick(Joystick.AxisLX, Joystick.AxisLY).Y < -0.99, "joystick stick down reads negative (XInput convention)");
            js.Apply([0, 0, 0, 0, 1, 0, 0x81, Joystick.BtnB]);
            Check(js.Button(Joystick.BtnB) && !js.TryDequeue(out _), "initial-state events do not fire presses");

            if (wavPath != null) { WriteWav(wavPath, all); Console.WriteLine("wrote " + wavPath); }
            Console.WriteLine($"{passed} self-tests passed");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    private static float[] Sine(double hz, double seconds)
    {
        int n = (int)(seconds * DeckDsp.SampleRate); var pcm = new float[n * 2];
        for (int i = 0; i < n; i++) pcm[2 * i] = pcm[2 * i + 1] = (float)(0.8 * Math.Sin(2 * Math.PI * hz * i / DeckDsp.SampleRate));
        return pcm;
    }

    private static void WriteWav(string path, List<float> samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(DeckDsp.SampleRate); w.Write(DeckDsp.SampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
    }
}
