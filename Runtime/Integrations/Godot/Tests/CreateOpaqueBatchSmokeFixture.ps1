param(
    [Parameter(Mandatory = $true)][string]$BasePackage,
    [Parameter(Mandatory = $true)][string]$OutputPackage,
    [switch]$MirrorRootDraw
)

$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($BasePackage) -or
    -not [IO.Path]::IsPathFullyQualified($OutputPackage) -or
    -not (Test-Path -LiteralPath $BasePackage -PathType Leaf) -or
    (Test-Path -LiteralPath $OutputPackage)) {
    throw 'Use an existing absolute input path and a new absolute output path.'
}
$package = [IO.File]::ReadAllBytes($BasePackage)
if ($package.Length -lt 24 -or [Text.Encoding]::ASCII.GetString($package, 0, 4) -ne 'LFXP') {
    throw 'Base package is not an LFXP package.'
}
$needle = [Text.Encoding]::UTF8.GetBytes('{"type":"render_config"')
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
if ($renderCount -ne 2 -or $renderIndex -lt 4) {
    throw 'Expected one template and one root render configuration in the base fixture.'
}
$oldSize = [BitConverter]::ToInt32($package, $renderIndex - 4)
$oldEnd = $renderIndex + $oldSize
if ($oldSize -le 0 -or $oldEnd -gt $package.Length) {
    throw 'Invalid root render configuration length.'
}
$config = [Text.Encoding]::UTF8.GetString($package, $renderIndex, $oldSize) | ConvertFrom-Json
if ($config.nodes.Count -ne 1 -or $config.nodes[0].kind -ne 'model') {
    throw 'Expected exactly one root ModelDraw.'
}
$extra = $config.nodes[0] | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
$extra.nodeId = 'c37416f0-a16d-489a-b54a-cd5294628d11'
$extra.assetPath = '/Tests/Transparent'
$extra.inputs[0].sourceNodeId = $null
$extra.inputs[0].defaults = @(3.5, 0, 0)
$config.nodes = @($config.nodes[0], $extra)
if ($MirrorRootDraw) {
    $scaleInputs = @($config.nodes[0].inputs | Where-Object { $_.id -eq 'scale' })
    if ($scaleInputs.Count -ne 1 -or $null -ne $scaleInputs[0].sourceNodeId) {
        throw 'Expected an unconnected root ModelDraw scale input.'
    }
    $scaleInputs[0].defaults = @(-1, 1, 1)
}
$renderBytes = [Text.Encoding]::UTF8.GetBytes(($config | ConvertTo-Json -Depth 32 -Compress))
$output = [byte[]]::new($package.Length - $oldSize + $renderBytes.Length)
[Array]::Copy($package, 0, $output, 0, $renderIndex - 4)
[Array]::Copy([BitConverter]::GetBytes([int]$renderBytes.Length), 0, $output, $renderIndex - 4, 4)
[Array]::Copy($renderBytes, 0, $output, $renderIndex, $renderBytes.Length)
[Array]::Copy($package, $oldEnd, $output, $renderIndex + $renderBytes.Length, $package.Length - $oldEnd)
[Array]::Copy([BitConverter]::GetBytes([uint64]$output.Length), 0, $output, 8, 8)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPackage)) | Out-Null
[IO.File]::WriteAllBytes($OutputPackage, $output)
Write-Output "Created synthetic opaque batch package: $OutputPackage"
