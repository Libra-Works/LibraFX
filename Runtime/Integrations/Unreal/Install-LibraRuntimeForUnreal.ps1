#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectPath,
    [switch] $Update
)

$ErrorActionPreference = 'Stop'
$pluginName = 'LibraRuntimeForUnreal'
$sourceRoot = Join-Path $PSScriptRoot $pluginName
$coreRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
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
    if (-not $child.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside plugin rejected: $Relative"
    }
    return $child
}

function Assert-NoLinks([string] $Path, [string] $Boundary) {
    $current = [IO.Path]::GetFullPath($Path)
    $limit = [IO.Path]::GetFullPath($Boundary)
    while ($current.StartsWith($limit, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                throw "Installing through a link is not supported: $current"
            }
        }
        if ($current -eq $limit) { break }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current -or [string]::IsNullOrEmpty($parent)) { break }
        $current = $parent
    }
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
    foreach ($folder in 'Source', 'Resources') {
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
    $bundle = Join-Path $stage 'Source\ThirdParty\LibraRuntimeCore'
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
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $coreRoot 'Core') -Filter '*.cpp' -File -Recurse) {
        if ($file.Name -eq 'VfxRuntime.cpp' -and $file.DirectoryName -eq (Join-Path $coreRoot 'Core')) { continue }
        $relative = [IO.Path]::GetRelativePath($coreRoot, $file.FullName).Replace('\', '/')
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($relative)) }
        finally { $sha.Dispose() }
        $suffix = [BitConverter]::ToString($digest).Replace('-', '').Substring(0, 12)
        $wrapper = Join-Path $stage "Source\LibraRuntimeCore\Private\Generated\LibraNative_$suffix.cpp"
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
