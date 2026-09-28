using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Skypeek.App.Infrastructure;

/// <summary>Draws the tray icon: orange tile normally, red with a problem-count badge when something is wrong.</summary>
public static class IconRenderer
{
    private static readonly Color Normal = Color.FromRgb(0xFF, 0x99, 0x00);
    private static readonly Color Problem = Color.FromRgb(0xD1, 0x34, 0x38);
    private static readonly Color Locked = Color.FromRgb(0x6E, 0x6E, 0x6E);

    public static System.Drawing.Icon Create(bool red, int badgeCount, bool locked = false)
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(locked ? Locked : red ? Problem : Normal);
            dc.DrawRoundedRectangle(fill, null, new Rect(1, 1, size - 2, size - 2), 7, 7);
            var glyph = Text("A", 22, Brushes.White, FontWeights.Bold);
            dc.DrawText(glyph, new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2 - 1));

            if (badgeCount > 0)
            {
                var label = badgeCount > 9 ? "9+" : badgeCount.ToString(CultureInfo.InvariantCulture);
                var badgeText = Text(label, 11, Brushes.White, FontWeights.Bold);
                var w = Math.Max(15, badgeText.Width + 6);
                var rect = new Rect(size - w, size - 15, w, 15);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), new Pen(Brushes.White, 1.2), rect, 7.5, 7.5);
                dc.DrawText(badgeText, new Point(rect.X + (rect.Width - badgeText.Width) / 2, rect.Y + (rect.Height - badgeText.Height) / 2));
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new System.Drawing.Icon(new MemoryStream(WrapPngAsIco(png.ToArray(), size)));
    }

    private static ImageSource? _windowIcon;

    /// <summary>The multi-size application icon (same as the exe's), so the taskbar gets a sharp image at any DPI.</summary>
    public static ImageSource WindowIcon() =>
        _windowIcon ??= BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Skypeek.ico", UriKind.Absolute));

    private static FormattedText Text(string text, double size, Brush brush, FontWeight weight) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0);

    /// <summary>ICO container holding a single PNG image (supported since Windows Vista).</summary>
    private static byte[] WrapPngAsIco(byte[] png, int size)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)1);
        w.Write((byte)size);
        w.Write((byte)size);
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(png.Length);
        w.Write(22);
        w.Write(png);
        return ms.ToArray();
    }
}
