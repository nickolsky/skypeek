
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
namespace Skypeek.Desktop.Views;

/// <summary>
/// Draws a <see cref="VpcMap"/>: three columns of boxes with curved lines for each route (subnet → route table →
/// connection). Lines are redrawn only when the box positions change, so drawing never loops with layout.
/// </summary>
public partial class VpcMapView : UserControl
{
    private static readonly IBrush LineBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0xC4, 0xC4, 0xC4));
    private static readonly IBrush LitBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
    private static readonly IBrush DimBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0xC4, 0xC4, 0xC4));
    private string _drawn = "";
    private VpcMap? _map;

    public VpcMapView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => Redraw();
        DataContextChanged += (_, _) =>
        {
            if (_map is { } old)
                foreach (var box in old.All) box.PropertyChanged -= OnBoxChanged;
            _map = DataContext as VpcMap;
            if (_map is { } map)
                foreach (var box in map.All) box.PropertyChanged += OnBoxChanged;
            _drawn = "";
        };
    }

    private void OnBoxChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapBox.IsHighlighted) or nameof(MapBox.IsDimmed))
            _drawn = "";
    }

    private void OnBoxClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { Tag: MapBox box } && DataContext is VpcMap map)
        {
            map.Select(box);
            _drawn = "";
            InvalidateArrange();
        }
    }

    /// <summary>Box edges relative to the lines canvas, by box id.</summary>
    private Dictionary<string, Rect> Positions(ItemsControl list)
    {
        var result = new Dictionary<string, Rect>();
        foreach (var item in list.Items.OfType<MapBox>())
        {
            if (list.ContainerFromItem(item) is not Control container || container.Bounds.Height == 0 || !Board.IsVisualAncestorOf(container)
                || container.TranslatePoint(new Point(0, 0), Lines) is not { } origin)
                continue;
            // The box's bottom margin is outside its bounds already; the container is the box.
            result[item.Id] = new Rect(origin, new Size(container.Bounds.Width, Math.Max(0, container.Bounds.Height - 6)));
        }
        return result;
    }

    private void Redraw()
    {
        if (DataContext is not VpcMap map || Bounds.Width == 0)
            return;
        var subnets = Positions(SubnetList);
        var tables = Positions(TableList);
        var connections = Positions(ConnectionList);
        var signature = string.Join(";", subnets.Concat(tables).Concat(connections).Select(p => $"{p.Key}:{p.Value.X:0},{p.Value.Y:0},{p.Value.Width:0},{p.Value.Height:0}"))
                        + "|" + map.Selected?.Id;
        if (signature == _drawn)
            return;
        _drawn = signature;

        Lines.Children.Clear();
        void Link(MapBox from, Dictionary<string, Rect> fromPositions, Dictionary<string, Rect> toPositions)
        {
            if (!fromPositions.TryGetValue(from.Id, out var a))
                return;
            foreach (var id in from.Next)
            {
                if (!toPositions.TryGetValue(id, out var b) || map.Find(id) is not { } to)
                    continue;
                var start = new Point(a.Right, a.Top + a.Height / 2);
                var end = new Point(b.Left, b.Top + b.Height / 2);
                var bend = Math.Max(12, (end.X - start.X) / 2);
                var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
                figure.Segments!.Add(new BezierSegment { Point1 = new Point(start.X + bend, start.Y), Point2 = new Point(end.X - bend, end.Y), Point3 = end });
                var lit = from.IsHighlighted && to.IsHighlighted;
                Lines.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = new PathGeometry { Figures = [figure] },
                    Stroke = lit ? LitBrush : map.Selected is null ? LineBrush : DimBrush,
                    StrokeThickness = lit ? 2 : 1.2,
                });
            }
        }
        foreach (var subnet in map.Subnets)
            Link(subnet, subnets, tables);
        foreach (var table in map.RouteTables)
            Link(table, tables, connections);
    }
}
