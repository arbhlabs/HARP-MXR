namespace MixDock.Audio;

/// <summary>RBJ Audio EQ Cookbook biquad. One instance PER channel and filter stage.</summary>
public sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _z1, _z2;
    public void Reset() => _z1 = _z2 = 0;
    public float Process(float input)
    {
        var y = _b0 * input + _z1;
        _z1 = _b1 * input - _a1 * y + _z2;
        _z2 = _b2 * input - _a2 * y;
        return double.IsFinite(y) ? (float)Math.Clamp(y, -8, 8) : 0;
    }
    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
        _a1 = a1 / a0; _a2 = a2 / a0;
    }
    public void Peak(int hz, double freq, double q, double gainDb)
    {
        double w = 2 * Math.PI * Math.Clamp(freq, 5, hz * .49) / hz;
        double sn = Math.Sin(w), cs = Math.Cos(w), alpha = sn / (2 * q);
        double a = Math.Pow(10, gainDb / 40);
        Set(1 + alpha * a, -2 * cs, 1 - alpha * a,
            1 + alpha / a, -2 * cs, 1 - alpha / a);
    }
    public void Shelf(int hz, double freq, double gainDb, bool high)
    {
        double w = 2 * Math.PI * Math.Clamp(freq, 5, hz * .49) / hz;
        double a = Math.Pow(10, gainDb / 40), cs = Math.Cos(w);
        double alpha = Math.Sin(w) / 2 * Math.Sqrt(2); // slope S=1
        double r = 2 * Math.Sqrt(a) * alpha;
        if (!high)
            Set(a * ((a + 1) - (a - 1) * cs + r),
                2 * a * ((a - 1) - (a + 1) * cs),
                a * ((a + 1) - (a - 1) * cs - r),
                (a + 1) + (a - 1) * cs + r,
                -2 * ((a - 1) + (a + 1) * cs),
                (a + 1) + (a - 1) * cs - r);
        else
            Set(a * ((a + 1) + (a - 1) * cs + r),
                -2 * a * ((a - 1) + (a + 1) * cs),
                a * ((a + 1) + (a - 1) * cs - r),
                (a + 1) - (a - 1) * cs + r,
                2 * ((a - 1) - (a + 1) * cs),
                (a + 1) - (a - 1) * cs - r);
    }
    public void Pass(int hz, double freq, bool high)
    {
        double w = 2 * Math.PI * Math.Clamp(freq, 5, hz * .49) / hz;
        double cs = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.70710678);
        double b = high ? (1 + cs) / 2 : (1 - cs) / 2;
        Set(b, high ? -(1 + cs) : (1 - cs), b,
            1 + alpha, -2 * cs, 1 - alpha);
    }
}
