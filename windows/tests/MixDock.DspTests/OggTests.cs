using MixDock.Audio;

static class OggTests
{
    static float[] ReadSeconds(ITrackReader r, double seconds)
    {
        int want = (int)(seconds * r.WaveFormat.SampleRate) * r.WaveFormat.Channels;
        var buf = new float[want]; int got = 0, n;
        while (got < want && (n = r.Read(buf.AsSpan(got, want - got))) > 0) got += n;
        return buf.AsSpan(0, got).ToArray();
    }

    // zero crossings of the first channel -> Hz
    static double Pitch(float[] interleaved, int channels, int rate)
    {
        int frames = interleaved.Length / channels, cross = 0;
        for (int i = 1; i < frames; i++)
            if (interleaved[(i - 1) * channels] < 0 != interleaved[i * channels] < 0) cross++;
        return cross / 2.0 / (frames / (double)rate);
    }

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static void Run(Action<bool, string> assert)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mixdock-ogg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var (file, codec, ch, rate) in new[]
            {
                ("tone-stereo.ogg", OggCodec.Vorbis, 2, 44100), ("tone-mono.ogg", OggCodec.Vorbis, 1, 22050),
                ("tone-stereo.opus", OggCodec.Opus, 2, 48000), ("tone-mono.opus", OggCodec.Opus, 1, 48000),
            })
            {
                string label = file;
                assert(TrackReaderFactory.Sniff(Fixture(file)) == codec, $"{label}: detected as {codec} from the Ogg header");
                // content decides, not the extension
                string odd = Path.Combine(tmp, Path.GetFileNameWithoutExtension(file) + ".dat" + (codec == OggCodec.Opus ? "1" : "2"));
                File.Copy(Fixture(file), odd, true);
                using (var r = TrackReaderFactory.Open(odd))
                    assert(r.WaveFormat.Channels == ch, $"{label}: opens by content under a wrong extension");

                using var rd = TrackReaderFactory.Open(Fixture(file));
                assert(rd.WaveFormat.Channels == ch && rd.WaveFormat.SampleRate == rate, $"{label}: {ch} channel(s) at {rate} Hz");
                assert(Math.Abs(rd.TotalTime.TotalMilliseconds - 2000) <= 50, $"{label}: duration {rd.TotalTime.TotalMilliseconds:0} ms is within 50 ms of 2000");
                var all = ReadSeconds(rd, 3);
                double decoded = all.Length / ch / (double)rate * 1000;
                assert(Math.Abs(decoded - 2000) <= 50, $"{label}: decodes {decoded:0} ms of audio");
                assert(all.All(float.IsFinite) && all.Max(Math.Abs) > .05f, $"{label}: decoded samples are audible and finite");

                rd.CurrentTime = TimeSpan.FromSeconds(1.5);
                assert(Math.Abs(rd.CurrentTime.TotalMilliseconds - 1500) <= 50, $"{label}: seek to 1.5 s reports {rd.CurrentTime.TotalMilliseconds:0} ms");
                double late = Pitch(ReadSeconds(rd, .3), ch, rate);
                assert(Math.Abs(late - 880) < 40, $"{label}: after seeking to 1.5 s the 880 Hz half plays ({late:0} Hz)");
                rd.CurrentTime = TimeSpan.FromSeconds(1.1);
                double justAfter = Pitch(ReadSeconds(rd, .2), ch, rate);
                assert(Math.Abs(justAfter - 880) < 40, $"{label}: seek to 1.1 s lands in the 880 Hz half, not a second early ({justAfter:0} Hz)");
                rd.CurrentTime = TimeSpan.FromSeconds(.9);
                double justBefore = Pitch(ReadSeconds(rd, .08), ch, rate);
                assert(Math.Abs(justBefore - 440) < 40, $"{label}: seek to 0.9 s lands in the 440 Hz half ({justBefore:0} Hz)");
                rd.CurrentTime = TimeSpan.FromSeconds(.4);
                double early = Pitch(ReadSeconds(rd, .3), ch, rate);
                assert(Math.Abs(early - 440) < 25, $"{label}: seeking back to 0.4 s gives the 440 Hz half ({early:0} Hz)");
                rd.CurrentTime = TimeSpan.Zero;
                assert(ReadSeconds(rd, 3).Length / ch / (double)rate * 1000 is > 1950 and < 2050, $"{label}: seek to start replays the whole song");
            }

            // damaged and unsupported files fail cleanly
            foreach (var file in new[] { "tone-stereo.ogg", "tone-stereo.opus" })
            {
                var bytes = File.ReadAllBytes(Fixture(file));
                string cut = Path.Combine(tmp, "cut-" + file); File.WriteAllBytes(cut, bytes[..40]);
                assert(Fails(cut, TrackProblem.Damaged, out var m1) && m1.Contains("damaged"), $"{file}: truncated header -> \"{m1}\"");

                var rot = (byte[])bytes.Clone();
                for (int i = 60; i < rot.Length; i += 7) rot[i] ^= 0xA5;
                string rotten = Path.Combine(tmp, "rot-" + file); File.WriteAllBytes(rotten, rot);
                bool clean = true;
                try { using var r = TrackReaderFactory.Open(rotten); ReadSeconds(r, 3); try { r.CurrentTime = TimeSpan.FromSeconds(1); ReadSeconds(r, 1); } catch (Exception) { clean = false; } }
                catch (TrackOpenException) { }
                catch (Exception) { clean = false; }
                assert(clean, $"{file}: scrambled body gives a clean error or silence, never a stray exception");
            }
            string junk = Path.Combine(tmp, "noise.ogg"); File.WriteAllBytes(junk, Enumerable.Range(0, 4000).Select(i => (byte)(i * 31 + 7)).ToArray());
            assert(Fails(junk, TrackProblem.Damaged, out var mj) && mj == "Can't play noise.ogg: the file is damaged", $"random bytes named .ogg -> \"{mj}\"");
            string odd2 = Path.Combine(tmp, "song.xyz"); File.WriteAllBytes(odd2, new byte[2000]);
            assert(Fails(odd2, TrackProblem.Unsupported, out var mu) && mu == "Can't play song.xyz: this file type isn't supported", $"unknown type -> \"{mu}\"");
            assert(Fails(Path.Combine(tmp, "gone.ogg"), TrackProblem.Missing, out var mg) && mg.Contains("gone.ogg"), $"missing file -> \"{mg}\"");
            assert(TrackReaderFactory.Sniff("OggS\0\x02\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\x01\x1e\x7fFLAC"u8.ToArray()) == OggCodec.Other, "Ogg FLAC is recognised as another codec");
            assert(TrackReaderFactory.OpenDialogFilter.Contains("*.ogg") && TrackReaderFactory.OpenDialogFilter.Contains("*.opus"), "open dialog offers .ogg and .opus");

            // other formats still go through the Windows decoder
            string wav = Path.Combine(tmp, "t.wav");
            using (var w = new NAudio.Wave.WaveFileWriter(wav, new NAudio.Wave.WaveFormat(44100, 16, 2)))
                w.WriteSamples(new short[44100 * 2], 0, 44100 * 2);
            using (var r = TrackReaderFactory.Open(wav))
                assert(r.WaveFormat.Channels == 2 && Math.Abs(r.TotalTime.TotalMilliseconds - 1000) < 20, "wav still opens through the generic reader");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    static bool Fails(string path, TrackProblem expected, out string message)
    {
        try { using var r = TrackReaderFactory.Open(path); message = "opened"; return false; }
        catch (TrackOpenException ex) { message = ex.Message; return ex.Problem == expected; }
        catch (Exception ex) { message = "escaped " + ex.GetType().Name; return false; }
    }
}
