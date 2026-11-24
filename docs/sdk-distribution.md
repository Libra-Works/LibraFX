# Engine SDK source distributions

The SDKs still live in this repository. `Libra-Works/ForUnity` and
`Libra-Works/ForUnreal` have **not** been created or migrated by this tooling.
Repository creation, history migration, removal of the originals, releases and
pushes require a separate approved migration.

Unity, Unreal and the Windows x86-64 Godot GDExtension can all be
staged locally with the same tool.

## GitHub Actions builds

The **Build LIBERA FX and SDKs** workflow runs on pull requests and pushes to
`main`. Use **Run workflow** to also retain downloadable artifacts from that run.
This does not create GitHub Releases, publish to package registries, or push to
other repositories.

Every build uses `year.month.build` (for example `2026.11.42`). The year
and month are from the workflow run's creation time in Japan Standard Time,
and the build number is the GitHub Actions run number. Reruns keep the same
version. The version appears in artifact names, the editor's About window
and macOS bundle metadata, and the archive's `build-info.json` (editor) or
`sdk-manifest.json` (SDK). The JSON also includes the source commit.
The Unity/Unreal package descriptors, SDK compatibility version, native ABI,
and file format versions are independent and are not rewritten by the workflow.

| Product | Artifact and CI boundary |
| --- | --- |
| LibraFX | Editor and converter for Windows x64 (MSI or portable ZIP), Apple Silicon macOS (unsigned `.app` in a ZIP), and Linux x64 (tar.gz). CI packages them but does not exercise their desktop UI or sign/notarize the macOS app. |
| LibraSDK | Portable C++ Core sources, public headers, CMake files and compatibility manifest. CI builds Core with Clang and runs the Runtime tests on Linux. |
| LibraForUnreal | Copy-ready source plugin plus the source SDK. CI verifies packaging and update protections without running UnrealBuildTool or Unreal Editor. |
| LibraForUnity | Copy-ready embedded package with the Windows x64 native bridge, plus the source SDK. CI builds the native DLL; it does not run Unity. |
| LibraForGodot | Copy-ready Windows x64 Godot 4.7 GDExtension, plus the source SDK. CI builds against pinned `godot-cpp` sources; it does not run Godot. |

Engine artifacts keep the source export and its hash manifest in `Source`.
`VERSION.json` is at the artifact root and records the distribution build and source revision.
Native files under `Native` are separate build outputs, not entries in the
source manifest.

### Quick install from an Actions artifact (Windows x64)

Close the engine before copying. Extract the artifact and copy only the folder
shown below, not the whole archive or the `Source` and `Native` folders.
Create the destination parent directory if it is missing.

| Artifact | Copy this folder | Destination in your project |
| --- | --- | --- |
| LibraForUnity | `works.libra.liberafx` | `Packages/works.libra.liberafx` |
| LibraForUnreal | `LibraRuntimeForUnreal` | `Plugins/LibraRuntimeForUnreal` |
| LibraForGodot | `liberafx` | `addons/liberafx` |

Each install folder contains `VERSION.json`. Unreal is source-only and still
needs the project's Editor target built. Unity and Godot include Windows x64
native binaries; other platforms need their own builds.

For Unity, remove any previous LIBERA FX package registration and move any
separately installed native bridge out of the project before importing this
package to avoid duplicate plugins. Back up existing installations first. For
an in-place update, preserve Unity-generated `.meta` files for unchanged assets;
deleting them can break references in scenes and prefabs. In Godot, preserve
the `.gdextension.uid` file if the editor has generated one. Keep projects'
own effects and scenes outside the distributed folders.

On Windows, install the MSI (administrator rights required) and open **LIBERA FX**
from the Start menu, or extract the entire portable ZIP and open `LiberaFx.exe`
at its root. Keep the `Editor` and `Converter` directories alongside the launcher.
The Actions artifact ZIP wraps both the MSI and portable ZIP; extract it first
when downloading from a workflow run.

On Linux, extract the inner `LibraFX-<version>-linux-x64.tar.gz` and launch `Editor/LiberaFx`
from a graphical session with Avalonia's system dependencies installed.
The tarball preserves executable permissions.

### Distribution tracks

LIBERA FX engine integrations follow two distribution tracks:

- **Binary package (end users / artists)**: Copy-ready plugin/package containing
  prebuilt native binaries (`works.libra.liberafx` for Unity, `liberafx` for
  Godot, or a prebuilt `LibraRuntimeForUnreal` produced by `Build-UnrealBinaryPlugin.ps1`).
  No C++ compiler is needed for supported prebuilt targets.
- **Source package (`Source/` + `sdk-manifest.json`, developers / studios)**:
  Full engine-independent C++ Core and adapter sources for adding project-native
  nodes, targeting additional platforms, or rebuilding against custom engine versions.

### Prebuilt Unreal binary plugin (compiler-free)

GitHub Hosted Runners do not include an Unreal Engine installation, so CI
publishes the copy-ready Unreal source plugin (`LibraRuntimeForUnreal`). To
produce a compiler-free binary plugin (`Binaries/` + `Installed: true`) on a
machine or self-hosted runner with Unreal Engine installed:

```powershell
.\Tools\Build-UnrealBinaryPlugin.ps1 `
  -UnrealEngineRoot "C:\Program Files\Epic Games\UE_5.7" `
  -OutputDirectory C:\SdkStaging\LibraUnrealBinary `
  -TargetPlatforms Win64
```

This writes a versioned directory such as `C:\SdkStaging\LibraUnrealBinary\UE5.7\LibraRuntimeForUnreal`
(automatically detected from `Build.version`, or specified via `-EngineVersion 5.8` / `5.9` / `6.0`).
Use `-DryRun` to stage and validate the plugin layout without invoking `RunUAT`.
Prebuilt Unreal binaries are specific to each engine minor version (e.g. `5.7`, `5.8`, `5.9`, `6.0`)
and target platform; copy the inner `LibraRuntimeForUnreal` folder from the matching `UE<version>`
directory into your project's `Plugins/` folder.

For release publishing, `Tools/Package-UnrealBinaryReleases.ps1` auto-discovers installed
Unreal Engine 5.7+ versions (or accepts `-UnrealEngineRoots`) and emits separate per-version
release archives (`LibraForUnreal-<BuildVersion>-UE<Major>.<Minor>-win64.zip`) alongside
the CI-built `LibraForUnreal-<BuildVersion>.zip` source plugin:

```powershell
.\Tools\Package-UnrealBinaryReleases.ps1 `
  -BuildVersion 2026.11.99 `
  -OutputDirectory C:\SdkStaging\ReleaseAssets
```

## Reproducible local staging

From PowerShell 7, export into a **new absolute directory outside the repository**:

```powershell
.\Tools\Export-EngineSdk.ps1 -Engine Core -OutputDirectory C:\SdkStaging\LibraSDK
.\Tools\Export-EngineSdk.ps1 -Engine Unity -OutputDirectory C:\SdkStaging\ForUnity
.\Tools\Export-EngineSdk.ps1 -Engine Unreal -OutputDirectory C:\SdkStaging\ForUnreal
.\Tools\Export-EngineSdk.ps1 -Engine Godot -OutputDirectory C:\SdkStaging\ForGodot
```

The parent directory must exist. Existing destinations, links and dirty source
trees are rejected. `-AllowDirty` is only for local development: its manifest
explicitly records that the source does not match the recorded HEAD exactly.
On Windows use short paths for the export, engine project and build output.
MSBuild and Unreal generate deeply nested intermediate paths; a fresh build
can fail on a long path before any LIBERA FX code runs.
Each file receives a SHA-256 entry in `sdk-manifest.json`. Generated binaries,
logs, installation markers and engine build caches are excluded. The export
never uploads, invokes an engine, builds, or edits an installed project.

Each export preserves `Runtime\Core`, `Runtime\include`, `Runtime\CMakeLists.txt`
and its engine adapter's relative paths. `SDK\compatibility.json` records the
shared ABI, bytecode/package ceiling and actual verification boundaries.
Compiler/editor sources are not runtime dependencies.

## First effect in a new project

New exports have two user-facing files: editable `.lfxproj` and compiled `.lfxb`.
The `.lfxb` always contains the existing LFXP package envelope, including for
graphs without children. Engine importers inspect its signature; old raw `.lfxb`
programs and `.lfxp` packages remain legacy inputs. This requires updated SDKs
from the same source revision, not the published 2026.11.8 binaries.
The [2026.11.22 Preview](https://github.com/Libra-Works/LibraFX/releases/tag/v2026.11.22)
includes the unified exports and the CPU particle, Flexible Plane and Quaternion/
Random additions. Later renderer and authoring changes in source are not
retroactive updates to those archives.
The current source revision includes those renderer/authoring changes, four 2D
fluid models (smoke/ink, free-surface water, shallow water and fuel/temperature
combustion), cell forces, generated-resource consumers and nested Spawnable
ownership with per-instance CPU systems.
See the bilingual [release notes](site/release-notes.md),
[production workflow](site/production-workflow.md) and
[batch conversion guide](site/batch-conversion.md). GPU CLI conversion remains
tracked in #72; the current converter explicitly rejects GPU graphs.
The current source revision also adds Procedural Model, Texture and Material
authoring, alongside recent graph, history and inspector workflow improvements.
These changes are included only in archives built from this source revision;
older previews are not updated in place. See the
[procedural resources guide](procedural-resources.md).
Particle-only GPU payloads remain version 1; ordinary fluid payloads use
version 2; generated resource/draw references and explicit automatic-plane
selection use version 3 inside the same effect envelope. Old SDKs must reject
unsupported payloads rather than silently omit them. Install the matching
generated solver shaders, native header and adapter resources together; the
fluid shader uniform block is 512 bytes.
See the bilingual [2D smoke guide](site/fluid-2d.md) for the actual verification
matrix and exclusions. Free-surface water is grid-based rather than a detached-
droplet particle liquid solver; 3D fluids remain future work.
Historical test fixture filenames elsewhere in this document still use `.lfxp`.
The `.lfxrm` diagnostic resource-mapping manifest is not a second playable binary.
The updated converter also fixes overlapping custom-input defaults and branch
metadata. Re-export older custom-node effects from their `.lfxproj` sources;
the original values cannot be reconstructed from already overwritten bytes.
Nested Spawnables or template CPU programs use container 5/package 13.
Root-only CPU particles use container 4/package 12; effects without CPU or
nested ownership retain container 3/package 11. The source compatibility
manifest's package ceiling must match the SDK revision being installed; it does
not update older downloaded archives. CPU particles, Flexible Plane and Quaternion/
Random additions have compiler/native regression coverage, but still require
engine acceptance by integrators. Build results do not establish rendered
pixels; the bilingual feature guides describe these verification boundaries.

Legacy raw-program assets are not automatically migrated into package asset
types. Import a replacement package as a new asset and reassign component
references explicitly. Keep existing Unity `.meta` files for unchanged assets;
renaming/reimporting a raw file is not a type migration. Unity import validation
requires the matching native plugin.

Start with a **numeric-only `.lfxb` package** with no render assets, GPU programs, or
editor connection. This separates package loading and game I/O from project
resource bindings. For a visible second check in Unity or Unreal, use the
same compiler-produced ModelDraw Cube package described in both adapters'
`USAGE.txt`; bind its sample mesh/material independently in each engine.
Generate effects in the authoring repository, not in the exported SDK:

```powershell
dotnet run --project Tools\Converter -- convert C:\Effects\input.lfxproj C:\Effects\effect.lfxb
```

| Step | Unity | Unreal | Godot |
| --- | --- | --- | --- |
| Install | Add the local package and the matching `liberafx_native` plugin. | Copy-install with the script below into a blank C++ project, then build its Editor target. | Install the built GDExtension descriptor and matching library. |
| Load | Copy `.lfxb` into Assets and assign the imported asset to `LiberaFxSpawnableComponent`. | Import `.lfxb`, then drag the imported asset into the level. It creates a `LibraEffectActor` with one `Libra Effect` component. | Copy `.lfxb` under `res://`, add `LibraEffectPlayer`, and pass the file bytes to `play_package` after the node enters the tree. |
| Start | Enter Play mode with Play On Enable set, or call `Play()`. Check `LastError` if startup is rejected. | Start Play In Editor with Auto Play set, or call `Play()` and check its result. | Check `play_package` returns `0` before issuing game I/O. |
| Drive and observe | Use `SetGameProperty` / `FireGameSignal` and subscribe to `GameIoEventReceived`. | Use `SetGameProperty` / `FireGameSignal` and bind `OnGameIoEvent`. | Use `set_game_property` / `fire_game_signal` and connect `game_io_event`. |
| Retire | Call `Stop()` or disable the component. | Call `Stop()` or destroy the actor. | Call `stop()` or remove the player from the scene tree. |

Use the exact published port name and lane count from the graph; an ordinary
internal Signal node is not a named game input. To distinguish an idle effect
from a failed load, verify an authored output event or value, then stop and
start the same asset again. Bind resources only when adding draw/GPU nodes;
those bindings refer to engine assets and belong in the host project.

Unity and Unreal players use the imported, embedded runtime data, not the
original source file's path. Godot instead reads the compiled file from
`res://`; include it in the export preset's non-resource filter. None of the
players needs the original `.lfxproj` or the Converter.

## Unity entry point

For a source-only install, in Unity Package Manager use **Add package from disk** and select
`Runtime\Integrations\Unity\package.json` in the export. This is a local package,
not an online package registry. The existing runtime/editor asmdefs separate
editor tooling from player code and enable the unsafe native bridge.
The source-only root asmdef excludes standalone .NET tooling/test sources from
Unity compilation by default; do not enable `LIBERAFX_STANDALONE_DOTNET_TOOLS`
in a Unity project. Run those tools through their own .NET projects instead.

Build `liberafx_native` from the exported Runtime CMake project with tests off.
For Windows x64, run from the SDK export root with the Visual Studio C++ tools:

```powershell
cmake -S Runtime -B C:\SdkBuild\Unity -G "Visual Studio 17 2022" -A x64 -DLIBERAFX_BUILD_TESTS=OFF
cmake --build C:\SdkBuild\Unity --config Release --target liberafx_native
```

Copy `C:\SdkBuild\Unity\Release\liberafx_native.dll` to the host project's
`Assets\Plugins\x86_64`. Check the platform/CPU in Unity's Plugin Inspector,
including Editor and the intended player target. The local package is
source-only: adding it to Package Manager does not install the native DLL.
Do not install a Windows DLL for other target platforms or assume desktop dynamic
loading is permitted on consoles. See the included `USAGE.txt` and
`GPU-USAGE.txt` for package import, bindings, initialization, Play, Stop, disposal
and minimal Cube sample generation using the full authoring repository.

Unity 6000.0 is a **declared package minimum**, not a completed Unity 6 acceptance
claim. Execution evidence covers the 7000-series editor with D3D11 in URP
and in the Built-in RP fallback.
Unity 6, other render pipelines/backends and platform SDKs still need their own
build/playback/resource-retirement acceptance.
The ModelDraw Cube uses a Built-in Render Pipeline shader. For that sample on
Unity 7 alpha, launch the Editor with `-enable-builtin-rp-fallback`; for an SRP
effect, use a material/shader that supplies the authored properties in that SRP.

## Unreal entry point

From the `LibraForUnreal` artifact, copy the top-level `LibraRuntimeForUnreal`
folder into `<Project>/Plugins/`. It includes the native Core sources, license,
and `VERSION.json`; do not copy the raw plugin folder from inside `Source`.
To update, close Unreal, back up any local changes outside `Plugins`, and replace
the entire old `LibraRuntimeForUnreal` folder instead of merging files. Do not
delete through a development link: unlink it first without deleting its target.

For a local source export or a guarded update, use the exported adapter's
`Install-LibraRuntimeForUnreal.ps1` with an explicit `.uproject`.
It copies the plugin and Core sources into the project;
`-Update` protects locally modified files. Prefer copy installation for a release;
`-Link` is an explicit local development convenience, not a redistributable SDK.
The installer does not launch Unreal, build, or edit the project's configuration.
For a first Windows install, start from a C++ Blank project and build its
`<ProjectName>Editor` Win64 Development target after installing the plugin.
Launch the project only after the build succeeds; the plugin is source-only.

Use **ULibraEffectComponent** as the public whole-effect entry point; internal
CPU/GPU/Spawnable helpers do not require author-added companion components.
Read its included `USAGE.txt` for asset import, explicit resource bindings,
Play/Stop, instance I/O and lifetime ownership. UE 5.7+ is the current declared
minimum. Keep the portable Core/Unreal integration Clang-compilable, and use MSVC
for final Windows Unreal integration verification. This does not certify console
SDKs, cooking or gameplay merely because a desktop source build succeeds.

## Project-defined nodes in Spawnable packages

Start with `Runtime/examples/CustomNodes/CustomScale.json`. Add the JSON to the
editor's project node directory, or pass that directory to the converter with
`--nodes`. Keep the JSON type UUID, ABI version, and port contract identical to
the native descriptor. The same definition and C++ calculation can be used by
all three engine adapters.

| Engine | Where to register native implementations |
| --- | --- |
| Unreal | Call `FLibraNativeNodes::Register` from a separate project module's `StartupModule`, before loading effects. See `Runtime/Integrations/Unreal/Examples/LibraProjectNodes`. |
| Unity | Build the C ABI plugin from the source SDK with `-DLIBERAFX_PROJECT_NODE_SOURCES=<absolute-source-path>` and replace the bundled native library with the result for each target platform. |
| Godot | Build the GDExtension from the source SDK with the same CMake option and replace the matching extension library. Keep the `.gdextension` and DLL layout unchanged. |

For Unity and Godot, `CustomScaleNode.cpp` is a complete example. Use its
`liberafx::host::projectNodes()` entry point to return one immutable descriptor
table for the project. Its callbacks and contexts must remain valid while the
adapter is loaded. Prebuilt binaries contain no project callbacks. To pass
multiple sources, use a quoted, semicolon-separated CMake list and define the
entry point in only one file.

The project registry is used by unified `.lfxb` package root and child programs.
Unreal and Unity also use it for legacy raw program loads; Godot's raw `play()` path remains
limited to v1/v2. Neither registration path requires custom rendering: return
values to built-in Model Draw or Sprite nodes and let the adapter render them.
Keep engine objects and engine API calls out of native parameter callbacks.

### C# and GDScript workflow

On the root graph of a CPU effect package (`.lfxb`), connect a signal (for
example, a named **Input Signal** called `sample`) to an **Output Signal**
called `action.sampleSpeed`. Add a float **Input Property** called `speed` and
wire it into the graph that drives your standard Model Draw or Sprite nodes.
Keep the effect alive through the next evaluation so it can consume the reply.

In Unity, bind before calling `Play()` on a `LiberaFxSpawnableComponent`. Disable
**Play On Enable** if your script controls when playback begins:

```csharp
player.BindScriptAction("action.sampleSpeed", _ => new[] { 2.5d }, "speed");
player.Play();
player.FireGameSignal("sample");
```

In Godot, bind on a `LibraEffectPlayer` that's in the scene tree before starting
playback. `package_bytes` holds the exported `.lfxb` package:

```gdscript
func resolve_speed(_event: Dictionary) -> PackedFloat64Array:
    return PackedFloat64Array([2.5])

func start_effect(player, package_bytes: PackedByteArray) -> void:
    player.bind_script_action("action.sampleSpeed", resolve_speed, "speed")
    player.play_package(package_bytes)
```

Fire `player.fire_game_signal("sample")` from gameplay after the package's first
process tick (or first manual `advance`).

Bindings are case-sensitive and belong to the component/player, not the package.
Duplicate binds return `false`; remove the old one with `UnbindScriptAction` /
`unbind_script_action`. All binding and handler code runs on the engine thread.
Omit the reply property for a side-effect-only action (return `null` in C#).
Otherwise return one number per lane of the named input property. The graph
sees it on the **next evaluation**. If the callback stops or replaces playback,
its reply is discarded. Ordinary Game I/O listeners still receive action signals
while that playback remains active. Advancing playback or firing another game
signal directly from an action handler returns `Busy`; defer it to gameplay
code if needed.

There is no dedicated visual script node, child action, synchronous worker
callback, or editor script execution. Use native C++ nodes when the result is
needed within the same evaluation.

## Fresh-project verification on Windows

The local source exports were installed in clean Unity 7000.0.0a16 and
UE 5.7.4 C++ Blank projects, using D3D11 on an RX 6700 XT. Unity used the
local package plus a Release DLL built from the export; Unreal used the copy
installer plus an MSVC Editor build. Both rendered the compiler-produced
ModelDraw Cube and exercised named Game I/O. Unity also verified `.lfxp`
import, per-instance Game I/O in Play Mode, and that Stop removed renderers.
Unreal verified placement through its viewport actor factory and automatic
playback of the saved map in PIE with the original `.lfxp` unavailable.

The main setup differences are the separate Unity native DLL and explicit
binding asset versus Unreal's native target build and asset placement
shortcut. Do the CPU-only check first to distinguish an SDK/startup error
from a missing mesh or shader binding. Neither adapter needs a live editor
connection for ordinary playback. This fresh-project run does not certify
other backends, console SDKs or shipped players.

## Local CPU debugger acceptance in Unity and Unreal

The LIBERA FX Preview pane's **Runtime debugger** uses a private, local
Windows session. Its invitation is not a Preview Connections invitation and
must not be saved in assets, source, or logs. Use the same graph revision and
compiled payload on both sides; a stale import or a preview-specific package
is not interchangeable with an offline `.lfxb` effect package.

1. Generate the ModelDraw **MaterialPulse** sample using either adapter's
   `--model-material-animation` instructions. Open `MaterialPulse.lfxproj`
   in LIBERA FX and play the corresponding `MaterialPulse.lfxp` with the
   sample's engine bindings. Verify the cube is visible before attaching.
   In LIBERA FX select the **Amplitude** (Multiply) node, toggle its breakpoint, enable
   **Match a compiled Spawnable package**, and start the local debugger.
2. In Unity Play Mode, select the live `LiberaFxSpawnableComponent`, click
   **Select current playback**, leave both child fields blank, paste the
   invitation, and click **Attach package debugger**. In Unreal PIE, select
   the live actor's **Libra Effect** component, paste the invitation into
   **Local Debugger Invitation**, and invoke **Attach Local Debugger**. This
   attaches the package root. If using a game-thread Blueprint helper, call
   `AttachExecutionDebugger` on that same live component.
3. Wait for the pause in LIBERA FX. Confirm the target is the package root
   and **Locate** selects Amplitude. Inspect its connected cosine operand
   and the disconnected `-0.5` multiplier separately from the saved node
   parameters. Click **Step** and verify a fresh pause at the next executable
   instruction in the selected scope; if the fixture reaches the end of the
   evaluation, use the next tick. A source node may compile to more than
   one instruction. Check the cube retains its last committed presentation
   and an unrelated game object continues updating while the effect is paused.
4. Remove the breakpoint before **Resume** to avoid hitting it on the next
   update. Confirm normal playback, then disconnect or end Play Mode/PIE
   and check that the debugger releases the session. Repeat with a fresh
   invitation to test detaching or destroying the effect while paused.

For child acceptance, get the container ID and instance ID from the selected
effect's Spawn event and the generation from its current playback; never reuse
IDs across a restart. Supply the child fields in Unity or use
`GetPackageDebuggerGeneration` and `AttachPackageDebugger` on Unreal's
public component. To generate a CPU-only child stepping fixture, set
`LIBERAFX_DATA_ROOT` to an isolated absolute path and run:

```powershell
dotnet run --project Tests\CommentImageSmokeTests.csproj -- --debugger-fixtures C:\SdkScratch\Debugger
```

Play `DebugChild.lfxp`, open
`DebugChild.lfxproj`, enter **Debug Child**, and set a breakpoint on **Child
Scale**. The first input is the child's Time, the second is the default `2`.
Step to **Child Offset**, which consumes the scaled value and the default `3`.
Check the reported container, instance, and playback generation before
resuming. For signal-specific troubleshooting, drive an authored signal in
that child and inspect its signal predecessor as well as its operands.

An accepted attach request in the engine is not proof of a breakpoint or a
successful step. Worker tests with an in-process controller and editor tests
with a simulated runtime cover their respective layers; this walkthrough is
for validating the actual editor-to-engine connection.

On Windows, the standalone LIBERA FX desktop editor was connected to
Unity 7000.0.0a16 Play Mode and UE 5.7.4 PIE with this fixture. Both stopped
before Amplitude, showed its live Cosine input and default `-0.5`, stepped
to **White - Blue - White**, and resumed. Game frames advanced and the
engine retained the model renderer during the pause. The desktop run
also verified exact child scope, live operands, stepping and resume using
`DebugChild`, while another effect's renderer stayed present. Separate
engine worker tests cover destruction while paused. GPU instructions are
not part of CPU stepping.

## Godot entry point

Build `Runtime\Integrations\Godot` from the staged source with a separate
`godot-cpp` checkout supplying the 4.7 API. Copy `liberafx.gdextension` and
the matching DLL into the Godot project and add a `LibraEffectPlayer` node.
Convert `.lfxproj` to `.lfxb` using the Converter from the full authoring
repository before copying the bytecode into the project. See the included
`Runtime\Integrations\Godot\USAGE.txt` for exact build, installation, and smoke
test commands.

The Godot adapter accepts standalone CPU bytecode and supported Spawnable
packages. It exposes named Game I/O, explicit Godot resource bindings,
CPU rendering, supported root/child GPU billboards, and local preview and
execution debugging. See its `USAGE.txt` and `LIVE_PREVIEW.txt` for subset and
rendering-device requirements. Headless playback and real Vulkan GPU execution
have been exercised on Godot 4.7.2 Windows x86-64; exported players have not
been verified.

## Component-free rendering in the Preview update

The renderer migration after 2026.11.22 is not contained in that Preview's
downloaded SDKs. The matching Preview update moves ordinary Draw, CPU Sprite and
FlexiblePlane to direct Unity Graphics submissions, Unreal native
primitive/proxy descriptors, and Godot RenderingServer Mesh/MultiMesh RIDs.
Whole-effect playback entry points and engine-native assets remain; portable
Core and the bytecode/package formats are unchanged by this renderer migration.
Compatible opaque work can be instanced, while incompatible state and transparent
draws remain separate. Native rendering resources are still allocated.

Unity layered Sprite composition also removes MeshRenderer fallback, as an
intentional choice to prioritize component-free drawing. The independent
pre-batch ordering stage retains authored order, camera depth and stable IDs,
but direct Graphics submission order does not reproduce global
`Renderer.sortingOrder` or guarantee interleaving with external Renderers.
A future sorting/submission backend can use the retained metadata; it is not
implemented in this revision.

See the [engine integration guide](site/engine-integration.md#rendering)
and each adapter's `USAGE.txt` for source verification, statistics and limitations.
Do not treat staging-byte counters as completed GPU transfers or claim a
speedup from object-count reductions alone.

## Version and ownership boundary

Native ABI 1, the compiled bytecode version and the package version are distinct.
Keep the editor/converter and SDK revision together, reject unsupported formats,
and regenerate fixtures when the compiler changes. Engine adapters own engine
objects and capture/apply snapshots on their required threads; Core owns portable
execution over caller/host-owned memory. No serialized pointers identify assets
or live instances. Host generation plus container and instance identity protects
targeted operations. Never retain freed state or use a dynamic editor DLL in Core.
Prefer the engine components above over a custom update loop. Drive their
playback and named I/O on the game/main thread; let the adapter marshal any
worker, render-thread, or debugger jobs. Check result codes at load and at
game I/O boundaries, and do not assume Stop sends natural-end graph events.

An eventual split must preserve the source lock/manifest, carry required notices
and samples, exercise independent installation in each target engine, and agree
on compatibility/release coordination before deleting duplicate sources here.
