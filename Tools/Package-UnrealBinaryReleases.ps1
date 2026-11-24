#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]{4}\.(?:[1-9]|1[0-2])\.[1-9][0-9]*\z')]
    [string] $BuildVersion,
    [string[]] $UnrealEngineRoots,
    [string] $TargetPlatforms,
    [switch] $AllowDirty,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute output directory.' }
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($output.Equals($repository, $comparison) -or
    $output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'Release zip output must be outside the source repository.'
}
if (-not (Test-Path -LiteralPath $output -PathType Container)) {
    [IO.Directory]::CreateDirectory($output) | Out-Null
}

if (-not $TargetPlatforms) {
    $TargetPlatforms = if ($IsWindows) { 'Win64' } elseif ($IsMacOS) { 'Mac' } else { 'Linux' }
}
$platformTag = switch -Regex ($TargetPlatforms) {
    '^Win64$' { 'win64'; break }
    '^Mac$' { 'macos'; break }
    '^Linux$' { 'linux-x64'; break }
    default { $TargetPlatforms.ToLowerInvariant() }
}

if (-not $DryRun -and (-not $UnrealEngineRoots -or $UnrealEngineRoots.Count -eq 0)) {
    $epicRoot = 'C:\Program Files\Epic Games'
    $discovered = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $epicRoot -PathType Container) {
        foreach ($dir in Get-ChildItem -LiteralPath $epicRoot -Directory -Filter 'UE_*' | Sort-Object Name) {
            $uat = Join-Path $dir.FullName 'Engine\Build\BatchFiles\RunUAT.bat'
            $verFile = Join-Path $dir.FullName 'Engine\Build\Build.version'
            if ((Test-Path -LiteralPath $uat -PathType Leaf) -and (Test-Path -LiteralPath $verFile -PathType Leaf)) {
                $ver = Get-Content -LiteralPath $verFile -Raw | ConvertFrom-Json
                if (($ver.MajorVersion -gt 5) -or ($ver.MajorVersion -eq 5 -and $ver.MinorVersion -ge 7)) {
                    $discovered.Add($dir.FullName)
                }
            }
        }
    }
    if ($discovered.Count -eq 0) {
        throw 'No Unreal Engine 5.7+ installations found under C:\Program Files\Epic Games; pass -UnrealEngineRoots.'
    }
    $UnrealEngineRoots = $discovered.ToArray()
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('lfx-ue-rel-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    $targets = if ($DryRun) { @('DryRun') } else { $UnrealEngineRoots }
    foreach ($ueRoot in $targets) {
        $stageDir = Join-Path $scratch ([Guid]::NewGuid().ToString('N').Substring(0, 6))
        $buildArgs = @{
            OutputDirectory = $stageDir
            BuildVersion = $BuildVersion
            TargetPlatforms = $TargetPlatforms
        }
        if ($AllowDirty) { $buildArgs['AllowDirty'] = $true }
        if ($DryRun) {
            $buildArgs['DryRun'] = $true
        } else {
            $buildArgs['UnrealEngineRoot'] = $ueRoot
        }
        & (Join-Path $PSScriptRoot 'Build-UnrealBinaryPlugin.ps1') @buildArgs

        $ueVersionDirs = Get-ChildItem -LiteralPath $stageDir -Directory
        foreach ($verDir in $ueVersionDirs) {
            $pluginDir = Join-Path $verDir.FullName 'LibraRuntimeForUnreal'
            if (-not (Test-Path -LiteralPath $pluginDir -PathType Container)) { continue }
            $zipName = "LibraForUnreal-$BuildVersion-$($verDir.Name)-$platformTag.zip"
            $zipPath = Join-Path $output $zipName
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
            $zipRoot = Join-Path $scratch ('zip-' + $verDir.Name)
            if (Test-Path -LiteralPath $zipRoot) { Remove-Item -LiteralPath $zipRoot -Recurse -Force }
            [IO.Directory]::CreateDirectory($zipRoot) | Out-Null
            Copy-Item -LiteralPath $pluginDir -Destination (Join-Path $zipRoot 'LibraRuntimeForUnreal') -Recurse
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $zipRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
            Write-Host "Packaged $zipPath"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
