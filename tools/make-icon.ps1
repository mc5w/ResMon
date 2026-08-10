<#
.SYNOPSIS
    Erzeugt ResMon.App\ResMon.ico — das Anwendungssymbol.

.DESCRIPTION
    Das Symbol ist ein durchgehender Linienzug wie eine Herzschlagkurve, in dem
    R, S und M stecken: R als Spitze mit Bogen, S als offener Zickzack, M als
    Doppelspitze, dazwischen und außen die Grundlinie.

    Unter 24 Pixeln fällt der Schriftzug in sich zusammen — bei 16 Pixeln ist ein
    Buchstabe gerade drei Pixel breit, die Innenräume laufen mit der Strichstärke
    zu. Für diese Größen liegt deshalb eine vereinfachte Pulslinie im Symbol,
    dieselbe Bildidee ohne Buchstaben. Windows sucht sich je Einsatzort die
    passende Auflösung: Titelleiste und Infobereich nehmen die kleine, Taskleiste
    und Alt-Tab die große.

    Die Datei ist eine erzeugte Binärdatei. Wird die Form geändert, gehören die
    Punktlisten hier geändert und das Skript neu ausgeführt:

        pwsh -File tools\make-icon.ps1

    Dieselbe Form steht als SVG in overlay.html und detail.html — dort für die
    Kopfzeile der kleinen Ansicht und neben den Reitern. Wer hier zieht, zieht
    dort mit.

.NOTES
    Kein Bestandteil des Builds; das Symbol ändert sich seltener als der Code.
#>

param(
    [string]$Output = (Join-Path $PSScriptRoot '..\ResMon.App\ResMon.ico'),
    [string]$Preview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# --- Form -----------------------------------------------------------------

# Punkte im 64er-Raster, Bildschirmkoordinaten (y nach unten).
# Grundlinie · R (Stamm, Bogen, Bein) · S (Zickzack) · M (Doppelspitze) · Grundlinie
$Trace = @(
    0,46,  9,46,                       # Grundlinie herein
    9,15,  20,20,  9,26,  19,46,       # R
    23,46, 33,46,  36,33, 27,31, 30,17, 39,17,  # S
    44,46, 47,46,                      # Abstieg auf die Grundlinie
    47,15, 53,30,  59,15, 59,46,       # M
    64,46                              # Grundlinie hinaus
)

# Vereinfachung für kleine Größen: kleine Vorwelle, hohe Spitze, tiefer
# Ausschlag, Nachwelle.
$Pulse = @(
    1,34, 13,34, 18,25, 23,34, 28,34, 34,7, 40,55, 45,34, 50,34, 54,28, 58,34, 63,34
)

# Ab dieser Kantenlänge trägt das Symbol den Schriftzug.
$LetterThreshold = 24

# Die Farben der Kacheln: CPU-Blau, GPU-Grün, RAM-Orange.
$Stops = @(
    [System.Drawing.Color]::FromArgb(255, 96, 165, 250),
    [System.Drawing.Color]::FromArgb(255, 74, 222, 128),
    [System.Drawing.Color]::FromArgb(255, 251, 146, 60)
)

function New-Frame {
    param([int]$Size)

    $points = if ($Size -ge $LetterThreshold) { $Trace } else { $Pulse }

    # Dünne Striche verschwinden bei kleinen Größen im Weichzeichner, deshalb
    # wird die Strichstärke nach unten hin kräftiger.
    $stroke = if ($Size -le 20) { 6.5 } elseif ($Size -le 32) { 5.6 } elseif ($Size -le 64) { 5.0 } else { 4.6 }

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 64.0
        $path = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        for ($i = 0; $i -lt $points.Length; $i += 2) {
            $path.Add((New-Object System.Drawing.PointF(($points[$i] * $scale), ($points[$i + 1] * $scale))))
        }

        $rect = New-Object System.Drawing.RectangleF(0, 0, [float]$Size, [float]$Size)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect, $Stops[0], $Stops[2], [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
        $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
        $blend.Colors = $Stops
        $blend.Positions = @(0.0, 0.5, 1.0)
        $brush.InterpolationColors = $blend

        $pen = New-Object System.Drawing.Pen($brush, [float]($stroke * $scale))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        $g.DrawLines($pen, $path.ToArray())

        $pen.Dispose()
        $brush.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bitmap
}

# --- ICO-Datei ------------------------------------------------------------

function Get-DibBytes {
    <#
        Ein Symbolbild im DIB-Format: BITMAPINFOHEADER, danach die Bildzeilen von
        unten nach oben und zum Schluss die AND-Maske. Die Maske bleibt leer —
        die Transparenz steckt im Alphakanal, und Windows wertet ihn bei 32 Bit
        aus. biHeight zählt beide Blöcke, also doppelte Höhe.
    #>
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $maskStride = [int](([math]::Floor(($w + 31) / 32)) * 4)
    $pixelBytes = $w * $h * 4
    $maskBytes = $maskStride * $h

    $writer.Write([int]40)          # biSize
    $writer.Write([int]$w)          # biWidth
    $writer.Write([int]($h * 2))    # biHeight (Bild + Maske)
    $writer.Write([int16]1)         # biPlanes
    $writer.Write([int16]32)        # biBitCount
    $writer.Write([int]0)           # biCompression = BI_RGB
    $writer.Write([int]($pixelBytes + $maskBytes))
    $writer.Write([int]0); $writer.Write([int]0)   # Auflösung
    $writer.Write([int]0); $writer.Write([int]0)   # Farbtabelle

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($w * 4)
        for ($y = $h - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
            $writer.Write($row)
        }
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $writer.Write((New-Object byte[] $maskBytes))
    $writer.Flush()

    # Das Komma verhindert, dass PowerShell das Feld beim Rückgeben in einzelne
    # Elemente auflöst — ohne es käme beim Aufrufer ein Object[] an, und der
    # BinaryWriter schriebe daraus nichts.
    return ,$stream.ToArray()
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

# Nur die 256er-Auflösung wird als PNG abgelegt — unkomprimiert wären das allein
# 256 kB. Alle übrigen bleiben DIB: System.Drawing.Icon kann PNG-Bilder in einem
# Symbol nicht auspacken, und über diesen Weg lädt der Infobereich sein Icon.
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-Frame -Size $size
    try {
        [byte[]]$bytes = if ($size -ge 256) { Get-PngBytes $bitmap } else { Get-DibBytes $bitmap }
        $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }
    finally {
        $bitmap.Dispose()
    }
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([int16]0)                  # reserviert
$w.Write([int16]1)                  # Typ 1 = Symbol
$w.Write([int16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    # 256 wird als 0 eingetragen; das Feld ist ein Byte breit.
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $w.Write([byte]$dimension)      # Breite
    $w.Write([byte]$dimension)      # Höhe
    $w.Write([byte]0)               # Farben in der Palette
    $w.Write([byte]0)               # reserviert
    $w.Write([int16]1)              # Ebenen
    $w.Write([int16]32)             # Bits je Pixel
    $w.Write([int]$frame.Bytes.Length)
    $w.Write([int]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $w.Write($frame.Bytes)
}

$w.Flush()
$path = [System.IO.Path]::GetFullPath($Output)
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
$w.Dispose()

"$path — $($frames.Count) Auflösungen, $([math]::Round((Get-Item $path).Length / 1024, 1)) kB"

if ($Preview) {
    # Kontaktabzug zum Draufschauen: alle Auflösungen auf dunklem und hellem
    # Grund. Die Reihenhöhe richtet sich nach dem größten Bild.
    $largest = ($sizes | Measure-Object -Maximum).Maximum
    $rowH = $largest + 40
    $sheetW = 40; foreach ($s in $sizes) { $sheetW += $s + 16 }

    $sheet = New-Object System.Drawing.Bitmap([int]$sheetW, [int]($rowH * 2))
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 17, 18, 20))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 243, 245, 249))), 0, $rowH, $sheetW, $rowH)

    $x = 20.0
    foreach ($s in $sizes) {
        $bitmap = New-Frame -Size $s
        try {
            # Auf gemeinsamer Grundlinie, so wie sie später nebeneinander stehen.
            $g.DrawImage($bitmap, [int]$x, [int]($rowH - 20 - $s))
            $g.DrawImage($bitmap, [int]$x, [int](2 * $rowH - 20 - $s))
        }
        finally { $bitmap.Dispose() }
        $x += $s + 16
    }

    $g.Dispose()
    $sheet.Save([System.IO.Path]::GetFullPath($Preview), [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    "Vorschau: $Preview"
}
