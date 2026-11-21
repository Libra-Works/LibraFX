#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectPath,
    [switch] $Update,
    [switch] $Link
)

$ErrorActionPreference = 'Stop'
$pluginName = 'LibraRuntimeForUnreal'
$sourceRoot = Join-Path $PSScriptRoot $pluginName
$coreRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..', '..'))
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$projectFile = [IO.Path]::GetFullPath($ProjectPath)
if (Test-Path -LiteralPath $projectFile -PathType Container) {
    $projects = @(Get-ChildItem -LiteralPath $projectFile -Filter '*.uproject' -File)
    if ($projects.Count -ne 1) { throw 'Specify a directory with exactly one .uproject file.' }
    $projectFile = $projects[0].FullName
}
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf) -or
    [IO.Path]::GetExtension($projectFile) -ne '.uproject') { throw 'A valid .uproject path is required.' }
$null = [IO.File]::ReadAllText($projectFile) | ConvertFrom-Json
$projectRoot = [IO.Path]::GetDirectoryName($projectFile)
$pluginsRoot = Join-Path $projectRoot 'Plugins'
$targetRoot = Join-Path $pluginsRoot $pluginName
$manifestName = '.libra-install-manifest.json'
$manifestPath = Join-Path $targetRoot $manifestName

function Get-Hash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-ChildPath([string] $Root, [string] $Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw "Absolute manifest path rejected: $Relative" }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $child = [IO.Path]::GetFullPath((Join-Path $rootPath $Relative))
    if (-not $child.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
        throw "Path outside plugin rejected: $Relative"
    }
    return $child
}

function Assert-NoLinks([string] $Path, [string] $Boundary) {
    $current = [IO.Path]::GetFullPath($Path)
    $limit = [IO.Path]::GetFullPath($Boundary)
    while ($current.StartsWith($limit, $pathComparison)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                throw "Installing through a link is not supported: $current"
            }
        }
        if ([string]::Equals($current, $limit, $pathComparison)) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current -or [string]::IsNullOrEmpty($parent)) { break }
        $current = $parent
    }
}

if ($Link) {
    $marker = Join-Path $sourceRoot '.libra-core-source-path'
    Assert-NoLinks $pluginsRoot $projectRoot
    Assert-NoLinks $sourceRoot $coreRoot
    if (Test-Path -LiteralPath $marker) {
        Assert-NoLinks $marker $coreRoot
        if (-not [string]::Equals([IO.File]::ReadAllText($marker).Trim(), $coreRoot, $pathComparison)) {
            throw 'Development Core path already points elsewhere; refusing to replace it.'
        }
    }
    $existing = Get-Item -LiteralPath $targetRoot -Force -ErrorAction SilentlyContinue
    if ($existing -and $existing.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        $destination = [IO.Path]::GetFullPath($existing.ResolvedTarget).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not [string]::Equals($destination, $sourceRoot, $pathComparison)) {
            throw 'Existing plugin link points elsewhere; refusing to replace it.'
        }
        [IO.File]::WriteAllText($marker, $coreRoot, [Text.UTF8Encoding]::new($false))
        Write-Output "Development plugin already linked: $targetRoot -> $sourceRoot"
        return
    }
    $backup = $null
    if ($existing) {
        if (-not $Update) { throw 'Use -Link -Update to replace an installer-managed copy with a link.' }
        Assert-NoLinks $targetRoot $projectRoot
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw 'Existing plugin has no installation manifest; refusing to replace it.'
        }
        Assert-NoLinks $manifestPath $projectRoot
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
        if ($manifest.Version -ne 1 -or $manifest.Plugin -ne $pluginName) { throw 'Unsupported installation manifest.' }
        $owned = @{}
        foreach ($entry in $manifest.Files) {
            $path = Get-ChildPath $targetRoot $entry.Path
            Assert-NoLinks $path $projectRoot
            if ($owned.ContainsKey($entry.Path)) { throw 'Duplicate installation manifest path.' }
            $owned[$entry.Path] = $true
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Hash $path) -ne $entry.Hash) {
                throw "Locally modified plugin file preserved; link conversion stopped: $($entry.Path)"
            }
        }
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $targetRoot 'Source') -File -Recurse) {
            Assert-NoLinks $file.FullName $projectRoot
            $relative = [IO.Path]::GetRelativePath($targetRoot, $file.FullName)
            if (-not $owned.ContainsKey($relative)) { throw "Unowned plugin source preserved; link conversion stopped: $relative" }
        }
        $backup = Join-Path $projectRoot ('.libra-plugin-backup-' + [Guid]::NewGuid().ToString('N'))
    }
    [IO.Directory]::CreateDirectory($pluginsRoot) | Out-Null
    [IO.File]::WriteAllText($marker, $coreRoot, [Text.UTF8Encoding]::new($false))
    if ($backup) { [IO.Directory]::Move($targetRoot, $backup) }
    try {
        $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        $null = New-Item -ItemType $linkType -Path $targetRoot -Target $sourceRoot
    }
    catch {
        if ($backup) { [IO.Directory]::Move($backup, $targetRoot) }
        throw
    }
    Write-Output "Development plugin linked: $targetRoot -> $sourceRoot"
    if ($backup) { Write-Output "Previous copy retained outside Plugins: $backup" }
    Write-Output 'Core and plugin sources are live references. No build or project/configuration edit was performed.'
    return
}

Assert-NoLinks $targetRoot $projectRoot
$previous = @{}
if (Test-Path -LiteralPath $targetRoot) {
    if (-not $Update) { throw 'The plugin already exists. Use -Update to update unchanged installer-owned files.' }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'Existing plugin has no installation manifest; refusing to overwrite it.'
    }
    Assert-NoLinks $manifestPath $projectRoot
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or $manifest.Plugin -ne $pluginName) { throw 'Unsupported installation manifest.' }
    foreach ($entry in $manifest.Files) {
        $path = Get-ChildPath $targetRoot $entry.Path
        Assert-NoLinks $path $projectRoot
        if ($previous.ContainsKey($entry.Path)) { throw 'Duplicate installation manifest path.' }
        $previous[$entry.Path] = $entry.Hash
    }
}

[IO.Directory]::CreateDirectory($pluginsRoot) | Out-Null
$stage = Join-Path $pluginsRoot ('.libra-stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $descriptor = Join-Path $sourceRoot ($pluginName + '.uplugin')
    [IO.File]::Copy($descriptor, (Join-Path $stage ($pluginName + '.uplugin')))
    $usage = Join-Path $PSScriptRoot 'USAGE.txt'
    if (Test-Path -LiteralPath $usage -PathType Leaf) {
        [IO.File]::Copy($usage, (Join-Path $stage 'USAGE.txt'))
    }
    $distributionRoot = [IO.Path]::GetDirectoryName($coreRoot)
    foreach ($name in 'VERSION.json', 'LICENSE') {
        $source = Join-Path $distributionRoot $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Assert-NoLinks $source $distributionRoot
            [IO.File]::Copy($source, (Join-Path $stage $name))
        }
    }
    foreach ($folder in 'Source', 'Resources', 'Shaders') {
        $source = Join-Path $sourceRoot $folder
        if (-not (Test-Path -LiteralPath $source)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
            Assert-NoLinks $file.FullName $sourceRoot
            $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
            $destination = Get-ChildPath $stage $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::Copy($file.FullName, $destination)
        }
    }
    $bundle = Join-Path $stage ([IO.Path]::Combine('Source', 'ThirdParty', 'LibraRuntimeCore'))
    foreach ($folder in 'Core', 'include') {
        $source = Join-Path $coreRoot $folder
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
            Assert-NoLinks $file.FullName $coreRoot
            $relative = [IO.Path]::GetRelativePath($coreRoot, $file.FullName)
            $destination = Get-ChildPath $bundle $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::Copy($file.FullName, $destination)
        }
    }
    $moduleIncludedSources = @(
        'LbVfxProgram.cpp',
        'LbVfxInstance.cpp',
        'LbVfxInstanceSignals.cpp',
        'LbVfxInstanceEvaluation.cpp',
        'LbLivePreview.cpp',
        'LbBuiltinNodes.cpp',
        'LbBuiltinMathNodes.cpp',
        'LbBuiltinMatrixNodes.cpp',
        'LbBuiltinSignalNodes.cpp',
        'LbBuiltinTimeNodes.cpp',
        'LbMatrixMath.cpp',
        'LbDelayNode.cpp',
        'LbGeneratorPool.cpp',
        'LbGeneratorPoolEvaluation.cpp',
        'LbNamedIo.cpp',
        'LbNamedIoProperties.cpp',
        'LbNamedIoExecution.cpp',
        'LbNamedIoEvents.cpp',
        'LbEffectLifecycle.cpp',
        'LbSpawnablePackage.cpp',
        'LbSpawnableHost.cpp',
        'LbSpawnableHostExecution.cpp',
        'LbSpawnableHostScheduling.cpp',
        'LbSpawnableHostApi.cpp',
        'LbLogicalNodes.cpp',
        'LbInstanceSession.cpp',
        'LbPhasePrimitives.cpp'
    )
    $excludedInteropSources = @(
        'LbInteropCoordinates.cpp',
        'LbInterop.cpp',
        'LbInteropInstance.cpp',
        'LbInteropEffectLifecycle.cpp',
        'LbInteropGameIo.cpp',
        'LbInteropSprite.cpp',
        'LbInteropGeneratorPool.cpp',
        'LbInteropSpawnableHost.cpp',
        'LbInteropSpawnableHostPool.cpp',
        'LbInteropEffectWorld.cpp',
        'LbInteropSession.cpp'
    )
    $sourcesWithHandwrittenWrappers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $modulePrivate = Join-Path $stage 'Source\LibraRuntimeCore\Private'
    foreach ($wrapper in Get-ChildItem -LiteralPath $modulePrivate -Filter '*.cpp' -File -Recurse) {
        foreach ($include in [regex]::Matches([IO.File]::ReadAllText($wrapper.FullName),
            '(?m)^#include\s+"([^"\r\n]+\.cpp)"')) {
            $source = $include.Groups[1].Value.Replace('\', '/')
            if (-not $source.StartsWith('Core/', [StringComparison]::Ordinal)) {
                $source = 'Core/' + $source
            }
            [void]$sourcesWithHandwrittenWrappers.Add($source)
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $coreRoot 'Core') -Filter '*.cpp' -File -Recurse) {
        if (($file.Name -in $moduleIncludedSources -or $file.Name -in $excludedInteropSources) -and
            $file.DirectoryName -eq (Join-Path $coreRoot 'Core'))
        {
            continue
        }
        $relative = [IO.Path]::GetRelativePath($coreRoot, $file.FullName).Replace('\', '/')
        if ($sourcesWithHandwrittenWrappers.Contains($relative) -or
            $relative -eq 'Core/CpuParticles/LbInteropCpuParticles.cpp')
        {
            continue
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($relative)) }
        finally { $sha.Dispose() }
        $suffix = [BitConverter]::ToString($digest).Replace('-', '').Substring(0, 12)
        $wrapper = Join-Path $stage ([IO.Path]::Combine('Source', 'LibraRuntimeCore', 'Private', 'Generated', "LibraNative_$suffix.cpp"))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($wrapper)) | Out-Null
        [IO.File]::WriteAllText($wrapper, "#include `"HAL/Platform.h`"`n#include `"$relative`"`n", [Text.UTF8Encoding]::new($false))
    }
    $incoming = @{}
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName)
        $incoming[$relative] = Get-Hash $file.FullName
    }
    foreach ($relative in $previous.Keys) {
        $existing = Get-ChildPath $targetRoot $relative
        if (Test-Path -LiteralPath $existing) {
            $hash = Get-Hash $existing
            if ($hash -ne $previous[$relative] -and
                (-not $incoming.ContainsKey($relative) -or $hash -ne $incoming[$relative])) {
                throw "Locally modified plugin file preserved; update stopped: $relative"
            }
        }
    }
    foreach ($relative in $incoming.Keys) {
        $existing = Get-ChildPath $targetRoot $relative
        Assert-NoLinks $existing $projectRoot
        if ((Test-Path -LiteralPath $existing) -and -not $previous.ContainsKey($relative) -and
            (Get-Hash $existing) -ne $incoming[$relative]) {
            throw "Unowned plugin file preserved; update stopped: $relative"
        }
    }
    $entries = @($incoming.Keys | Sort-Object | ForEach-Object { @{ Path = $_; Hash = $incoming[$_] } })
    $manifest = @{ Version = 1; Plugin = $pluginName; Files = $entries } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $stage $manifestName), $manifest, [Text.UTF8Encoding]::new($false))
    if (-not (Test-Path -LiteralPath $targetRoot)) {
        [IO.Directory]::Move($stage, $targetRoot)
    }
    else {
        foreach ($relative in $incoming.Keys) {
            $destination = Get-ChildPath $targetRoot $relative
            Assert-NoLinks $destination $projectRoot
            if (Test-Path -LiteralPath $destination) {
                $hash = Get-Hash $destination
                if ($hash -ne $incoming[$relative] -and
                    (-not $previous.ContainsKey($relative) -or $hash -ne $previous[$relative])) {
                    throw "Plugin changed during installation; file preserved: $relative"
                }
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::Move((Get-ChildPath $stage $relative), $destination, $true)
        }
        foreach ($relative in $previous.Keys) {
            if ($incoming.ContainsKey($relative)) { continue }
            $obsolete = Get-ChildPath $targetRoot $relative
            if (Test-Path -LiteralPath $obsolete) {
                Assert-NoLinks $obsolete $projectRoot
                if ((Get-Hash $obsolete) -ne $previous[$relative]) { throw "Changed obsolete file preserved: $relative" }
                [IO.File]::Delete($obsolete)
            }
        }
        [IO.File]::Move((Join-Path $stage $manifestName), $manifestPath, $true)
    }
    Write-Output "Installed source plugin: $targetRoot"
    Write-Output 'No Unreal build was run. Existing project/configuration files were not changed.'
}
finally {
    if ([IO.Directory]::Exists($stage)) {
        if ([IO.Path]::GetDirectoryName($stage) -ne $pluginsRoot -or
            -not [IO.Path]::GetFileName($stage).StartsWith('.libra-stage-')) {
            throw 'Unexpected staging path; cleanup refused.'
        }
        [IO.Directory]::Delete($stage, $true)
    }
}
