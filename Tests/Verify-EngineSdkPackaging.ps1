#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidateSet('Core', 'Unity', 'Godot')]
    [string] $Engine,
    [Parameter(Mandatory)]
    [string] $ArtifactRoot
)

$ErrorActionPreference = 'Stop'

function Assert-FileExists([string] $Path, [string] $Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Message ($Path)"
    }
}

function Assert-PeX64Dll([string] $Path) {
    Assert-FileExists $Path 'Missing native Windows x64 DLL'
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 256) { throw "Native DLL is too small to be valid: $Path" }
        $reader = [IO.BinaryReader]::new($stream)
        $dosMagic = $reader.ReadUInt16()
        if ($dosMagic -ne 0x5A4D) { throw "Invalid DOS header in native DLL: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset + 24 -ge $stream.Length) { throw "Invalid PE offset in native DLL: $Path" }
        $stream.Position = $peOffset
        $peSignature = $reader.ReadUInt32()
        if ($peSignature -ne 0x00004550) { throw "Invalid PE signature in native DLL: $Path" }
        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x8664) { throw "Native DLL is not x86-64 (machine=0x$($machine.ToString('X4'))): $Path" }
        $reader.ReadUInt16() | Out-Null # NumberOfSections
        $reader.ReadUInt32() | Out-Null # TimeDateStamp
        $reader.ReadUInt32() | Out-Null # PointerToSymbolTable
        $reader.ReadUInt32() | Out-Null # NumberOfSymbols
        $optionalHeaderSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        if (($characteristics -band 0x2000) -eq 0) { throw "Native binary does not have DLL characteristic: $Path" }
        if ($optionalHeaderSize -lt 112) { throw "Optional header too small in native DLL: $Path" }
        $peMagic = $reader.ReadUInt16()
        if ($peMagic -ne 0x020B) { throw "Native DLL is not PE32+ (magic=0x$($peMagic.ToString('X4'))): $Path" }
        # Export directory RVA is at offset 112 inside PE32+ optional header.
        $stream.Position = $peOffset + 24 + 112
        $exportRva = $reader.ReadUInt32()
        $exportSize = $reader.ReadUInt32()
        if ($exportRva -eq 0 -or $exportSize -eq 0) {
            throw "Native DLL has an empty export directory: $Path"
        }
    }
    finally {
        $stream.Dispose()
    }
}

$sourceRoot = if ($Engine -eq 'Core') { $ArtifactRoot } else { Join-Path $ArtifactRoot 'Source' }
$manifestPath = Join-Path $sourceRoot 'sdk-manifest.json'
Assert-FileExists $manifestPath 'Missing SDK source manifest'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.engine -ne $Engine) {
    throw "SDK manifest engine '$($manifest.engine)' does not match '$Engine'."
}
if (-not $manifest.files -or $manifest.files.Count -eq 0) {
    throw 'SDK manifest contains no file entries.'
}
foreach ($entry in $manifest.files) {
    $file = Join-Path $sourceRoot $entry.path
    Assert-FileExists $file "Missing SDK source file listed in manifest: $($entry.path)"
    $actualHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $entry.sha256) {
        throw "SHA-256 mismatch for SDK source file: $($entry.path)"
    }
}

if ($Engine -eq 'Core') {
    Write-Output "Verified Core SDK manifest ($($manifest.files.Count) files)."
    exit 0
}

$rootVersionPath = Join-Path $ArtifactRoot 'VERSION.json'
Assert-FileExists $rootVersionPath 'Missing artifact root VERSION.json'
$rootVersion = Get-Content -LiteralPath $rootVersionPath -Raw | ConvertFrom-Json
if ($rootVersion.engine -ne $Engine -or $rootVersion.sourceRevision -ne $manifest.sourceRevision) {
    throw 'Artifact root VERSION.json does not match Source/sdk-manifest.json.'
}

if ($Engine -eq 'Unity') {
    $packageRoot = Join-Path $ArtifactRoot 'works.libra.liberafx'
    foreach ($required in 'VERSION.json', 'LICENSE', 'package.json', 'USAGE.txt', 'GPU-USAGE.txt',
        'Runtime\LiberaFx.Unity.asmdef', 'Editor\LiberaFx.Unity.Editor.asmdef',
        'Runtime\Plugins\x86_64\liberafx_native.dll.meta') {
        Assert-FileExists (Join-Path $packageRoot $required) "Missing required Unity package file: $required"
    }
    $pkg = Get-Content -LiteralPath (Join-Path $packageRoot 'package.json') -Raw | ConvertFrom-Json
    if ($pkg.name -ne 'works.libra.liberafx') {
        throw "Unexpected Unity package name: $($pkg.name)"
    }
    $editorTests = Join-Path $packageRoot 'Editor\Tests'
    if (Test-Path -LiteralPath $editorTests) {
        throw "Copy-ready Unity package must not include internal Editor/Tests: $editorTests"
    }
    Assert-PeX64Dll (Join-Path $packageRoot 'Runtime\Plugins\x86_64\liberafx_native.dll')
    Write-Output 'Verified Unity binary distribution package (works.libra.liberafx).'
    exit 0
}

if ($Engine -eq 'Godot') {
    $addonRoot = Join-Path $ArtifactRoot 'liberafx'
    foreach ($required in 'VERSION.json', 'LICENSE', 'USAGE.txt', 'LIVE_PREVIEW.txt',
        'liberafx.gdextension', 'liberafx_live_preview.gd', 'liberafx_package_preview.gd', 'icon.png') {
        Assert-FileExists (Join-Path $addonRoot $required) "Missing required Godot addon file: $required"
    }
    $gdext = Get-Content -LiteralPath (Join-Path $addonRoot 'liberafx.gdextension') -Raw
    if ($gdext -notmatch 'entry_symbol\s*=\s*"liberafx_godot_init"') {
        throw 'liberafx.gdextension is missing entry_symbol = "liberafx_godot_init".'
    }
    if ($gdext -notmatch 'windows\.x86_64\s*=\s*"([^"]+)"') {
        throw 'liberafx.gdextension is missing windows.x86_64 library entry.'
    }
    $winLibRelative = $Matches[1].TrimStart('.', '/', '\')
    $winLibPath = Join-Path $addonRoot $winLibRelative
    Assert-PeX64Dll $winLibPath
    Write-Output 'Verified Godot binary distribution addon (liberafx).'
    exit 0
}
