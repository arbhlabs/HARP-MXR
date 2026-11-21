using System.ComponentModel;
namespace MixDock.Ui;

public sealed class LevelMeter:Control
{
    private float _level;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Accent {get;set;}=Theme.Cyan;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Available {get;set;}=true;
    public LevelMeter(){DoubleBuffered=true;ResizeRedraw=true;BackColor=Theme.Surface;AccessibleName="Signal level";}
    public void SetPeak(float peak)
    {
        Available=true;
        float safe=float.IsFinite(peak)?Math.Clamp(peak,0,4):0;
        float normalized=safe<=.001f?0:Math.Clamp((20*MathF.Log10(safe)+60)/60,0,1);
        _level=Math.Max(normalized,_level-.045f);Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) => Theme.Guard(() => PaintCore(e), "LevelMeter.1", this);
    private void PaintCore(PaintEventArgs e)
    {
        float s=UiMetrics.Scale(this);
        if(Width<45*s)
        {
            int compactcount=32;float compacttop=15*s,compactbottom=Height-8*s,compactstep=(compactbottom-compacttop)/compactcount;
            Theme.TextAt(e.Graphics,"dB",new Rectangle(0,0,Width,(int)(14*s)),6,Theme.Muted,false,true);
            for(int i=0;i<compactcount;i++){float f=(i+1)/(float)compactcount;using var b=new SolidBrush(Available&&f<=_level?(f>.95?Theme.Red:f>.8?Theme.Amber:Color.FromArgb(78,199,126)):Theme.Raised);e.Graphics.FillRectangle(b,Width/2f-4*s,compactbottom-(i+1)*compactstep,8*s,Math.Max(1,compactstep-s));}
            return;
        }
        int top=(int)(24*s),bottom=Height-(int)(32*s),x=(int)(8*s),w=(int)(16*s);
        Theme.TextAt(e.Graphics,"PEAK",new(0,0,Width,top),7.5f,Theme.Muted,true);
        int count=36;float step=(bottom-top)/(float)count;
        for(int i=0;i<count;i++){
            float fraction=(i+1)/(float)count;Color lit=fraction>.95?Theme.Red:fraction>.8?Color.FromArgb(242,191,100):Accent;
            using var b=new SolidBrush(Available&&fraction<=_level?lit:Color.FromArgb(39,45,57));
            e.Graphics.FillRectangle(b,x,bottom-(i+1)*step,w,Math.Max(1,step-2*s));
        }
        foreach(int db in new[]{0,-12,-24,-36,-48,-60}){
            int y=top+(int)((bottom-top)*(-db/60f))-7*(int)s;
            Theme.TextAt(e.Graphics,db.ToString(),new(x+w+(int)(7*s),y,Width-x-w,(int)(16*s)),7.5f,Theme.Muted);
        }
        Theme.TextAt(e.Graphics,Available?(_level>.001?$"{(_level*60-60):0} dB":"−∞ dB"):"—",new(0,bottom+(int)(8*s),Width,(int)(20*s)),8,Theme.Muted);
    }
}


