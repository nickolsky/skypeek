using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Skypeek.Desktop.Infrastructure;

/// <summary>Draws the tray icon: orange tile normally, red with a problem-count badge when something is wrong.</summary>
public static class IconRenderer
{
    private static readonly Color Normal = Color.FromRgb(0xFF, 0x99, 0x00);
    private static readonly Color Problem = Color.FromRgb(0xD1, 0x34, 0x38);
    private static readonly Color Locked = Color.FromRgb(0x6E, 0x6E, 0x6E);

    public static WindowIcon Create(bool red, int badgeCount, bool locked = false)
    {
        // Drawn at 2x so the icon stays sharp on high-DPI trays and the macOS menu bar.
        const int size = 64;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(192, 192));
        using (var dc = bitmap.CreateDrawingContext())
        {
            var s = size / 2.0; // drawing units (96 dpi) on a 192 dpi bitmap
            var fill = new ImmutableSolidColorBrush(locked ? Locked : red ? Problem : Normal);
            dc.DrawRectangle(fill, null, new Rect(1, 1, s - 2, s - 2), 7, 7);
            var glyph = Text("A", 22);
            dc.DrawText(glyph, new Point((s - glyph.Width) / 2, (s - glyph.Height) / 2 - 1));

            if (badgeCount > 0)
            {
                var label = badgeCount > 9 ? "9+" : badgeCount.ToString(CultureInfo.InvariantCulture);
                var badgeText = Text(label, 11);
                var w = Math.Max(15, badgeText.Width + 6);
                var rect = new Rect(s - w, s - 15, w, 15);
                dc.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), new ImmutablePen(Brushes.White, 1.2), rect, 7.5, 7.5);
                dc.DrawText(badgeText, new Point(rect.X + (rect.Width - badgeText.Width) / 2, rect.Y + (rect.Height - badgeText.Height) / 2));
            }
        }
        using var png = new MemoryStream();
        bitmap.Save(png, new PngBitmapEncoderOptions());
        png.Position = 0;
        return new WindowIcon(png);
    }

    private static WindowIcon? _windowIcon;

    /// <summary>The multi-size application icon (same as the exe's).</summary>
    public static WindowIcon WindowIcon() =>
        _windowIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://Skypeek/Assets/Skypeek.ico")));

    private static FormattedText Text(string text, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size, Brushes.White);
}
