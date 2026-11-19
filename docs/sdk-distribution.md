# Engine SDK source distributions

The SDKs still live in this repository. `Libra-Works/ForUnity` and
`Libra-Works/ForUnreal` have **not** been created or migrated by this tooling.
Repository creation, history migration, removal of the originals, releases and
pushes require a separate approved migration.

Unity, Unreal and the Windows x86-64 Godot GDExtension can all be
staged locally with the same tool.

## Reproducible local staging

From PowerShell 7, export into a **new absolute directory outside the repository**:

```powershell
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

Start with a **CPU-only `.lfxp`** with no render assets, GPU programs, or
editor connection. This separates package loading and game I/O from project
resource bindings. For a visible second check in Unity or Unreal, use the
same compiler-produced ModelDraw Cube package described in both adapters'
`USAGE.txt`; bind its sample mesh/material independently in each engine.
Generate effects in the authoring repository, not in the exported SDK:

```powershell
dotnet run --project Tools\Converter -- convert C:\Effects\input.lfxproj C:\Effects\effect.lfxp
```

| Step | Unity | Unreal | Godot |
| --- | --- | --- | --- |
| Install | Add the local package and the matching `liberafx_native` plugin. | Copy-install with the script below into a blank C++ project, then build its Editor target. | Install the built GDExtension descriptor and matching library. |
| Load | Copy `.lfxp` into Assets and assign the imported asset to `LiberaFxSpawnableComponent`. | Import `.lfxp`, then drag the imported asset into the level. It creates a `LibraEffectActor` with one `Libra Effect` component. | Copy `.lfxp` under `res://`, add `LibraEffectPlayer`, and pass the file bytes to `play_package` after the node enters the tree. |
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

In Unity Package Manager, use **Add package from disk** and select
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

Use the exported adapter's `Install-LibraRuntimeForUnreal.ps1` with an explicit
`.uproject`. It copies the selected plugin and Core sources into the project;
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
is not interchangeable with the offline `.lfxp`.

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
