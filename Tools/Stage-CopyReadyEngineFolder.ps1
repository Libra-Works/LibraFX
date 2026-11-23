#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidateSet('Unity', 'Godot')]
    [string] $Engine,
    [Parameter(Mandatory)]
    [string] $ArtifactRoot
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $ArtifactRoot 'Source'
$adapter = Join-Path $source "Runtime\Integrations\$Engine"
$native = Join-Path $ArtifactRoot 'Native'
if ($Engine -eq 'Unity') {
    $target = Join-Path $ArtifactRoot 'works.libra.liberafx'
    $items = @('package.json', 'Runtime', 'Editor', 'USAGE.txt', 'GPU-USAGE.txt')
    $requiredNative = @(
        @{ Source = 'win-x64\liberafx_native.dll'; Target = 'Runtime\Plugins\x86_64\liberafx_native.dll' }
    )
    $optionalNative = @(
        @{ Source = 'linux-x64\libliberafx_native.so'; Target = 'Runtime\Plugins\Linux\x86_64\libliberafx_native.so' },
        @{ Source = 'osx-arm64\libliberafx_native.dylib'; Target = 'Runtime\Plugins\macOS\libliberafx_native.dylib' }
    )
} else {
    $target = Join-Path $ArtifactRoot 'liberafx'
    $items = @('USAGE.txt', 'LIVE_PREVIEW.txt', 'liberafx_live_preview.gd', 'liberafx_package_preview.gd', 'icon.png')
    $requiredNative = @(
        @{ Source = 'win-x64\liberafx.gdextension'; Target = 'liberafx.gdextension' },
        @{ Source = 'win-x64\bin\liberafx_godot.dll'; Target = 'bin\liberafx_godot.dll' }
    )
    $optionalNative = @(
        @{ Source = 'linux-x64\bin\libliberafx_godot.so'; Target = 'bin\libliberafx_godot.so' },
        @{ Source = 'osx-arm64\bin\libliberafx_godot.dylib'; Target = 'bin\libliberafx_godot.dylib' }
    )
}

$copies = @(
    foreach ($item in $items) {
        @{ Source = (Join-Path $adapter $item); Target = (Join-Path $target $item) }
    }
    foreach ($item in 'VERSION.json', 'LICENSE') {
        @{ Source = (Join-Path $source $item); Target = (Join-Path $target $item) }
    }
    foreach ($entry in $requiredNative) {
        @{ Source = (Join-Path $native $entry.Source); Target = (Join-Path $target $entry.Target) }
        if ($Engine -eq 'Unity') {
            $metaName = [IO.Path]::GetFileName($entry.Target) + '.meta'
            @{ Source = (Join-Path $PSScriptRoot "Packaging\Unity\$metaName"); Target = (Join-Path $target ($entry.Target + '.meta')) }
        }
    }
    foreach ($entry in $optionalNative) {
        $candidate = Join-Path $native $entry.Source
        if (Test-Path -LiteralPath $candidate) {
            @{ Source = $candidate; Target = (Join-Path $target $entry.Target) }
            if ($Engine -eq 'Unity') {
                $metaName = [IO.Path]::GetFileName($entry.Target) + '.meta'
                @{ Source = (Join-Path $PSScriptRoot "Packaging\Unity\$metaName"); Target = (Join-Path $target ($entry.Target + '.meta')) }
            }
        }
    }
)
if (Test-Path -LiteralPath $target) { throw "Copy-ready folder already exists: $target" }
foreach ($copy in $copies) {
    if (-not (Test-Path -LiteralPath $copy.Source)) { throw "Missing distribution input: $($copy.Source)" }
}
foreach ($copy in $copies) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy.Target)) | Out-Null
    Copy-Item -LiteralPath $copy.Source -Destination $copy.Target -Recurse
}
if ($Engine -eq 'Unity') {
    $editorTests = Join-Path $target 'Editor\Tests'
    if (Test-Path -LiteralPath $editorTests) {
        Remove-Item -LiteralPath $editorTests -Recurse -Force
    }
}
