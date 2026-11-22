using System.Text.Json;
using MixDock.Audio;
using MixDock.Settings;

namespace MixDock.Ui;

public sealed class LibraryView : Panel
{
    private readonly ListView _list=new(){View=View.Details,FullRowSelect=true,HideSelection=false,MultiSelect=false,BorderStyle=BorderStyle.None,BackColor=Theme.Surface,ForeColor=Theme.Text};
    private readonly TextBox _search=new(){PlaceholderText="Search tracks",BackColor=Theme.Raised,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle};
    /// <summary>Workspace toggle (PERFORM ↔ BROWSE); MainForm decides what the expanded library covers.</summary>
    public readonly StudioButton Browse=new(){Text="BROWSE ▲",AccessibleName="Browse: expand the library over deck A's FX and pads"};
    private readonly StudioButton _add=new(){Text="+ TRACKS"},_loadA=new(){Text="LOAD A",Accent=Theme.Cyan},_loadB=new(){Text="LOAD B",Accent=Theme.Amber};
    private readonly List<string> _paths=new();
    private readonly bool _preview;
    private readonly int _defaultDeck;
    private readonly string _path=Path.Combine(Path.GetDirectoryName(AppConfig.FilePath)!,"library.json");
    public event Action<int,string>? LoadRequested;
    public event Action? Changed;
    public string[] TrackPaths() => _paths.ToArray();
    public void SelectTrack(string path)
    {
        _search.Clear();
        foreach (ListViewItem row in _list.Items)
            if (string.Equals(row.Tag as string, path, StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = row.Focused = true;
                row.EnsureVisible();
                break;
            }
    }
    public void Reload(){if(_preview)return;try{_paths.Clear();if(File.Exists(_path))_paths.AddRange(JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path))??[]);RefreshRows();}catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){Log.Write("Library reload: "+ex.Message);}}
    public LibraryView(bool preview, int defaultDeck = 0)
    {
        _preview=preview;_defaultDeck=defaultDeck is 0 or 1 ? defaultDeck : 0;
        BackColor=Theme.Surface;AccessibleName=defaultDeck==1?"Track library (deck B)":"Track library (deck A)";
        _list.OwnerDraw=true;
        _list.DrawColumnHeader+=(_,e)=>{using var bg=new SolidBrush(Theme.Raised);e.Graphics.FillRectangle(bg,e.Bounds);TextRenderer.DrawText(e.Graphics,e.Header!.Text,Font,e.Bounds,Theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);};
        _list.DrawItem+=(_,e)=>{if(_list.View!=View.Details)e.DrawDefault=true;};
        _list.DrawSubItem+=(_,e)=>{using var bg=new SolidBrush(e.Item!.Selected?Theme.Raised:Theme.Surface);e.Graphics.FillRectangle(bg,e.Bounds);TextRenderer.DrawText(e.Graphics,e.SubItem!.Text,Font,e.Bounds,Theme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        _list.Columns.Add("TRACK / FILE",330);_list.Columns.Add("BPM",55);_list.Columns.Add("KEY",55);_list.Columns.Add("TIME",65);
        Controls.AddRange([_search,_add,Browse,_loadA,_loadB,_list]);
        if(!preview)try{if(File.Exists(_path))_paths.AddRange(JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path))??[]);}catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){Log.Write("Library: "+ex.Message);}
        _search.TextChanged+=(_,_)=>RefreshRows();
        _add.Click+=(_,_)=>
        {
            using var dlg=new OpenFileDialog{Title="Add tracks to HARP//MXR",Multiselect=true,Filter=TrackReaderFactory.LibraryDialogFilter};
            if(dlg.ShowDialog(this)!=DialogResult.OK)return;
            AddTracks(dlg.FileNames);
        };
        // Double-click loads the nearest deck; LOAD A / LOAD B stay available on both sides.
        _loadA.Click+=(_,_)=>Load(0);_loadB.Click+=(_,_)=>Load(1);_list.DoubleClick+=(_,_)=>Load(_defaultDeck);
        RefreshRows();
    }
    public void AddTracks(IEnumerable<string> paths)
    {
        foreach(var p in paths)if(!_paths.Contains(p,StringComparer.OrdinalIgnoreCase))_paths.Add(p);
        if(!_preview)try{Directory.CreateDirectory(Path.GetDirectoryName(_path)!);File.WriteAllText(_path+".tmp",JsonSerializer.Serialize(_paths));File.Move(_path+".tmp",_path,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Log.Write("Library could not save: "+ex.Message);}
        RefreshRows();Changed?.Invoke();
    }
    public void SetMetadata(string path,double bpm,string? key,TimeSpan duration)
    {
        foreach(ListViewItem item in _list.Items)if(item.Tag is string p&&string.Equals(p,path,StringComparison.OrdinalIgnoreCase))
        {
            string[] values=[bpm>0?bpm.ToString("0.0"):"—",key??"—",duration.TotalSeconds>0?$"{(int)duration.TotalMinutes}:{duration.Seconds:00}":"—"];
            for(int i=0;i<3;i++)if(item.SubItems[i+1].Text!=values[i])item.SubItems[i+1].Text=values[i];
        }
    }
    private void Load(int d){if(_list.SelectedItems.Count>0&&_list.SelectedItems[0].Tag is string p)LoadRequested?.Invoke(d,p);}
    private void RefreshRows()
    {
        _list.BeginUpdate();_list.Items.Clear();
        foreach(var p in _paths.Where(p=>Path.GetFileName(p).Contains(_search.Text,StringComparison.OrdinalIgnoreCase)))
        {
            // Metadata stays unknown until measured; no invented BPM, key or duration.
            var item=new ListViewItem([Path.GetFileName(p),"—","—","—"]){Tag=p};_list.Items.Add(item);
        }
        if(_paths.Count==0)_list.Items.Add(new ListViewItem(["Add tracks, or choose a live app above","—","—","—"]));
        _list.EndUpdate();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(_list==null)return;
        float s=UiMetrics.Scale(this);int D(float v)=>(int)(v*s);
        _add.SetBounds(D(4),D(3),D(84),D(26));_search.SetBounds(D(94),D(4),Math.Max(40,Width-D(370)),D(24));
        Browse.SetBounds(Width-D(268),D(3),D(88),D(26));
        _loadA.SetBounds(Width-D(176),D(3),D(84),D(26));_loadB.SetBounds(Width-D(88),D(3),D(84),D(26));
        _list.SetBounds(D(4),D(33),Width-D(8),Math.Max(10,Height-D(37)));
        foreach(Control c in Controls)c.Font=Font;
        // columns fill the client width exactly, so no unpainted header strip is left at the right edge
        int meta=D(62);for(int i=1;i<4;i++)_list.Columns[i].Width=meta;
        _list.Columns[0].Width=Math.Max(50,_list.ClientSize.Width-3*meta);
    }
}
