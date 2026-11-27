#Requires -Version 7
<#
.SYNOPSIS
Regenerates assets/icon.png (the Yak package icon) from the drawing code the plug-in uses at run time.
.DESCRIPTION
Component, tab and plug-in icons are drawn in code (src/DittoHopper.Grasshopper/Icons.cs), so the plug-in
ships no image resources. This script compiles that one file on its own and writes the 256 px package icon,
the same mark as assets/icon.svg. -Sheet <path> also writes a contact sheet of every icon at 1x and 4x.
.EXAMPLE
./tools/Generate-Icons.ps1 -Sheet "$env:TEMP/dittohopper-icons.png"
#>
param([string]$Sheet)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/..").Path
Add-Type -AssemblyName System.Drawing
if (-not ('DittoHopper.Grasshopper.Icons' -as [type])) {
    Add-Type -Path "$repo/src/DittoHopper.Grasshopper/Icons.cs" -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Drawing.Common, System.Private.Windows.GdiPlus, System.Private.Windows.Core
}
$icons = [DittoHopper.Grasshopper.Icons]

$logo = $icons::Logo(256)
try { $logo.Save("$repo/assets/icon.png", [System.Drawing.Imaging.ImageFormat]::Png) } finally { $logo.Dispose() }
Write-Output 'Wrote assets/icon.png'

if ($Sheet) {
    $kinds = [Enum]::GetValues([DittoHopper.Grasshopper.ComponentIcon])
    $cell = 112
    $bitmap = [System.Drawing.Bitmap]::new($cell * ($kinds.Count + 1), 2 * $cell)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(212, 212, 212))
        $g.InterpolationMode = 'NearestNeighbor'
        $g.PixelOffsetMode = 'Half'
        for ($i = 0; $i -le $kinds.Count; $i++) {
            $small = if ($i -lt $kinds.Count) { $icons::Create($kinds[$i], 24) } else { $icons::Logo(24) }
            try {
                $g.DrawImage($small, $i * $cell + 44, 20, 24, 24)        # actual size
                $g.DrawImage($small, $i * $cell + 8, $cell + 8, 96, 96)  # 4x nearest neighbour: the real pixels
            } finally { $small.Dispose() }
        }
        $bitmap.Save([IO.Path]::GetFullPath($Sheet), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "Wrote $Sheet"
    } finally { $g.Dispose(); $bitmap.Dispose() }
}
