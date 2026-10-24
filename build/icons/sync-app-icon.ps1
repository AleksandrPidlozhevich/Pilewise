# One app icon, three containers. Velopack wants a different file per OS
# (.ico on Windows, .png on Linux, .icns on macOS), so the macOS icon is the
# master and the other two are the same pictures pulled out of it.
# Regenerate Assets/Pilewise.icns from Assets/Pilewise-macos.svg (the mark in
# Assets/Pilewise.svg, on Apple's icon grid), then run this script.

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\Pilewise\Assets')
$icnsPath = Join-Path $root 'Pilewise.icns'
$bytes = [IO.File]::ReadAllBytes($icnsPath)
if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'icns') {
    throw "Not an icns file: $icnsPath"
}

function Read-Be32([byte[]]$b, [int]$o) {
    return ([int]$b[$o] -shl 24) -bor ([int]$b[$o + 1] -shl 16) -bor ([int]$b[$o + 2] -shl 8) -bor [int]$b[$o + 3]
}

# icns types that hold a PNG, in the order iconutil writes them. Later entries
# repeat a size at @2x; the first one of each size is kept.
$pngs = @()
$offset = 8
while ($offset + 8 -le $bytes.Length) {
    $type = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
    $length = Read-Be32 $bytes ($offset + 4)
    if ($length -lt 8) { throw "Bad icns chunk $type at $offset" }
    $start = $offset + 8
    $dataLength = $length - 8
    $isPng = $dataLength -gt 24 -and $bytes[$start] -eq 0x89 -and $bytes[$start + 1] -eq 0x50
    if ($isPng) {
        $width = Read-Be32 $bytes ($start + 16)
        $height = Read-Be32 $bytes ($start + 20)
        $png = New-Object byte[] $dataLength
        [Array]::Copy($bytes, $start, $png, 0, $dataLength)
        $pngs += [pscustomobject]@{ Type = $type; Width = $width; Height = $height; Png = $png }
    }
    $offset += $length
}

$bySize = @{}
foreach ($png in $pngs) {
    if (-not $bySize.ContainsKey($png.Width)) { $bySize[$png.Width] = $png }
}

$linux = $bySize[1024]
if (-not $linux) { throw 'Pilewise.icns has no 1024px image for the Linux icon.' }
[IO.File]::WriteAllBytes((Join-Path $root 'Pilewise.png'), $linux.Png)

# An ICO directory stores the size in one byte (0 means 256), so 512 and 1024
# stay in the icns only.
$icoSizes = @(16, 32, 64, 128, 256)
$frames = foreach ($size in $icoSizes) {
    if (-not $bySize.ContainsKey($size)) { throw "Pilewise.icns has no ${size}px image." }
    $bySize[$size]
}

$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)
$imageOffset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $side = if ($frame.Width -ge 256) { 0 } else { $frame.Width }
    $writer.Write([byte]$side)
    $writer.Write([byte]$side)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Png.Length)
    $writer.Write([uint32]$imageOffset)
    $imageOffset += $frame.Png.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Png) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'Pilewise.ico'), $stream.ToArray())

Write-Host "Wrote Pilewise.png ($($linux.Width)px) and Pilewise.ico ($($icoSizes -join ', '))"
