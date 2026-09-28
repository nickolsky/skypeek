param([string]$Out, [string]$PreviewDir)
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
Add-Type -AssemblyName System.Xaml

# Each size is drawn natively (not downscaled) so small sizes stay sharp.
function Render([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = [double]$size
    $pad = [Math]::Max(0.5, $s * 0.04)
    $rect = New-Object System.Windows.Rect($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $radius = $s * 0.22

    # Tile: warm orange gradient with a subtle darker bottom edge.
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = New-Object System.Windows.Point(0, 0)
    $grad.EndPoint = New-Object System.Windows.Point(0, 1)
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xFF, 0xB0, 0x2E), 0.0)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xF2, 0x7A, 0x0C), 1.0)))
    $dc.DrawRoundedRectangle($grad, $null, $rect, $radius, $radius)

    if ($size -ge 32) {
        # Soft top highlight for depth on larger sizes.
        $hl = New-Object System.Windows.Media.LinearGradientBrush
        $hl.StartPoint = New-Object System.Windows.Point(0, 0)
        $hl.EndPoint = New-Object System.Windows.Point(0, 1)
        $hl.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(60, 255, 255, 255), 0.0)))
        $hl.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0, 255, 255, 255), 1.0)))
        $hlRect = New-Object System.Windows.Rect($pad, $pad, ($s - 2 * $pad), (($s - 2 * $pad) * 0.48))
        $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry($rect, $radius, $radius)))
        $dc.DrawRectangle($hl, $null, $hlRect)
        $dc.Pop()
    }

    # Glyph: bold "A" with a small pulse line under it (health), drawn white.
    $white = [System.Windows.Media.Brushes]::White
    $face = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily("Segoe UI")), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Bold, [System.Windows.FontStretches]::Normal)
    $fontSize = if ($size -le 20) { $s * 0.80 } else { $s * 0.62 }
    $ft = New-Object System.Windows.Media.FormattedText("A", [System.Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $face, $fontSize, $white, 1.0)
    $geo = $ft.BuildGeometry((New-Object System.Windows.Point(0, 0)))
    $b = $geo.Bounds
    $glyphTop = if ($size -ge 48) { $s * 0.17 } else { ($s - $b.Height) / 2 }
    $dx = ($s - $b.Width) / 2 - $b.X
    $dy = $glyphTop - $b.Y
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform($dx, $dy)))
    $shadow = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(55, 120, 50, 0))
    if ($size -ge 32) {
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(0, $s * 0.015)))
        $dc.DrawGeometry($shadow, $null, $geo)
        $dc.Pop()
    }
    $dc.DrawGeometry($white, $null, $geo)
    $dc.Pop()

    if ($size -ge 48) {
        $pen = New-Object System.Windows.Media.Pen($white, [Math]::Max(1.5, $s * 0.045))
        $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $y = $s * 0.80
        $pts = @(@(0.22, 0), @(0.40, 0), @(0.46, -0.07), @(0.52, 0.06), @(0.58, 0), @(0.78, 0))
        $fig = New-Object System.Windows.Media.PathFigure
        $fig.StartPoint = New-Object System.Windows.Point(($s * $pts[0][0]), ($y + $s * $pts[0][1]))
        foreach ($p in $pts[1..($pts.Count - 1)]) {
            $fig.Segments.Add((New-Object System.Windows.Media.LineSegment((New-Object System.Windows.Point(($s * $p[0]), ($y + $s * $p[1]))), $true)))
        }
        $pg = New-Object System.Windows.Media.PathGeometry
        $pg.Figures.Add($fig)
        $dc.DrawGeometry($null, $pen, $pg)
    }
    $dc.Close()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($sz in $sizes) {
    $png = Render $sz
    $images += , $png
    if ($PreviewDir) { [System.IO.File]::WriteAllBytes((Join-Path $PreviewDir "icon_$sz.png"), $png) }
}

# ICO container with PNG-compressed entries (supported by Windows Vista and later).
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int32]$data.Length); $w.Write([int32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Out, $ms.ToArray())
"wrote $Out ($($ms.Length) bytes, sizes $($sizes -join ','))"
