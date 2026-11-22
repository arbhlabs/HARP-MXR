namespace MixDock.Ui;

// Time-based ballistics keep the same release and hold times across repaint rates.
internal sealed class MeterEnvelope
{
    private long _last, _holdUntil;
    public float Level { get; private set; }
    public float Peak { get; private set; }

    public void Update(float amplitude, long now)
    {
        float input = !float.IsFinite(amplitude) || amplitude <= .001f
            ? 0 : Math.Clamp(1 + MathF.Log10(amplitude) / 3, 0, 1);
        Level = Math.Max(input, Level - Math.Max(0, now - _last) / 750f);
        if (input >= Peak)
        {
            Peak = input;
            _holdUntil = now + 700;
        }
        else if (now > _holdUntil)
            Peak = Math.Max(input, Peak - (now - Math.Max(_last, _holdUntil)) / 1200f);
        _last = now;
    }

    public bool IsHeldSegment(int index, int count) =>
        Peak > 0 && index == (int)Math.Ceiling(Peak * count) - 1;
}
