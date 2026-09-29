using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>Tiny 0–100% line chart for the last hour of a metric.</summary>
public sealed class Sparkline : Control
{
    private static readonly IImmutableSolidColorBrush Track = new ImmutableSolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    public static readonly StyledProperty<MetricEvaluation?> EvaluationProperty =
        AvaloniaProperty.Register<Sparkline, MetricEvaluation?>(nameof(Evaluation));

    static Sparkline() => AffectsRender<Sparkline>(EvaluationProperty);

    public MetricEvaluation? Evaluation
    {
        get => GetValue(EvaluationProperty);
        set => SetValue(EvaluationProperty, value);
    }

    public Sparkline()
    {
        Width = 90;
        Height = 22;
        VerticalAlignment = VerticalAlignment.Center;
        UpdateToolTip();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EvaluationProperty)
            UpdateToolTip();
    }

    /// <summary>Says which period the line covers (the last metrics poll's window, one hour).</summary>
    private void UpdateToolTip()
    {
        var points = Evaluation?.Points;
        ToolTip.SetTip(this, points is not { Count: > 0 } ? "No data in the last hour"
            : $"{points[0].Timestamp.ToLocalTime():t} – {points[^1].Timestamp.ToLocalTime():t}: {points.Count} point(s), one every {Math.Max(1, Evaluation!.PeriodSeconds / 60)} min · "
              + $"min {points.Min(p => p.Value):0}, avg {Evaluation.Average:0}, peak {Evaluation.Peak:0}. Longer periods: History and Usage below.");
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        dc.DrawRectangle(Track, null, new Rect(0, 0, w, h));

        var points = Evaluation?.Points;
        if (points is null || points.Count < 2)
            return;

        var start = points[0].Timestamp;
        var span = Math.Max(1, (points[^1].Timestamp - start).TotalSeconds);
        var pen = new ImmutablePen(LevelToBrushConverter.For(Evaluation!.Level), 1.4);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var x = (points[i].Timestamp - start).TotalSeconds / span * (w - 2) + 1;
                var y = h - 1 - Math.Clamp(points[i].Value, 0, 100) / 100 * (h - 2);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false);
                else ctx.LineTo(new Point(x, y), true);
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }
}
