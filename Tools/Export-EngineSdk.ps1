#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Unity', 'Unreal', 'Godot')]
    [string] $Engine,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [switch] $AllowDirty
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute output directory.' }
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($output.Equals($repository, $comparison) -or
    $output.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'SDK staging must be outside the source repository.'
}
if (Test-Path -LiteralPath $output) { throw 'Output already exists; choose a new directory.' }
$parent = [IO.Path]::GetDirectoryName($output)
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The output parent must already exist.' }

function Assert-NoLinks([string] $Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and
            (Get-Item -LiteralPath $current -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "SDK export does not follow links: $current"
        }
    }
}
Assert-NoLinks $parent
Assert-NoLinks $repository
$revision = & git -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source revision.' }
$dirty = @(& git -C $repository status --porcelain --untracked-files=normal).Count -ne 0
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source changes.' }
if ($dirty -and -not $AllowDirty) { throw 'Commit source changes first, or explicitly use -AllowDirty for a local development export.' }
$compatibility = Get-Content -LiteralPath (Join-Path $repository 'SDK\compatibility.json') -Raw | ConvertFrom-Json
$roots = @('Runtime\Core', 'Runtime\include', "Runtime\Integrations\$Engine")
$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($relative in $roots) {
    $root = Join-Path $repository $relative
    Assert-NoLinks $root
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $relativeFile = [IO.Path]::GetRelativePath($repository, $file.FullName)
        if ($relativeFile -match '(^|[\\/])(bin|obj|Binaries|Intermediate|Saved|\.git|ThirdParty)([\\/]|$)' -or
            $file.Name.StartsWith('.') -or
            $file.Extension -in '.dll', '.pdb', '.exe', '.so', '.a', '.lib', '.dylib', '.obj', '.o', '.log', '.tmp', '.user') { continue }
        Assert-NoLinks $file.FullName
        $files.Add($file)
    }
}
foreach ($relative in @('Runtime\CMakeLists.txt', 'SDK\compatibility.json', 'docs\sdk-distribution.md')) {
    $file = Get-Item -LiteralPath (Join-Path $repository $relative)
    Assert-NoLinks $file.FullName
    $files.Add($file)
}
$stage = Join-Path $parent ('.liberafx-sdk-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $entries = foreach ($file in $files | Sort-Object FullName -Unique) {
        $relative = [IO.Path]::GetRelativePath($repository, $file.FullName)
        $destination = Join-Path $stage $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($file.FullName, $destination, $false)
        [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        engine = $Engine
        sdkVersion = $compatibility.sdkVersion
        sourceRevision = "$revision".Trim()
        uncommittedSource = $dirty
        nativeAbi = $compatibility.nativeAbi
        files = @($entries)
    }
    [IO.File]::WriteAllText((Join-Path $stage 'sdk-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::Move($stage, $output)
    Write-Output "Local $Engine SDK source export: $output"
    Write-Output 'No build, engine launch, repository creation, commit, push or upload was performed.'
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
