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

    private Label? _diagnostics;
    private ComboBox? _profiles;

    // Canvas map (1366 x 768 logical units, scaled uniformly by DjLayout):
    //   header 4-32 · waveforms 36-236 · deck A x8-548 | mixer x556-810 | deck B x818-1358 · decks 242-606 · library / status 612-768
    private const int DeckTop = 242, DeckBottom = 608, DeckWidth = 540;
    private static int DeckX(int d) => d == 0 ? 8 : 818;
    private readonly Label?[] _sourceBadges = new Label?[2], _padHelp = new Label?[2];
    private readonly StatusChips _chips = new();
    private bool _browsing;
    private readonly List<Control> _browseHidden = new();
    private readonly HashSet<Control> _dynamicText = new();
    private readonly StudioButton?[] _freezeButtons = new StudioButton?[2];

    private void InstallDjShell()
    {
        var dj = _dj = new DjLayout();
        Controls.Clear(); Controls.Add(dj);
        AutoScaleMode=AutoScaleMode.None;
        Text = "HARP//MXR · DJ console"; ClientSize = new Size(1366,768); MinimumSize = new Size(960,540);
        if(!_preview)WindowState=FormWindowState.Maximized;
        Label LabelAt(string text,int x,int y,int w,int h,float size=8,Color? color=null,Color? back=null)
        {
            var l=new Label{Text=text,ForeColor=color??Theme.Muted,BackColor=back??Theme.Background,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
            dj.Place(l,x,y,w,h,size);return l;
        }
        StudioButton Button(string text,int x,int y,int w,int h,Action click,Color? color=null)
        {
            var b=new StudioButton{Text=text,Accent=color??Theme.Muted,AccessibleName=text};
            b.Click+=(_,_)=>click();dj.Place(b,x,y,w,h);return b;
        }

        // ---------------- header: identity · output · master · system status · tools · emergency
        LabelAt("HARP//MXR",10,4,126,28,14,Theme.Text);
        _outputs.DrawMode=DrawMode.OwnerDrawFixed;_outputs.DrawItem+=(_,e)=>{using var bg=new SolidBrush(Theme.Idle);e.Graphics.FillRectangle(bg,e.Bounds);string text=e.Index>=0?_outputs.Items[e.Index]?.ToString()??"Output":"Select master output";TextRenderer.DrawText(e.Graphics,text,_outputs.Font,e.Bounds,Theme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        _outputs.DropDownStyle=ComboBoxStyle.DropDownList; _outputs.BackColor=Theme.Idle;_outputs.ForeColor=Theme.Text;
        _outputs.AccessibleName="Master output device";
        LabelAt("OUT",140,6,30,24,8);dj.Place(_outputs,170,6,196,24);
        _outputs.SelectionChangeCommitted+=async(_,_)=>{if(!_preview)await SafeAsync(SetOutputAsync);};
        LabelAt("MASTER",372,6,54,24,8);_signalBar.Read=()=>_engine.Mixer.Peak;_signalBar.BackColor=Theme.Background;dj.Place(_signalBar,426,6,100,24);
        _master.Vertical=false;_master.BackColor=Theme.Background;dj.Place(_master,526,2,124,32);
        _masterValue.Tag="numeric";_masterValue.BackColor=Theme.Background;_masterValue.TextAlign=ContentAlignment.MiddleLeft;dj.Place(_masterValue,650,6,40,24,9);
        _chips.Read=ReadStatusChips;dj.Place(_chips,692,6,184,24);
        Button("MAPPING",880,5,72,26,OpenMappings,Theme.Cyan).AccessibleName="Controller mappings: press any Xbox button, stick or trigger to bind it";
        Button("MIDI",956,5,44,26,OpenMidiSettings).AccessibleName="MIDI ports, pad notes, clock and chord key";
        _padButton.Text="CONTROLS";dj.Place(_padButton,1004,5,76,26);
        Button("PAD GRID",1084,5,72,26,()=>{PlaceGridFloat();_gridFloat.Visible=!_gridFloat.Visible;if(_gridFloat.Visible)_gridFloat.BringToFront();}).AccessibleName="Floating 4x4 pad grid";
        Button("SETTINGS",1160,5,74,26,()=>ShowDiagnostics());
        dj.Place(_panic,1238,5,120,26);

        // ---------------- performance timeline
        dj.Place(_waves,0,36,1366,200);

        // ---------------- decks (identical structure, mirrored on the right)
        for(int d=0;d<2;d++)
        {
            int dd=d,x=DeckX(d);Color accent=d==0?Theme.Cyan:Theme.Amber;var panel=Panel(d);
            // identity row: letter · what is loaded · position
            _deckTags[d]=LabelAt(DeckName(d),x,DeckTop,30,30,17,accent);_deckTags[d]!.TextAlign=ContentAlignment.MiddleCenter;
            _deckTags[d]!.AccessibleName=$"Deck {DeckName(d)} (bright = the controller's focused deck)";
            _titles[d]=LabelAt("Choose an app or load a track",x+34,DeckTop,300,30,11,Theme.Text);_dynamicText.Add(_titles[d]);
            _times[d]=LabelAt("— / —",x+338,DeckTop,202,30,11,Theme.Text);_times[d].TextAlign=ContentAlignment.MiddleRight;_dynamicText.Add(_times[d]);
            _times[d].Tag = "numeric"; { int dl = d; _times[d].Click += (_, _) => DeckGoLive(dl); }
            // source row: type badge · picker · connection · release
            _sourceBadges[d]=LabelAt("NO SOURCE",x,DeckTop+34,70,26,8,Theme.Muted,Theme.Idle);_sourceBadges[d]!.TextAlign=ContentAlignment.MiddleCenter;
            _sourceBadges[d]!.AccessibleName=$"Deck {DeckName(d)} source type";
            dj.Place(panel.Sources,x+74,DeckTop+34,160,26,9);
            StudioButton? deckBrowser=null;deckBrowser=DeckBrowserButton(Button("BROWSER",x+238,DeckTop+34,80,26,()=>OnDeckBrowserClick(dd,deckBrowser!)),d);
            ((Label)panel.StatusControl).BackColor=Theme.Background;dj.Place(panel.StatusControl,x+322,DeckTop+34,136,26,8);_dynamicText.Add(panel.StatusControl);
            var release=Button("RELEASE",x+462,DeckTop+34,78,26,()=>{if(!_preview)_=SafeAsync(()=>ReleaseDeckAsync(dd));});
            release.AccessibleName="Stop this deck and put the app's sound back where it was";
            // transport: the most used controls sit directly under the deck identity and stay visible in BROWSE
            _plays[d]=Button("PLAY / PAUSE",x,DeckTop+64,140,30,()=>Dispatch($"{DeckName(dd)}.PlayPause",true),accent);
            _plays[d].AccessibleName=$"Deck {DeckName(d)} play / pause";
            _syncButtons[d]=Button("SYNC",x+144,DeckTop+64,64,30,()=>{if(!_preview)SetMessage(DeckSync.Sync(_engine,dd));},accent);
            _masterButtons[d]=Button("MASTER",x+212,DeckTop+64,70,30,()=>{if(Source(0).ActivePid==null||Source(1).ActivePid==null){SetMessage("MASTER needs sources on both decks.");return;}_engine.Sync.Enable(1-dd);SetFocus(dd);SetMessage($"Deck {DeckName(dd)} is sync master; the other deck follows.");},accent);
            Button("TAP",x+286,DeckTop+64,56,30,()=>Dispatch($"{DeckName(dd)}.TapBpm",true),accent).AccessibleName=$"Deck {DeckName(d)} tap tempo";
            Button("−",x+346,DeckTop+64,44,30,()=>_monitor.StepNudge(dd,-.5),accent).AccessibleName=$"Deck {DeckName(d)} nudge slower";
            Button("+",x+394,DeckTop+64,44,30,()=>_monitor.StepNudge(dd,.5),accent).AccessibleName=$"Deck {DeckName(d)} nudge faster";
            Button("RESET",x+442,DeckTop+64,98,30,()=>{DeckSync.SetNudge(_engine,dd,0);_monitor.ResetNudge(dd);},accent).AccessibleName=$"Deck {DeckName(d)} reset tempo nudge";
            // FX: the current stick FX set (click = next set) and its four effects (hold = play); the XY pad plays the set like the right stick
            InstallSetButtons(dd,x,DeckTop+98,accent,Button);
            var freeze=Button("FREEZE",x+325,DeckTop+98,62,24,()=>{_mouseFreeze[dd]=!_mouseFreeze[dd];UpdateMouseFx(dd,.5,.5,_mouseFxHeld[dd]);},accent);
            _freezeButtons[d]=freeze;freeze.AccessibleName=$"Deck {DeckName(d)} freeze the effect amount";
            var echo=Button("ECHO THROW",x+390,DeckTop+98,85,24,()=>{},accent);
            echo.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){_engine.Mixer.Echo(dd).Send=.7f;echo.Active=echo.Capture=true;}};
            echo.MouseUp+=(_,_)=>{_engine.Mixer.Echo(dd).Send=0;echo.Active=echo.Capture=false;};
            echo.MouseCaptureChanged+=(_,_)=>{if(!echo.Capture){_engine.Mixer.Echo(dd).Send=0;echo.Active=false;}};
            var riser=Button("RISER",x+478,DeckTop+98,62,24,()=>{},accent);
            riser.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){SetFocus(dd,true);_engine.Mixer.Riser.Build=.5f;_engine.Mixer.Riser.Amount=.6f;riser.Active=riser.Capture=true;}};
            riser.MouseUp+=(_,_)=>{_engine.Mixer.Riser.StopAfter(0);riser.Active=riser.Capture=false;};
            riser.MouseCaptureChanged+=(_,_)=>{if(!riser.Capture){_engine.Mixer.Riser.StopAfter(0);riser.Active=false;}};
            var roll=Button("BEAT ROLL",x,DeckTop+126,80,32,()=>{},accent);
            roll.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){Dispatch($"{DeckName(dd)}.Roll:1",true);roll.Active=roll.Capture=true;}};
            roll.MouseUp+=(_,_)=>{Dispatch($"{DeckName(dd)}.Roll:1",false);roll.Active=roll.Capture=false;};
            roll.MouseCaptureChanged+=(_,_)=>{if(!roll.Capture){Dispatch($"{DeckName(dd)}.Roll:1",false);roll.Active=false;}};
            var xy=new XyPad{Accent=accent};xy.Changed+=(px,py,on)=>UpdateMouseFx(dd,px,py,on);dj.Place(xy,x+84,DeckTop+126,168,32,8);
            InstallStickReadouts(dj, d, x + 256, DeckTop + 126, accent);
            // performance pads
            string[] modes=["HOT CUE","SAMPLE","ONE-SHOT","CHORD","DRUM","LOOP","BEAT JUMP"];
            for(int m=0;m<modes.Length;m++)
            {
                var mode=modes[m];var b=Button(mode,x+(int)Math.Round(m*77.5),DeckTop+162,75,26,()=>SelectPadMode(dd,mode),accent);
                b.AccessibleName=$"Deck {DeckName(d)} pad mode {mode}";_modeButtons.Add((dd,mode,b));
            }
            _padHelp[d]=LabelAt("",x,DeckTop+190,DeckWidth,18,8,accent);_dynamicText.Add(_padHelp[d]!);
            for(int k=0;k<16;k++)
            {
                int key=k;var b=Button((k+1).ToString(),x+(k%4)*77,DeckTop+210+(k/4)*32,74,29,()=>{},accent);
                b.AccessibleName=$"Deck {DeckName(d)} pad {k+1}";
                b.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){DjPadDown(dd,key,(ModifierKeys&Keys.Shift)!=0);b.Capture=true;}};
                b.MouseUp+=(_,_)=>{DjPadUp(dd,key);b.Capture=false;};
                b.MouseCaptureChanged+=(_,_)=>{if(!b.Capture)DjPadUp(dd,key);};
                b.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Space&&!b.Active){DjPadDown(dd,key,e.Shift);e.Handled=true;}};
                b.KeyUp+=(_,e)=>{if(e.KeyCode==Keys.Space){DjPadUp(dd,key);e.Handled=true;}};
                _cells[d][k]=b;
            }
            // pad, loop and tempo cluster beside the pads
            _jogs[d].Accent=accent;_jogs[d].Read=()=>JogTempo(dd);_jogs[d].KeyText=()=>DeckKey(dd);_jogs[d].BackColor=Theme.Background;
            dj.Place(_jogs[d],x+310,DeckTop+208,118,94);
            Button("SLIP LOOP",x+310,DeckTop+306,118,29,()=>Dispatch($"{DeckName(dd)}.Loop",true),accent).AccessibleName=$"Deck {DeckName(d)} slip loop";
            Button("HALVE",x+432,DeckTop+210,108,29,()=>Dispatch($"{DeckName(dd)}.LoopHalve",true)).AccessibleName=$"Deck {DeckName(d)} halve loop";
            Button("DOUBLE",x+432,DeckTop+242,108,29,()=>Dispatch($"{DeckName(dd)}.LoopDouble",true)).AccessibleName=$"Deck {DeckName(d)} double loop";
            Button("QUANTIZE",x+432,DeckTop+274,108,29,CycleQuantize);
            Button("BANK / MODE",x+432,DeckTop+306,108,29,()=>{SetFocus(dd);CycleBankMode();});
            _buttonHints[d].BackColor=Theme.Background;dj.Place(_buttonHints[d],x,DeckTop+340,DeckWidth,24);
        }

        // ---------------- mixer: two channel strips around the meters, then crossfader and cue
        for(int d=0;d<2;d++)
        {
            int dd=d,sx=d==0?558:710;var panel=Panel(d);var accent=d==0?Theme.Cyan:Theme.Amber;
            LabelAt(DeckName(d),sx,DeckTop,100,22,11,accent).TextAlign=ContentAlignment.MiddleCenter;
            panel.Trim.Vertical=false;panel.Trim.BackColor=Theme.Background;dj.Place(panel.Trim,sx,DeckTop+22,100,28);
            LabelAt("TRIM",sx,DeckTop+50,100,16,8).TextAlign=ContentAlignment.MiddleCenter;
            var knobs=new[]{panel.High,panel.Mid,panel.Low};var kills=new[]{panel.HighKill,panel.MidKill,panel.LowKill};
            for(int i=0;i<3;i++)
            {
                knobs[i].BackColor=Theme.Background;
                dj.Place(knobs[i],d==0?592:712,DeckTop+70+i*56,64,54);
                dj.Place(kills[i],d==0?558:780,DeckTop+84+i*56,30,24,8);
            }
            panel.Filter.BackColor=Theme.Background;dj.Place(panel.Filter,sx+10,DeckTop+238,80,54);
            panel.MeterControl.BackColor=Theme.Background;dj.Place(panel.MeterControl,d==0?662:686,DeckTop+22,20,320,8);
            dj.Place(panel.MuteControl,sx,DeckTop+296,100,24,8);
            _cues[d]=Button("CUE "+DeckName(d),sx,DeckTop+324,100,30,()=>{if(!_preview)_=CueToggledAsync(dd,!(dd==0?_engine.Mixer.CueA:_engine.Mixer.CueB));else SetMessage("Preview: headphone output stays off.");},accent);
            _cues[d].AccessibleName=$"Headphone cue deck {DeckName(d)}";
        }
        LabelAt("A  ◂  CROSSFADER  ▸  B",558,DeckTop+360,250,18,8).TextAlign=ContentAlignment.MiddleCenter;
        _cross.BackColor=Theme.Background;dj.Place(_cross,558,DeckTop+378,250,40);
        LabelAt("CUE VOL",558,DeckTop+424,80,16,8).TextAlign=ContentAlignment.MiddleCenter;
        LabelAt("CUE / MIX",728,DeckTop+424,80,16,8).TextAlign=ContentAlignment.MiddleCenter;
        dj.Place(_monitor.VolumeControl,558,DeckTop+440,80,30);dj.Place(_monitor.BlendControl,728,DeckTop+440,80,30);
        var hp=Button("PHONES",642,DeckTop+428,82,42,()=>ShowDiagnostics());
        hp.AccessibleName="Choose headphone output for cue listening";

        // ---------------- library (left) · automation and controller status (right)
        _library=new LibraryView(_preview,0);_library.LoadRequested+=(d,path)=>{if(!_preview)_=SafeAsync(()=>LoadSongAsync(Panel(d),Source(d),d==0?_takeoverA:_takeoverB,d,path));};
        _library.Browse.Click+=(_,_)=>SetBrowsing(!_browsing);
        dj.Place(_library,8,DeckBottom+4,DeckWidth,768-DeckBottom-8,9);
        int rx=DeckX(1);
        // Auto Mix transition card (AutoMixBar.DesignHeight); the status, PROFILE and message rows sit below it.
        int am=AutoMixBar.DesignHeight;
        dj.Place(_autoBar,rx,DeckBottom+4,DeckWidth,am);
        _performanceStatus=LabelAt("BANK 1/4 · QUANT 1/16 · MIDI off",rx,DeckBottom+am+6,DeckWidth,20,8);_dynamicText.Add(_performanceStatus);
        LabelAt("PROFILE",rx,DeckBottom+am+28,58,24,8);
        InstallControllerProfileBox(dj, rx+60, DeckBottom+am+28, 170, 24);
        _padEnabled.BackColor=Theme.Background;dj.Place(_padEnabled,rx+238,DeckBottom+am+28,104,24,8);
        _padStatus.BackColor=Theme.Background;dj.Place(_padStatus,rx+346,DeckBottom+am+28,194,24,8);_dynamicText.Add(_padStatus);
        _message.BackColor=Theme.Background;dj.Place(_message,rx,DeckBottom+am+54,DeckWidth,768-DeckBottom-am-58,8);_message.TextAlign=ContentAlignment.TopLeft;_dynamicText.Add(_message);
        _gridCenter.Visible=false;_hud.Visible=false;
        InstallControllerFeedback(dj);
        InstallFxOverlay(dj);
        Controls.Add(_overlay);Controls.Add(_gridFloat);
        ShowFocus();
        dj.PerformLayout();
    }

    /// <summary>BROWSE: the library grows over deck A's FX and pads; waveforms, both transports, the mixer and deck B stay in view.
    /// Covered controls are hidden (not just overdrawn) so they cannot be clicked or tabbed to; controller actions keep working.</summary>
    private void SetBrowsing(bool on)
    {
        if(_dj==null||_library==null||_browsing==on)return;
        _browsing=on;
        var covered=new RectangleF(8,DeckTop+96,DeckWidth,DeckBottom-DeckTop-96);
        if(on)
        {
            _browseHidden.Clear();
            foreach(var c in _dj.ControlsWithin(covered,_library).ToList())if(c.Visible){c.Visible=false;_browseHidden.Add(c);}
            _dj.Move(_library,8,DeckTop+98,DeckWidth,768-DeckTop-102);
        }
        else
        {
            _dj.Move(_library,8,DeckBottom+4,DeckWidth,768-DeckBottom-8);
            foreach(var c in _browseHidden)c.Visible=true;
            _browseHidden.Clear();
        }
        _library.Browse.Text=on?"PERFORM ▼":"BROWSE ▲";_library.Browse.Active=on;
        _library.BringToFront();_dj.Invalidate();
    }

    private StatusChips.Chip[] ReadStatusChips()
    {
        var audio=_preview?new StatusChips.Chip("OFFLINE",Theme.Muted)
            :_engine.Mixer.Mute?new("MUTED",Theme.Red)
            :_engine.OutputId!=null?new("AUDIO",Theme.Green):new("NO OUT",Theme.Amber);
        var pad=!_padEnabled.Checked?new StatusChips.Chip("CTRL",Theme.Edge)
            :_padWasConnected?new("CTRL",Theme.Green):new("CTRL",Theme.Amber);
        // green only when a port is really open; "MIDI on but no port" is a warning, not a success
        var midi=_midi?.Port!=null?new StatusChips.Chip("MIDI",Theme.Green)
            :_config.MidiEnabled?new("MIDI",Theme.Amber):new("MIDI",Theme.Edge);
        return [audio,pad,midi];
    }
    private string? DeckKey(int d)
    {
        var key=_engine.Mixer.Key(d);
        return key.Key is MusicalKey k&&key.Confidence>=KeyConfidenceMin?ChordTheory.KeyName(k):null;
    }
    private string PadHelp(int d)
    {
        var src=Source(d);bool midi=_midi?.Port!=null;
        string notes=midi?$" · MIDI notes {PadBanks.Note(_config.MidiNoteBase,0,0)}–{PadBanks.Note(_config.MidiNoteBase,3,3)}":"";
        return _padModes[d] switch
        {
            "HOT CUE"=>src.IsFile?"HOT CUE · empty pad sets a cue · lit pad jumps · SHIFT+click overwrites":"HOT CUE needs a song file · live app audio cannot jump back",
            "SAMPLE"=>"SAMPLE · rows = banks 1–4 · click plays · SHIFT+click captures"+notes,
            "ONE-SHOT"=>"ONE-SHOT · rows = banks 1–4 · click fires once · SHIFT+click captures"+notes,
            "CHORD"=>"CHORD · rows = banks 1–4 · pads play chords in the current key"+notes,
            "DRUM"=>"DRUM · rows = banks 1–4 · pads play drum voices"+notes,
            "LOOP"=>"LOOP · hold to roll 1/8 … 16 beats · release returns to the track",
            "BEAT JUMP"=>"BEAT JUMP · top rows jump back · bottom rows jump forward",
            _=>""
        };
    }

    private readonly Label?[] _deckTags=new Label?[2];
    /// <summary>Letter and ring identify the active deck independently of hue.</summary>
    private void ShowFocus()
    {
        MoveFocusRing(_focus);
        for(int d=0;d<2;d++)
        {
            if(_deckTags[d] is not Label t)continue;
            var accent=d==0?Theme.Cyan:Theme.Amber;bool on=d==_focus;
            t.BackColor=on?accent:Theme.Background;t.ForeColor=on?Theme.Background:accent;
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
        _mouseFxHeld[d]=on;
        MouseSetPad(d,x,y,on);   // the XY pad plays the current stick FX set, like the right stick (MainForm.StickFx.cs)
    }
    private (double Bpm, double Phase) JogTempo(int deck)
    {
        var heard = Tempo(deck);
        if (heard.Bpm > 0) return heard;
        var track = _tracks[deck];
        var source = Source(deck);
        if (track == null || track.Path != source.FilePath || !track.Grid.Valid) return heard;
        var reader = Reader(deck);
        double position = Math.Max(0, source.Position.TotalSeconds - reader.BufferedMs / 1000.0);
        double beats = (position - track.Grid.FirstBeatSec) / track.Grid.PeriodSec;
        return (track.Grid.Bpm * reader.EffectiveRate, beats - Math.Floor(beats));
    }

    private void RefreshDj()
    {
        if(_dj==null)return;
        _signalBar.Invalidate();
        RefreshControllerFeedback(_lastFrame, _padEnabled.Checked && _padWasConnected && !_bindingsOpen, _padMapper.Shift);
        if(_performanceStatus!=null)
        {
            bool recent=_moveText.Length>0&&Environment.TickCount64-_moveAt<1600;
            string text=recent?$"▶ {_moveText}":$"BANK {_bank+1}/4 · QUANT {PadQuant.Label(_config.Quantize)} · {_midi?.Status??"MIDI: off"}";
            if(_performanceStatus.Text!=text){_performanceStatus.Text=text;_performanceStatus.ForeColor=recent?(_focus==0?Theme.Cyan:Theme.Amber):Theme.Muted;}
        }
        _chips.Tick();
        for(int d=0;d<2;d++)
        {
            var src=Source(d);
            bool hasLive=!src.IsFile&&src.ActivePid is >0;
            if(_sourceBadges[d] is Label badge)
            {
                string kind=src.IsFile?"FILE":hasLive?"LIVE APP":"NO SOURCE";
                if(badge.Text!=kind){badge.Text=kind;var accent=d==0?Theme.Cyan:Theme.Amber;badge.ForeColor=kind=="NO SOURCE"?Theme.Muted:Theme.Background;badge.BackColor=kind=="NO SOURCE"?Theme.Idle:accent;}
            }
            if(_padHelp[d] is Label help){string text=PadHelp(d);if(help.Text!=text)help.Text=text;}
            if(_freezeButtons[d] is StudioButton fz)fz.Active=_mouseFreeze[d];
            _titles[d].Text=src.IsFile?Path.GetFileName(src.FilePath):Panel(d).SelectedApp?.Name??"Choose an app or load a track";
            _times[d].Text=src.IsFile?$"{Mmss(src.Position)}  −{Mmss(src.Duration-src.Position)}":src.ActivePid!=null?"LIVE":"— / —";
            _cues[d].Active=d==0?_engine.Mixer.CueA:_engine.Mixer.CueB;
            bool paused=DeckIsPaused(d),live=!src.IsFile&&src.ActivePid is >0;
            _plays[d].Text=src.IsFile||live?(paused?(_pauseLabel[d]??"PAUSED")+" · PLAY":"PAUSE"):"PLAY / PAUSE";_plays[d].Active=(src.IsFile||live)&&!paused;
            if(live)TickBufferedPause(d);
            if(live&&paused)_times[d].Text=_pauseLabel[d]=="FROZEN"?"FROZEN · app plays":_appHeld[d]?$"PAUSED · app waits":$"PAUSED · {Reader(d).BufferedMs/1000} s saved";
            else if(live&&BehindLiveMs(d)>=1000)_times[d].Text=$"−{BehindLiveMs(d)/1000} s · GO LIVE";
            else if(live&&Reader(d).LookAheadMs>0)_times[d].Text=$"LIVE · {Reader(d).LookAheadMs/1000.0:0.#} s ahead";
            if(live&&!paused&&MemoryTag(d) is {} tag)_times[d].Text+=" · "+tag;
            if(src.IsFile&&src.FilePath!=null)
            {
                var track=_tracks[d];double bpm=track?.Path==src.FilePath&&track.Grid.Valid?track.Grid.Bpm:0;
                var key=_engine.Mixer.Key(d);string? name=key.Key is MusicalKey k&&key.Confidence>=KeyConfidenceMin?ChordTheory.KeyName(k):null;
                _library?.SetMetadata(src.FilePath,bpm,name,src.Duration);
            }
            _jogs[d].Invalidate();
            _syncButtons[d].Active=_engine.Sync.Role(d)==SyncRole.Slave;_masterButtons[d].Active=_engine.Sync.Role(d)==SyncRole.Master;

            var padAccent=d==0?Theme.Cyan:Theme.Amber;bool midiOut=_midi?.Port!=null;
            for(int k=0;k<16;k++)
            {
                var b=_cells[d][k];string text=(k+1).ToString();Color? lamp=null;bool loaded;
                switch(_padModes[d])
                {
                    case "HOT CUE":
                        double? cue=src.IsFile&&src.FilePath!=null?_hotCues.Get(src.FilePath,k):null;
                        text=cue is double c?$"{k+1} · {Mmss(TimeSpan.FromSeconds(c))}":$"{k+1}";loaded=cue!=null;break;
                    case "LOOP": text=new[]{"1/8","1/4","1/2","1","2","4","8","16"}[k%8];loaded=true;break;
                    case "BEAT JUMP": text=(k<8?"◂ ":"▸ ")+new[]{"1/8","1/4","1/2","1","2","4","8","16"}[k%8];loaded=true;break;
                    default:
                        loaded=Sampler(d).Length(k)>0||ModeOf(k/4) is BankMode.Chord or BankMode.Drum;
                        // the note this pad sends, shown only while a MIDI port is really open
                        if(midiOut)text=$"{k+1}  ·  n{PadBanks.Note(_config.MidiNoteBase,k/4,k%4)}";
                        break;
                }
                if(_padModes[d]=="HOT CUE"&&loaded||(_padModes[d] is not ("HOT CUE" or "LOOP" or "BEAT JUMP")&&Sampler(d).Length(k)>0))lamp=padAccent;
                if(_dawLit[k]&&d==_focus)lamp=Theme.Text;
                if(b.Text!=text)b.Text=text;
                b.Lamp=lamp;
                bool playing=(_engine.Mixer.Pads(d).PlayingMask&(1<<k))!=0||Sampler(d).PlayingPad==k;
                b.Active=_mousePadHeld[d,k]||playing||(_latched[k]&&_latchDeck[k]==d);
                b.ForeColor=loaded?padAccent:Theme.Muted;
                bool usable=_padModes[d]!="HOT CUE"||src.IsFile;
                if(b.Enabled!=usable)b.Enabled=usable;
            }
        }
        foreach(var (d,mode,b) in _modeButtons)b.Active=_padModes[d]==mode;
        if(_diagnostics!=null&&!_diagnostics.IsDisposed)_diagnostics.Text=_engine.Timing+"\nRequested buffer: "+_engine.Profile.RequestedMs+" ms. Applies when outputs reopen.\nCallback timings are not end-to-end latency.\nShared WASAPI · Remote off · Protected apps: "+MixDock.Platform.ProtectedApps.Summary;
    }
    private void ShowDiagnostics(){using var f=CreateDiagnostics();f.ShowDialog(this);_diagnostics=null;_profiles=null;}
    private (CheckBox Enable,StudioButton Forget,StudioButton Clear)? _memoryControls;
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
        flow.Controls.Add(new Label{Text="Waveform memory: HARP//MXR recognises tracks it has heard and draws the whole waveform at once. Only derived data (waveform, fingerprint, analysis) is stored on this PC, never audio.",AutoSize=false,Width=610,Height=52,ForeColor=Theme.Muted});
        var memory=new CheckBox{Text="Remember tracks (waveform memory)",AutoSize=true,Checked=_config.WaveformMemory,AccessibleName="Waveform memory on or off"};
        memory.CheckedChanged+=(_,_)=>{_config.WaveformMemory=memory.Checked;try{if(!_preview)_config.Save();}catch(Exception ex){Log.Write("save waveform memory: "+ex.Message);}};
        flow.Controls.Add(memory);
        var memoryRow=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=false};
        var forget=new StudioButton{Text="Forget this track",Size=new Size(200,32),AccessibleName="Forget the track on the focused deck"};forget.Click+=(_,_)=>ForgetFocusedTrack();
        var clear=new StudioButton{Text="Clear cache",Size=new Size(200,32),AccessibleName="Clear the waveform memory cache"};clear.Click+=(_,_)=>ClearTrackCache();
        memoryRow.Controls.Add(forget);memoryRow.Controls.Add(clear);flow.Controls.Add(memoryRow);
        _memoryControls=(memory,forget,clear);
        var maps=new StudioButton{Text="Keyboard / controller mappings",Size=new Size(610,32)};maps.Click+=(_,_)=>OpenMappings();flow.Controls.Add(maps);
        var apply=new StudioButton{Text="Apply buffer now (brief output restart)",Size=new Size(610,32)};apply.Click+=async(_,_)=>{if(_preview)return;await SafeAsync(async()=>{await _audioChanges.WaitAsync();try{string? main=_engine.OutputId,cue=_engine.CueId;bool ca=_engine.Mixer.CueA,cb=_engine.Mixer.CueB;await _engine.StopCueAsync();await _engine.StopOutputAsync();if(main!=null)await _engine.SetOutputAsync(main);if(cue!=null)await _engine.SetCueOutputAsync(cue);_engine.Mixer.CueA=ca;_engine.Mixer.CueB=cb;}finally{_audioChanges.Release();}});};flow.Controls.Add(apply);
        return f;
    }
}

