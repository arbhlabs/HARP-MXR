using System.Drawing.Drawing2D;
using System.ComponentModel;
namespace MixDock.Ui;

public sealed class Knob : Control
{
    private int _value=50,_initial; private Point _start; private bool _drag,_hover;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Caption {get;set;}="EQ";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Bipolar {get;set;}=true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Accent {get;set;}=Theme.Cyan;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value {get=>_value;set {int v=Math.Clamp(value,0,100);if(v==_value&&_precise==v)return;_value=v;_precise=v;Invalidate();ValueChanged?.Invoke(this,EventArgs.Empty);}}
    public event EventHandler? ValueChanged;
    /// <summary>Raised only for user edits (mouse, wheel, keys, double-click), not for programmatic/pad writes.</summary>
    public event EventHandler? UserEdited;
    private double _precise=50;
    /// <summary>Full-precision value (the pad's fine filter drives fractions; Value is the rounded view).</summary>
    public double Precise=>_precise;
    public void SetPrecise(double v){v=Math.Clamp(v,0,100);if(v==_precise)return;_precise=v;int iv=(int)Math.Round(v);if(iv!=_value){_value=iv;}Invalidate();ValueChanged?.Invoke(this,EventArgs.Empty);}
    private void UserSet(int v){int before=_value;double bp=_precise;Value=v;if(_value!=before||_precise!=bp)UserEdited?.Invoke(this,EventArgs.Empty);}
    public Knob() {Size=new(112,116);DoubleBuffered=true;ResizeRedraw=true;TabStop=true;Cursor=Cursors.Hand;BackColor=Theme.Surface;AccessibleRole=AccessibleRole.Slider;}
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "Knob.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        if(Height<80*UiMetrics.Scale(this)){PaintCompact(e.Graphics);return;}
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=Math.Min(UiMetrics.Scale(this),Math.Min(Height/96f,Width/80f)),cx=Width/2f,cy=46*s,r=24*s;
        Theme.TextAt(g,Caption,new(0,0,Width,(int)(19*s)),8.5f,Theme.Muted,true,true);
        var ring=new RectangleF(cx-r,cy-r,2*r,2*r);
        using(var p=new Pen(Theme.Edge,4*s){StartCap=LineCap.Round,EndCap=LineCap.Round})Theme.Arc(g,p,ring,135,270);
        float delta=Bipolar?(float)(_precise-50)*2.7f:(float)_precise*2.7f;
        using(var p=new Pen(Accent,4*s){StartCap=LineCap.Round,EndCap=LineCap.Round})
            if(float.IsFinite(delta)&&Math.Abs(delta)>.01)Theme.Arc(g,p,ring,Bipolar?Math.Min(270,270+delta):135,Math.Abs(delta));
        using(var p=new Pen(Theme.Muted,s))g.DrawLine(p,cx,cy-r-6*s,cx,cy-r-3*s);
        var body=new RectangleF(cx-19*s,cy-19*s,38*s,38*s);
        using(var b=new SolidBrush(Color.FromArgb(8,10,16)))Theme.Ellipse(g,b,new RectangleF(body.X,body.Y+3*s,body.Width,body.Height));
        using(var b=new SolidBrush(Theme.Raised))g.FillEllipse(b,body);
        using(var p=new Pen(_hover||Focused?Accent:Color.FromArgb(75,82,100),s))Theme.Ellipse(g,p,body);
        double angle=(135+_precise*2.7)*Math.PI/180;
        using(var p=new Pen(Theme.Text,2.5f*s){StartCap=LineCap.Round,EndCap=LineCap.Round})
            g.DrawLine(p,cx+(float)Math.Cos(angle)*10*s,cy+(float)Math.Sin(angle)*10*s,cx+(float)Math.Cos(angle)*16*s,cy+(float)Math.Sin(angle)*16*s);
        string value=Bipolar?$"{(Value-50)*.24:+0.0;-0.0;0.0} dB":$"{Value}%";
        if(Caption=="FILTER")value=Math.Abs(_precise-50)<0.05?"OFF":$"{(_precise<50?"LOW-PASS":"HIGH-PASS")} {Math.Abs(_precise-50)*2:0.0}%";
        Theme.TextAt(g,value,new(0,(int)(76*s),Width,(int)(19*s)),Caption=="FILTER"?8:9,Accent,true,true);
    }
    private void PaintCompact(Graphics g)
    {
        g.SmoothingMode=SmoothingMode.AntiAlias;float s=UiMetrics.Scale(this),cx=Width/2f,cy=17*s,r=12*s;
        using var body=new SolidBrush(Theme.Raised);g.FillEllipse(body,cx-r,cy-r,2*r,2*r);
        using var edge=new Pen(_hover||Focused?Accent:Theme.Edge,1.5f*s);g.DrawEllipse(edge,cx-r,cy-r,2*r,2*r);
        double a=(135+Precise*2.7)*Math.PI/180;using var mark=new Pen(Accent,2*s);
        g.DrawLine(mark,cx+(float)Math.Cos(a)*5*s,cy+(float)Math.Sin(a)*5*s,cx+(float)Math.Cos(a)*10*s,cy+(float)Math.Sin(a)*10*s);
        Theme.TextAt(g,Caption=="FILTER"?"LP / HP":Caption,new Rectangle(0,(int)(32*s),Width,(int)(15*s)),7,Theme.Muted,false,true);
    }
    protected override void OnMouseEnter(EventArgs e){_hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){_hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
    protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
    protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){Focus();_drag=true;_start=e.Location;_initial=Value;Capture=true;}base.OnMouseDown(e);}
    protected override void OnMouseMove(MouseEventArgs e){if(_drag){int v=_initial+(int)((_start.Y-e.Y)/(2*UiMetrics.Scale(this)));UserSet(Math.Abs(v-50)<=2?50:v);}base.OnMouseMove(e);}
    protected override void OnMouseUp(MouseEventArgs e){_drag=false;Capture=false;base.OnMouseUp(e);}
    protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture)_drag=false;base.OnMouseCaptureChanged(e);}
    protected override void OnMouseWheel(MouseEventArgs e){UserSet(Value+Math.Sign(e.Delta));base.OnMouseWheel(e);}
    protected override void OnDoubleClick(EventArgs e){UserSet(Bipolar?50:85);base.OnDoubleClick(e);}
    protected override bool IsInputKey(Keys k)=>(k&Keys.KeyCode)is Keys.Left or Keys.Right or Keys.Up or Keys.Down||base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode is Keys.Up or Keys.Right){UserSet(Value+1);e.Handled=true;}
        if(e.KeyCode is Keys.Down or Keys.Left){UserSet(Value-1);e.Handled=true;}
        if(e.KeyCode==Keys.Home){UserSet(50);e.Handled=true;}base.OnKeyDown(e);
    }
}

