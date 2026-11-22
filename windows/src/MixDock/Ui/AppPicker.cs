using System.Diagnostics;
namespace MixDock.Ui;

public sealed class AppPicker:ComboBox
{
    private readonly Dictionary<string,Image?> _icons=new(StringComparer.OrdinalIgnoreCase);
    public AppPicker()
    {
        DropDownStyle=ComboBoxStyle.DropDownList;DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=26;
        FlatStyle=FlatStyle.Flat;BackColor=Theme.Raised;ForeColor=Theme.Text;Font=Theme.Font(13,true);
        DropDownHeight=320;IntegralHeight=false;AccessibleName="Choose an audio app";
    }
    public static string Key(string name)=>name.Split(" (",2)[0].Trim().ToLowerInvariant();
    public static string DisplayName(string name)=>MixDock.Platform.DeckBrowserCommand.GroupLabel(name,DisplayName)??Key(name) switch{
        "vivaldi"=>"Vivaldi","spotify"=>"Spotify","chrome"=>"Chrome","msedge"=>"Microsoft Edge",
        "firefox"=>"Firefox","brave"=>"Brave","opera"=>"Opera","vlc"=>"VLC","ms-teams"=>"Microsoft Teams",
        var n=>n.Length==0?"Choose an app":char.ToUpper(n[0])+n[1..]};
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);if(Parent is not IConsoleScale)ItemHeight=(int)(48*UiMetrics.Scale(this));else Parent.PerformLayout();}
    protected override void OnDrawItem(DrawItemEventArgs e) => Theme.Guard(() => DrawItemCore(e), "AppPicker", this);
    private void DrawItemCore(DrawItemEventArgs e)
    {
        if(e.Bounds.Width<1)return;
        bool selected=(e.State&DrawItemState.Selected)!=0;
        using var bg=new SolidBrush(selected?Color.FromArgb(49,57,73):Theme.Raised);e.Graphics.FillRectangle(bg,e.Bounds);
        var app=e.Index>=0?Items[e.Index] as MixDock.Platform.AudioApp:null;
        float s=UiMetrics.Scale(this);bool compact=e.Bounds.Height<36*s;int iconSize=Math.Max(8,Math.Min((int)(28*s),e.Bounds.Height-(int)(6*s))),pad=(int)((compact?7:12)*s);
        var iconRect=new Rectangle(e.Bounds.Left+pad,e.Bounds.Top+(e.Bounds.Height-iconSize)/2,iconSize,iconSize);
        string key=app is null?"":Key(app.Name);
        if(app!=null&&!_icons.ContainsKey(key)){
            Image? image=null;
            try{using var process=Process.GetProcessById(app.Pid);string? path=process.MainModule?.FileName;
                if(path!=null){using var icon=Icon.ExtractAssociatedIcon(path);image=icon?.ToBitmap();}}
            catch(Exception ex)when(ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException){}
            _icons[key]=image;
        }
        if(app!=null&&_icons.GetValueOrDefault(key) is Image img)e.Graphics.DrawImage(img,iconRect);
        else{
            using var b=new SolidBrush(Theme.Edge);Theme.Ellipse(e.Graphics,b,iconRect);
            Theme.TextAt(e.Graphics,app is null?"+":DisplayName(app.Name)[..1],iconRect,compact?8:12,Theme.Text,true,true);
        }
        Theme.TextAt(e.Graphics,app is null?"Pick an app or song":DisplayName(app.Name),
            new(iconRect.Right+pad,e.Bounds.Top,e.Bounds.Width-iconSize-pad*3,e.Bounds.Height),compact?9:12,Theme.Text,true);
        if((e.State&DrawItemState.Focus)!=0)e.DrawFocusRectangle();
    }
    protected override void Dispose(bool disposing){if(disposing)foreach(var img in _icons.Values)img?.Dispose();base.Dispose(disposing);}
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || !IsHandleCreated) return;
        using var g = Graphics.FromHwnd(Handle);
        float s = UiMetrics.Scale(this); int aw = (int)(24 * s);
        using var bg = new SolidBrush(Theme.Raised);
        g.FillRectangle(bg, Width - aw, 0, aw, Height);
        using var edge = new Pen(Focused ? Theme.Cyan : Theme.Edge, s);
        g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var chevron = new Pen(Theme.Muted, 1.5f * s);
        float x = Width - aw / 2f, y = Height / 2f;
        g.DrawLines(chevron, [new PointF(x - 4*s,y - 2*s),new PointF(x,y + 2*s),new PointF(x + 4*s,y - 2*s)]);
    }
}

