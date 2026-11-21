using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MeshMonitor.Native;

internal sealed record MapHome(double X, double Y, int Zoom);
internal sealed record OverlayPath(string Label,(double X,double Y)[] Points, string? Stroke = null, double Weight = 2);
public sealed partial class MeshMap
{
    private readonly Canvas annotations=new(){IsHitTestVisible=false};
    private readonly Dictionary<string,OverlayPath[]> overlays=[];
    internal void SetOverlay(string key,OverlayPath[] paths){overlays[key]=paths; Render();}
    internal void ClearOverlays(){overlays.Clear();annotations.Children.Clear();}
    private void DrawOverlays(double left,double top,double world)
    {
        annotations.Children.Clear();
        
        foreach(var path in overlays.Values.SelectMany(rows=>rows))
        {
            if(path.Points.Length==0)continue;
            var color = Colors.DarkSlateBlue;
            if (path.Stroke is { Length: 7 } hex && hex[0] == '#' &&
                int.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                color = Windows.UI.Color.FromArgb(255,(byte)(rgb >> 16),(byte)(rgb >> 8),(byte)rgb);
            var brush = new SolidColorBrush(color);
            var points=new PointCollection();
            double anchor=centerX;
            foreach(var pt in path.Points)
            {
                anchor=MapProjection.NearestWorld(pt.X,anchor);
                points.Add(new Point(anchor*world-left,pt.Y*world-top));
            }
            if(points.Count>1)annotations.Children.Add(new Polyline{Points=points,Stroke=brush,StrokeThickness=path.Weight});
            else
            {
                var p=points[0];
                if(p.X< -100 || p.X>ActualWidth || p.Y< -30 || p.Y>ActualHeight)continue;
                var label=new TextBlock{Text="◆ "+path.Label,Foreground=brush};
                Canvas.SetLeft(label,p.X-5);Canvas.SetTop(label,p.Y-10);annotations.Children.Add(label);
            }
        }
    }
}
public sealed partial class MainWindow
{
    private void ConfigureMapTools(Func<string,MenuFlyoutSubItem> group,ToolItem add,SourceRow s)
    {
        var map=group("Map and surveys");
        add(map,"Show waypoints and markers",c=>ShowAnnotations(c,s),!s.IsUnified);
        add(map,"Add local marker",c=>CreateAnnotation(c,s,false),!s.IsUnified);
        add(map,"Create waypoint",c=>CreateAnnotation(c,s,true),s.Type=="meshtastic_tcp");
        add(map,"Manage existing waypoint",c=>EditAnnotation(c,s,true),s.Type=="meshtastic_tcp");
        add(map,"Manage local marker",c=>EditAnnotation(c,s,false),!s.IsUnified);
        add(map,"Import GeoJSON or KML",c=>ImportGeoLayer(c),signedIn);
        add(map,"Map and layout preferences",c=>LayoutSettings());
        add(map,"Use current map view at startup",_=>{SaveDesktop(desktop with {Home=NodeMap.CurrentView});return Task.CompletedTask;});
        add(map,"Fit nodes at startup",_=>{SaveDesktop(desktop with {Home=null});return Task.CompletedTask;});
        add(map,"Show GeoJSON layer",c=>GeoLayer(c));
        add(map,"Clear additional layers",_=>{NodeMap.ClearOverlays();return Task.CompletedTask;});
        add(map,"Review telemetry outliers",c=>Outliers(c,s),radioAdmin&&!s.IsUnified);
        add(map,"Start coverage survey",c=>Survey(c,true));
        add(map,"View or stop survey",c=>Survey(c,false));
    }
    private async Task ShowAnnotations(MeshApi c,SourceRow s)
    {
        var paths=new List<OverlayPath>();
        foreach(var endpoint in new[]{"markers","waypoints"})
        {
            var rows=Records(await c.RequestAsync(SourceEndpoint(s,endpoint)));
            foreach(var row in rows)
                if(row.Number("latitude") is double lat && row.Number("longitude") is double lon)
                    paths.Add(new(row.String("label")??row.String("name")??"Waypoint",[MapProjection.Project(lat,lon)]));
        }
        Activity.SelectedItem=MapTab; await LoadSourceAsync();
        NodeMap.SetOverlay("annotations",paths.ToArray());
        ShowNotice($"{paths.Count} waypoints and markers loaded.",InfoBarSeverity.Informational);
    }
    private async Task CreateAnnotation(MeshApi c,SourceRow s,bool waypoint)
    {
        var name=new TextBox{Header="Name"};var desc=new TextBox{Header="Description"};
        var lat=new NumberBox{Header="Latitude",Value=selectedMapNode?.Latitude??double.NaN};
        var lon=new NumberBox{Header="Longitude",Value=selectedMapNode?.Longitude??double.NaN};
        var send=new CheckBox{Content="Broadcast waypoint on the radio"};
        var channel=new NumberBox{Header="Channel",Value=0,Minimum=0,Maximum=7};
        var err=DialogError(); var fields=new List<FrameworkElement>{name,desc,lat,lon};
        if(waypoint){fields.Add(send);fields.Add(channel);}fields.Add(err);
        var dlg=Form(waypoint?"Create waypoint":"Create local marker","Create",fields.ToArray());
        dlg.PrimaryButtonClick+=(_,a)=>{if(name.Text.Trim().Length==0 || !(lat.Value is >=-90 and <=90 && lon.Value is >=-180 and <=180)){
            a.Cancel=true;err.Text="Enter a name and valid coordinates.";}};
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
        object body=waypoint?new{name=name.Text,description=desc.Text,latitude=lat.Value,longitude=lon.Value,@virtual=send.IsChecked!=true,channel=(int)channel.Value}:
            new{label=name.Text,description=desc.Text,latitude=lat.Value,longitude=lon.Value};
        await c.RequestAsync(SourceEndpoint(s,waypoint?"waypoints":"markers"),body);
        await ShowAnnotations(c,s);
    }
    private async Task EditAnnotation(MeshApi c,SourceRow s,bool waypoint)
    {
        string path=SourceEndpoint(s,waypoint?"waypoints":"markers");
        var record=await ChooseRecord(waypoint?"Waypoints":"Local markers",Records(await c.RequestAsync(path)),
            row=>row.String(waypoint?"name":"label")??"Untitled","Edit");
        if(record is not {} row)return;
        string id=row.Property(waypoint?"waypointId":"id").ToString();
        var name=new TextBox{Header="Name",Text=row.String(waypoint?"name":"label")??""};
        var description=new TextBox{Header="Description",Text=row.String("description")??""};
        var lat=new NumberBox{Header="Latitude",Value=row.Number("latitude")??double.NaN};
        var lon=new NumberBox{Header="Longitude",Value=row.Number("longitude")??double.NaN};
        var broadcast=new CheckBox{Content="Broadcast waypoint",IsChecked=row.Flag("isVirtual")==false};
        var details=new List<FrameworkElement>{name,description,lat,lon};
        if(waypoint)details.Add(broadcast);
        var dlg=Form("Edit · "+s.Name,"Save",details.ToArray());dlg.SecondaryButtonText="Delete…";
        var action=await dlg.ShowAsync();string endpoint=path+"/"+Esc(id);
        if(action==ContentDialogResult.Secondary)
        {
            if(await Form("Delete "+name.Text,"Delete",Wrapped("Remove this entry from "+s.Name+"?")).ShowAsync()!=ContentDialogResult.Primary)return;
            await c.RequestAsync(endpoint,new {},HttpMethod.Delete);
        }
        else if(action==ContentDialogResult.Primary)
        {
            if(!(lat.Value is >=-90 and <=90 && lon.Value is >=-180 and <=180) || name.Text.Trim().Length==0)
                throw new InvalidOperationException("A name and valid coordinates are required.");
            var body=new Dictionary<string,object?>{[waypoint?"name":"label"]=name.Text.Trim(),["description"]=description.Text,
                ["latitude"]=lat.Value,["longitude"]=lon.Value};
            if(waypoint)body["virtual"]=broadcast.IsChecked!=true;
            else foreach(string key in new[]{"color","icon","altitude"})if(row.Property(key).ValueKind!=JsonValueKind.Undefined)body[key]=row.Property(key);
            await c.RequestAsync(endpoint,body,waypoint?HttpMethod.Patch:HttpMethod.Put);
        }
        else return;
        await ShowAnnotations(c,s);
    }

    private async Task ImportGeoLayer(MeshApi c)
    {
        var picker=new Windows.Storage.Pickers.FileOpenPicker();
        foreach(var ext in new[]{".geojson",".json",".kml",".kmz"})picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file=await picker.PickSingleFileAsync(); if(file==null)return;
        using var input=await file.OpenStreamForReadAsync();
        await c.UploadGeoLayer(file.Name,input);
        await GeoLayer(c);
    }

    private async Task LayoutSettings()
    {
        var width=new NumberBox{Header="Sources sidebar width",Minimum=180,Maximum=420,Value=desktop.SidebarWidth};
        var startup=new ComboBox{Header="Opening tab",ItemsSource=Activity.Items.OfType<PivotItem>().Select(item=>item.Header?.ToString()??"Map").ToArray(),
            SelectedIndex=Math.Clamp(desktop.OpeningTab,0,Activity.Items.Count-1)};
        var columns = new ComboBox { Header="Telemetry chart columns", ItemsSource=new[]{1,2,3}, SelectedIndex=Math.Clamp(desktop.ChartColumns,1,3)-1 };
        if(await Form("Workspace layout","Save",width,startup,columns).ShowAsync()!=ContentDialogResult.Primary)return;
        if(!double.IsFinite(width.Value))return;
        SaveDesktop(desktop with { SidebarWidth=width.Value, OpeningTab=startup.SelectedIndex, ChartColumns=columns.SelectedIndex+1 });
        Workspace.ColumnDefinitions[0].Width=new GridLength(width.Value);
        if (ReferenceEquals(Activity.SelectedItem, DashboardTab) && api != null && SourcesList.SelectedItem is SourceRow source)
            await LoadDashboardAsync(api, source);
    }
    private async Task GeoLayer(MeshApi c)
    {
        var choice=await ChooseRecord("GeoJSON layers",Records(await c.RequestAsync("api/geojson/layers")),r=>r.String("name")??"Layer","Show on map");
        if(choice is not {} layer)return;
        var data=await c.RequestAsync($"api/geojson/layers/{Esc(layer.Property("id").ToString())}/data");
        var paths=new List<OverlayPath>();
        void Read(JsonElement g,string name, string? stroke=null, double weight=2)
        {
            var coords=g.Property("coordinates");
            (double,double) Point(JsonElement p)=>MapProjection.Project(p[1].GetDouble(),p[0].GetDouble());
            void Line(JsonElement line)=>paths.Add(new(name,line.EnumerateArray().Select(Point).ToArray(),stroke,weight));
            switch(g.String("type"))
            {
                case "FeatureCollection": foreach(var feature in Records(g.Property("features")))Read(feature,name);break;
                case "Feature": var style=g.Property("properties"); Read(g.Property("geometry"),style.String("name")??name, style.String("stroke")??style.String("marker-color"), Math.Clamp(style.Number("stroke-width")??2,1,10));break;
                case "GeometryCollection":foreach(var child in Records(g.Property("geometries")))Read(child,name,stroke,weight);break;
                case "Point":paths.Add(new(name,[Point(coords)],stroke,weight));break;
                case "MultiPoint":foreach(var pt in coords.EnumerateArray())paths.Add(new(name,[Point(pt)],stroke,weight));break;
                case "LineString": Line(coords);break;
                case "Polygon":case "MultiLineString":foreach(var line in coords.EnumerateArray())Line(line);break;
                case "MultiPolygon":foreach(var poly in coords.EnumerateArray())foreach(var ring in poly.EnumerateArray())Line(ring);break;
            }
        }
        Read(data,layer.String("name")??"Feature");Activity.SelectedItem=MapTab;await LoadSourceAsync();
        NodeMap.SetOverlay("geo-"+layer.Property("id"),paths.ToArray());
    }
    private async Task Outliers(MeshApi c,SourceRow s)
    {
        const string path="api/purge/telemetry/outliers";
        var types=Records((await c.RequestAsync(path+"/types?"+Scope(s))).Property("data").Property("types"));
        if(types.Length==0){await ShowResult("Telemetry outliers","No telemetry types on this source.");return;}
        var metric=new ComboBox{Header="Metric",ItemsSource=types.Select(t=>t.ToString()).ToArray(),SelectedIndex=0};
        var node=new TextBox{Header="Node ID (blank for all nodes on this source)"};
        var auto=new CheckBox{Content="Automatic outlier detection",IsChecked=true};
        var min=new NumberBox{Header="Minimum accepted value (optional)"};
        var max=new NumberBox{Header="Maximum accepted value (optional)"};
        if(await Form("Preview outliers · "+s.Name,"Preview",metric,node,auto,min,max).ShowAsync()!=ContentDialogResult.Primary)return;
        var body=new Dictionary<string,object?>{["sourceId"]=s.Id,["telemetryType"]=metric.SelectedItem.ToString(),
            ["auto"]=auto.IsChecked==true,["min"]=double.IsFinite(min.Value)?min.Value:null,["max"]=double.IsFinite(max.Value)?max.Value:null};
        if(node.Text.Trim().Length>0)body["nodeId"]=node.Text.Trim();
        var preview=(await c.RequestAsync(path+"/preview",body)).Property("data");
        var dlg=DetailDialog("Outlier preview · "+s.Name,new ScrollViewer{Content=Wrapped(Pretty(preview)),MaxHeight=380});
        if(preview.Number("cutoffId")!=null && preview.String("fingerprint")!=null)dlg.PrimaryButtonText="Delete flagged samples";
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
        body["cutoffId"]=preview.Number("cutoffId");body["fingerprint"]=preview.String("fingerprint");
        await ShowResult("Removal result",Pretty((await c.RequestAsync(path,body)).Property("data")));
    }

    private async Task Survey(MeshApi c,bool create)
    {
        const string path="api/analysis/coverage/surveys";
        if(create)
        {
            var name=new TextBox{Header="Survey name"};var sender=new TextBox{Header="Sender node ID",Text=selectedMapNode?.Id??""};
            if(await Form("Start live coverage survey","Start",name,sender).ShowAsync()!=ContentDialogResult.Primary)return;
            await c.RequestAsync(path,new{name=name.Text,senderId=sender.Text.Trim(),live=true});
        }
        else
        {
            var choice=await ChooseRecord("Coverage surveys",Records(await c.RequestAsync(path)),r=>r.String("name")??"Survey","Details");
            if(choice is not {} survey)return;
            var box=Wrapped(Pretty(survey));var dlg=DetailDialog("Survey details",new ScrollViewer{Content=box,MaxHeight=340});
            if(survey.Property("endAt").ValueKind==JsonValueKind.Null)dlg.PrimaryButtonText="Stop survey";
            if(await dlg.ShowAsync()==ContentDialogResult.Primary)await c.RequestAsync(path+"/"+survey.Property("id")+"/stop",new {});
        }
    }
}
