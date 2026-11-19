#Requires -Version 7.0
param([Parameter(Mandatory = $true)][string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute isolated output directory.' }
$root = Join-Path $OutputDirectory ('unreal-installer-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$project = Join-Path $root 'Test.uproject'
[IO.File]::WriteAllText($project, '{}')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$core = Join-Path $repository 'Runtime\Core'
$nativeCMake = [IO.File]::ReadAllText((Join-Path $repository 'Runtime\CMakeLists.txt'))
$interopTarget = [regex]::Match($nativeCMake, 'add_library\(liberafx_native\s+SHARED\b([^)]*)\)')
$interopSources = @([regex]::Matches($interopTarget.Groups[1].Value, 'Core/[\w./-]+\.cpp') |
    ForEach-Object { $_.Value.Replace('/', '\') })
if ($interopSources.Count -eq 0) { throw 'Could not identify standalone C ABI sources.' }
$installer = Join-Path $repository 'Runtime\Integrations\Unreal\Install-LibraRuntimeForUnreal.ps1'
& $installer -ProjectPath $project

function Assert-Wrappers {
    $private = Join-Path $root 'Plugins\LibraRuntimeForUnreal\Source\LibraRuntimeCore\Private'
    $includes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $private -Filter '*.cpp' -File -Recurse) {
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            if ($line -notmatch '^#include "([^"]+\.cpp)"$') { continue }
            $path = $Matches[1].Replace('/', '\')
            if (-not $path.StartsWith('Core\')) { $path = 'Core\' + $path }
            if (-not $includes.ContainsKey($path)) { $includes[$path] = 0 }
            $includes[$path]++
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $core -Filter '*.cpp' -File -Recurse) {
        $relative = 'Core\' + [IO.Path]::GetRelativePath($core, $file.FullName)
        $count = if ($includes.ContainsKey($relative)) { $includes[$relative] } else { 0 }
        $expected = if ($relative -in $interopSources) { 0 } else { 1 }
        if ($count -ne $expected) {
            throw "Expected $expected Unreal wrappers for ${relative}; found $count."
        }
    }
    foreach ($shader in Get-ChildItem -LiteralPath (Join-Path $repository 'Runtime\Integrations\Unreal\LibraRuntimeForUnreal\Shaders') -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $repository 'Runtime\Integrations\Unreal\LibraRuntimeForUnreal'), $shader.FullName)
        if (-not (Test-Path -LiteralPath (Join-Path $root "Plugins\LibraRuntimeForUnreal\$relative") -PathType Leaf)) {
            throw "Missing installed shader: $relative"
        }
    }
}

Assert-Wrappers
$plugin = Join-Path $root 'Plugins\LibraRuntimeForUnreal'
$generated = Join-Path $plugin 'Source\LibraRuntimeCore\Private\Generated'
$digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Core/LbBuiltinNodes.cpp'))
$suffix = [Convert]::ToHexString($digest).Substring(0, 12)
$oldRelative = "Source\LibraRuntimeCore\Private\Generated\LibraNative_$suffix.cpp"
$oldWrapper = Join-Path $plugin $oldRelative
[IO.Directory]::CreateDirectory($generated) | Out-Null
[IO.File]::WriteAllText($oldWrapper, "#include `"HAL/Platform.h`"`n#include `"Core/LbBuiltinNodes.cpp`"`n", [Text.UTF8Encoding]::new($false))
$manifestPath = Join-Path $plugin '.libra-install-manifest.json'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$manifest.Files += [pscustomobject]@{ Path = $oldRelative; Hash = (Get-FileHash -LiteralPath $oldWrapper -Algorithm SHA256).Hash }
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
& $installer -ProjectPath $project -Update
if (Test-Path -LiteralPath $oldWrapper) { throw 'Update did not remove the obsolete duplicate wrapper.' }
Assert-Wrappers
Write-Output 'Unreal copy installation and update wrap each supported Core source exactly once, excluding the standalone C ABI.'
