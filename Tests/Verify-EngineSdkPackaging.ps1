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
        $numberOfSections = $reader.ReadUInt16()
        $reader.ReadUInt32() | Out-Null # TimeDateStamp
        $reader.ReadUInt32() | Out-Null # PointerToSymbolTable
        $reader.ReadUInt32() | Out-Null # NumberOfSymbols
        $optionalHeaderSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        if (($characteristics -band 0x2000) -eq 0) { throw "Native binary does not have DLL characteristic: $Path" }
        if ($optionalHeaderSize -lt 128) { throw "Optional header too small in native DLL: $Path" }
        $peMagic = $reader.ReadUInt16()
        if ($peMagic -ne 0x020B) { throw "Native DLL is not PE32+ (magic=0x$($peMagic.ToString('X4'))): $Path" }
        # Export directory RVA is at offset 112 inside PE32+ optional header; Import directory is at 120.
        $stream.Position = $peOffset + 24 + 112
        $exportRva = $reader.ReadUInt32()
        $exportSize = $reader.ReadUInt32()
        $importRva = $reader.ReadUInt32()
        $importSize = $reader.ReadUInt32()
        if ($exportRva -eq 0 -or $exportSize -eq 0) {
            throw "Native DLL has an empty export directory: $Path"
        }
        if ($importRva -ne 0 -and $importSize -ge 20) {
            $sections = @()
            $stream.Position = $peOffset + 24 + $optionalHeaderSize
            for ($i = 0; $i -lt $numberOfSections; $i++) {
                $reader.ReadBytes(8) | Out-Null
                $vSize = $reader.ReadUInt32()
                $vAddr = $reader.ReadUInt32()
                $rSize = $reader.ReadUInt32()
                $rPtr = $reader.ReadUInt32()
                $reader.ReadBytes(16) | Out-Null
                $sections += [pscustomobject]@{ VirtualAddress = $vAddr; Size = [Math]::Max($vSize, $rSize); RawPtr = $rPtr }
            }
            $rvaToOffset = {
                param([uint32] $Rva)
                foreach ($sec in $sections) {
                    if ($Rva -ge $sec.VirtualAddress -and $Rva -lt ($sec.VirtualAddress + $sec.Size)) {
                        return $sec.RawPtr + ($Rva - $sec.VirtualAddress)
                    }
                }
                return $null
            }
            $impOffset = & $rvaToOffset $importRva
            if ($null -ne $impOffset) {
                for ($entryIdx = 0; $entryIdx -lt 256; $entryIdx++) {
                    $stream.Position = $impOffset + ($entryIdx * 20)
                    if ($stream.Position + 20 -gt $stream.Length) { break }
                    $origFirstThunk = $reader.ReadUInt32()
                    $reader.ReadUInt32() | Out-Null
                    $reader.ReadUInt32() | Out-Null
                    $nameRva = $reader.ReadUInt32()
                    $firstThunk = $reader.ReadUInt32()
                    if ($origFirstThunk -eq 0 -and $nameRva -eq 0 -and $firstThunk -eq 0) { break }
                    $nameOffset = & $rvaToOffset $nameRva
                    if ($null -eq $nameOffset) { continue }
                    $stream.Position = $nameOffset
                    $nameBytes = [System.Collections.Generic.List[byte]]::new()
                    while ($stream.Position -lt $stream.Length) {
                        $b = $reader.ReadByte()
                        if ($b -eq 0) { break }
                        $nameBytes.Add($b)
                    }
                    $dllName = [Text.Encoding]::ASCII.GetString($nameBytes.ToArray())
                    if ($dllName -imatch '^(vcruntime|msvcp|ucrtbase).*d\.dll$' -or $dllName -imatch 'clang_rt\.asan') {
                        throw "Release native DLL depends on debug or sanitizer runtime '$dllName': $Path"
                    }
                }
            }
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ElfX64So([string] $Path) {
    Assert-FileExists $Path 'Missing native Linux x64 shared library'
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        throw "Invalid ELF magic in shared library: $Path"
    }
    if ($bytes[4] -ne 2 -or $bytes[5] -ne 1) {
        throw "Linux shared library must be 64-bit little-endian ELF: $Path"
    }
    $eType = [BitConverter]::ToUInt16($bytes, 16)
    $eMachine = [BitConverter]::ToUInt16($bytes, 18)
    if ($eType -ne 3 -or $eMachine -ne 0x3E) {
        throw "Linux shared library must be ET_DYN x86-64 (type=$eType, machine=0x$($eMachine.ToString('X4'))): $Path"
    }
}

function Assert-MachODylib([string] $Path) {
    Assert-FileExists $Path 'Missing native macOS dylib'
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 32) { throw "macOS dylib is too small: $Path" }
    $magic = [BitConverter]::ToUInt32($bytes, 0)
    if ($magic -notin 0xFEEDFACFu, 0xCFFAEDFEu, 0xCAFEBABEu, 0xBEBAFECA) {
        throw "Invalid Mach-O magic (0x$($magic.ToString('X8'))) in dylib: $Path"
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
    $linuxSo = Join-Path $packageRoot 'Runtime\Plugins\Linux\x86_64\libliberafx_native.so'
    if (Test-Path -LiteralPath $linuxSo) {
        Assert-FileExists ($linuxSo + '.meta') 'Missing .meta for Linux native plugin'
        Assert-ElfX64So $linuxSo
    } elseif (Test-Path -LiteralPath ($linuxSo + '.meta')) {
        throw "Dangling .meta without Linux binary: $($linuxSo).meta"
    }
    $macosDylib = Join-Path $packageRoot 'Runtime\Plugins\macOS\libliberafx_native.dylib'
    if (Test-Path -LiteralPath $macosDylib) {
        Assert-FileExists ($macosDylib + '.meta') 'Missing .meta for macOS native plugin'
        Assert-MachODylib $macosDylib
    } elseif (Test-Path -LiteralPath ($macosDylib + '.meta')) {
        throw "Dangling .meta without macOS binary: $($macosDylib).meta"
    }
    Write-Output 'Verified Unity binary distribution package (works.libra.liberafx).'
    exit 0
}

if ($Engine -eq 'Godot') {
    $addonRoot = Join-Path $ArtifactRoot 'liberafx'
    foreach ($required in 'VERSION.json', 'LICENSE', 'USAGE.txt', 'LIVE_PREVIEW.txt',
        'plugin.cfg', 'liberafx_editor_plugin.gd', 'liberafx_import_plugin.gd', 'liberafx_package.gd', 'liberafx_effect_playback.gd',
        'liberafx.gdextension', 'liberafx_live_preview.gd', 'liberafx_package_preview.gd', 'liberafx_resource_adapter.gd', 'icon.png') {
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
    if ($gdext -notmatch 'linux\.x86_64\s*=\s*"([^"]+)"') {
        throw 'liberafx.gdextension is missing linux.x86_64 library entry.'
    }
    $linuxLibPath = Join-Path $addonRoot ($Matches[1].TrimStart('.', '/', '\'))
    if (Test-Path -LiteralPath $linuxLibPath) { Assert-ElfX64So $linuxLibPath }
    if ($gdext -notmatch 'macos\.arm64\s*=\s*"([^"]+)"') {
        throw 'liberafx.gdextension is missing macos.arm64 library entry.'
    }
    $macosLibPath = Join-Path $addonRoot ($Matches[1].TrimStart('.', '/', '\'))
    if (Test-Path -LiteralPath $macosLibPath) { Assert-MachODylib $macosLibPath }
    Write-Output 'Verified Godot binary distribution addon (liberafx).'
    exit 0
}
