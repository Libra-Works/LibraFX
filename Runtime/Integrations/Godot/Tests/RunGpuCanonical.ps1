param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$GodotExecutable
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$artifact = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path $artifact) {
    throw "ArtifactDirectory must not already exist: $artifact"
}
if (-not (Test-Path -LiteralPath $GodotExecutable -PathType Leaf)) {
    throw "Godot executable not found: $GodotExecutable"
}
$managed = Join-Path $artifact 'managed'
$project = Join-Path $artifact 'godot-project'
$data = Join-Path $artifact 'user-data'
New-Item -ItemType Directory -Path $managed, $project, $data | Out-Null
# Copy sources, not bin/obj: this keeps all restore/build outputs session-local.
foreach ($name in @('LibraGpu.Core', 'LibraGpu.Core.Tests')) {
    $src = Join-Path $root "Gpu/$name"
    $dst = Join-Path $managed $name
    New-Item -ItemType Directory -Path $dst | Out-Null
    Get-ChildItem -LiteralPath $src -File | Copy-Item -Destination $dst
    if (Test-Path (Join-Path $src 'Shaders')) {
        Copy-Item -LiteralPath (Join-Path $src 'Shaders') -Destination $dst -Recurse
    }
}
$settings = @{
    LIBERAFX_DATA_ROOT = Join-Path $data 'liberafx'
    DOTNET_CLI_HOME = Join-Path $data 'dotnet'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
    APPDATA = Join-Path $data 'appdata'
    LOCALAPPDATA = Join-Path $data 'localappdata'
}
$previous = @{}
try {
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }
    New-Item -ItemType Directory -Path $env:LIBERAFX_DATA_ROOT, $env:DOTNET_CLI_HOME, $env:APPDATA, $env:LOCALAPPDATA | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'gpu_canonical.gd') -Destination $project
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Shaders/LgpkCompute.glsl.inc') -Destination $project
    [System.IO.File]::WriteAllText((Join-Path $project 'project.godot'), @"
config_version=5
[application]
config/name="LibraFX GPU canonical verification"
[rendering]
renderer/rendering_method="mobile"
"@)
    & dotnet run --project (Join-Path $managed 'LibraGpu.Core.Tests/LibraGpu.Core.Tests.csproj') -- --export $project
    if ($LASTEXITCODE -ne 0) { throw "Core reference export failed: $LASTEXITCODE" }
    & $GodotExecutable --console --path $project --rendering-method mobile --rendering-driver vulkan --audio-driver Dummy --script res://gpu_canonical.gd
    if ($LASTEXITCODE -ne 0) { throw "Godot GPU canonical verification failed: $LASTEXITCODE" }
}
finally {
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
}
