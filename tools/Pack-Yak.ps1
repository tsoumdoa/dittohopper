#Requires -Version 7
<#
.SYNOPSIS
Builds, tests and verifies a local Windows Yak package in artifacts/yak/. Never publishes.
.DESCRIPTION
Builds the solution in Release, runs the Rhino-free tests, stages the payload (the .gha, the DLLs it needs,
LICENSE, README and icon), checks every DittoHopper assembly carries the release version, builds the .yak
with Rhino's Yak and checks its contents. With -Version, first bumps <Version> in Directory.Build.props and
commits that on a clean working tree.
.EXAMPLE
./tools/Pack-Yak.ps1 -Version 0.1.0-beta.2   # bump, commit, package
.EXAMPLE
./tools/Pack-Yak.ps1 -AllowDirty              # a local trial of uncommitted work
#>
param(
    [string]$Version,
    [string]$YakPath = $(if ($env:YAK_PATH) { $env:YAK_PATH } else { 'C:\Program Files\Rhino 8\System\Yak.exe' }),
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')

function Test-Dirty { @(& git -C $repo status --porcelain).Count -gt 0 }

if ($Version) {
    Assert-ReleaseVersion $Version
    if ($AllowDirty) { throw '-Version commits the bump, so it cannot be combined with -AllowDirty.' }
    if (Test-Dirty) { throw 'The working tree is dirty. Commit your changes before bumping the version.' }
    $current = Get-ReleaseVersion $repo
    if ((Compare-ReleaseVersion $Version $current) -le 0) { throw "The new version $Version must be greater than $current." }
    Set-ReleaseVersion $repo $Version
    & git -C $repo commit -m "Release $Version" -- Directory.Build.props
    if ($LASTEXITCODE -ne 0) { throw "The bump commit failed; Directory.Build.props says $Version but is not committed." }
}

$version = Get-ReleaseVersion $repo
$manifest = Get-Content -LiteralPath (Join-Path $repo 'packaging/manifest.yml') -Raw
if ([regex]::Matches($manifest, '(?m)^version: "@VERSION@"\r?$').Count -ne 1) { throw 'packaging/manifest.yml needs exactly one version: "@VERSION@" line.' }
if (-not (Test-Path -LiteralPath $YakPath -PathType Leaf)) { throw "Yak not found: $YakPath (set YAK_PATH)." }
$dirty = Test-Dirty
if ($dirty -and -not $AllowDirty) { throw 'The working tree is dirty. Commit your changes, or pass -AllowDirty for a local trial.' }

$solution = Join-Path $repo 'DittoHopper.sln'
& dotnet build $solution -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
& dotnet test $solution -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

$bin = Join-Path $repo 'src/DittoHopper.Grasshopper/bin/Release/net8.0-windows'
$payload = [ordered]@{
    'DittoHopper.Grasshopper.gha' = Join-Path $bin 'DittoHopper.Grasshopper.gha'
    'DittoHopper.Core.dll' = Join-Path $bin 'DittoHopper.Core.dll'
    'DittoHopper.Rhino.dll' = Join-Path $bin 'DittoHopper.Rhino.dll'
    'System.IO.Hashing.dll' = Join-Path $bin 'System.IO.Hashing.dll'
    'LICENSE' = Join-Path $repo 'LICENSE'
    'README.md' = Join-Path $repo 'packaging/README.md'
    'icon.png' = Join-Path $repo 'assets/icon.png'
}
foreach ($name in $payload.Keys) {
    $source = $payload[$name]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) { throw "Missing or empty: $source" }
    if ($name -like 'DittoHopper.*') {
        $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($source).ProductVersion
        if ($actual -cne $version) { throw "$name is version $actual, expected $version." }
    }
}

$output = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/yak'))
$stage = Join-Path $output 'stage'
if (-not $stage.StartsWith([IO.Path]::GetFullPath($repo) + [IO.Path]::DirectorySeparatorChar)) { throw "Unsafe staging path: $stage" }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($name in $payload.Keys) { Copy-Item -LiteralPath $payload[$name] -Destination (Join-Path $stage $name) }
$manifest.Replace('@VERSION@', $version) | Set-Content -LiteralPath (Join-Path $stage 'manifest.yml') -Encoding utf8

Push-Location -LiteralPath $stage
try {
    & $YakPath build --platform win
    if ($LASTEXITCODE -ne 0) { throw 'Yak build failed.' }
} finally { Pop-Location }
$packages = @(Get-ChildItem -LiteralPath $stage -Filter '*.yak' -File)
if ($packages.Count -ne 1) { throw "Expected one .yak, found $($packages.Count)." }
$package = $packages[0]
if ($package.Name -cnotlike "dittohopper-$version-rh8*-win.yak") { throw "Unexpected package name $($package.Name)." }

# The archive holds exactly the payload plus Yak's manifest, byte for byte.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $expected = @($payload.Keys) + 'manifest.yml' | Sort-Object
    $names = @($zip.Entries | ForEach-Object FullName | Sort-Object)
    if (@(Compare-Object $expected $names -CaseSensitive).Count -gt 0) { throw "Unexpected package contents: $($names -join ', ')" }
    foreach ($entry in $zip.Entries) {
        $stream = $entry.Open()
        try {
            if ($entry.FullName -eq 'manifest.yml') {
                $text = [IO.StreamReader]::new($stream).ReadToEnd()
                if ($text -cnotmatch '(?m)^name: dittohopper\r?$' -or $text -cnotmatch ('(?m)^version: ' + [regex]::Escape($version) + '\r?$') -or
                    $text -notmatch 'guid:e59805d1-fcad-4d4f-b916-57b439bf9d91') { throw 'The packaged manifest has the wrong name, version or plug-in GUID.' }
                continue
            }
            $sha = [Security.Cryptography.SHA256]::HashData($stream)
            if ([Convert]::ToHexString($sha) -cne (Get-FileHash -LiteralPath $payload[$entry.FullName] -Algorithm SHA256).Hash) { throw "The archive's $($entry.FullName) differs from the build." }
        } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }

$destination = Join-Path $output $package.Name
Copy-Item -LiteralPath $package.FullName -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $($package.Name)" | Set-Content -LiteralPath "$destination.sha256" -Encoding ascii
$commit = (& git -C $repo rev-parse --short HEAD) + $(if ($dirty) { ' + uncommitted changes' } else { '' })
Write-Output "Package: $destination"
Write-Output "SHA-256: $hash"
Write-Output "Version $version from $commit. Nothing was published."
