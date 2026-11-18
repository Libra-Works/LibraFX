#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EndpointDirectory,
    [Parameter(Mandatory)][ValidateSet('list','create','replace','end','destroy')][string] $Operation,
    [string] $Target,
    [string] $CompiledPackage,
    [string] $Label = 'Tool live effect',
    [ValidateRange(1,120)][int] $TimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($EndpointDirectory)) { throw 'Select an absolute isolated endpoint directory.' }
$descriptorPath = Join-Path $EndpointDirectory 'endpoint.json'
if ((Get-Item -LiteralPath $EndpointDirectory -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'Linked endpoints are not supported.'
}
$descriptor = [IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json
if ($descriptor.protocol -ne 1 -or $descriptor.transport -ne 'isolated-local-spool') { throw 'Unsupported endpoint.' }
$requestPath = Join-Path $EndpointDirectory 'request.json'
if (Test-Path -LiteralPath $requestPath) { throw 'Endpoint already has a command. Only one connected local tool is supported.' }
$command = @{
    protocol = 1; session = $descriptor.session; sequence = [DateTime]::UtcNow.Ticks.ToString()
    operation = $Operation; target = $Target; label = $Label; policy = 'restart'
}
if ($Operation -in @('create','replace')) {
    if (-not [IO.Path]::IsPathRooted($CompiledPackage) -or [IO.Path]::GetExtension($CompiledPackage) -ne '.lfxp') {
        throw 'Supply an absolute explicit converter-produced .lfxp path.'
    }
    $item = Get-Item -LiteralPath $CompiledPackage
    if ($item.Length -gt 16MB -or $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Oversized or linked package rejected.' }
    $command.packageBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($item.FullName))
}
if ($Operation -notin @('create','list') -and $Target -notmatch '^[0-9a-fA-F]{16}:[0-9a-fA-F]{16}$') {
    throw 'Select an exact target returned by list/create for this session.'
}
$temporary = Join-Path $EndpointDirectory ('client-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, ($command | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $requestPath)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $responsePath = Join-Path $EndpointDirectory 'response.json'
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $responsePath) {
            try {
                $response = [IO.File]::ReadAllText($responsePath) | ConvertFrom-Json
                if ($response.session -eq $command.session -and $response.sequence -eq $command.sequence) {
                    $response
                    if ($response.result -ne 'Ok') { throw "Live operation rejected: $($response.result): $($response.error)" }
                    return
                }
            } catch [System.IO.IOException] { }
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'No correlated response before timeout. Do not retry create blindly: inspect list first.'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
