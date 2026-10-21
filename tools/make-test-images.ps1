# Writes the small real image files the header-parser tests read (tests\data\img). WebP files are not made here: Windows has no encoder
# (they were produced by a browser canvas, see tests\data\img\README.txt). Run once; the files are committed.
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\data\img'
New-Item -ItemType Directory -Force $out | Out-Null

function New-Bitmap([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::SteelBlue)
    $g.FillEllipse([System.Drawing.Brushes]::Orange, [int]($w / 4), [int]($h / 4), [int]($w / 2), [int]($h / 2))
    $g.Dispose()
    return $bmp
}

$png = New-Bitmap 120 80;   $png.Save((Join-Path $out 'png-120x80.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$tall = New-Bitmap 60 90;   $tall.Save((Join-Path $out 'png-60x90.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$jpg = New-Bitmap 200 100;  $jpg.Save((Join-Path $out 'jpeg-200x100.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
$gif = New-Bitmap 64 48;    $gif.Save((Join-Path $out 'gif-64x48.gif'), [System.Drawing.Imaging.ImageFormat]::Gif)
$bmp = New-Bitmap 40 30;    $bmp.Save((Join-Path $out 'bmp-40x30.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$ico = New-Bitmap 48 48
$icon = [System.Drawing.Icon]::FromHandle($ico.GetHicon())
$fs = [IO.File]::Create((Join-Path $out 'ico-48x48.ico')); $icon.Save($fs); $fs.Close()

Get-ChildItem $out | Select-Object Name, Length
