using System.Buffers.Binary;

namespace MixDock.Linux;

/// <summary>Xbox controller through the Linux joystick API (/dev/input/js*), no extra libraries.
/// Axis and button numbers follow the in-kernel xpad driver (USB / wireless adapter) and xpadneo (Bluetooth).</summary>
public sealed class Joystick : IDisposable
{
    public const int AxisLX = 0, AxisLY = 1, AxisLT = 2, AxisRX = 3, AxisRY = 4, AxisRT = 5, AxisDX = 6, AxisDY = 7;
    public const int BtnA = 0, BtnB = 1, BtnX = 2, BtnY = 3, BtnLB = 4, BtnRB = 5, BtnView = 6, BtnMenu = 7, BtnGuide = 8, BtnL3 = 9, BtnR3 = 10;

    private readonly short[] _axes = new short[16];
    private readonly bool[] _buttons = new bool[24];
    private readonly Queue<(int button, bool down)> _events = new();
    private Thread? _thread;
    private volatile bool _run = true;
    public string? Device { get; private set; }
    public bool Connected => Device != null;

    /// <summary>One js_event: u32 time, s16 value, u8 type (1 button, 2 axis, 0x80 init), u8 number.</summary>
    public static (short value, int type, int number) Parse(ReadOnlySpan<byte> ev) =>
        (BinaryPrimitives.ReadInt16LittleEndian(ev[4..]), ev[6], ev[7]);

    public void Apply(ReadOnlySpan<byte> ev)
    {
        var (value, type, number) = Parse(ev);
        bool init = (type & 0x80) != 0;
        lock (_events)
        {
            if ((type & 0x7f) == 2 && number < _axes.Length) _axes[number] = value;
            else if ((type & 0x7f) == 1 && number < _buttons.Length)
            {
                bool down = value != 0;
                if (_buttons[number] != down && !init) _events.Enqueue((number, down));
                _buttons[number] = down;
            }
        }
    }

    public short Axis(int n) { lock (_events) return n < _axes.Length ? _axes[n] : (short)0; }
    public bool Button(int n) { lock (_events) return n < _buttons.Length && _buttons[n]; }

    /// <summary>Trigger 0..1 (the kernel reports -32767 released .. 32767 fully pressed).</summary>
    public double Trigger(int axis)
    {
        double t = Math.Clamp((Axis(axis) + 32767) / 65534.0, 0, 1);
        return t < 0.08 ? 0 : t;
    }

    /// <summary>Stick through the Windows app's radial deadzone; Y is flipped so up is positive, as in XInput.</summary>
    public (double X, double Y) Stick(int axisX, int axisY) =>
        MixDock.Platform.Gamepad.Stick(Axis(axisX), (short)Math.Clamp(-Axis(axisY), short.MinValue, short.MaxValue));

    public bool TryDequeue(out (int button, bool down) ev) { lock (_events) return _events.TryDequeue(out ev); }

    public void Start() { _thread = new Thread(Loop) { IsBackground = true, Name = "mixdock-js" }; _thread.Start(); }

    private void Loop()
    {
        var ev = new byte[8];
        while (_run)
        {
            string? dev = Enumerable.Range(0, 8).Select(i => $"/dev/input/js{i}").FirstOrDefault(File.Exists);
            if (dev == null) { Thread.Sleep(2000); continue; }
            try
            {
                using var fs = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                Device = dev;
                while (_run)
                {
                    int got = 0;
                    while (got < 8) { int r = fs.Read(ev, got, 8 - got); if (r <= 0) throw new IOException("closed"); got += r; }
                    Apply(ev);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Device = null;
            lock (_events) { Array.Clear(_axes); Array.Clear(_buttons); _events.Clear(); }
            Thread.Sleep(2000);
        }
    }

    public void Dispose() => _run = false;
}
