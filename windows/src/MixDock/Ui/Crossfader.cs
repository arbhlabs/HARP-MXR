using System.ComponentModel;
using System.Drawing.Drawing2D;
namespace MixDock.Ui;

public class Fader : Control
{
    private int _value=50;private bool _hover;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Vertical {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Blend {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public Color Accent {get;set;}=Theme.Text;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public int ResetValue {get;set;}=85;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value {get=>_value;set{int v=Math.Clamp(value,0,100);if(v==_value)return;_value=v;Invalidate();ValueChanged?.Invoke(this,EventArgs.Empty);}}
    // Reflect an engine-driven fade without sending the rounded display value back to the mixer.
    internal void DisplayValue(int value)
    {
        int next = Math.Clamp(value,0,100);
        if (next == _value) return;
        _value = next;
        Invalidate();
    }
    public event EventHandler? ValueChanged;
    public Fader(){DoubleBuffered=true;ResizeRedraw=true;TabStop=true;Cursor=Cursors.Hand;BackColor=Theme.Surface;AccessibleRole=AccessibleRole.Slider;}
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "Crossfader.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=UiMetrics.Scale(this),start=24*s,end=(Vertical?Height:Width)-24*s;
        if(end<=start)return;float axis=Vertical?Width/2f:Height/2f;
        PointF P(float v)=>Vertical?new(axis,v):new(v,axis);
        for(int i=0;i<=10;i++){float pos=start+(end-start)*i/10;using var p=new Pen(i==5?Theme.Muted:Theme.Edge,s);
            if(Vertical)g.DrawLine(p,axis-16*s,pos,axis+16*s,pos);else g.DrawLine(p,pos,axis-12*s,pos,axis+12*s);}
        using(var p=new Pen(Color.FromArgb(9,12,18),8*s){StartCap=LineCap.Round,EndCap=LineCap.Round})g.DrawLine(p,P(start),P(end));
        float at=start+(end-start)*(Vertical?100-Value:Value)/100;
        if(Blend){using var a=new Pen(Color.FromArgb(110,Theme.Cyan),3*s);using var b=new Pen(Color.FromArgb(110,Theme.Amber),3*s);g.DrawLine(a,P(start),P(at));g.DrawLine(b,P(at),P(end));}
        else{using var p=new Pen(Accent,3*s);g.DrawLine(p,P(Vertical?end:start),P(at));}
        float thumbHalf=Math.Min(16*s,Height/2f-2);
        var thumb=Vertical?new RectangleF(axis-21*s,at-10*s,42*s,20*s):new RectangleF(at-9*s,axis-thumbHalf,18*s,2*thumbHalf);
        if(!Theme.Ok(thumb))return;using var path=Theme.Round(thumb,4*s);using var fill=new SolidBrush(Theme.Raised);
        g.FillPath(fill,path);using var edge=new Pen(_hover||Focused?Accent:Color.FromArgb(115,126,145),s);g.DrawPath(edge,path);
        using var line=new Pen(Theme.Text,2*s);
        if(Vertical)g.DrawLine(line,axis-12*s,at,axis+12*s,at);else g.DrawLine(line,at,axis-thumbHalf+3,at,axis+thumbHalf-3);
    }
    private void SetAt(Point p){float pad=24*UiMetrics.Scale(this);int v=(int)Math.Round(100*((Vertical?p.Y:p.X)-pad)/Math.Max(1,(Vertical?Height:Width)-pad*2));Value=Vertical?100-v:v;}
    protected override void OnMouseEnter(EventArgs e){_hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){_hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
    protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
    protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){Focus();Capture=true;SetAt(e.Location);}base.OnMouseDown(e);}
    protected override void OnMouseMove(MouseEventArgs e){if(Capture)SetAt(e.Location);base.OnMouseMove(e);}
    protected override void OnMouseUp(MouseEventArgs e){Capture=false;base.OnMouseUp(e);}
    protected override void OnMouseWheel(MouseEventArgs e){Value+=Math.Sign(e.Delta);base.OnMouseWheel(e);}
    protected override void OnDoubleClick(EventArgs e){Value=ResetValue;base.OnDoubleClick(e);}
    protected override bool IsInputKey(Keys k)=>(k&Keys.KeyCode)is Keys.Left or Keys.Right or Keys.Up or Keys.Down||base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e){
        if(e.KeyCode is Keys.Right or Keys.Up){Value++;e.Handled=true;}
        if(e.KeyCode is Keys.Left or Keys.Down){Value--;e.Handled=true;}
        if(e.KeyCode==Keys.Home){Value=ResetValue;e.Handled=true;}base.OnKeyDown(e);}
}
public sealed class Crossfader:Fader
{
    public Crossfader(){Height=72;Blend=true;ResetValue=50;AccessibleName="Crossfader, A to B";}
}

