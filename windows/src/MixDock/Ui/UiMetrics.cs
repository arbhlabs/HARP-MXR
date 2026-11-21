namespace MixDock.Ui;
internal interface IConsoleScale { float ScaleFactor { get; } }
internal static class UiMetrics
{
    public static float Scale(Control c)
    {
        for(Control? p=c;p!=null;p=p.Parent)if(p is IConsoleScale canvas)return canvas.ScaleFactor;
        return c.DeviceDpi/96f;
    }
}
