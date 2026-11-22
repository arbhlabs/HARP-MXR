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
            _autoBar.Toggle();
            Check(_autoMix!.Enabled, "Auto Mix button enables the production scheduler");
            _autoBar.Toggle();
            Check(!_autoMix.Enabled, "Auto Mix button disables the scheduler");
            Check(_autoMix.Card == AutoMixCard.Off && !_autoMix.Fading, "Auto Mix off shows the idle card and runs no transition");
            var menu = default(PadFrame) with {Buttons=PadButton.Start};
            Check(!MappingHold(default,0) && !MappingHold(menu,100) && MappingHold(menu,1000) && !MappingHold(menu,1100), "Solo Menu hold opens mappings once");
            MappingHold(default,1200);
            var panicChord = menu with {Buttons=PadButton.Start | PadButton.Back};
            Check(!MappingHold(panicChord,1300) && !MappingHold(panicChord,2400), "Panic chord cannot open mapping editor");
            var normalA = _padMapper.Update(PadButton.A,3000).Single();
            Dispatch(normalA.Action,true);
            Check(normalA.Kind==PadEventKind.Press && _engine.A.Parameters.LowKill && !_engine.A.Parameters.BassCut, "Default A applies full bass kill on the focused deck");
            _padMapper.Update(0,3050);
            Dispatch(_padMapper.Update(PadButton.A,3100).Single().Action,true);
            _padMapper.Update(0,3150);
            CycleControllerProfile(1, 0);
            Check(_config.ControllerProfileName == "Classic" && _controllerProfile.Text == "Classic", "Profile cycling changes the active dropdown");
            _controllerProfile.SelectedItem = "Live DJ";
            typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_controllerProfile, [EventArgs.Empty]);
            Dispatch(_padMapper.Update(PadButton.A,3200).Single().Action,true);
            Check(_engine.A.Parameters.BassCut && !_engine.A.Parameters.LowKill && _moveText.Contains("Live DJ"), "Live DJ dropdown applies BassCut and shows its name");
            _padMapper.Update(0,3250);
            Dispatch(_padMapper.Update(PadButton.A,3300).Single().Action,true);
            Check(!_engine.A.Parameters.BassCut, "Second A restores bass");
            _padMapper.Reset(0);
            var meter = new MeterEnvelope();
            meter.Update(1, 100);
            meter.Update(0, 799);
            Check(meter.Peak == 1 && meter.Level < .1f && meter.IsHeldSegment(31,32), "Peak marker survives transient and holds 700 ms");
            meter.Update(0, 1400);
            Check(Math.Abs(meter.Peak - .5f) < .001, "Peak marker releases by elapsed time");
            _engine.Mixer.Mute=true;
            Check(_preview&&_engine.OutputId==null&&_engine.CueId==null,"Offline preview: no output or cue player");
            var chips=ReadStatusChips();
            Check(chips[0].Text=="OFFLINE"&&chips[1].Color!=Theme.Green&&chips[2].Color!=Theme.Green,"Status chips never claim audio, controller or MIDI that is not open");
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
            UpdateMouseFx(0,.95,.9,true);_rig.Begin();PublishRacks(Environment.TickCount64);Check(Fx(0).Rack.State.Any(),"XY applies effect to DSP");
            UpdateMouseFx(0,.95,.9,false);_rig.Begin();PublishRacks(Environment.TickCount64);Check(!Fx(0).Rack.State.Any(),"XY release returns dry");
            foreach(var mode in new[]{"SAMPLE","ONE-SHOT","CHORD","DRUM","LOOP","BEAT JUMP","HOT CUE"})
            {SelectPadMode(0,mode);Check(_padModes[0]==mode,"Pad mode reachable: "+mode);}

            using(var settings=CreateDiagnostics())
            {
                _profiles!.SelectedItem=LatencyProfile.Low;
                typeof(ComboBox).GetMethod("OnSelectionChangeCommitted",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(_profiles,[EventArgs.Empty]);
                Check(_engine.Profile==LatencyProfile.Low&&_config.LatencyMs==60,"Settings profile selection updates engine and persisted configuration model");
                Check(_diagnostics!.Text.Contains("Callback timings are not end-to-end"),"Settings distinguishes callback cost from output latency");
                var (memOn,memForget,memClear)=_memoryControls!.Value;
                Check(memOn.Checked==_config.WaveformMemory&&memForget.Enabled&&memClear.Enabled,"Settings shows waveform memory switch, forget and clear");
                memOn.Checked=false;Check(!_config.WaveformMemory,"Waveform memory switch reaches configuration");memOn.Checked=true;Check(_config.WaveformMemory,"Waveform memory switch restores");
                var memId=Guid.NewGuid();var memPoints=Enumerable.Range(0,60).Select(i=>new FingerprintPoint(i,(uint)(i+1))).ToArray();
                Check(_trackCache.Put(new TrackCacheEntry(memId,null,12,new WaveColumn[1200],memPoints,null,true)),"Preview cache stores a derived-data entry");
                void Press(Control c)=>typeof(Control).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,[EventArgs.Empty]);
                Press(memForget);Check(_trackCache.TryGet(memId)!=null,"Forget this track leaves other cached tracks alone");
                Press(memClear);Check(_trackCache.TryGet(memId)==null,"Clear cache removes every cached track");
            }
            _diagnostics=null;_profiles=null;_engine.Profile=LatencyProfile.Standard;
            BindingsForm.VerifyOffscreen(this, directory, Check);
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
            for(int d=0;d<2;d++){_trackPaths[d]=wav;_tracks[d]=tr;_cueSec[d]=0;Panel(d).SetStatus("FILE · offline preview",true);}
            _library!.AddTracks([wav]);
            Check(DeckAnalysis(0)?.Segments.Length > 0, "Auto Mix reads the loaded file's musical sections after a seek");
            Check(tr.Columns.Length>1000,"Three-band file waveform analysis");
            Check(JogTempo(0).Bpm is > 119 and < 121, "Jog displays measured file tempo before live detector lock");
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
            foreach(var size in new[]{new Size(1280,720),new Size(1366,768),new Size(1920,1080),new Size(2560,1392),new Size(2560,1440),new Size(3840,2160)})
            foreach(int density in new[]{100,125,150,200})
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
                var clipped = _dj.Controls.OfType<StudioButton>().Where(button =>
                {
                    var measured = TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.SingleLine);
                    return measured.Width > button.Width || measured.Height > button.Height;
                }).Select(button => button.Text).Distinct();
                Check(!clipped.Any(), $"Console button captions fit at {size}, {density}% ({string.Join(", ",clipped)})");
                // Static captions must fit too (dynamic text such as titles and messages may ellipsize by design).
                var clippedLabels = _dj.Controls.OfType<Label>().Where(l => l.Visible && !_dynamicText.Contains(l) && l.Text.Length > 0 &&
                    TextRenderer.MeasureText(l.Text,l.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width > l.Width).Select(l => l.Text).Distinct();
                Check(!clippedLabels.Any(), $"Console labels fit at {size}, {density}% ({string.Join(", ",clippedLabels)})");
                // Worst-case dynamic pad captions (long hot cue time, MIDI note) and the longest pad help line.
                var pad=_cells[0][15];
                var tooWide=new[]{"16 · 59:59","16  ·  n127","◂ 1/8"}.Where(t=>TextRenderer.MeasureText(t,pad.Font,Size.Empty,TextFormatFlags.SingleLine).Width>pad.Width).ToList();
                Check(tooWide.Count==0,$"Worst-case pad captions fit at {size}, {density}% ({string.Join(", ",tooWide)})");
                var help=_padHelp[0]!;string longest="ONE-SHOT · rows = banks 1–4 · click fires once · SHIFT+click captures · MIDI notes 111–126";
                Check(TextRenderer.MeasureText(longest,help.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width<=help.Width,$"Longest pad help line with MIDI notes fits at {size}, {density}%");
                // No two visible console controls may overlap (overlays live outside the canvas). The controller-feedback HUD is
                // the one by-design exception: it floats over the waveforms for ~1.4 s, so it must sit inside them instead.
                var visible = _dj.Controls.Cast<Control>().Where(c => c.Visible && c != _fxOverlay).ToList(); var overlaps = new List<string>();
                for (int i = 0; i < visible.Count; i++) for (int j = i + 1; j < visible.Count; j++)
                    if (visible[i].Bounds.IntersectsWith(visible[j].Bounds)) overlaps.Add($"{visible[i].AccessibleName ?? visible[i].Text} × {visible[j].AccessibleName ?? visible[j].Text}");
                Check(overlaps.Count == 0, $"No overlapping console controls at {size}, {density}% ({string.Join("; ", overlaps.Take(6))})");
                Check(_waves.Bounds.Contains(_fxOverlay.Bounds), $"Controller feedback HUD sits inside the waveforms at {size}, {density}%");
                float smallest = _dj.Controls.Cast<Control>().Where(c => c.Visible).Min(c => c.Font.Unit == GraphicsUnit.Pixel ? c.Font.Size : c.Font.SizeInPoints * DeviceDpi / 72f);
                // Worst-case transition cards: every piece of text must fit the card at its smallest allowed size.
                var cardIssues = AutoMixWorstCards().SelectMany(card => _autoBar.Overflow(card)).Distinct().ToList();
                Check(cardIssues.Count == 0, $"Auto Mix transition card text fits at {size}, {density}% ({string.Join(" | ", cardIssues)})");
                Check(size.Width > 1280 || smallest >= 9.5f, $"Smallest console font {smallest:0.0} px at {size} (floor 9.5 px at 1280x720)");
                string stem=$"runtime-{size.Width}x{size.Height}-{density}pct";
                SavePreviewShot(_dj, Path.Combine(directory,stem+".png"));
                if (ShotPath != null && size.Width == 1366 && density == 100)
                    File.Copy(Path.Combine(directory, stem+".png"), ShotPath, true);
                if (size.Width == 1920 && density == 100)
                {
                    SetBrowsing(true);_dj.PerformLayout();Application.DoEvents();
                    var shown = _dj.Controls.Cast<Control>().Where(c => c.Visible && c != _fxOverlay).ToList(); bool clean = true;
                    for (int i = 0; i < shown.Count; i++) for (int j = i + 1; j < shown.Count; j++) clean &= !shown[i].Bounds.IntersectsWith(shown[j].Bounds);
                    Check(clean && _library!.Height > _dj.Height / 2 && _cells[1][0].Visible && _plays[0].Visible && !_cells[0][0].Visible, "BROWSE expands the library, hides only deck A FX/pads, keeps deck A transport and deck B");
                    SavePreviewShot(_dj, Path.Combine(directory, "browse-1920x1080.png"));
                    SetBrowsing(false);_dj.PerformLayout();
                    Check(_cells[0][0].Visible && _library!.Height < _dj.Height / 4, "PERFORM restores every hidden control");
                    // Active-state evidence: hot cues on A (pad 1 set earlier), a held pad and a held XY effect on B.
                    SelectPadMode(0,"HOT CUE");_hotCues.Set(wav,4,9.5);SelectPadMode(1,"BEAT JUMP");
                    _mousePadHeld[1,9]=true;UpdateMouseFx(1,.8,.7,true);_mouseFx[1]=PadFx.Flanger;UpdateMouseFx(1,.8,.7,true);
                    RefreshDj();Application.DoEvents();
                    Check(_cells[0][0].Text.Contains(':') && _cells[0][0].Lamp != null && !_cells[0][1].Text.Contains(':'), "Hot cue pads show their time and a lamp; empty pads stay plain");
                    Check(_cells[1][9].Active && _cells[1][9].Text.StartsWith("▸"), "Held beat-jump pad is lit and labelled with its direction");
                    _autoBar.ShowCard(AutoMixWorstCards()[0]);Application.DoEvents();
                    SavePreviewShot(_dj, Path.Combine(directory, "states-1920x1080.png"));
                    _autoBar.ShowCard(_autoMix!.Card);
                    _mousePadHeld[1,9]=false;UpdateMouseFx(1,.5,.5,false);SelectPadMode(0,"SAMPLE");SelectPadMode(1,"SAMPLE");RefreshDj();
                }
                if (size.Width == 1366 && density == 100)
                {
                    var frame = new PadFrame(.85,.45,-.6,.8,0,0,PadButton.X);
                    ProcessAxes(frame,.016,Environment.TickCount64);
                    RefreshControllerFeedback(frame,true,false);
                    SavePreviewShot(_dj,Path.Combine(directory,"sticks.png"));
                    RefreshControllerFeedback(default,true,true);
                    SavePreviewShot(_shiftOverlay!,Path.Combine(directory,"shift.png"));
                    ProcessAxes(default,.016,Environment.TickCount64);
                    RefreshControllerFeedback(default,false,false);
                }
                File.WriteAllText(Path.Combine(directory,stem+".bounds.json"),JsonSerializer.Serialize(bounds,new JsonSerializerOptions{WriteIndented=true}));
            }
            UiCheckExitCode=failures==0?0:1;
        }
        catch(Exception ex){checks.Add(new{error=ex.ToString()});failures++;UiCheckExitCode=1;}
        finally
        {
            File.WriteAllText(Path.Combine(directory,"ui-check.json"),JsonSerializer.Serialize(new{failures,actualWindowDpi=DeviceDpi,desktopDpiChanged=false,scaleExercise="Synthetic WM_DPICHANGED at 96/120/144/192 DPI through the real WinForms handler; not physical multi-monitor certification",checks},new JsonSerializerOptions{WriteIndented=true}));
            Close();
        }
    }
    /// <summary>Longest realistic transition-card contents (running with a lane taken over, planned, live with no track end).</summary>
    private static AutoMixCard[] AutoMixWorstCards() =>
    [
        new("MIXING", TransitionPlanner.Label(TransitionStyle.BassSwap), "12A→11B · -12.5 BPM · half/double time · bass swap at bar 1024",
            "32 bars left", "A → B · 174.5 BPM · 12A", MixLane.Fader | MixLane.EqA | MixLane.EqB | MixLane.FilterA | MixLane.FxA,
            .42, true, "Drum & bass", MixLane.FilterB | MixLane.TempoB),
        new("MIX NOW", TransitionPlanner.Label(TransitionStyle.BreakdownEntry), "32 bars · 10B→11B · +4.0 BPM · breakdown entry at bar 512",
            "in 16 bars", "B → A · 128.5 BPM · 10B", MixLane.None, 0, false, "Open format"),
        new("READY", "", "Deck B live · no track end known · press MIX NOW", "", "B → A · 128.5 BPM · 10B",
            MixLane.None, 0, false, ""),
    ];
}

