using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;
using MixDock.Ui;

namespace MixDock;

public sealed partial class MainForm
{
    private DjLayout? _dj;
    private readonly JogView[] _jogs = [new(), new()];
    private readonly Label[] _titles = [new(),new()], _times = [new(),new()];
    private readonly StudioButton[] _cues = [new(),new()], _plays = [new(),new()];
    private readonly StudioButton[][] _cells = [new StudioButton[16],new StudioButton[16]];
    private readonly string[] _padModes = ["SAMPLE","SAMPLE"];
    private readonly List<(int Deck,string Mode,StudioButton Button)> _modeButtons = new();
    private readonly List<(int Deck,PadFx Fx,StudioButton Button)> _fxButtons = new();
    private readonly PadFx[] _mouseFx = [PadFx.Reverb,PadFx.Reverb];
    private readonly bool[] _mouseFreeze = new bool[2];
    private readonly bool[] _mouseFxHeld = new bool[2];
    private readonly HotCueStore _hotCues = new();
    private LibraryView? _library;
    private readonly bool[,] _mousePadHeld=new bool[2,16];
    private readonly string?[,] _mousePadRoll=new string?[2,16];
    private Label? _performanceStatus;
    private readonly SignalBar _signalBar=new();
    private readonly StudioButton[] _syncButtons=new StudioButton[2],_masterButtons=new StudioButton[2];
    private LibraryView? _libraryB;
    private readonly Label[] _fxStatus=new Label[2];
    private Label? _diagnostics;
    private ComboBox? _profiles;

    private void InstallDjShell()
    {
        var dj = _dj = new DjLayout();
        Controls.Clear(); Controls.Add(dj);
        AutoScaleMode=AutoScaleMode.None;
        Text = "HARP//MXR · DJ console"; ClientSize = new Size(1366,768); MinimumSize = new Size(960,540);
        if(!_preview)WindowState=FormWindowState.Maximized;
        Label LabelAt(string text,int x,int y,int w,int h,float size=8,Color? color=null)
        {
            var l=new Label{Text=text,ForeColor=color??Theme.Muted,BackColor=Theme.Surface,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
            dj.Place(l,x,y,w,h,size);return l;
        }
        StudioButton Button(string text,int x,int y,int w,int h,Action click,Color? color=null)
        {
            var b=new StudioButton{Text=text,Accent=color??Theme.Muted,AccessibleName=text};
            b.Click+=(_,_)=>click();dj.Place(b,x,y,w,h);return b;
        }
        LabelAt("MIXDOCK",12,3,116,30,15,Theme.Text);
        _outputs.DrawMode=DrawMode.OwnerDrawFixed;_outputs.DrawItem+=(_,e)=>{using var bg=new SolidBrush(Theme.Surface);e.Graphics.FillRectangle(bg,e.Bounds);string text=e.Index>=0?_outputs.Items[e.Index]?.ToString()??"Output":"Select master output";TextRenderer.DrawText(e.Graphics,text,_outputs.Font,e.Bounds,Theme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        _outputs.DropDownStyle=ComboBoxStyle.DropDownList; _outputs.BackColor=Theme.Surface;_outputs.ForeColor=Theme.Text;
        _outputs.AccessibleName="Master output device"; dj.Place(_outputs,140,6,265,24);
        _outputs.SelectionChangeCommitted+=async(_,_)=>{if(!_preview)await SafeAsync(SetOutputAsync);};
        LabelAt("MASTER",420,5,58,25);_signalBar.Read=()=>_engine.Mixer.Peak;dj.Place(_signalBar,477,5,115,25);_master.Vertical=false;dj.Place(_master,605,1,150,34);dj.Place(_masterValue,764,5,55,25,10);
        Button("MAP PAD",822,5,68,26,OpenMappings,Theme.Cyan).AccessibleName="Controller mappings: bind any Xbox button, stick or trigger";
        Button("MIDI",895,5,55,26,OpenMidiSettings);
        dj.Place(_padButton,956,5,52,26);Button("PAD GRID",1014,5,72,26,()=>{PlaceGridFloat();_gridFloat.Visible=!_gridFloat.Visible;if(_gridFloat.Visible)_gridFloat.BringToFront();});
        Button("SETTINGS",1092,5,90,26,()=>ShowDiagnostics());dj.Place(_panic,1190,5,164,26);

        for(int d=0;d<2;d++)
        {
            int dd=d, fxX=d==0?10:693;Color accent=d==0?Theme.Cyan:Theme.Amber;
            LabelAt($"FX{d+1}",fxX+8,43,29,24,8,accent);
            var effects=new[]{PadFx.Reverb,PadFx.Flanger,PadFx.Phaser,PadFx.Crush,PadFx.Gate};
            for(int f=0;f<effects.Length;f++)
            {
                var effect=effects[f];var b=Button(effect.ToString().ToUpperInvariant(),fxX+40+f*66,43,63,24,()=>{_mouseFx[dd]=effect;UpdateMouseFx(dd,.5,.5,_mouseFxHeld[dd]);},accent);
                _fxButtons.Add((dd,effect,b));
            }
            Button("FREEZE",fxX+374,43,70,24,()=>{_mouseFreeze[dd]=!_mouseFreeze[dd];UpdateMouseFx(dd,.5,.5,_mouseFxHeld[dd]);},accent);
            var echo=Button("ECHO THROW",fxX+450,43,100,24,()=>{},accent);
            echo.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){_engine.Mixer.Echo(dd).Send=.7f;echo.Capture=true;}};
            echo.MouseUp+=(_,_)=>{_engine.Mixer.Echo(dd).Send=0;echo.Capture=false;};
            echo.MouseCaptureChanged+=(_,_)=>{if(!echo.Capture)_engine.Mixer.Echo(dd).Send=0;};
            var riser=Button("RISER",fxX+556,43,104,24,()=>{},accent);
            riser.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){SetFocus(dd,true);_engine.Mixer.Riser.Build=.5f;_engine.Mixer.Riser.Amount=.6f;riser.Capture=true;}};
            riser.MouseUp+=(_,_)=>{_engine.Mixer.Riser.StopAfter(0);riser.Capture=false;};riser.MouseCaptureChanged+=(_,_)=>{if(!riser.Capture)_engine.Mixer.Riser.StopAfter(0);};
            _fxStatus[d]=LabelAt("AMOUNT 0% · X 50% · Y 50%",fxX+10,78,270,24,8);
            var roll=Button("BEAT ROLL",fxX+300,77,98,26,()=>{},accent);
            roll.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){Dispatch($"{DeckName(dd)}.Roll:1",true);roll.Capture=true;}};
            roll.MouseUp+=(_,_)=>{Dispatch($"{DeckName(dd)}.Roll:1",false);roll.Capture=false;};
            roll.MouseCaptureChanged+=(_,_)=>{if(!roll.Capture)Dispatch($"{DeckName(dd)}.Roll:1",false);};
            var xy=new XyPad();xy.Changed+=(x,y,on)=>UpdateMouseFx(dd,x,y,on);dj.Place(xy,fxX+410,75,250,30);
        }
        dj.Place(_waves,0,115,1366,182);
        LabelAt("MIXER",578,301,210,22,9,Theme.Text).TextAlign=ContentAlignment.MiddleCenter;
        for(int d=0;d<2;d++)
        {
            int dd=d,x=d==0?10:807,mx=d==0?578:720;var panel=Panel(d);var accent=d==0?Theme.Cyan:Theme.Amber;
            _deckTags[d]=LabelAt(DeckName(d),x+8,309,30,28,17,accent);_deckTags[d]!.TextAlign=ContentAlignment.MiddleCenter;
            _deckTags[d]!.AccessibleName=$"Deck {DeckName(d)} (highlighted = the controller's focused deck)";
            _titles[d]=LabelAt("Choose an app or load a track",x+46,304,355,23,11,Theme.Text);
            _times[d]=LabelAt("— / —",x+402,306,138,24,11,Theme.Text);
            dj.Place(panel.Sources,x+8,334,530,32,9);
            dj.Place(panel.StatusControl,x+8,368,245,20,8);
            var release=Button("RELEASE",x+257,367,73,25,()=>{if(!_preview)_=SafeAsync(()=>ReleaseDeckAsync(dd));});
            release.AccessibleName="Stop this deck and put the app's sound back where it was";
            _syncButtons[d]=Button("SYNC",x+334,367,65,25,()=>{if(!_preview)SetMessage(DeckSync.Sync(_engine,dd));},accent);
            _masterButtons[d]=Button("MASTER",x+403,367,75,25,()=>{if(Source(0).ActivePid==null||Source(1).ActivePid==null){SetMessage("MASTER needs sources on both decks.");return;}_engine.Sync.Enable(1-dd);SetFocus(dd);SetMessage($"Deck {DeckName(dd)} is sync master; the other deck follows.");},accent);
            Button("TAP",x+482,367,58,25,()=>Dispatch($"{DeckName(dd)}.TapBpm",true),accent);
            string[] modes=["HOT CUE","SAMPLE","ONE-SHOT","CHORD","DRUM","LOOP","BEAT JUMP"];
            for(int m=0;m<modes.Length;m++)
            {
                var mode=modes[m];var b=Button(mode,x+8+m*76,398,73,25,()=>SelectPadMode(dd,mode),accent);_modeButtons.Add((dd,mode,b));
            }
            LabelAt("16 PADS / 4 BANKS · SHIFT+CLICK TO CAPTURE / SET CUE",x+8,426,530,20,7.5f,accent);
            for(int k=0;k<16;k++)
            {
                int key=k;var b=Button((k+1).ToString(),x+8+(k%4)*69,451+(k/4)*29,65,26,()=>{},accent);
                b.AccessibleName=$"Deck {DeckName(d)} pad {k+1}";
                b.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){DjPadDown(dd,key,(ModifierKeys&Keys.Shift)!=0);b.Capture=true;}};
                b.MouseUp+=(_,_)=>{DjPadUp(dd,key);b.Capture=false;};
                b.MouseCaptureChanged+=(_,_)=>{if(!b.Capture)DjPadUp(dd,key);};
                b.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Space&&!b.Active){DjPadDown(dd,key,e.Shift);e.Handled=true;}};
                b.KeyUp+=(_,e)=>{if(e.KeyCode==Keys.Space){DjPadUp(dd,key);e.Handled=true;}};
                _cells[d][k]=b;
            }
            _jogs[d].Accent=accent;_jogs[d].Read=()=>Tempo(dd);dj.Place(_jogs[d],x+326,449,112,112);
            Button("−",x+451,452,38,26,()=>_monitor.StepNudge(dd,-.5),accent);
            Button("+",x+493,452,38,26,()=>_monitor.StepNudge(dd,.5),accent);
            LabelAt("TEMPO",x+450,481,85,24,8).TextAlign=ContentAlignment.MiddleCenter;
            Button("RESET",x+451,510,80,25,()=>{DeckSync.SetNudge(_engine,dd,0);_monitor.ResetNudge(dd);},accent);
            Button("HALVE",x+8,572,64,25,()=>Dispatch($"{DeckName(dd)}.LoopHalve",true));
            Button("DOUBLE",x+76,572,66,25,()=>Dispatch($"{DeckName(dd)}.LoopDouble",true));
            Button("QUANTIZE",x+146,572,86,25,CycleQuantize);
            Button("BANK / MODE",x+236,572,98,25,()=>{SetFocus(dd);CycleBankMode();});
            Button("SLIP LOOP",x+342,572,90,25,()=>Dispatch($"{DeckName(dd)}.Loop",true),accent);
            _plays[d]=Button("PLAY / PAUSE",x+436,572,104,25,()=>Dispatch($"{DeckName(dd)}.PlayPause",true),accent);
            LabelAt("SAMPLE / CHORD / DRUM · BEND / REVERSE / SPIN / ROLL: PAD MAPPINGS",x+8,601,530,19,7);

            LabelAt(DeckName(d),mx,325,65,20,9,accent).TextAlign=ContentAlignment.MiddleCenter;
            panel.Trim.Vertical=false;dj.Place(panel.Trim,mx-1,345,68,30);LabelAt("TRIM",mx,375,65,14,7).TextAlign=ContentAlignment.MiddleCenter;
            var knobs=new[]{panel.High,panel.Mid,panel.Low,panel.Filter};var kills=new[]{panel.HighKill,panel.MidKill,panel.LowKill};
            for(int i=0;i<4;i++)
            {
                dj.Place(knobs[i],mx,390+i*49,65,49);
                if(i<3){dj.Place(kills[i],d==0?mx-8:mx+46,401+i*49,27,20,6);kills[i].BringToFront();}   // sits on the knob's empty margin: must draw above it
            }
            dj.Place(panel.MeterControl,d==0?651:687,338,34,213,7);
            dj.Place(panel.MuteControl,mx,589,65,24,7);
            _cues[d]=Button("CUE "+DeckName(d),mx,617,65,25,()=>{if(!_preview)_=CueToggledAsync(dd,!(dd==0?_engine.Mixer.CueA:_engine.Mixer.CueB));else SetMessage("Preview: headphone output stays off.");},accent);
        }
        LabelAt("A    EQUAL-POWER CROSSFADER    B",578,650,210,18,7).TextAlign=ContentAlignment.MiddleCenter;
        dj.Place(_cross,578,664,210,39);
        // cue volume | HEADPHONES | cue/mix side by side, no overlap (the button used to sit under both sliders)
        LabelAt("CUE VOL",572,708,66,16,7);LabelAt("CUE / MIX",728,708,66,16,7);
        dj.Place(_monitor.VolumeControl,568,724,72,29);dj.Place(_monitor.BlendControl,726,724,72,29);
        var hp=Button("HEADPHONES",642,711,82,36,()=>ShowDiagnostics());
        hp.AccessibleName="Choose headphone output for cue listening";

        _library=new LibraryView(_preview,0);_library.LoadRequested+=(d,path)=>{if(!_preview)_=SafeAsync(()=>LoadSongAsync(Panel(d),Source(d),d==0?_takeoverA:_takeoverB,d,path));};
        dj.Place(_library,10,627,550,91);dj.Place(_autoBar,10,719,550,33);
        _libraryB=new LibraryView(_preview,1);_libraryB.LoadRequested+=(d,path)=>{if(!_preview)_=SafeAsync(()=>LoadSongAsync(Panel(d),Source(d),d==0?_takeoverA:_takeoverB,d,path));};
        _library.Changed+=()=>_libraryB.Reload();_libraryB.Changed+=()=>_library.Reload();
        dj.Place(_libraryB,807,627,550,91);
        _performanceStatus=LabelAt("BANK 1/4 · QUANT 1/16 · MIDI off",815,722,530,26,8);
        _gridCenter.Visible=false;_hud.Visible=false;
        dj.Place(_message,10,754,820,14,7);dj.Place(_padEnabled,840,749,88,19,7);dj.Place(_padStatus,936,750,419,18,7);
        Controls.Add(_overlay);Controls.Add(_gridFloat);
        ShowFocus();
        dj.PerformLayout();
    }
    private readonly Label?[] _deckTags=new Label?[2];
    /// <summary>The controller's focused deck is the filled letter tag (cyan A / amber B).</summary>
    private void ShowFocus()
    {
        for(int d=0;d<2;d++)
        {
            if(_deckTags[d] is not Label t)continue;
            var accent=d==0?Theme.Cyan:Theme.Amber;bool on=d==_focus;
            t.BackColor=on?accent:Theme.Surface;t.ForeColor=on?Theme.Background:accent;
        }
    }

    private static string RollAction(int d,int key)=>$"{DeckName(d)}.Roll:"+new[]{.125,.25,.5,1,2,4,8,16}[key%8].ToString(System.Globalization.CultureInfo.InvariantCulture);
    private async Task ReleaseDeckAsync(int d)
    {
        await _audioChanges.WaitAsync();
        try{(d==0?_takeoverA:_takeoverB).Release();await Source(d).StopAsync();DeckSync.Reset(_engine,d);Panel(d).Sources.SelectedIndex=-1;Panel(d).SetStatus("Released · choose a source");SetMessage($"Deck {DeckName(d)} released; original app route restored.");}
        finally{_audioChanges.Release();}
    }
    private void SelectPadMode(int d,string mode)
    {
        _padModes[d]=mode;SetFocus(d,true);
        if(mode is "SAMPLE" or "ONE-SHOT" or "CHORD" or "DRUM")
            _config.Banks[_bank].Mode=mode switch{"SAMPLE"=>BankMode.Sample,"ONE-SHOT"=>BankMode.OneShot,"CHORD"=>BankMode.Chord,_=>BankMode.Drum};
        RefreshDj();
    }
    private void DjPadDown(int d,int key,bool shift)
    {
        _mousePadHeld[d,key]=true;
        var b=_cells[d][key];b.Active=true;b.Invalidate();
        switch(_padModes[d])
        {
            case "HOT CUE":
                var src=Source(d);
                if(!src.IsFile||src.FilePath==null){SetMessage("File hot cues need a song file. Live app history cannot seek.");return;}
                double? cue=_hotCues.Get(src.FilePath,key);
                if(shift||cue==null){_hotCues.Set(src.FilePath,key,src.Position.TotalSeconds);if(!_preview)_hotCues.Save();SetMessage($"Hot cue {key+1} set.");}
                else src.SeekTo(TimeSpan.FromSeconds(cue.Value));
                break;
            case "LOOP": _mousePadRoll[d,key]=RollAction(d,key);Dispatch(_mousePadRoll[d,key]!,true);break;
            case "BEAT JUMP": _loopBeats[d]=new[]{.125,.25,.5,1,2,4,8,16}[key%8];Dispatch($"{DeckName(d)}.{(key<8?"BeatJumpBack":"BeatJumpFwd")}",true);break;
            default:
                SetBank(key/4);SetFocus(d,true);
                _config.Banks[_bank].Mode=_padModes[d] switch{"SAMPLE"=>BankMode.Sample,"ONE-SHOT"=>BankMode.OneShot,"CHORD"=>BankMode.Chord,_=>BankMode.Drum};
                if(shift)PadCapture(d,key%4);else BankPadDown(d,key%4,false);
                break;
        }
    }
    private void DjPadUp(int d,int key)
    {
        _mousePadHeld[d,key]=false;
        _cells[d][key].Active=false;_cells[d][key].Invalidate();
        if(_mousePadRoll[d,key] is string action){_mousePadRoll[d,key]=null;Dispatch(action,false);}
        if(_heldKey[key%4]==key&&_heldDeck[key%4]==d)BankPadUp(key%4);
    }
    private void UpdateMouseFx(int d,double x,double y,bool on)
    {
        _mouseFxHeld[d]=on;var (bpm,phase)=Tempo(d);
        Fx(d).State=new(_mouseFx[d],on?FxCurves.Amount(x*2-1,y*2-1):0,x,y,bpm,phase,_mouseFreeze[d],Stamp:System.Diagnostics.Stopwatch.GetTimestamp());
        foreach(var item in _fxButtons.Where(i=>i.Deck==d)){item.Button.Active=item.Fx==_mouseFx[d];item.Button.Invalidate();}
        _hud.SetFx(_mouseFx[d],Fx(d).State.Amount,x,y,_mouseFreeze[d],d);
    }
    private void RefreshDj()
    {
        if(_dj==null)return;
        _signalBar.Invalidate();
        if(_performanceStatus!=null)
        {
            bool recent=_moveText.Length>0&&Environment.TickCount64-_moveAt<1600;
            string text=recent?$"▶ {_moveText}":$"BANK {_bank+1}/4 · QUANT {PadQuant.Label(_config.Quantize)} · {_midi?.Status??"MIDI: off"}";
            if(_performanceStatus.Text!=text){_performanceStatus.Text=text;_performanceStatus.ForeColor=recent?(_focus==0?Theme.Cyan:Theme.Amber):Theme.Muted;}
        }
        for(int d=0;d<2;d++)
        {
            var src=Source(d);
            _titles[d].Text=src.IsFile?Path.GetFileName(src.FilePath):Panel(d).SelectedApp?.Name??"Choose an app or load a track";
            _times[d].Text=src.IsFile?$"{Mmss(src.Position)} / {Mmss(src.Duration)}":src.ActivePid!=null?"LIVE":"— / —";
            _cues[d].Active=d==0?_engine.Mixer.CueA:_engine.Mixer.CueB;_cues[d].Invalidate();
            bool paused=DeckIsPaused(d),live=!src.IsFile&&src.ActivePid is >0;
            _plays[d].Text=src.IsFile||live?(paused?(_pauseLabel[d]??"PAUSED")+" · PLAY":"PAUSE"):"PLAY / PAUSE";_plays[d].Active=(src.IsFile||live)&&!paused;_plays[d].Invalidate();
            if(live&&paused)_times[d].Text=_pauseLabel[d]=="FROZEN"?"FROZEN · app still playing":"PAUSED";
            else if(live&&Reader(d).LookAheadMs>0)_times[d].Text=$"LIVE · look-ahead {Reader(d).LookAheadMs/1000.0:0.#} s";
            if(src.IsFile&&src.FilePath!=null)
            {
                var track=_tracks[d];double bpm=track?.Path==src.FilePath&&track.Grid.Valid?track.Grid.Bpm:0;
                var key=_engine.Mixer.Key(d);string? name=key.Key is MusicalKey k&&key.Confidence>=KeyConfidenceMin?ChordTheory.KeyName(k):null;
                _library?.SetMetadata(src.FilePath,bpm,name,src.Duration);_libraryB?.SetMetadata(src.FilePath,bpm,name,src.Duration);
            }
            _jogs[d].Invalidate();
            _syncButtons[d].Active=_engine.Sync.Role(d)==SyncRole.Slave;_masterButtons[d].Active=_engine.Sync.Role(d)==SyncRole.Master;_syncButtons[d].Invalidate();_masterButtons[d].Invalidate();
            var fx=Fx(d).State;_fxStatus[d].Text=$"AMOUNT {fx.Amount*100:0}% · X {fx.X*100:0}% · Y {fx.Y*100:0}%{(fx.Freeze?" · FROZEN":"")}";
            for(int k=0;k<16;k++)
            {
                var b=_cells[d][k];string text=(k+1).ToString();
                if(_padModes[d]=="HOT CUE"&&src.FilePath!=null&&_hotCues.Get(src.FilePath,k) is double cue)text=$"C{k+1} {Mmss(TimeSpan.FromSeconds(cue))}";
                if(_padModes[d] is "LOOP" or "BEAT JUMP")text=(_padModes[d]=="BEAT JUMP"?(k<8?"−":"+"):"")+new[]{"1/8","1/4","1/2","1","2","4","8","16"}[k%8];
                if(b.Text!=text)b.Text=text;
                bool playing=(_engine.Mixer.Pads(d).PlayingMask&(1<<k))!=0||Sampler(d).PlayingPad==k;
                b.Active=_mousePadHeld[d,k]||playing||(_latched[k]&&_latchDeck[k]==d);
                b.ForeColor=Sampler(d).Length(k)>0||ModeOf(k/4) is BankMode.Chord or BankMode.Drum?(d==0?Theme.Cyan:Theme.Amber):Theme.Muted;
                b.Invalidate();
            }
        }
        foreach(var (d,mode,b) in _modeButtons){b.Active=_padModes[d]==mode;b.Invalidate();}
        if(_diagnostics!=null&&!_diagnostics.IsDisposed)_diagnostics.Text=_engine.Timing+"\nRequested buffer: "+_engine.Profile.RequestedMs+" ms. Applies when outputs reopen.\nCallback timings are not end-to-end latency.\nShared WASAPI · Remote off · Protected apps: "+MixDock.Platform.ProtectedApps.Summary;
    }
    private void ShowDiagnostics(){using var f=CreateDiagnostics();f.ShowDialog(this);_diagnostics=null;_profiles=null;}
    private Form CreateDiagnostics()
    {
        var f=new Form{Text="HARP//MXR · Settings",Size=new Size(690,520),BackColor=Theme.Background,ForeColor=Theme.Text,StartPosition=FormStartPosition.CenterParent,Font=Theme.Font(10)};
        var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new Padding(20),WrapContents=false,AutoScroll=true};f.Controls.Add(flow);
        flow.Controls.Add(new Label{Text="Speakers stay in the top bar. Here: headphones for cue, buffer size, and controller.",AutoSize=false,Width=610,Height=36,ForeColor=Theme.Muted});
        flow.Controls.Add(new Label{Text="Buffer size (shared WASAPI — applies when outputs reopen)",AutoSize=true});
        _profiles=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=610,DisplayMember="Name"};_profiles.Items.AddRange(LatencyProfile.All.Cast<object>().ToArray());_profiles.SelectedItem=_engine.Profile;
        _profiles.SelectionChangeCommitted+=(_,_)=>{if(_profiles.SelectedItem is LatencyProfile p){_engine.Profile=p;_config.LatencyMs=p.RequestedMs;RefreshDj();}};flow.Controls.Add(_profiles);
        _diagnostics=new Label{AutoSize=false,Size=new Size(610,110)};flow.Controls.Add(_diagnostics);RefreshDj();
        flow.Controls.Add(new Label{Text="Headphones for CUE A / CUE B (must be a different device than master)",AutoSize=true,ForeColor=Theme.Muted});
        var headphones=new ComboBox{Width=610,DropDownStyle=ComboBoxStyle.DropDownList};headphones.Items.Add("Headphones: automatic (different from master)");
        if(!_preview)headphones.Items.AddRange(AudioDiscovery.Outputs().Where(o=>o.Id!=_engine.OutputId&&OutputPolicy.IsAllowed(o,_takeoverA.SinkId,_takeoverB.SinkId)).Cast<object>().ToArray());
        headphones.SelectedIndex=0;headphones.SelectionChangeCommitted+=async(_,_)=>{if(!_preview&&headphones.SelectedItem is OutputEndpoint o)await SafeAsync(async()=>{await _engine.SetCueOutputAsync(o.Id);_monitor.SetCueDevice(_engine.CueName,true);});};flow.Controls.Add(headphones);
        var refresh=new StudioButton{Text="Refresh apps and outputs",Size=new Size(610,32)};refresh.Click+=(_,_)=>{if(!_preview)RefreshDevices();};flow.Controls.Add(refresh);
        flow.Controls.Add(new Label{Text="Waveform look-ahead for live apps (seconds, 0-30). The deck is heard this much later so the waveform shows what is coming. Use 0 for browser video, so sound matches the picture.",AutoSize=false,Width=610,Height=52,ForeColor=Theme.Muted});
        var lookRow=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=false};
        for(int ld=0;ld<2;ld++)
        {
            int dd=ld;
            var num=new NumericUpDown{Minimum=0,Maximum=(decimal)AppConfig.MaxLookAheadSec,DecimalPlaces=0,Increment=1,Width=80,Value=(decimal)_config.LookAhead(dd),AccessibleName=$"Deck {DeckName(dd)} look-ahead seconds"};
            num.ValueChanged+=(_,_)=>{_config.SetLookAhead(dd,(double)num.Value);ApplyLookAhead(dd);try{if(!_preview)_config.Save();}catch(Exception ex){Log.Write("save look-ahead: "+ex.Message);}};
            lookRow.Controls.Add(new Label{Text=$"Deck {DeckName(dd)}",AutoSize=true,Padding=new Padding(0,6,6,0)});lookRow.Controls.Add(num);
        }
        flow.Controls.Add(lookRow);
        var maps=new StudioButton{Text="Keyboard / controller mappings",Size=new Size(610,32)};maps.Click+=(_,_)=>OpenMappings();flow.Controls.Add(maps);
        var apply=new StudioButton{Text="Apply buffer now (brief output restart)",Size=new Size(610,32)};apply.Click+=async(_,_)=>{if(_preview)return;await SafeAsync(async()=>{await _audioChanges.WaitAsync();try{string? main=_engine.OutputId,cue=_engine.CueId;bool ca=_engine.Mixer.CueA,cb=_engine.Mixer.CueB;await _engine.StopCueAsync();await _engine.StopOutputAsync();if(main!=null)await _engine.SetOutputAsync(main);if(cue!=null)await _engine.SetCueOutputAsync(cue);_engine.Mixer.CueA=ca;_engine.Mixer.CueB=cb;}finally{_audioChanges.Release();}});};flow.Controls.Add(apply);
        return f;
    }
}

