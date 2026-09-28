using System.Windows;
using System.Windows.Media;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

/// <summary>Small horizontal 0–100% bar coloured by health level (like a disk or memory gauge).</summary>
public sealed class UsageBar : FrameworkElement
{
    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double?), typeof(UsageBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(HealthLevel), typeof(UsageBar), new FrameworkPropertyMetadata(HealthLevel.Ok, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Percent
    {
        get => (double?)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public HealthLevel Level
    {
        get => (HealthLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public UsageBar()
    {
        Width = 60;
        Height = 6;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        if (Percent is not { } p || double.IsNaN(p))
            return;

        // A sliver stays visible at 0% so the colour still shows the level.
        var fill = Math.Max(h, Math.Clamp(p, 0, 100) / 100 * w);
        var brush = Level switch
        {
            HealthLevel.Critical => LevelToBrushConverter.Critical,
            HealthLevel.Warn => LevelToBrushConverter.Warn,
            HealthLevel.Unknown => LevelToBrushConverter.Unknown,
            _ => LevelToBrushConverter.Ok,
        };
        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, fill, h), radius, radius);
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
