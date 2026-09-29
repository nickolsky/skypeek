using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>Small horizontal 0–100% bar coloured by health level (like a disk or memory gauge).</summary>
public sealed class UsageBar : Control
{
    private static readonly IImmutableSolidColorBrush Track = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public static readonly StyledProperty<double?> PercentProperty = AvaloniaProperty.Register<UsageBar, double?>(nameof(Percent));
    public static readonly StyledProperty<HealthLevel> LevelProperty = AvaloniaProperty.Register<UsageBar, HealthLevel>(nameof(Level), HealthLevel.Ok);

    static UsageBar() => AffectsRender<UsageBar>(PercentProperty, LevelProperty);

    public double? Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public HealthLevel Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public UsageBar()
    {
        Width = 60;
        Height = 6;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var radius = h / 2;
        dc.DrawRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        if (Percent is not { } p || double.IsNaN(p))
            return;

        // A sliver stays visible at 0% so the colour still shows the level.
        var fill = Math.Max(h, Math.Clamp(p, 0, 100) / 100 * w);
        var brush = Level == HealthLevel.Unknown ? LevelToBrushConverter.Unknown : LevelToBrushConverter.For(Level);
        dc.DrawRectangle(brush, null, new Rect(0, 0, fill, h), radius, radius);
    }
}
