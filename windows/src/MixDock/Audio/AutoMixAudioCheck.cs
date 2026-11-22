using System.Text.Json;
using NAudio.Wave;

namespace MixDock.Audio;

/// <summary>Offline artifact check from two decoded files through the production DSP and mix bus.</summary>
internal static class AutoMixAudioCheck
{
    private const int Rate = 44100, Frames = 882, FirstHz = 1000, SecondHz = 1600;

    internal static async Task RunAsync(string directory, Action<bool,string> check)
    {
        Directory.CreateDirectory(directory);
        string a = Path.Combine(directory, "automix-a-1000hz.wav");
        string b = Path.Combine(directory, "automix-b-1600hz.wav");
        WriteTrack(a, FirstHz, 22, false);
        WriteTrack(b, SecondHz, 40, true);
        await using var engine = new AudioEngine();
        await engine.DeckA.LoadFileAsync(a);
        await engine.DeckB.LoadFileAsync(b);
        engine.Mixer.Master = .8f;
        engine.Mixer.Crossfader = 0;
        var host = new EngineMixHost(engine);
        var mix = new AutoMix(host) { Enabled = true };
        var block = new float[Frames*2];

        async Task Render(int blocks = 1)
        {
            for (int i=0; i<blocks; i++)
            {
                // Run at decoder speed, never silently pass a test with an empty ring.
                long deadline = Environment.TickCount64+3000;
                while ((engine.ReaderA.BufferedMs < 50 && !engine.DeckA.Paused
                    || engine.ReaderB.BufferedMs < 50 && !engine.DeckB.Paused)
                    && Environment.TickCount64 < deadline) await Task.Delay(2);
                engine.Mixer.Read(block);
                mix.Tick(1000.0*Frames/Rate);
            }
        }
        await Render(25);
        double firstA = ToneAmplitude(block, FirstHz), firstB = ToneAmplitude(block, SecondHz);
        check(firstA > .03 && firstB < firstA*.015, "Auto Mix start renders deck A alone");
        check(!mix.Fading, "Auto Mix never starts on a timer");
        mix.MixNow();
        bool started = false;
        for (int i=0; i<400 && !(started && !mix.Fading); i++) { await Render(); started |= mix.Fading; }
        await Render(10);
        double lastA = ToneAmplitude(block, FirstHz), lastB = ToneAmplitude(block, SecondHz);
        check(started && engine.Mixer.Crossfader > .99 && lastB > .03 && lastA < lastB*.05, "MIX NOW (no beat grid: echo out) finishes on decoded deck B");

        string equal = Path.Combine(directory, "automix-b-equal-1600hz.wav");
        WriteTrack(equal, SecondHz, 22, false);
        await host.LoadFileAsync(0, equal, mix);
        check(engine.DeckA.AutoMixCued && engine.DeckA.Paused, "A file loaded on the off-air deck waits cued for the next transition");
        mix.Enabled = false;
        check(engine.DeckA.Paused && !engine.DeckA.AutoMixCued, "Disabling Auto Mix relinquishes the held cue");
        check(engine.OutputId==null && engine.CueId==null, "Auto Mix audio gate opened zero WASAPI outputs");
        var proof = new { sampleRate=Rate, framesPerBlock=Frames, firstHz=FirstHz, secondHz=SecondHz,
            initialToneA=firstA, initialToneB=firstB, finalToneA=lastA, finalToneB=lastB };
        File.WriteAllText(Path.Combine(directory,"automix-audio.json"),
            JsonSerializer.Serialize(proof, new JsonSerializerOptions {WriteIndented=true}));
    }

    private static void WriteTrack(string path, int hz, int seconds, bool gap)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(Rate,16,1));
        var chunk = new short[Rate];
        for (int i=0;i<Rate;i++) chunk[i]=(short)(3000*Math.Sin(2*Math.PI*hz*i/Rate));
        var zeros = new short[Rate];
        for (int second=0; second<seconds; second++)
            writer.WriteSamples(gap && second>=10 && second<15 ? zeros : chunk, 0, Rate);
    }

    private static double ToneAmplitude(float[] data, int hz)
    {
        double sin=0, cos=0;
        for (int i=0; i<Frames; i++)
        {
            double phase=2*Math.PI*hz*i/Rate;
            sin += data[i*2]*Math.Sin(phase);
            cos += data[i*2]*Math.Cos(phase);
        }
        return 2*Math.Sqrt(sin*sin+cos*cos)/Frames;
    }
}
