# Builds PadDeck.exe (single self-contained file, no .NET install needed to run it).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# 1. App icon: a 3x3 pad mark, drawn here so the repo needs no binary files.
if (-not (Test-Path app.ico)) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 24, 32, 48, 64, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $colors = '#e47987','#3276bf','#334155','#334155','#eaf3fe','#3276bf','#3276bf','#334155','#e47987'
        $cell = $s / 3.0
        $pad = [Math]::Max(1, $cell * 0.1)
        for ($i = 0; $i -lt 9; $i++) {
            $x = ($i % 3) * $cell + $pad; $y = [Math]::Floor($i / 3) * $cell + $pad
            $w = $cell - 2 * $pad
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($colors[$i]))
            $g.FillRectangle($brush, [single]$x, [single]$y, [single]$w, [single]$w)
            $brush.Dispose()
        }
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
    Write-Host "Created app.ico"
}

# 2. Publish.
dotnet publish PadDeck.csproj -c Release -o dist -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Get-ChildItem dist -Exclude PadDeck.exe | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$exe = Get-Item dist\PadDeck.exe
Write-Host ("Built {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
