#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $UnrealEngineRoot,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [ValidatePattern('^[0-9]+\.[0-9]+$')]
    [string] $EngineVersion,
    [string] $TargetPlatforms,
    [ValidatePattern('^[0-9]{4}\.(?:[1-9]|1[0-2])\.[1-9][0-9]*\z')]
    [string] $BuildVersion,
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
    throw 'Binary plugin output must be outside the source repository.'
}
if (Test-Path -LiteralPath $output) { throw 'Output directory already exists; choose a new directory.' }
$parent = [IO.Path]::GetDirectoryName($output)
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The output parent directory must already exist.' }

if (-not $TargetPlatforms) {
    $TargetPlatforms = if ($IsWindows) { 'Win64' } elseif ($IsMacOS) { 'Mac' } else { 'Linux' }
}

$uat = $null
if (-not $DryRun) {
    if ([string]::IsNullOrWhiteSpace($UnrealEngineRoot)) {
        throw 'Specify -UnrealEngineRoot (for example C:\Program Files\Epic Games\UE_5.7), or use -DryRun.'
    }
    $ueRoot = [IO.Path]::GetFullPath($UnrealEngineRoot)
    $uatRelative = if ($IsWindows) { 'Engine\Build\BatchFiles\RunUAT.bat' } else { 'Engine/Build/BatchFiles/RunUAT.sh' }
    $uat = Join-Path $ueRoot $uatRelative
    if (-not (Test-Path -LiteralPath $uat -PathType Leaf)) {
        throw "Unreal Automation Tool not found at: $uat"
    }
    if (-not $EngineVersion) {
        $buildVersionFile = Join-Path $ueRoot 'Engine\Build\Build.version'
        if (-not (Test-Path -LiteralPath $buildVersionFile -PathType Leaf)) {
            throw "Cannot detect UE version (Build.version missing); specify -EngineVersion explicitly (e.g. 5.7, 5.8, 5.9, 6.0)."
        }
        $ueMeta = Get-Content -LiteralPath $buildVersionFile -Raw | ConvertFrom-Json
        $EngineVersion = "$($ueMeta.MajorVersion).$($ueMeta.MinorVersion)"
    }
} elseif (-not $EngineVersion) {
    $EngineVersion = '5.7'
}
$versionFolder = "UE$EngineVersion"

$work = Join-Path $parent ('.libra-ue-bin-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    $source = Join-Path $work 'Source'
    $exportArgs = @{
        Engine = 'Unreal'
        OutputDirectory = $source
        AllowDirty = [bool]$AllowDirty
    }
    if ($BuildVersion) { $exportArgs['BuildVersion'] = $BuildVersion }
    & (Join-Path $PSScriptRoot 'Export-EngineSdk.ps1') @exportArgs

    $projectDir = Join-Path $work 'HostProject'
    [IO.Directory]::CreateDirectory($projectDir) | Out-Null
    $projectFile = Join-Path $projectDir 'LibraBinaryBuild.uproject'
    [IO.File]::WriteAllText($projectFile, '{"FileVersion":3,"EngineAssociation":"5.7","Modules":[]}', [Text.UTF8Encoding]::new($false))

    & (Join-Path $source 'Runtime\Integrations\Unreal\Install-LibraRuntimeForUnreal.ps1') -ProjectPath $projectFile
    $stagedPlugin = Join-Path $projectDir 'Plugins\LibraRuntimeForUnreal'
    $stagedUplugin = Join-Path $stagedPlugin 'LibraRuntimeForUnreal.uplugin'
    if (-not (Test-Path -LiteralPath $stagedUplugin -PathType Leaf)) {
        throw "Missing staged .uplugin: $stagedUplugin"
    }

    $packagedPlugin = Join-Path $output "$versionFolder\LibraRuntimeForUnreal"
    $uatDisplay = if ($uat) { $uat } else { '<UnrealEngineRoot>/Engine/Build/BatchFiles/RunUAT' }
    function Write-PluginVersionMetadata([string] $SourceVersionPath, [string] $DestinationDir) {
        $meta = Get-Content -LiteralPath $SourceVersionPath -Raw | ConvertFrom-Json -AsHashtable
        $meta['unrealEngineVersion'] = $EngineVersion
        $meta['targetPlatforms'] = $TargetPlatforms
        [IO.File]::WriteAllText((Join-Path $DestinationDir 'VERSION.json'),
            ($meta | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    if ($DryRun) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($packagedPlugin)) | Out-Null
        Copy-Item -LiteralPath $stagedPlugin -Destination $packagedPlugin -Recurse
        Write-PluginVersionMetadata (Join-Path $source 'VERSION.json') $packagedPlugin
        Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $packagedPlugin
        Write-Output "Dry-run Unreal plugin staged (no UAT BuildPlugin executed): $packagedPlugin"
        Write-Output "Command: `"$uatDisplay`" BuildPlugin -Plugin=`"$stagedUplugin`" -Package=`"$packagedPlugin`" -TargetPlatforms=$TargetPlatforms -Rocket"
        return
    }

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($packagedPlugin)) | Out-Null
    & $uat BuildPlugin "-Plugin=$stagedUplugin" "-Package=$packagedPlugin" "-TargetPlatforms=$TargetPlatforms" -Rocket
    if ($LASTEXITCODE -ne 0) {
        throw "RunUAT BuildPlugin failed with exit code $LASTEXITCODE."
    }
    $outUplugin = Join-Path $packagedPlugin 'LibraRuntimeForUnreal.uplugin'
    $outBinaries = Join-Path $packagedPlugin 'Binaries'
    if (-not (Test-Path -LiteralPath $outUplugin -PathType Leaf) -or
        -not (Test-Path -LiteralPath $outBinaries -PathType Container)) {
        throw "Prebuilt Unreal plugin output is incomplete: $packagedPlugin"
    }
    Write-PluginVersionMetadata (Join-Path $source 'VERSION.json') $packagedPlugin
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $packagedPlugin -Force
    Write-Output "Built compiler-free Unreal binary plugin ($versionFolder / $TargetPlatforms): $packagedPlugin"
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
