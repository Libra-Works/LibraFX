#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $UnityExe = 'C:\Program Files\Unity\Hub\Editor\7000.0.0a16\Editor\Unity.exe',
    [string] $GodotExe = 'C:\Users\taku3\Downloads\Godot_v4.7.2-stable_win64.exe\Godot_v4.7.2-stable_win64_console.exe',
    [string] $UnrealEngineRoot = 'C:\Program Files\Epic Games\UE_5.7',
    [string] $GodotExtensionDll,
    [switch] $SkipUnreal,
    [switch] $KeepScratch
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('lfx-i71-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$results = [System.Collections.Generic.List[PSCustomObject]]::new()

function Invoke-Stage([string]$Name, [scriptblock]$Body) {
    Write-Host "==> [$Name]" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $detail = & $Body
    $sw.Stop()
    Write-Host "    PASS ($([Math]::Round($sw.Elapsed.TotalSeconds, 1))s)" -ForegroundColor Green
    $results.Add([PSCustomObject]@{ Stage = $Name; Status = 'PASS'; Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2); Detail = $detail })
}

try {
    $fixtures = Join-Path $workRoot 'fixtures'
    Invoke-Stage '1. LIBERA FX Authoring, Undo/Redo, Save/Reload & Mapped .lfxb Export' {
        $env:LIBERAFX_DATA_ROOT = Join-Path $workRoot 'data-root'
        [IO.Directory]::CreateDirectory($env:LIBERAFX_DATA_ROOT) | Out-Null
        & dotnet run --project (Join-Path $repository 'Tests') -- --issue71-e2e-fixtures $fixtures
        if ($LASTEXITCODE -ne 0) { throw "Issue71E2EFixtures failed (exit $LASTEXITCODE)." }
        "Generated unreal.lfxb, unity.lfxb, godot.lfxb under $fixtures"
    }

    Invoke-Stage '2. Unity Isolated Project E2E (Auto-Resolve, Reimport, Play/Pause/Resume/Stop)' {
        if (-not (Test-Path -LiteralPath $UnityExe -PathType Leaf)) { throw "Unity.exe not found: $UnityExe" }
        $unityStage = Join-Path $workRoot 'unity-stage'
        [IO.Directory]::CreateDirectory($unityStage) | Out-Null
        & (Join-Path $repository 'Tools\Export-EngineSdk.ps1') -Engine Unity -OutputDirectory (Join-Path $unityStage 'Source') -AllowDirty
        Copy-Item -LiteralPath (Join-Path $unityStage 'Source\VERSION.json') -Destination (Join-Path $unityStage 'VERSION.json')
        $nativeBuild = Join-Path $workRoot 'unity-native'
        & cmake -S (Join-Path $unityStage 'Source\Runtime') -B $nativeBuild
        if ($LASTEXITCODE -ne 0) { throw 'Unity native CMake configure failed.' }
        & cmake --build $nativeBuild --config Release --target liberafx_native --parallel 4
        if ($LASTEXITCODE -ne 0) { throw 'Unity native CMake build failed.' }
        $dllDir = Join-Path $unityStage 'Native\win-x64'
        [IO.Directory]::CreateDirectory($dllDir) | Out-Null
        Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release\liberafx_native.dll') -Destination (Join-Path $dllDir 'liberafx_native.dll')
        & (Join-Path $repository 'Tools\Stage-CopyReadyEngineFolder.ps1') -Engine Unity -ArtifactRoot $unityStage

        $proj = Join-Path $workRoot 'unity-proj'
        [IO.Directory]::CreateDirectory((Join-Path $proj 'Packages')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $proj 'Assets\LibraTest')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $proj 'Assets\Editor')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $proj 'Packages\manifest.json'),
            '{"dependencies":{"works.libra.liberafx":"file:works.libra.liberafx","com.unity.modules.jsonserialize":"1.0.0"}}', [Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath (Join-Path $unityStage 'works.libra.liberafx') -Destination (Join-Path $proj 'Packages\works.libra.liberafx') -Recurse

        # First pass: create TestMesh.asset, copy unity.lfxb, then reimport & test disconnected playback.
        $checkCs = @'
using System;
using System.IO;
using LiberaFx.Unity;
using UnityEditor;
using UnityEngine;

public static class Issue71E2ECheck
{
    public static void Prepare()
    {
        var meshPath = "Assets/LibraTest/TestMesh.asset";
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = UnityEngine.Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
        UnityEngine.Object.DestroyImmediate(cube);
        AssetDatabase.CreateAsset(mesh, meshPath);
        AssetDatabase.SaveAssets();
        Debug.Log("UNITY_ISSUE71_PREPARE_PASS");
    }

    public static void Run()
    {
        var fxPath = "Assets/LibraTest/effect.lfxb";
        var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(fxPath);
        if (asset == null)
        {
            AssetDatabase.ImportAsset(fxPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(fxPath);
        }
        if (asset == null) throw new Exception("Failed to load imported LiberaFxSpawnableAsset.");
        if (asset.AutoResolvedAssets == null || asset.AutoResolvedAssets.Length == 0 ||
            asset.AutoResolvedAssets[0].mesh == null)
        {
            string dbgRender = "";
            using (var h = new NativeSpawnableHost(asset.PackageBytes))
            {
                if (h.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out var rb) == NativeResult.Ok)
                    dbgRender = System.Text.Encoding.UTF8.GetString(rb);
                h.GetTemplateCount(out var tc);
                dbgRender += " | templates=" + tc;
            }
            throw new Exception("AutoResolvedAssets did not resolve TestMesh.asset on import. guidPath=" +
                AssetDatabase.GUIDToAssetPath("0123456789abcdef0123456789abcdef") +
                ", count=" + (asset.AutoResolvedAssets?.Length ?? -1) + ", render=" + dbgRender);
        }

        var go = new GameObject("Issue71Test");
        var comp = go.AddComponent<LiberaFxSpawnableComponent>();
        if (comp.ConfigureForSession(asset, null) != NativeResult.Ok)
            throw new Exception("ConfigureForSession failed: " + comp.LastError);
        var playResult = comp.Play();
        if (playResult != NativeResult.Ok) throw new Exception("Play() failed: " + playResult + " (" + comp.LastError + ")");
        if (comp.Pause() != NativeResult.Ok || !comp.IsPaused) throw new Exception("Pause() failed.");
        if (comp.Resume() != NativeResult.Ok || comp.IsPaused) throw new Exception("Resume() failed.");
        if (comp.Stop() != NativeResult.Ok) throw new Exception("Stop() failed.");
        if (comp.Play(true) != NativeResult.Ok) throw new Exception("Restart Play(true) failed.");
        comp.Stop();
        UnityEngine.Object.DestroyImmediate(go);
        Debug.Log("UNITY_ISSUE71_E2E_PASS");
    }
}
'@
        [IO.File]::WriteAllText((Join-Path $proj 'Assets\Editor\Issue71E2ECheck.cs'), $checkCs, [Text.UTF8Encoding]::new($false))
        $logPrepare = Join-Path $workRoot 'unity-prepare.log'
        $prepProc = Start-Process -FilePath $UnityExe -ArgumentList @(
            '-batchmode', '-quit', '-nographics', '-disable-assembly-updater',
            '-projectPath', "`"$proj`"",
            '-executeMethod', 'Issue71E2ECheck.Prepare',
            '-logFile', "`"$logPrepare`""
        ) -Wait -PassThru
        if ($prepProc.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $logPrepare) { Select-String -LiteralPath $logPrepare -Pattern 'error CS|Exception|Error:' | Select-Object -First 25 | Out-Host }
            throw "Unity Prepare failed (exit $($prepProc.ExitCode), see $logPrepare)."
        }
        $metaPath = Join-Path $proj 'Assets\LibraTest\TestMesh.asset.meta'
        $meta = [IO.File]::ReadAllText($metaPath) -replace 'guid:\s*[0-9a-fA-F]{32}', 'guid: 0123456789abcdef0123456789abcdef'
        [IO.File]::WriteAllText($metaPath, $meta, [Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath (Join-Path $fixtures 'unity.lfxb') -Destination (Join-Path $proj 'Assets\LibraTest\effect.lfxb')
        $log = Join-Path $workRoot 'unity-e2e.log'
        $unityArgs = @(
            '-batchmode', '-quit', '-nographics', '-disable-assembly-updater',
            '-projectPath', "`"$proj`"",
            '-executeMethod', 'Issue71E2ECheck.Run',
            '-logFile', "`"$log`""
        )
        $unityProc = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -Wait -PassThru
        if ($unityProc.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $log) {
                Select-String -LiteralPath $log -Pattern 'error CS|Exception|Error:' | Select-Object -First 25 | Out-Host
                Get-Content -LiteralPath $log -Tail 20 | Out-Host
            }
            throw "Unity E2E failed (exit $($unityProc.ExitCode), see $log)."
        }
        if (-not (Select-String -LiteralPath $log -Pattern 'UNITY_ISSUE71_E2E_PASS' -Quiet)) {
            throw "UNITY_ISSUE71_E2E_PASS marker not found in $log."
        }
        "Unity auto-resolve, reimport, Play/Pause/Resume/Stop passed ($log)"
    }

    Invoke-Stage '3. Godot Isolated Project E2E (ImportPlugin, Auto-Resolve, Disconnected Playback)' {
        if (-not (Test-Path -LiteralPath $GodotExe -PathType Leaf)) { throw "Godot executable not found: $GodotExe" }
        $proj = Join-Path $workRoot 'godot-proj'
        $addonDir = Join-Path $proj 'addons\liberafx'
        [IO.Directory]::CreateDirectory((Join-Path $addonDir 'bin')) | Out-Null

        if ($GodotExtensionDll -and (Test-Path -LiteralPath $GodotExtensionDll -PathType Leaf)) {
            Copy-Item -LiteralPath $GodotExtensionDll -Destination (Join-Path $addonDir 'bin\liberafx_godot.dll')
        } else {
            $relZip = Join-Path $workRoot 'LibraForGodot-2026.11.99.zip'
            Invoke-WebRequest -Uri 'https://github.com/Libra-Works/LibraFX/releases/download/v2026.11.99/LibraForGodot-2026.11.99.zip' -OutFile $relZip
            $unzipped = Join-Path $workRoot 'godot-rel'
            Expand-Archive -LiteralPath $relZip -DestinationPath $unzipped -Force
            Copy-Item -LiteralPath (Join-Path $unzipped 'liberafx\bin\liberafx_godot.dll') -Destination (Join-Path $addonDir 'bin\liberafx_godot.dll')
        }
        Copy-Item (Join-Path $repository 'Runtime\Integrations\Godot\*.gd') $addonDir
        Copy-Item (Join-Path $repository 'Runtime\Integrations\Godot\plugin.cfg') $addonDir
        Copy-Item (Join-Path $repository 'Runtime\Integrations\Godot\liberafx.gdextension') $addonDir
        Copy-Item (Join-Path $repository 'Runtime\Integrations\Godot\icon.png') $addonDir
        Copy-Item -LiteralPath (Join-Path $fixtures 'godot.lfxb') -Destination (Join-Path $proj 'effect.lfxb')

        @'
config_version=5
[application]
config/name="Issue71GodotE2E"
[editor_plugins]
enabled=PackedStringArray("res://addons/liberafx/plugin.cfg")
'@ | Set-Content (Join-Path $proj 'project.godot') -Encoding UTF8

        @'
[gd_resource type="BoxMesh" format=3]
[resource]
'@ | Set-Content (Join-Path $proj 'test_mesh.tres') -Encoding UTF8

        & $GodotExe --headless --editor --quit --path $proj
        if ($LASTEXITCODE -ne 0) { throw "Godot editor import failed (exit $LASTEXITCODE)." }
        if (-not (Test-Path -LiteralPath (Join-Path $proj 'effect.lfxb.import'))) {
            throw 'Godot EditorImportPlugin did not create effect.lfxb.import.'
        }

        @'
extends SceneTree
func _init() -> void:
	var pkg := ResourceLoader.load("res://effect.lfxb")
	if pkg == null:
		push_error("Failed to load imported res://effect.lfxb")
		quit(1)
		return
	var resolved: Variant = pkg.get("auto_resolved_resources")
	if not resolved is Dictionary or not resolved.has("res://test_mesh.tres"):
		push_error("auto_resolved_resources missing res://test_mesh.tres")
		quit(2)
		return
	var fx_script := load("res://addons/liberafx/liberafx_effect_playback.gd") as Script
	var fx: Node = fx_script.new()
	fx.set("package", pkg)
	fx.set("autoplay", false)
	root.add_child(fx)
	var rc := int(fx.call("play"))
	if rc != 0 or not bool(fx.call("is_playing")):
		push_error("LibraFxEffect.play() failed: " + str(rc))
		quit(3)
		return
	var player: Node = fx.get("_player")
	if player != null and player.has_method("pause"):
		if int(fx.call("pause")) != 0 or not bool(fx.call("is_paused")):
			push_error("LibraFxEffect.pause() failed")
			quit(4)
			return
		if int(fx.call("resume")) != 0 or bool(fx.call("is_paused")):
			push_error("LibraFxEffect.resume() failed")
			quit(5)
			return
	fx.call("stop")
	if bool(fx.call("is_playing")):
		push_error("LibraFxEffect.stop() left player playing")
		quit(6)
		return
	if int(fx.call("play")) != 0:
		push_error("LibraFxEffect restart play() failed")
		quit(7)
		return
	fx.call("stop")
	print("GODOT_ISSUE71_E2E_PASS")
	quit(0)
'@ | Set-Content (Join-Path $proj 'e2e_check.gd') -Encoding UTF8

        & $GodotExe --headless --path $proj --script (Join-Path $proj 'e2e_check.gd')
        if ($LASTEXITCODE -ne 0) { throw "Godot E2E playback check failed (exit $LASTEXITCODE)." }
        "Godot EditorImportPlugin + auto-resolve + LibraFxEffect play/pause/resume/stop passed"
    }

    if (-not $SkipUnreal) {
        Invoke-Stage '4. Unreal Isolated Project E2E (BuildPlugin + UnrealEditor-Cmd Automation)' {
            $ueCmd = Join-Path $UnrealEngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
            if (-not (Test-Path -LiteralPath $ueCmd -PathType Leaf)) { throw "UnrealEditor-Cmd.exe not found: $ueCmd" }
            $ueOut = Join-Path ([IO.Path]::GetTempPath()) ('lfx-i71-ue-' + [Guid]::NewGuid().ToString('N').Substring(0, 5))
            try {
                & (Join-Path $repository 'Tools\Build-UnrealBinaryPlugin.ps1') -UnrealEngineRoot $UnrealEngineRoot -OutputDirectory $ueOut -AllowDirty -BuildVersion '2026.11.999'
                $builtPlugin = Join-Path $ueOut 'UE5.7\LibraRuntimeForUnreal'
                $hostProjDir = Join-Path $workRoot 'ue-host-proj'
                $hostPluginsDir = Join-Path $hostProjDir 'Plugins\LibraRuntimeForUnreal'
                [IO.Directory]::CreateDirectory((Join-Path $hostProjDir 'Plugins')) | Out-Null
                Copy-Item -LiteralPath $builtPlugin -Destination $hostPluginsDir -Recurse
                $hostProj = Join-Path $hostProjDir 'HostProject.uproject'
                [IO.File]::WriteAllText($hostProj, '{"FileVersion":3,"EngineAssociation":"5.7","Plugins":[{"Name":"LibraRuntimeForUnreal","Enabled":true}]}', [Text.UTF8Encoding]::new($false))
                & $ueCmd $hostProj -ExecCmds='Automation RunTests Libra.Runtime.Spawnable.ComponentFixture+Libra.Runtime.Rendering.OpaqueBatchCompatibility;Quit' -Unattended -NoSound -NoSplash -NullRHI -stdout -FullStdOutLogOutput
                if ($LASTEXITCODE -ne 0) { throw "UnrealEditor-Cmd automation failed (exit $LASTEXITCODE)." }
                "UnrealEditor-Cmd automation tests passed on $hostProj"
            } finally {
                if (-not $KeepScratch -and (Test-Path -LiteralPath $ueOut)) {
                    Remove-Item -LiteralPath $ueOut -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    Write-Host "`n=== Issue #71 E2E Acceptance Summary ===" -ForegroundColor White
    $results | Format-Table -AutoSize
}
finally {
    if (-not $KeepScratch -and (Test-Path -LiteralPath $workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
