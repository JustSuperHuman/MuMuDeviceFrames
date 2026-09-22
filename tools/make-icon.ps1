# Regenerates assets\MuMuDeviceFrames.ico: a phone glyph at 16/24/32/48/64/128/256 px, PNG-compressed entries.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\assets\MuMuDeviceFrames.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); $p
}

$entries = foreach ($s in 16, 24, 32, 48, 64, 128, 256) {
  $b = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $u = $s / 32.0
  $body = Rounded (8 * $u) (2 * $u) (16 * $u) (28 * $u) (4.5 * $u)
  $screen = Rounded (10 * $u) (4 * $u) (12 * $u) (24 * $u) (3 * $u)
  $g.FillPath([System.Drawing.Brushes]::White, $body)
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 110, 255))), $screen)
  if ($s -ge 48) { # dynamic island
    $iw = 5 * $u; $ih = 1.6 * $u
    $g.FillPath([System.Drawing.Brushes]::White, (Rounded ((16 - 2.5) * $u) (5.2 * $u) $iw $ih ($ih / 2)))
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  [pscustomobject]@{ Size = $s; Png = $ms.ToArray() }
}

$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$e.Png.Length); $w.Write([uint32]$offset)
  $offset += $e.Png.Length
}
foreach ($e in $entries) { $w.Write($e.Png) }
$w.Dispose(); $fs.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes)"
