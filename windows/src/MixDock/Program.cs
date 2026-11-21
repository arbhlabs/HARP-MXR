using System.Diagnostics;
using System.Windows.Forms;

namespace MixDock;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest") || args.Contains("--offline") || args.Contains("--probe"))
            return SelfTest.Run(args.Contains("--probe") ? args.SkipWhile(a => a != "--probe").ToArray() : args)
                .GetAwaiter().GetResult();
        if (args.Contains("--unmute-all")) { Audio.AppRedirect.ClearSelf(); Audio.SessionTakeover.RecoverFromJournal(); Audio.AppRedirect.RecoverFromJournal(); return 0; }
        int ui=Array.IndexOf(args,"--ui-check");
        if(ui>=0&&ui+1<args.Length)
        {
            MainForm.UiCheckDirectory=Path.GetFullPath(args[ui+1]);
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(true));
            return MainForm.UiCheckExitCode;
        }
        Log.Write("MixDock start");
        { int si = Array.IndexOf(args, "--shot"); if (si >= 0 && si + 1 < args.Length) MainForm.ShotPath = args[si + 1]; MainForm.ShotAutoMix = args.Contains("--automix"); { int sk = Array.IndexOf(args, "--soak"); if (sk >= 0 && sk + 1 < args.Length) MainForm.ShotSoak = int.Parse(args[sk + 1]); } { int so = Array.IndexOf(args, "--song"); if (so >= 0 && so + 1 < args.Length) MainForm.ShotSong = args[so + 1]; } int ai = Array.IndexOf(args, "--app"); if (ai >= 0 && ai + 1 < args.Length) MainForm.ShotApp = args[ai + 1]; } Audio.AppRedirect.ClearSelf();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Write("ThreadException " + e.Exception);
            // never an exception dialog (a stage tool must not stop on a popup): the error is in the log and the next paint/tick carries on
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write("UnhandledException " + e.ExceptionObject);
            // the process is going down: give every app its own sound back first
            try { Audio.TakeoverController.ReleaseAll(); } catch { }
        };
        // a faulted fire-and-forget task must never take the app down later
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("UnobservedTaskException " + e.Exception); e.SetObserved(); };
        Application.Run(new MainForm(args.Contains("--ui-preview")));
        return 0;
    }
}
