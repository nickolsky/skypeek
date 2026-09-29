using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

public sealed class LevelToBrushConverter : IValueConverter
{
    public static readonly IImmutableSolidColorBrush Critical = new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0x46, 0x4A));
    public static readonly IImmutableSolidColorBrush Warn = new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x2D));
    public static readonly IImmutableSolidColorBrush Ok = new ImmutableSolidColorBrush(Color.FromRgb(0x4C, 0xB8, 0x6B));
    public static readonly IImmutableSolidColorBrush Unknown = new ImmutableSolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public static IImmutableSolidColorBrush For(HealthLevel level) => level switch
    {
        HealthLevel.Critical => Critical,
        HealthLevel.Warn => Warn,
        HealthLevel.Ok => Ok,
        _ => Unknown,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HealthLevel level ? For(level) : Unknown;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>AWS returns UTC times; bindings that format a DateTime show it in local time.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime { Kind: not DateTimeKind.Local } d ? (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d).ToLocalTime() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True unless the value is null or an empty string (for IsVisible).</summary>
public sealed class NotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not (null or string { Length: 0 });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when a count or collection has items (or, with <see cref="Invert"/>, when it has none).</summary>
public sealed class CountConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            ICollection c => c.Count,
            _ => 0,
        };
        return (count > 0) != Invert;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Picks one of two brushes for a bool (replaces WPF DataTriggers on Foreground).</summary>
public sealed class BoolBrushConverter : IValueConverter
{
    public IBrush? True { get; set; }
    public IBrush? False { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? True : False;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Picks one of two texts for a bool.</summary>
public sealed class BoolTextConverter : IValueConverter
{
    public string True { get; set; } = "";
    public string False { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? True : False;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Stripe colour of a VPC map box by its kind (public, NAT, gateway, endpoint…).</summary>
public sealed class StripeBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, IImmutableSolidColorBrush> Brushes = new()
    {
        ["public"] = new ImmutableSolidColorBrush(Color.Parse("#FF6FD08C")),
        ["igw"] = new ImmutableSolidColorBrush(Color.Parse("#FF6FD08C")),
        ["eigw"] = new ImmutableSolidColorBrush(Color.Parse("#FF6FD08C")),
        ["nat"] = new ImmutableSolidColorBrush(Color.Parse("#FF60CDFF")),
        ["other"] = new ImmutableSolidColorBrush(Color.Parse("#FFB39DDB")),
        ["tgw"] = new ImmutableSolidColorBrush(Color.Parse("#FFB39DDB")),
        ["pcx"] = new ImmutableSolidColorBrush(Color.Parse("#FFB39DDB")),
        ["vgw"] = new ImmutableSolidColorBrush(Color.Parse("#FFB39DDB")),
        ["vpce"] = new ImmutableSolidColorBrush(Color.Parse("#FFFFD479")),
        ["table"] = new ImmutableSolidColorBrush(Color.Parse("#FFC4C4C4")),
        ["blackhole"] = new ImmutableSolidColorBrush(Color.Parse("#FFFF7B7F")),
    };

    private static readonly IImmutableSolidColorBrush Default = new ImmutableSolidColorBrush(Color.Parse("#FF8F8F8F"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string kind && Brushes.TryGetValue(kind, out var brush) ? brush : Default;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
