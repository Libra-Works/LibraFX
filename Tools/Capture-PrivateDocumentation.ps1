#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EditorExe,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateSet('Fireworks.lfxproj', 'SolarSystem.lfxproj', 'SolarSystemMatrix.lfxproj')]
    [string] $Sample = 'SolarSystem.lfxproj'
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This capture runner requires Windows.' }
foreach ($path in @($EditorExe, $OutputDirectory)) {
    if (![IO.Path]::IsPathFullyQualified($path) -or $path.StartsWith('\\')) {
        throw 'Use absolute local paths for the isolated editor and capture destination.'
    }
}
$EditorExe = [IO.Path]::GetFullPath($EditorExe)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$samplePath = Join-Path (Split-Path -Parent $EditorExe) "Assets\Samples\$Sample"
if (!(Test-Path -LiteralPath $EditorExe -PathType Leaf) -or
    !(Test-Path -LiteralPath $samplePath -PathType Leaf) -or
    (Test-Path -LiteralPath $OutputDirectory)) {
    throw 'Supply a built editor with the bundled sample and a new output directory.'
}

function Get-LoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $listener.LocalEndpoint.Port
    } finally { $listener.Stop() }
}

$null = New-Item -ItemType Directory -Path $OutputDirectory
$mcpPort = Get-LoopbackPort
do { $previewPort = Get-LoopbackPort } until ($previewPort -ne $mcpPort)
$settings = @{
    LIBERAFX_DATA_ROOT = (Join-Path $OutputDirectory 'editor-data')
    LIBERAFX_MCP_DEBUG = '1'
    LIBERAFX_MCP_DEBUG_PORT = "$mcpPort"
    LIBERAFX_PREVIEW_DEBUG_PORT = "$previewPort"
}
$previous = @{}
$process = $null
$client = $null
try {
    try {
        foreach ($key in $settings.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $settings[$key])
        }
        $process = Start-Process -FilePath $EditorExe -WorkingDirectory (Split-Path -Parent $EditorExe) -PassThru
    } finally {
        foreach ($key in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key])
        }
    }
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "LiberaFx.Editor.Debug.$($process.Id)", [IO.Pipes.PipeDirection]::In)
    try {
        $pipe.Connect(15000)
        $reader = [IO.StreamReader]::new($pipe)
        try { $connection = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $pipe.Dispose() }
    $endpoint = "http://127.0.0.1:$mcpPort/mcp"
    if ($connection.mcpEndpoint -ne $endpoint -or [string]::IsNullOrWhiteSpace($connection.mcpToken)) {
        throw 'The isolated editor did not advertise its expected loopback endpoint.'
    }
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $mcpPort -State Listen -ErrorAction SilentlyContinue |
        Where-Object OwningProcess -eq $process.Id)) {
        if ($process.HasExited -or [DateTime]::UtcNow -ge $startupDeadline) {
            throw 'The isolated editor did not open its loopback MCP listener.'
        }
        Start-Sleep -Milliseconds 250
    }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    $client.DefaultRequestHeaders.Add('X-LiberaFx-Token', $connection.mcpToken)
    $client.DefaultRequestHeaders.Add('Accept', 'application/json, text/event-stream')

    function Invoke-Tool([int] $id, [string] $name, [hashtable] $arguments) {
        $payload = @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'
            params = @{ name = $name; arguments = $arguments } } | ConvertTo-Json -Depth 8 -Compress
        $content = [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
        try { $reply = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult() } finally { $content.Dispose() }
        try {
            $null = $reply.EnsureSuccessStatusCode()
            $body = $reply.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($reply.Content.Headers.ContentType.MediaType -eq 'text/event-stream') {
                $body = ($body -split '\r?\n' | Where-Object { $_.StartsWith('data: ') } | Select-Object -Last 1).Substring(6)
            }
            $response = $body | ConvertFrom-Json
            if ($response.error -or $response.result.isError) {
                $message = if ($response.error) { $response.error.message }
                    else { ($response.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text }
                throw "MCP documentation operation failed ($name): $message"
            }
            $response.result.content
        } finally { $reply.Dispose() }
    }

    $null = Invoke-Tool 1 'libera_graph_open_project' @{ path = $samplePath }
    for ($attempt = 0; ; ++$attempt) {
        try {
            $capture = @(Invoke-Tool (2 + $attempt) 'libera_graph_capture_documentation' @{ sample = $Sample; frameAll = $true })
            break
        } catch {
            if ($attempt -ge 15 -or $_.Exception.Message -notlike '*The active graph view is not visible.*') { throw }
            Start-Sleep -Milliseconds 200
        }
    }
    $image = $capture | Where-Object { $_.type -eq 'image' -and $_.mimeType -eq 'image/png' }
    $text = $capture | Where-Object type -eq 'text'
    $metadata = $text.text | ConvertFrom-Json
    if (!$image.data -or $metadata.sample -cne $Sample -or $metadata.scope -cne 'graph-viewport-only' -or
        $metadata.enginePlaybackVerified -ne $false) {
        throw 'The capture did not contain the expected image and graph-only metadata.'
    }
    $name = [IO.Path]::GetFileNameWithoutExtension($Sample)
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory "$name.png"), [Convert]::FromBase64String($image.data))
    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$name.json"), $text.text)
    Write-Output "Captured $Sample locally in $OutputDirectory (graph viewport only)."
} finally {
    if ($client) { $client.Dispose() }
    if ($process -and !$process.HasExited) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id }
    }
    if ($process) { $process.Dispose() }
}
