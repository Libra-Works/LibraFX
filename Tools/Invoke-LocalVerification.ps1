#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $SkipDotNetTests,
    [switch] $SkipRuntimeTests,
    [switch] $SkipPackaging,
    [ValidateSet('Unity', 'Unreal', 'Godot', 'All')]
    [string[]] $IncludeEngines = @(),
    [string] $UnityExe,
    [string] $UnrealEngineRoot,
    [string] $GodotExe,
    [switch] $StrictClean,
    [switch] $KeepScratch,
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('lfx-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
$failed = $false

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "==> [$Name]" -ForegroundColor Cyan
    try {
        $detail = & $Action
        $sw.Stop()
        $results.Add([pscustomobject]@{
            Step     = $Name
            Status   = 'PASS'
            Seconds  = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
            Detail   = ($detail | Out-String).Trim()
        })
        Write-Host "    PASS ($([Math]::Round($sw.Elapsed.TotalSeconds, 1))s)" -ForegroundColor Green
    }
    catch {
        $sw.Stop()
        $script:failed = $true
        $results.Add([pscustomobject]@{
            Step     = $Name
            Status   = 'FAIL'
            Seconds  = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
            Detail   = $_.Exception.Message
        })
        Write-Host "    FAIL ($([Math]::Round($sw.Elapsed.TotalSeconds, 1))s): $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Add-Skip([string] $Name, [string] $Reason) {
    $results.Add([pscustomobject]@{
        Step    = $Name
        Status  = 'SKIP'
        Seconds = 0
        Detail  = $Reason
    })
    Write-Host "==> [$Name] SKIP: $Reason" -ForegroundColor Yellow
}

function Find-UnityExe {
    if ($UnityExe) { return $UnityExe }
    $hubRoot = 'C:\Program Files\Unity\Hub\Editor'
    if (Test-Path -LiteralPath $hubRoot) {
        $candidate = Get-ChildItem -LiteralPath $hubRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
            Join-Path $_.FullName 'Editor\Unity.exe'
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    return $null
}

function Find-UnrealRoot {
    if ($UnrealEngineRoot) { return $UnrealEngineRoot }
    $epicRoot = 'C:\Program Files\Epic Games'
    if (Test-Path -LiteralPath $epicRoot) {
        foreach ($preferred in @('UE_5.7', 'UE_5.8', 'UE_5.9', 'UE_5.6')) {
            $candidate = Join-Path $epicRoot $preferred
            if (Test-Path -LiteralPath (Join-Path $candidate 'Engine\Build\BatchFiles\RunUAT.bat') -PathType Leaf) {
                return $candidate
            }
        }
    }
    return $null
}

function Find-GodotExe {
    if ($GodotExe) { return $GodotExe }
    $cmd = Get-Command godot, godot4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

$wantUnity = ($IncludeEngines -contains 'All') -or ($IncludeEngines -contains 'Unity')
$wantUnreal = ($IncludeEngines -contains 'All') -or ($IncludeEngines -contains 'Unreal')
$wantGodot = ($IncludeEngines -contains 'All') -or ($IncludeEngines -contains 'Godot')

try {
    if (-not $SkipDotNetTests) {
        Invoke-Step 'DotNet Tests (Editor/Compiler)' {
            $dataRoot = Join-Path $workRoot 'lfx-data'
            [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
            $prevDataRoot = $env:LIBERAFX_DATA_ROOT
            $env:LIBERAFX_DATA_ROOT = $dataRoot
            try {
                & dotnet run --project (Join-Path $repository 'Tests')
                if ($LASTEXITCODE -ne 0) { throw "dotnet run --project Tests failed (exit $LASTEXITCODE)." }
            }
            finally {
                $env:LIBERAFX_DATA_ROOT = $prevDataRoot
            }
            'dotnet run --project Tests passed'
        }
    } else {
        Add-Skip 'DotNet Tests (Editor/Compiler)' 'Skipped via -SkipDotNetTests'
    }

    if (-not $SkipRuntimeTests) {
        Invoke-Step 'C++ Runtime CMake Build & CTest' {
            $rtBuild = Join-Path $workRoot 'rt-build'
            & cmake -S (Join-Path $repository 'Runtime') -B $rtBuild -DLIBERAFX_BUILD_TESTS=ON -DLIBERAFX_BUILD_INTEROP=ON
            if ($LASTEXITCODE -ne 0) { throw "CMake configure failed (exit $LASTEXITCODE)." }
            & cmake --build $rtBuild --config Release --parallel 4
            if ($LASTEXITCODE -ne 0) { throw "CMake build failed (exit $LASTEXITCODE)." }
            & ctest --test-dir $rtBuild -C Release --output-on-failure --no-tests=error
            if ($LASTEXITCODE -ne 0) { throw "CTest failed (exit $LASTEXITCODE)." }
            'Runtime build and CTest passed'
        }
    } else {
        Add-Skip 'C++ Runtime CMake Build & CTest' 'Skipped via -SkipRuntimeTests'
    }

    $stagedUnityArtifact = $null
    if (-not $SkipPackaging) {
        Invoke-Step 'Documentation Links' {
            $global:LASTEXITCODE = 0
            & (Join-Path $repository 'Tools\Test-DocumentationLinks.ps1')
            'Local documentation links passed'
        }

        Invoke-Step 'Core SDK Export & Manifest Verify' {
            $coreOut = Join-Path $workRoot 'sdk-core'
            $exportArgs = @{ Engine = 'Core'; OutputDirectory = $coreOut; BuildVersion = '2026.11.999' }
            if (-not $StrictClean) { $exportArgs['AllowDirty'] = $true }
            & (Join-Path $repository 'Tools\Export-EngineSdk.ps1') @exportArgs
            & (Join-Path $repository 'Tests\Verify-EngineSdkPackaging.ps1') -Engine Core -ArtifactRoot $coreOut
            'Core SDK manifest verified'
        }

        Invoke-Step 'Unity Binary Package Stage & Verify' {
            $unityOut = Join-Path $workRoot 'sdk-unity'
            [IO.Directory]::CreateDirectory($unityOut) | Out-Null
            $exportArgs = @{ Engine = 'Unity'; OutputDirectory = (Join-Path $unityOut 'Source'); BuildVersion = '2026.11.999' }
            if (-not $StrictClean) { $exportArgs['AllowDirty'] = $true }
            & (Join-Path $repository 'Tools\Export-EngineSdk.ps1') @exportArgs
            Copy-Item -LiteralPath (Join-Path $unityOut 'Source\VERSION.json') -Destination (Join-Path $unityOut 'VERSION.json')
            $nativeBuild = Join-Path $workRoot 'unity-native-build'
            & cmake -S (Join-Path $unityOut 'Source\Runtime') -B $nativeBuild
            if ($LASTEXITCODE -ne 0) { throw "Unity native CMake configure failed." }
            & cmake --build $nativeBuild --config Release --target liberafx_native --parallel 4
            if ($LASTEXITCODE -ne 0) { throw "Unity native CMake build failed." }
            $dllDir = Join-Path $unityOut 'Native\win-x64'
            [IO.Directory]::CreateDirectory($dllDir) | Out-Null
            Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release\liberafx_native.dll') -Destination (Join-Path $dllDir 'liberafx_native.dll')
            & (Join-Path $repository 'Tools\Stage-CopyReadyEngineFolder.ps1') -Engine Unity -ArtifactRoot $unityOut
            & (Join-Path $repository 'Tests\Verify-EngineSdkPackaging.ps1') -Engine Unity -ArtifactRoot $unityOut
            $script:stagedUnityArtifact = Join-Path $unityOut 'works.libra.liberafx'
            'Unity binary package verified (PE exports/imports, .meta, layout)'
        }

        Invoke-Step 'Unreal Source Packaging & Dry-Run Binary Stage' {
            & (Join-Path $repository 'Tests\Verify-UnrealPluginPackaging.ps1')
            $ueDryOut = Join-Path $workRoot 'ue-bin-dryrun'
            $dryArgs = @{ OutputDirectory = $ueDryOut; DryRun = $true; BuildVersion = '2026.11.999' }
            if (-not $StrictClean) { $dryArgs['AllowDirty'] = $true }
            & (Join-Path $repository 'Tools\Build-UnrealBinaryPlugin.ps1') @dryArgs
            'Unreal source packaging and versioned dry-run passed'
        }
    } else {
        Add-Skip 'Packaging & Docs' 'Skipped via -SkipPackaging'
    }

    if ($wantUnity) {
        $unityPath = Find-UnityExe
        if (-not $unityPath) {
            Add-Skip 'Engine: Unity Batch Compile Check' 'Unity.exe not found; pass -UnityExe to specify.'
        } elseif (-not $stagedUnityArtifact -or -not (Test-Path -LiteralPath $stagedUnityArtifact)) {
            Add-Skip 'Engine: Unity Batch Compile Check' 'Staged Unity package unavailable (enable packaging).'
        } else {
            Invoke-Step "Engine: Unity Batch Compile Check ($(Split-Path (Split-Path (Split-Path $unityPath)) -Leaf))" {
                $proj = Join-Path $workRoot 'unity-proj'
                [IO.Directory]::CreateDirectory((Join-Path $proj 'Packages')) | Out-Null
                [IO.Directory]::CreateDirectory((Join-Path $proj 'Assets')) | Out-Null
                [IO.File]::WriteAllText((Join-Path $proj 'Packages\manifest.json'),
                    '{"dependencies":{"com.unity.modules.jsonserialize":"1.0.0"}}',
                    [Text.UTF8Encoding]::new($false))
                Copy-Item -LiteralPath $stagedUnityArtifact -Destination (Join-Path $proj 'Packages\works.libra.liberafx') -Recurse
                $logFile = Join-Path $workRoot 'unity-batch.log'
                & $unityPath -batchmode -quit -nographics -noUpm -disable-assembly-updater -projectPath $proj -logFile $logFile
                if ($LASTEXITCODE -ne 0) {
                    throw "Unity batchmode exited with $LASTEXITCODE (see $logFile)."
                }
                "Unity batch compile succeeded ($logFile)"
            }
        }
    }

    $ueOut = $null
    if ($wantUnreal) {
        $ueRoot = Find-UnrealRoot
        if (-not $ueRoot) {
            Add-Skip 'Engine: Unreal RunUAT BuildPlugin' 'Unreal Engine root not found; pass -UnrealEngineRoot.'
        } else {
            Invoke-Step "Engine: Unreal RunUAT BuildPlugin ($(Split-Path $ueRoot -Leaf))" {
                # Keep path short because UnrealBuildTool enforces a 260-char action path limit on Windows.
                $ueOut = Join-Path ([IO.Path]::GetTempPath()) ('lfx-ue-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
                $buildArgs = @{ UnrealEngineRoot = $ueRoot; OutputDirectory = $ueOut; BuildVersion = '2026.11.999' }
                if (-not $StrictClean) { $buildArgs['AllowDirty'] = $true }
                & (Join-Path $repository 'Tools\Build-UnrealBinaryPlugin.ps1') @buildArgs
                "Unreal binary plugin built under $ueOut"
            }
        }
    }

    if ($wantGodot) {
        $godotPath = Find-GodotExe
        if (-not $godotPath) {
            Add-Skip 'Engine: Godot Headless Smoke' 'Godot executable not found in PATH; pass -GodotExe.'
        } else {
            Invoke-Step 'Engine: Godot Headless Smoke' {
                $godotTests = Join-Path $repository 'Runtime\Integrations\Godot\Tests'
                & $godotPath --headless --path $godotTests --script (Join-Path $godotTests 'smoke.gd')
                if ($LASTEXITCODE -ne 0) { throw "Godot headless smoke failed (exit $LASTEXITCODE)." }
                'Godot headless smoke passed'
            }
        }
    }

    Write-Host "`n=== Local Verification Summary ===" -ForegroundColor White
    $results | Format-Table -AutoSize
    if ($ReportPath) {
        [IO.File]::WriteAllText($ReportPath, ($results | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        Write-Host "Report written to: $ReportPath"
    }
    if ($failed) { exit 1 }
}
finally {
    if (-not $KeepScratch) {
        if (Test-Path -LiteralPath $workRoot) {
            Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($ueOut -and (Test-Path -LiteralPath $ueOut)) {
            Remove-Item -LiteralPath $ueOut -Recurse -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host "Scratch retained at: $workRoot" -ForegroundColor Yellow
        if ($ueOut) { Write-Host "Unreal output retained at: $ueOut" -ForegroundColor Yellow }
    }
}
