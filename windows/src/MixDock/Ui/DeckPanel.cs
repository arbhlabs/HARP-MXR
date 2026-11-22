using MixDock.Audio;
using MixDock.Platform;
using MixDock.Settings;

namespace MixDock.Ui;

public interface IDeckView
{
    AudioApp? SelectedApp {get;}
    event EventHandler? ParametersChanged;
    event EventHandler? ArmRequested;
    DeckParameters GetParameters();
    void SetPeak(float peak);
    void SetApps(IEnumerable<AudioApp> apps);
    void SetStatus(string text,bool live=false);
}

public sealed class DeckPanel:SurfacePanel,IDeckView
{
    public readonly AppPicker Sources=new();
    public readonly Knob High=new(){Caption="HIGH",Accent=Color.FromArgb(114,176,255),AccessibleName="High EQ"},
        Mid=new(){Caption="MID",Accent=Color.FromArgb(118,214,166),AccessibleName="Mid EQ"},
        Low=new(){Caption="LOW",Accent=Color.FromArgb(238,188,108),AccessibleName="Low EQ"},
        Filter=new(){Caption="FILTER",AccessibleName="Filter, low-pass to high-pass"};
    public readonly Fader Trim=new(){Vertical=true,AccessibleName="Deck trim",ResetValue=85};
    public readonly StudioButton HighKill=new(){Text="KILL",AccessibleName="Kill high frequencies"},
        MidKill=new(){Text="KILL",AccessibleName="Kill mid frequencies"},LowKill=new(){Text="KILL",AccessibleName="Kill low frequencies"};
    public readonly PadRow Pads=new();
    private readonly LevelMeter _meter=new();
    private readonly StudioButton _mute=new(){Text="MUTE DECK"};
    private readonly Label _status=new(){AutoEllipsis=true,Text="Choose an app to start",ForeColor=Theme.Muted,BackColor=Theme.Surface};
    public Control MeterControl => _meter;
    public Control MuteControl => _mute;
    public Control StatusControl => _status;
    private readonly string _deck;
    private readonly Color _accent;
    private bool _muted,_connected;
    private float _peak;
    public AudioApp? SelectedApp=>Sources.SelectedItem as AudioApp;
    public int? SelectedPid=>SelectedApp?.Pid;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Muted {get=>_muted;set{_muted=value;_mute.Active=value;_mute.Text=value?"DECK MUTED":"MUTE DECK";_mute.Invalidate();ParametersChanged?.Invoke(this,EventArgs.Empty);}}
    public event EventHandler? ParametersChanged;
    public event EventHandler? ArmRequested;
    public event EventHandler? DisarmRequested;
    public void RequestDisarm() => DisarmRequested?.Invoke(this, EventArgs.Empty);
    public DeckPanel(string deck,Color accent,DeckState state)
    {
        _deck=deck;_accent=accent;Filter.Accent=Trim.Accent=_meter.Accent=_mute.Accent=accent;
        High.Value=state.High;Mid.Value=state.Mid;Low.Value=state.Low;Filter.Value=state.Filter;Trim.Value=state.Trim;
        _status.Font=Theme.Font(8.5f);Muted=state.Mute;
        Controls.AddRange([Sources,High,Mid,Low,Filter,Trim,HighKill,MidKill,LowKill,_meter,_mute,_status,Pads]);
        Pads.Accent=accent;
        foreach(var knob in new[]{High,Mid,Low,Filter})knob.ValueChanged+=(_,_)=>ParametersChanged?.Invoke(this,EventArgs.Empty);
        Trim.ValueChanged+=(_,_)=>{Invalidate();ParametersChanged?.Invoke(this,EventArgs.Empty);};
        foreach(var (kill,knob,band) in new[]{(HighKill,High,2),(MidKill,Mid,1),(LowKill,Low,0)}){
            kill.Accent=knob.Accent;
            kill.Click+=(_,_)=>ToggleKill(band);
        }
        _mute.Click+=(_,_)=>Muted=!Muted;
        // Committed user selection only: populating or refreshing the picker must never arm a source.
        Sources.SelectionChangeCommitted+=(_,_)=>ArmRequested?.Invoke(this,EventArgs.Empty);
        AccessibleName=$"Deck {deck}";
    }
    public void SetApps(IEnumerable<AudioApp> apps)
    {
        var old=SelectedApp;
        var distinct=apps.GroupBy(a=>AppPicker.Key(a.Name)).Select(g=>
            g.FirstOrDefault(a=>a.Pid==old?.Pid)??g.FirstOrDefault(a=>!a.Name.Contains(" ("))??g.First())
            .ToList();
        Sources.BeginUpdate();Sources.Items.Clear();Sources.Items.Add(new MixDock.Platform.AudioApp(-2, "Load a song…")); Sources.Items.AddRange(distinct.Cast<object>().ToArray());
        if(old!=null)Sources.SelectedItem=distinct.FirstOrDefault(a=>AppPicker.Key(a.Name)==AppPicker.Key(old.Name));
        Sources.EndUpdate();Sources.Invalidate();
    }
    public void SetPeak(float peak)
    {
        _meter.SetPeak(peak);
        bool wasPlaying=_peak>.001f;_peak=peak;
        if(wasPlaying!=(_peak>.001f))Invalidate(new Rectangle(0,0,Width,(int)(64*DeviceDpi/96f)));
    }
    private bool _padFocus;
    /// <summary>This deck is the controller's focus deck (LB/RB): drawn as a small tag next to the title.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PadFocus {get=>_padFocus;set{if(_padFocus==value)return;_padFocus=value;Invalidate();}}
    /// <summary>0 = low, 1 = mid, 2 = high.</summary>
    private bool _bassCut;
    public void ToggleBassCut()
    {
        _bassCut = !_bassCut;
        LowKill.Active = _bassCut;
        LowKill.Text = _bassCut ? "−24" : "KILL";
        LowKill.Invalidate();
        ParametersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleKill(int band)
    {
        var k=band==0?LowKill:band==1?MidKill:HighKill;
        if (band == 0 && _bassCut)
        {
            _bassCut = false;
            k.Active = false;
        }
        k.Active=!k.Active;k.Text="KILL";k.Invalidate();ParametersChanged?.Invoke(this,EventArgs.Empty);
    }
    public void ResetEq()
    {
        _bassCut = false;
        Low.Value=Mid.Value=High.Value=50;
        foreach(var k in new[]{LowKill,MidKill,HighKill}){k.Active=false;k.Text="KILL";k.Invalidate();}
        ParametersChanged?.Invoke(this,EventArgs.Empty);
    }
    public void SetStatus(string text,bool live=false){_status.Text=text;_connected=live;Invalidate();}
    public DeckParameters GetParameters()=>new((Low.Value-50)*.24,(Mid.Value-50)*.24,(High.Value-50)*.24,
        (Filter.Precise-50)/50.0,Trim.Value/100.0,Muted,LowKill.Active && !_bassCut,MidKill.Active,HighKill.Active,_bassCut);
    public DeckState GetState()=>new(){Low=Low.Value,Mid=Mid.Value,High=High.Value,Filter=Filter.Value,Trim=Trim.Value,Mute=Muted,LastProcessName=SelectedApp is null?null:AppPicker.Key(SelectedApp.Name)};
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(Sources is null)return;float s=DeviceDpi/96f;
        int D(float n)=>(int)Math.Round(n*s);float w=Width/s,h=Height/s;
        void Place(Control c,float x,float y,float cw,float ch)=>c.SetBounds(D(x),D(y),D(cw),D(ch));
        Place(Sources,24,64,w-48,52);Place(_status,24,124,w-48,24);
        float eqx=20,second=w*.43f,meter=w-78;
        Place(High,eqx,152,112,96);Place(HighKill,eqx+24,248,64,24);
        Place(Mid,eqx,272,112,96);Place(MidKill,eqx+24,368,64,24);
        Place(Low,eqx,392,112,96);Place(LowKill,eqx+24,488,64,24);
        Place(Filter,second-8,152,128,96);
        Place(Trim,second+24,312,64,152);
        Place(_meter,meter,152,64,328);
        Place(_mute,second,488,Math.Max(112,w-second-24),28);
        Place(Pads,24,Math.Max(520,h-50),w-48,40);
    }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "DeckPanel.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        base.OnPaint(e);float s=DeviceDpi/96f;int D(float n)=>(int)(n*s);
        using(var p=new Pen(_accent,3*s))e.Graphics.DrawLine(p,D(24),D(1),Width-D(24),D(1));
        Theme.TextAt(e.Graphics,$"DECK {_deck}",new(D(24),D(20),D(128),D(28)),13,_accent,true);
        if(_padFocus)Theme.TextAt(e.Graphics,"◆ PAD",new(D(118),D(22),D(72),D(24)),7.5f,_accent,true);
        bool playing=_connected&&_peak>.001f&&!Muted;
        using(var b=new SolidBrush(playing?_accent:Theme.Muted))Theme.Ellipse(e.Graphics,b,new RectangleF(Width-D(88),D(31),D(6),D(6)));
        Theme.TextAt(e.Graphics,_connected?(Muted?"MUTED":"LIVE"):"IDLE",new(Width-D(76),D(22),D(64),D(24)),7.5f,_connected&&!Muted?_accent:Theme.Muted,true);
        using(var p=new Pen(Theme.Edge))e.Graphics.DrawLine(p,D(24),D(144),Width-D(24),D(144));
        int second=D(Width/s*.43f);
        Theme.TextAt(e.Graphics,"LP  ·  OFF  ·  HP",new(second-D(8),D(248),D(128),D(24)),7.5f,Theme.Muted,false,true);
        Theme.TextAt(e.Graphics,"TRIM",new(second+D(24),D(288),D(64),D(24)),8.5f,Theme.Muted,true,true);
        Theme.TextAt(e.Graphics,$"{Trim.Value}%",new(second+D(24),D(456),D(64),D(24)),9,_accent,true,true);
    }
}

