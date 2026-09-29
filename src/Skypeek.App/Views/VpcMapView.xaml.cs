using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Skypeek.App.Views;

/// <summary>
/// Draws a <see cref="VpcMap"/>: three columns of boxes with curved lines for each route (subnet → route table →
/// connection). Lines are redrawn only when the box positions change, so drawing never loops with layout.
/// </summary>
public partial class VpcMapView
{
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xC4, 0xC4, 0xC4)));
    private static readonly Brush LitBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xC4, 0xC4, 0xC4)));
    private string _drawn = "";

    public VpcMapView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => Redraw();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is VpcMap old)
                foreach (var box in old.All) box.PropertyChanged -= OnBoxChanged;
            if (e.NewValue is VpcMap map)
                foreach (var box in map.All) box.PropertyChanged += OnBoxChanged;
            _drawn = "";
        };
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void OnBoxChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapBox.IsHighlighted) or nameof(MapBox.IsDimmed))
            _drawn = "";
    }

    private void OnBoxClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MapBox box } && DataContext is VpcMap map)
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
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container || container.ActualHeight == 0 || !container.IsDescendantOf(Board))
                continue;
            var origin = container.TransformToVisual(Lines).Transform(new Point(0, 0));
            // The box's bottom margin is not part of it.
            result[item.Id] = new Rect(origin, new Size(container.ActualWidth, Math.Max(0, container.ActualHeight - 6)));
        }
        return result;
    }

    private void Redraw()
    {
        if (DataContext is not VpcMap map || ActualWidth == 0)
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
                var figure = new PathFigure { StartPoint = start };
                figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true));
                var lit = from.IsHighlighted && to.IsHighlighted;
                Lines.Children.Add(new Path
                {
                    Data = new PathGeometry([figure]),
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
