#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $GodotExecutable,
    [Parameter(Mandatory)][string] $ProjectDirectory,
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [string[]] $RenderingMethods = @('mobile', 'forward_plus'),
    [string[]] $Scripts = @('fluid_2d_models_smoke', 'fluid_2d_draw_consumers_smoke',
        'fluid_2d_field_consumers_smoke', 'fluid_2d_generated_smoke', 'fluid_2d_vorticity_smoke',
        'fluid_2d_gpu_smoke', 'gpu_scope_smoke')
)
$ErrorActionPreference = 'Stop'
foreach ($fluidPath in @($GodotExecutable, $ProjectDirectory, $ArtifactDirectory)) {
    if (-not [IO.Path]::IsPathFullyQualified($fluidPath)) { throw 'Absolute verification paths required.' }
}
if (-not (Test-Path -LiteralPath $GodotExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $ProjectDirectory 'project.godot') -PathType Leaf)) {
    throw 'A Godot executable and an isolated staged project are required.'
}
[IO.Directory]::CreateDirectory($ArtifactDirectory) | Out-Null
$env:LIBERAFX_FLUID_GODOT_OUTPUT = $ArtifactDirectory
$failed = @()
foreach ($method in $RenderingMethods) {
    if ($method -notin @('mobile', 'forward_plus')) { throw 'Unsupported verification renderer.' }
    foreach ($script in $Scripts) {
        if ($script -notmatch '^(fluid_2d_[a-z_]+_smoke|gpu_scope_smoke)$') { throw 'Unsupported verification script name.' }
        $name = "$method-$script"
        $log = Join-Path $ArtifactDirectory "$name.log"
        $verificationProcess = Start-Process -FilePath $GodotExecutable -ArgumentList @(
            '--path', $ProjectDirectory, '--rendering-method', $method, '--rendering-driver', 'vulkan',
            '--script', "res://$script.gd", '--log-file', $log
        ) -WindowStyle Hidden -PassThru
        Write-Output "START $name PID=$($verificationProcess.Id)"
        $completed = $false
        for ($attempt = 0; $attempt -lt 6; $attempt++) {
            if ($verificationProcess.WaitForExit(30000)) { $completed = $true; break }
            Write-Output "RUNNING $name"
        }
        if (-not $completed) {
            $verificationProcess.Kill()
            $verificationProcess.WaitForExit()
            $failed += $name
            Write-Output "FAIL $name timeout"
        } elseif ($verificationProcess.ExitCode -ne 0) {
            $failed += $name
            Write-Output "FAIL $name exit=$($verificationProcess.ExitCode) log=$log"
            if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 14 }
        } else {
            Write-Output "PASS $name log=$log"
        }
        $verificationProcess.Dispose()
    }
}
if ($failed.Count -ne 0) { throw "Godot batch failed: $($failed -join ', ')" }
Write-Output 'PASS all Fluid2D Godot batch stages'
