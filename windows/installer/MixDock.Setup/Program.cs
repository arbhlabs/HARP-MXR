using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--extract")
        {
            try{Extract(Path.GetFullPath(args[1]));return 0;}
            catch(Exception ex){File.WriteAllText(Path.GetFullPath(args[1])+".error.txt",ex.ToString());return 1;}
        }
        ApplicationConfiguration.Initialize();
        using var form=new Form{Text="HARP//MXR 1.3.3 · Setup",ClientSize=new Size(520,285),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,StartPosition=FormStartPosition.CenterScreen,BackColor=Color.FromArgb(16,18,20),ForeColor=Color.FromArgb(236,240,248),Font=new Font("Segoe UI",10)};
        var title=new Label{Text="MIXDOCK",Font=new Font("Segoe UI",23,FontStyle.Bold),ForeColor=Color.FromArgb(70,216,227),Location=new Point(24,20),Size=new Size(470,44)};
        var text=new Label{Text="DJ console for Windows apps and files.\n\nInstalls for your account. No administrator rights or audio drivers. Your tracks and settings stay in place.\n\nThe app will stay closed until you open its shortcut.",Location=new Point(26,80),Size=new Size(465,140)};
        var install=new Button{Text="Install HARP//MXR",Location=new Point(284,235),Size=new Size(145,32),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(34,37,40)};
        var close=new Button{Text="Close",Location=new Point(435,235),Size=new Size(65,32),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(34,37,40)};close.Click+=(_,_)=>form.Close();
        form.Controls.AddRange([title,text,install,close]);
        try{form.Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);}catch{}
        install.Click+=async(_,_)=>
        {
            install.Enabled=false;close.Enabled=false;text.Text="Installing HARP//MXR for your account…";
            string temp=Path.Combine(Path.GetTempPath(),"MixDock-Setup-"+Guid.NewGuid().ToString("N"));
            try
            {
                await Task.Run(()=>Extract(temp));
                var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=temp};
                start.Environment.Remove("PSModulePath"); // Let Windows PowerShell resolve its own built-in modules.
                foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(temp,"install.ps1"),"-Quiet"})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)??throw new IOException("Could not start the per-user installer.");
                var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();await output;
                if(process.ExitCode!=0)throw new IOException(await error);
                text.Text="Installed.\n\nOpen the HARP//MXR desktop or Start menu shortcut when you are ready. TEST_ME.md is in the installed folder.\n\nAudio devices and the system default output were left untouched.";
                install.Text="Installed";
            }
            catch(Exception ex){text.Text="Installation could not finish.\n\n"+ex.Message;install.Enabled=true;}
            finally{close.Enabled=true;try{if(Directory.Exists(temp))Directory.Delete(temp,true);}catch{}}
        };
        Application.Run(form);return 0;
    }
    static void Extract(string destination)
    {
        // Test and normal installation use exactly the same embedded payload and integrity check.
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new IOException("Extraction folder must be empty.");
        Directory.CreateDirectory(destination);
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")??throw new IOException("Missing installer payload.");
        using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        var allowed=new HashSet<string>(new[]{"MixDock.exe","install.ps1","uninstall.ps1","TEST_ME.md"},StringComparer.Ordinal);
        foreach(var item in zip.Entries)
        {
            if(!allowed.Remove(item.FullName))throw new IOException("Unexpected installer payload entry.");
            item.ExtractToFile(Path.Combine(destination,item.FullName),false);
        }
        if(allowed.Count!=0)throw new IOException("Installer payload is incomplete.");
        using var hashStream=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.sha256")??throw new IOException("Missing integrity manifest.");
        using var reader=new StreamReader(hashStream);string expected=reader.ReadToEnd().Trim();
        using var file=File.OpenRead(Path.Combine(destination,"MixDock.exe"));
        if(!Convert.ToHexString(SHA256.HashData(file)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("Installer payload integrity check failed.");
    }
}
