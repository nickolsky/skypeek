using System.Windows;
using System.Windows.Media;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

/// <summary>Tiny 0–100% line chart for the last hour of a metric.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty EvaluationProperty = DependencyProperty.Register(
        nameof(Evaluation), typeof(MetricEvaluation), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d).UpdateToolTip()));

    /// <summary>Says which period the line covers (the last metrics poll's window, one hour).</summary>
    private void UpdateToolTip()
    {
        var points = Evaluation?.Points;
        ToolTip = points is not { Count: > 0 } ? "No data in the last hour"
            : $"{points[0].Timestamp.ToLocalTime():t} – {points[^1].Timestamp.ToLocalTime():t}: {points.Count} point(s), one every {Math.Max(1, Evaluation!.PeriodSeconds / 60)} min · "
              + $"min {points.Min(p => p.Value):0}, avg {Evaluation.Average:0}, peak {Evaluation.Peak:0}. Longer periods: Usage (last 30 days).";
    }

    public MetricEvaluation? Evaluation
    {
        get => (MetricEvaluation?)GetValue(EvaluationProperty);
        set => SetValue(EvaluationProperty, value);
    }

    public Sparkline()
    {
        Width = 90;
        Height = 22;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), null, new Rect(0, 0, w, h));

        var points = Evaluation?.Points;
        if (points is null || points.Count < 2)
            return;

        var start = points[0].Timestamp;
        var span = Math.Max(1, (points[^1].Timestamp - start).TotalSeconds);
        var brush = (Brush)new LevelToBrushConverter().Convert(Evaluation!.Level, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture);
        var pen = new Pen(brush, 1.4);
        pen.Freeze();

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var x = (points[i].Timestamp - start).TotalSeconds / span * (w - 2) + 1;
                var y = h - 1 - Math.Clamp(points[i].Value, 0, 100) / 100 * (h - 2);
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
