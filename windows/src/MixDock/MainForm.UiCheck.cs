using System.Reflection;
using System.Text.Json;
using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;
using MixDock.Ui;

namespace MixDock;

public sealed partial class MainForm
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DpiRect {public int Left,Top,Right,Bottom;}
    public static string? UiCheckDirectory;
    public static int UiCheckExitCode=1;
    private async Task RunUiCheckAsync()
    {
        var checks=new List<object>();int failures=0;
        void Check(bool ok,string name){checks.Add(new{name,ok});if(!ok)failures++;}
        void Mouse(Control c,string method,int x,int y,MouseButtons button=MouseButtons.Left,int delta=0)
            =>typeof(Control).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,[new MouseEventArgs(button,1,x,y,delta)]);
        void Key(Control c,Keys key)=>typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,[new KeyEventArgs(key)]);
        var directory=UiCheckDirectory!;Directory.CreateDirectory(directory);
        try
        {
            _engine.Mixer.Mute=true;
            Check(_preview&&_engine.OutputId==null&&_engine.CueId==null,"Offline preview: no output or cue player");
            foreach(int d in new[]{0,1})
            {
                var p=Panel(d);var dsp=d==0?_engine.A:_engine.B;
                foreach(var (kill,read) in new (StudioButton,Func<bool>)[]{(p.LowKill,()=>dsp.Parameters.LowKill),(p.MidKill,()=>dsp.Parameters.MidKill),(p.HighKill,()=>dsp.Parameters.HighKill)})
                {kill.PerformClick();Check(read(),$"Deck {d}: EQ kill reaches DSP");kill.PerformClick();Check(!read(),$"Deck {d}: EQ kill restores");}
                Key(p.Low,Keys.Up);Check(dsp.Parameters.LowDb>0,$"Deck {d}: keyboard EQ reaches DSP");Key(p.Low,Keys.Home);
                Mouse(p.Filter,"OnMouseWheel",10,10,delta:120);Check(dsp.Parameters.Filter>0,$"Deck {d}: wheel filter reaches DSP");
                Key(p.Filter,Keys.Home);Check(Math.Abs(dsp.Parameters.Filter)<1e-9,$"Deck {d}: filter Home resets");
                Mouse(p.Filter,"OnMouseDown",25,25);Mouse(p.Filter,"OnMouseMove",25,0);Mouse(p.Filter,"OnMouseUp",25,0);
                Check(dsp.Parameters.Filter>0,$"Deck {d}: drag filter reaches DSP");Key(p.Filter,Keys.Home);
                Key(p.Trim,Keys.Down);Check(dsp.Parameters.Trim<.85,$"Deck {d}: trim key reaches DSP");Key(p.Trim,Keys.Home);
                ((StudioButton)p.MuteControl).PerformClick();Check(dsp.Parameters.Mute,$"Deck {d}: mute reaches DSP");((StudioButton)p.MuteControl).PerformClick();
                _cues[d].PerformClick();Check(_engine.CueId==null,$"Deck {d}: cue preview opens no device");
            }
            _cross.Value=0;Check(_engine.Mixer.Crossfader==0,"Crossfader A endpoint reaches mixer");
            Key(_cross,Keys.Home);Check(Math.Abs(_engine.Mixer.Crossfader-.5)<.001,"Crossfader Home centers");
            _cross.Value=100;Check(_engine.Mixer.Crossfader==1,"Crossfader B endpoint reaches mixer");_cross.Value=50;
            _master.Value=65;Check(Math.Abs(_engine.Mixer.Master-.65)<.001,"Master reaches mixer");_master.Value=88;
            _panic.PerformClick();Check(!_engine.Mixer.Mute,"Panic restores");_panic.PerformClick();Check(_engine.Mixer.Mute,"Panic kills");
            _monitor.VolumeControl.Value=37;Check(Math.Abs(_engine.Cue.Volume-.37)<.001,"Cue volume reaches cue mixer");
            _monitor.BlendControl.Value=41;Check(Math.Abs(_engine.Cue.Blend-.41)<.001,"Cue blend reaches cue mixer");
            _monitor.VolumeControl.Value=60;_monitor.BlendControl.Value=0;
            UpdateMouseFx(0,.95,.9,true);Check(Fx(0).State.Amount>0,"XY applies effect to DSP");
            UpdateMouseFx(0,.95,.9,false);Check(Fx(0).State.Amount==0,"XY release returns dry");
            foreach(var mode in new[]{"SAMPLE","ONE-SHOT","CHORD","DRUM","LOOP","BEAT JUMP","HOT CUE"})
            {SelectPadMode(0,mode);Check(_padModes[0]==mode,"Pad mode reachable: "+mode);}

            using(var settings=CreateDiagnostics())
            {
                _profiles!.SelectedItem=LatencyProfile.Low;
                typeof(ComboBox).GetMethod("OnSelectionChangeCommitted",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(_profiles,[EventArgs.Empty]);
                Check(_engine.Profile==LatencyProfile.Low&&_config.LatencyMs==60,"Settings profile selection updates engine and persisted configuration model");
                Check(_diagnostics!.Text.Contains("Callback timings are not end-to-end"),"Settings distinguishes callback cost from output latency");
            }
            _diagnostics=null;_profiles=null;_engine.Profile=LatencyProfile.Standard;
            float MixLevel(double cross,bool cue=false)
            {
                var ra=new SampleRing(88200);var rb=new SampleRing(88200);var input=Enumerable.Repeat(.01f,22050).ToArray();ra.Write(input);
                var mix=new MasterMixer(new DeckReader(ra,new DeckDsp()),new DeckReader(rb,new DeckDsp())){Crossfader=cross,Master=1,CueA=cue};
                var output=new float[2048];mix.Read(output);
                if(cue){Check(mix.CueRingA.Count>0,"Cue tap exists with deck cut by crossfader");var cm=new CueMixer(mix){Volume=1,Blend=0};cm.Read(output);}
                return output.Select(Math.Abs).Max();
            }
            float aLevel=MixLevel(0),centerLevel=MixLevel(.5);
            Check(aLevel>0&&Math.Abs(centerLevel/aLevel-Math.Sqrt(.5))<.01,"Rendered crossfader center is -3 dB / equal power");
            Check(MixLevel(1)<.000001,"Rendered crossfader B endpoint silences A");
            Check(MixLevel(1,true)>0,"Headphone cue survives crossfader cut");
            Check(RollAction(0,0).EndsWith("0.125"),"Loop fractions use invariant parsing under German locale");
            var wav=Path.Combine(directory,"offline-fixture.wav");
            using(var stream=File.Create(wav))using(var writer=new BinaryWriter(stream))
            {
                const int sr=44100,frames=sr*20;writer.Write("RIFF"u8);writer.Write(36+frames*4);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(sr);writer.Write(sr*4);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(frames*4);
                for(int n=0;n<frames;n++){double t=n/(double)sr,beat=t%.5,env=Math.Exp(-beat*18);short sample=(short)(8500*(env*Math.Sin(2*Math.PI*85*t)+.27*Math.Sin(2*Math.PI*900*t)+.13*Math.Sin(2*Math.PI*8500*t)));writer.Write(sample);writer.Write(sample);}
            }
            await _engine.DeckA.LoadFileAsync(wav);await _engine.DeckB.LoadFileAsync(wav);await Task.Delay(180);
            Check(_engine.DeckA.IsFile&&_engine.DeckA.Duration.TotalSeconds>19,"Real file decoding without a device");
            _engine.DeckA.SeekTo(TimeSpan.FromSeconds(4));_engine.DeckB.SeekTo(TimeSpan.FromSeconds(8));await Task.Delay(100);
            Check(_engine.DeckA.Position.TotalSeconds>=4,"Real file seek");
            _plays[0].PerformClick();Check(_engine.DeckA.Paused,"Transport pauses real decoder");_plays[0].PerformClick();Check(!_engine.DeckA.Paused,"Transport resumes real decoder");
            var tr=await Task.Run(()=>TrackWaveform.Analyze(wav,CancellationToken.None));
            for(int d=0;d<2;d++){_trackPaths[d]=wav;_tracks[d]=tr;_cueSec[d]=0;Panel(d).SetStatus("FILE · offline verification fixture / no audio output",true);}
            _library!.AddTracks([wav]);_libraryB!.AddTracks([wav]);
            Check(tr.Columns.Length>1000,"Three-band file waveform analysis");
            SelectPadMode(0,"HOT CUE");DjPadDown(0,0,true);DjPadUp(0,0);Check(_hotCues.Get(wav,0)>=0,"File hot cue set");
            SelectPadMode(0,"CHORD");DjPadDown(0,0,false);SelectPadMode(0,"HOT CUE");DjPadUp(0,0);Check(_heldKey[0]<0,"Changing pad mode while held cannot strand a chord voice");
            SelectPadMode(0,"SAMPLE");SelectPadMode(1,"SAMPLE");
            var storePath=Path.Combine(directory,"hotcues-test.json");var store=new HotCueStore(storePath);store.Set(wav,15,3.25);store.Save();var restored=new HotCueStore(storePath);restored.Load();Check(restored.Get(wav,15)==3.25,"Hot cue atomic persistence round trip");
            _outputs.Items.Add(new OutputEndpoint("offline","OUTPUT OFF · offline verification"));_outputs.SelectedIndex=0;
            _engine.Mixer.Mute=false;var buffer=new float[4096];for(int i=0;i<8;i++){_engine.Mixer.Read(buffer);await Task.Delay(15);}
            TickUi();Check(_engine.A.Peak>0||_engine.Mixer.Peak>0,"Decoded signal reaches offline DSP/meters");
            Check(_engine.Timing.Count>0,"Real offline callback diagnostics recorded");
            Check(_engine.OutputId==null&&_engine.CueId==null,"No WASAPI player opened by interactions or decoding");
            _message.Text="OFFLINE RUNTIME CHECK · generated fixture · no audio device / no app capture";
            FormBorderStyle=FormBorderStyle.None;MaximumSize=new Size(6000,6000);MinimumSize=Size.Empty;
            foreach(var size in new[]{new Size(1920,1080),new Size(1366,768)})
            foreach(int density in new[]{100,150,200})
            {
                // Exercise the real WinForms DPI-change handler on this transparent offscreen window only.
                // Windows monitor settings and all other windows remain untouched.
                int dpi=density*96/100;IntPtr rect=System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
                try{System.Runtime.InteropServices.Marshal.StructureToPtr(new DpiRect{Left=-20000,Top=-20000,Right=-20000+size.Width,Bottom=-20000+size.Height},rect,false);SendMessage(Handle,0x02E0,(IntPtr)((dpi<<16)|dpi),rect);}
                finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(rect);}
                Check(DeviceDpi==dpi,$"WinForms handles WM_DPICHANGED to {dpi} DPI ({density}%)");
                ClientSize=size;_dj!.PerformLayout();RefreshDj();_waves.Frame();Application.DoEvents();
                var bounds=new List<object>();bool contained=true;
                foreach(Control c in _dj.Controls)
                {
                    bool ok=c.Left>=0&&c.Top>=0&&c.Right<=_dj.Width+1&&c.Bottom<=_dj.Height+1&&c.Width>0&&c.Height>0;
                    contained&=ok;bounds.Add(new{name=c.AccessibleName??c.Text,type=c.GetType().Name,bounds=c.Bounds.ToString(),ok});
                }
                Check(contained,$"All shell controls within {size.Width}x{size.Height}, {density}% scale exercise");
                using var bmp=new Bitmap(size.Width,size.Height);_dj.DrawToBitmap(bmp,new Rectangle(Point.Empty,size));
                string stem=$"runtime-{size.Width}x{size.Height}-{density}pct";bmp.Save(Path.Combine(directory,stem+".png"));
                File.WriteAllText(Path.Combine(directory,stem+".bounds.json"),JsonSerializer.Serialize(bounds,new JsonSerializerOptions{WriteIndented=true}));
            }
            UiCheckExitCode=failures==0?0:1;
        }
        catch(Exception ex){checks.Add(new{error=ex.ToString()});failures++;UiCheckExitCode=1;}
        finally
        {
            File.WriteAllText(Path.Combine(directory,"ui-check.json"),JsonSerializer.Serialize(new{failures,actualWindowDpi=DeviceDpi,desktopDpiChanged=false,scaleExercise="Synthetic WM_DPICHANGED at 96/144/192 DPI through the real WinForms handler; not physical multi-monitor certification",checks},new JsonSerializerOptions{WriteIndented=true}));
            Close();
        }
    }
}

