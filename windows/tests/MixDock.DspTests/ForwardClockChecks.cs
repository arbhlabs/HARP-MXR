using MixDock.Audio;

internal static class ForwardClockChecks
{
    private const int Rate = 44100, Block = 441;
    public static void Run(Action<bool, string> check, bool soak)
    {
        // This is the render path used by SYNC's "lined up the beat" request. The negative offset
        // comes from a real skip-back duration in the pre-fix application's log.
        var history = new SampleHistory();
        var ring = new SampleRing(Rate * 4);
        var reader = new DeckReader(ring, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { History = history, LookAheadMs = 1000 };
        // Also lets this regression run against the original reader to demonstrate the audible replay.
        typeof(DeckReader).GetProperty("ForwardLive")?.SetValue(reader, true);
        var packet = new float[16 * Block];
        var output = new float[Block * 2];
        double previousHead = -1;
        float previousSample = 0;
        int backwards = 0, repeats = 0;
        RunClock(1400, history, ring, packet, (step, stall) =>
        {
            if (step == 500) reader.EnterHistory(false);
            if (step == 700) reader.Player!.Jump(-.469 * Rate);
            if (step is 900 or 940) reader.LeaveHistory();
            if (stall) return;
            reader.Read(output);
            double head = reader.PlayheadFrame;
            if (head + .01 < previousHead) backwards++;
            previousHead = head;
            float sample = output[^2];
            if (step > 300 && sample > .07f && previousSample > .07f && sample < previousSample - .00007f) repeats++;
            previousSample = sample;
        });
        check(backwards == 0 && repeats == 0, $"Live SYNC correction under burst capture and output stalls has no backward jump or sampled replay ({backwards} jumps, {repeats} replays)");

        // Buffered pause: the reader is frozen while capture goes on. On resume the deck continues from the exact frame it
        // stopped on (nothing dropped, nothing replayed), now simply further behind live; "go live" then skips forward.
        {
            var h2 = new SampleHistory(); var ring2 = new SampleRing(Rate * 34);
            var rd = new DeckReader(ring2, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { History = h2, ForwardLive = true };
            var blk = new float[Block * 2]; var o2 = new float[Block * 2]; long seq = 0;
            void Feed() { for (int i = 0; i < Block; i++) blk[2 * i] = blk[2 * i + 1] = (float)(.12 + seq++ * 0.000000015); lock (h2.Gate) { h2.Write(blk); ring2.Write(blk); } }
            for (int s = 0; s < 400; s++) { Feed(); rd.Read(o2); }
            float before = o2[^2]; double headBefore = rd.PlayheadFrame;
            rd.Frozen = true;
            for (int s = 0; s < 2000; s++) { Feed(); rd.Read(o2); }                 // 20 s paused, app keeps playing
            check(Math.Abs(o2[^2]) < 1e-6f && rd.BufferedMs > 19900, $"Paused live deck is silent and keeps recording ({rd.BufferedMs} ms saved)");
            rd.Frozen = false;
            Feed(); rd.Read(o2);
            double headAfter = rd.PlayheadFrame;
            float expected = before + Block * 0.000000015f;                          // the very next block of audio
            check(Math.Abs(o2[^2] - expected) < 2e-6f && headAfter >= headBefore && headAfter - headBefore < Block * 3,
                $"Resume continues from the paused frame (sample {o2[^2]:F7}, expected {expected:F7}; playhead moved {headAfter - headBefore:F0} frames)");
            int over = rd.Overruns; float prev = o2[^2]; bool mono = true;
            for (int s = 0; s < 600; s++) { Feed(); rd.Read(o2); if (o2[^2] < prev - 1e-7f) mono = false; prev = o2[^2]; }
            check(mono && rd.Overruns == over && rd.BufferedMs > 19500, $"Time-shifted playback keeps its delay, with no drop and no replay ({rd.BufferedMs} ms behind)");
            double headShift = rd.PlayheadFrame;
            rd.LookAheadMs = 0;                                                       // go live
            for (int s = 0; s < 50; s++) { Feed(); rd.Read(o2); }
            check(rd.PlayheadFrame > headShift + Rate * 15 && rd.BufferedMs < 400, $"Go live skips forward to the present ({rd.BufferedMs} ms buffered)");
        }
        if (soak)
        {
            history = new SampleHistory();
            ring = new SampleRing(Rate * 4);
            reader = new DeckReader(ring, new DeckDsp { Parameters = new DeckParameters(Trim: 1) }) { History = history };
            typeof(DeckReader).GetProperty("ForwardLive")?.SetValue(reader, true);
            int maxLead = 0, minLead = int.MaxValue, audible = 0;
            previousHead = -1; previousSample = 0; backwards = repeats = 0;
            RunClock(60000, history, ring, packet, (step, stall) =>
            {
                if (stall) return;
                reader.Read(output);
                double head = reader.PlayheadFrame;
                if (head + .01 < previousHead) backwards++;
                previousHead = head;
                float sample = output[^2];
                if (step > 200 && sample > .07f && previousSample > .07f && sample < previousSample - .00007f) repeats++;
                previousSample = sample;
                if (sample > .07f) audible++;
                if (step <= 1000) return;
                minLead = Math.Min(minLead, reader.BufferedMs);
                maxLead = Math.Max(maxLead, reader.BufferedMs);
            });
            check(backwards == 0 && repeats == 0 && audible > 58000,
                $"10 simulated minutes, four clock offsets and 50–200 ms stalls: {backwards} backward jumps, {repeats} sampled replays, {audible} audible callbacks");
            check(minLead >= 0 && maxLead < 390, $"Soak delay stays bounded ({minLead}–{maxLead} ms)");
        }
    }

    private static void RunClock(int steps, SampleHistory history, SampleRing ring, float[] packet, Action<int, bool> render)
    {
        int[] packets = [3, 1, 4, 2, 3];
        int packetIndex = 0, due = packets[0];
        double ready = 0;
        long sequence = 0;
        for (int step = 0; step < steps; step++)
        {
            double drift = ((step / 15000) % 4) switch { 0 => .005, 1 => -.005, 2 => .001, _ => -.001 };
            ready += Block * (1 + drift);
            if (step >= due)
            {
                int frames = (int)ready;
                ready -= frames;
                for (int i = 0; i < frames; i++)
                {
                    float sample = (float)(.12 + sequence++ * 0.000000015);
                    packet[i * 2] = packet[i * 2 + 1] = sample;
                }
                var span = packet.AsSpan(0, frames * 2);
                lock (history.Gate) { history.Write(span); ring.Write(span); }
                due += packets[++packetIndex % packets.Length];
            }
            int stallStart = step % 1137;
            int duration = ((step / 1137) % 4 + 1) * 5;
            render(step, stallStart >= 714 && stallStart < 714 + duration);
        }
    }
}
