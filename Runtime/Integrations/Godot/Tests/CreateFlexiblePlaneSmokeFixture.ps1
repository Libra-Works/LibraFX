param(
    [Parameter(Mandatory = $true)][string]$BasePackage,
    [Parameter(Mandatory = $true)][string]$OutputPackage,
    [switch]$CameraFacing,
    [switch]$IncludeModel
)

$ErrorActionPreference = 'Stop'
$basePath = [System.IO.Path]::GetFullPath($BasePackage)
$outputPath = [System.IO.Path]::GetFullPath($OutputPackage)
if (-not [System.IO.Path]::IsPathFullyQualified($BasePackage) -or
    -not [System.IO.Path]::IsPathFullyQualified($OutputPackage)) {
    throw 'BasePackage and OutputPackage must be absolute paths.'
}
if (-not (Test-Path -LiteralPath $basePath -PathType Leaf)) {
    throw "Base package not found: $basePath"
}
if (Test-Path -LiteralPath $outputPath) {
    throw "Output package already exists: $outputPath"
}

$package = [System.IO.File]::ReadAllBytes($basePath)
if ($package.Length -lt 24 -or
    [System.Text.Encoding]::ASCII.GetString($package, 0, 4) -ne 'LFXP') {
    throw 'Base package is not an LFXP package.'
}

$inputs = @(
    [ordered]@{
        id = 'width'
        width = 1
        sourceComponent = 0
        defaults = @(1)
        sourceNodeId = $null
        materialParameter = $null
    },
    [ordered]@{
        id = 'tint'
        width = 4
        sourceComponent = 0
        defaults = @(1, 1, 1, 1)
        sourceNodeId = $null
        materialParameter = $null
    },
    [ordered]@{
        id = 'position0'
        width = 3
        sourceComponent = 0
        defaults = @(0, 0, 0)
        sourceNodeId = $null
        materialParameter = $null
    },
    [ordered]@{
        id = 'position1'
        width = 3
        sourceComponent = 0
        defaults = @(2, 0, 0)
        sourceNodeId = $null
        materialParameter = $null
    }
)
$node = [ordered]@{
    nodeId = '0e438f2e-41c5-4321-9e03-29e7b895b93d'
    kind = 'flexiblePlane'
    assetPath = '/Game/FX/T_Ribbon'
    billboard = $false
    materials = @()
    textures = @()
    flexiblePlane = [ordered]@{
        version = 1
        capacity = 0
        sourceKind = 'list'
        sourceNodeId = '81beac2d-d75a-43d2-8e02-4f032c2e83a6'
        facing = $(if ($CameraFacing) { 'camera' } else { 'fixedUp' })
        uvMode = 'stretch'
        maxAge = 0
        repeatLength = 1
        up = @(0, 0, 1)
        points = @(
            [ordered]@{ position = @(0, 0, 0); width = 0.5; color = @(1, 0, 0, 1) },
            [ordered]@{ position = @(2, 0, 0); width = 0.5; color = @(0, 1, 0, 1) }
        )
    }
    inputs = $inputs
}
$configuration = [ordered]@{
    type = 'render_config'
    programSequence = 0
    nodes = @($node)
}
$needle = [System.Text.Encoding]::UTF8.GetBytes('{"type":"render_config"')
$renderIndex = -1
$renderCount = 0
for ($index = 24; $index -le $package.Length - $needle.Length; $index++) {
    $matches = $true
    for ($offset = 0; $offset -lt $needle.Length; $offset++) {
        if ($package[$index + $offset] -ne $needle[$offset]) {
            $matches = $false
            break
        }
    }
    if ($matches) {
        $renderIndex = $index
        $renderCount++
    }
}
if ($renderIndex -lt 4 -or $renderCount -ne 2) {
    throw 'Expected one template and one root render configuration in the base fixture.'
}

$oldSize = [BitConverter]::ToInt32($package, $renderIndex - 4)
$oldEnd = $renderIndex + $oldSize
if ($oldSize -le 0 -or $oldEnd -gt $package.Length) {
    throw 'Root render configuration has an invalid blob length.'
}
if ($IncludeModel) {
    $oldConfig = [System.Text.Encoding]::UTF8.GetString($package, $renderIndex, $oldSize) |
        ConvertFrom-Json
    if ($oldConfig.nodes.Count -ne 1 -or $oldConfig.nodes[0].kind -ne 'model') {
        throw 'Expected the base fixture to contain exactly one root ModelDraw.'
    }
    $configuration.nodes = @($oldConfig.nodes[0], $node)
}
$renderBytes = [System.Text.Encoding]::UTF8.GetBytes(
    (ConvertTo-Json -InputObject $configuration -Depth 32 -Compress))
$newSize = $package.Length - $oldSize + $renderBytes.Length
$output = [byte[]]::new($newSize)
[Array]::Copy($package, 0, $output, 0, $renderIndex - 4)
[Array]::Copy([BitConverter]::GetBytes([int]$renderBytes.Length), 0,
    $output, $renderIndex - 4, 4)
[Array]::Copy($renderBytes, 0, $output, $renderIndex, $renderBytes.Length)
[Array]::Copy($package, $oldEnd, $output, $renderIndex + $renderBytes.Length,
    $package.Length - $oldEnd)
[Array]::Copy([BitConverter]::GetBytes([uint64]$output.Length), 0, $output, 8, 8)

$outputDirectory = [System.IO.Path]::GetDirectoryName($outputPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[System.IO.File]::WriteAllBytes($outputPath, $output)
Write-Output "Created synthetic Flexible Plane package: $outputPath"
