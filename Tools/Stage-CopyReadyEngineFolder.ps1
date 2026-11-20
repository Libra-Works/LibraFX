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
$native = Join-Path $ArtifactRoot 'Native\win-x64'
if ($Engine -eq 'Unity') {
    $target = Join-Path $ArtifactRoot 'works.libra.liberafx'
    $items = @('package.json', 'Runtime', 'Editor', 'USAGE.txt', 'GPU-USAGE.txt')
    $nativeItems = @('liberafx_native.dll')
} else {
    $target = Join-Path $ArtifactRoot 'liberafx'
    $items = @('USAGE.txt', 'LIVE_PREVIEW.txt', 'liberafx_live_preview.gd', 'liberafx_package_preview.gd', 'icon.png')
    $nativeItems = @('liberafx.gdextension', 'bin\liberafx_godot.dll')
}

$copies = @(
    foreach ($item in $items) {
        @{ Source = (Join-Path $adapter $item); Target = (Join-Path $target $item) }
    }
    foreach ($item in 'VERSION.json', 'LICENSE') {
        @{ Source = (Join-Path $source $item); Target = (Join-Path $target $item) }
    }
    foreach ($item in $nativeItems) {
        $relative = if ($Engine -eq 'Unity') { "Runtime\Plugins\x86_64\$item" } else { $item }
        @{ Source = (Join-Path $native $item); Target = (Join-Path $target $relative) }
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
