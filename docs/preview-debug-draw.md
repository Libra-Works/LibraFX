# #65: preview-transfer-only Debug Text / Debug Model (SOURCE handoff)

## Authoring contract

Create **Debug Text [PREVIEW ONLY]** or **Debug Model [PREVIEW ONLY]**, connect a
Matrix to **World Matrix**, and optionally connect SOURCE.Valid to **Visible**.
Set the authored text/model binding and size in the ordinary parameter editor.
Unconnected World Matrix means the same explicit identity default used by other
Matrix inputs. There is no screen-space/HUD mode. Text is authored UTF-8 content,
not an arbitrary runtime string or a formatting/script interpreter.

The node's always-visible EN/JA badge distinguishes enabled from disabled.
Parameter edits use existing UI-thread workspace commands/history. Node UUIDs,
pin IDs and parameter IDs are permanent. No existing project/package/bytecode
version or built-in UUID was replaced.

The Preview pane has a session-local, default-OFF transfer permission.
`VfxCompileOptions.IncludePreviewDebugDraw` is also default OFF; explicitly pass
`VfxCompileOptions.LiveTransfer` only at a live transfer boundary. Ordinary
Compile, package export, converter and resource manifest export strip observers.
Preview Signal keys do **not** implicitly enable debug output.

## Execution and isolation

- Debug draws are terminal observers: no Signal relay, output value or lifetime
  operation is removed/replaced. Ordinary producers remain even if their only
  consumer was a stripped observer. No reachability/DCE pass was introduced.
- Strip materializes group overrides before removing terminal observers.
- Debug strings/model uses are not written to bytecode. Debug-only resource
  manifest uses/definitions are removed by exact instance scope and node ID.
  Shared ordinary assets are preserved. Source projects/history are not edited.
- The existing `render_config` path carries explicit `previewOnly:true`,
  `debugVersion:1` descriptors. Matrix lowering supplies four real row bindings.
  No Matrix is packed into a four-component runtime value.
- Core `platform/LbDebugDrawQueue.h` owns bounded snapshots in caller-provided
  storage; no engine SDK/OS/graphics calls or allocations occur there. Rejected
  submissions retain their batch and must not partially display/retain data.
- UE `FLibraDebugDrawScope` consumes that queue on the engine owner thread and
  creates actual TextRender/StaticMesh components. Both the editor preview and
  existing package root/child renderer paths use it.
- Unity's existing prepared payload/render-scope path creates actual world
  TextMesh/mesh objects. Debug world objects are explicitly owned, unparented
  (owner movement cannot reinterpret a world Matrix), and remain in the owner's
  scene. The native scope's existing Stop/replace/destruction paths dispose them.
- Scopes replace rather than accumulate draws. Editor-native preview derives
  renderer identities from container/instance/authored-node identity; package
  renderers keep separate owners for each exact runtime instance.
- Maximum 128 draws per scope, 1024 UTF-8 text bytes, 512 UTF-8 model binding
  bytes, and size `(0,10000]`. UE keeps the existing 4096 aggregate renderer cap.
  Unity uses the existing bounded native template/instance capacities.
- Visible matrices must be finite, affine, nonsingular TRS. Unsupported shear
  is an explicit adapter error; never an identity/model fallback. An explicitly
  invisible zero pose is not displayed, permitting SOURCE.Valid -> Visible.
  Missing models/material bindings fail; no substitute basic-shape asset is used.
- Explicit timeline Stop removes debug descriptors; Pause retains observations.
  UE preview reset/disconnect/retransfer and engine scope destruction release
  debug objects. Failed matrix reads/render preparation surface errors.

## Integration performed for parent work

`VfxProgram.CompileForPackage` calls `SkeletalPoseSchema.Lower` after group
materialization/validation and before GameIo lowering. Direct scene generation
has the equivalent pre-Matrix hook; standalone converter links the schema.

The requested #63 compiler gates now permit dedicated instance GameIO only
directly inside Spawnable children, retain nested shared GameIO rejection, and
reject dedicated instance GameIO from plain non-package compilation.

Compiler ownership is released. No edits to GameIoSchema, GraphValidator,
SpawnableHost, Interop, native wrapper or skeletal host implementations were
made by #65.

## Regression SOURCE and verification limits

No build, test, application, engine GUI, external request, commit or push was
performed. `git diff --check` is static inspection, not compilation.

Pure contract entry point:

```
PreviewDebugDrawTests.Run(string? fixtureDirectory = null)
LiveGameDebugDisconnectTests.Run()
```

It does not construct/show windows or start an engine. With no directory it
does not write fixtures; persistence uses MemoryStream. It covers bytecode
equivalence, default-OFF/disabled stripping, four-row Matrix bindings,
Signal/lifetime preservation, exclusive-vs-shared resource manifests,
Undo/Redo/project/copy identity and explicit Stop state.
The first entry also calls the second. `LiveGameDebugDisconnectTests.Run()` is
strictly managed and needs no Avalonia initialization, native library, files or
network: it covers the actual Unity transport lease helper, stale owner/session,
takeover, detached mutation rejection and legacy descriptor compatibility.
The helper is source-linked only into the contract test project, not the editor.

Other added SOURCE:

- `Runtime/tests/DebugDrawQueueTests.cpp`: bounds, per-instance identity,
  copied strings, atomic rejection/retry and empty-frame cleanup.
- `Libra.Editor.DebugDraw.WorldAndCleanup`: real isolated UE text component,
  world translation, stale sequence/retransfer/clear/destructor ownership.
- `Libra.Editor.DebugDraw.LiveDisconnect`: compiler-produced text package through
  actual UE live session/component forwarding, stale target generation rejection,
  real text component destruction, retained ordinary lifetime and suppression of
  debug in future playback. Requires explicit
  `-LibraDebugFixture=<isolated text-preview.lfxp>`.
- `LiberaFx.Unity.Editor.LiberaFxDebugDrawVerification.Run`: compiler-produced
  text package -> actual world TextMesh, owner movement independence, Stop,
  restart and disabled-owner cleanup in an isolated preview scene.
  Also checks debug-only clear leaves elapsed time/render scope ownership intact
  and that local restarts cannot resurrect disconnected debug.
- Existing Unity `LiberaFxLiveGameVerification.RunBatch()` and UE
  `Libra.Runtime.LiveGame.ComponentSession` gain debug lease takeover/stale
  disconnect/replacement and preservation of the actual ordinary mesh playback.
  These are engine tests, **not** no-window pure contracts.

Suggested **later**, user-authorized commands (substitute isolated paths; do not
run against an open user's engine project):

```powershell
dotnet build LiberaFx.csproj --artifacts-path "<isolated-root>\dotnet"
dotnet build Tools/Converter/LiberaFx.Converter.csproj --artifacts-path "<isolated-root>\converter"
$env:LIBERAFX_DATA_ROOT = "<isolated-root>\data"
dotnet run --project Tests/CommentImageSmokeTests.csproj -- --preview-debug "<isolated-root>\debug-fixture"
cmake -S Runtime -B "<isolated-root>\native" -DLIBERAFX_BUILD_TESTS=ON
cmake --build "<isolated-root>\native" --config Debug
ctest --test-dir "<isolated-root>\native" -C Debug -R liberafx_debug_draw_queue_tests --output-on-failure
```

Build the installed UE plugin against the chosen engine/project (including its
Clang configuration), then run `Libra.Editor.DebugDraw.WorldAndCleanup` only in
an isolated project. For Unity, use an isolated project with matching native
plugin and set `LIBERAFX_DEBUG_DRAW_FIXTURE` to generated `text-preview.lfxp`,
then invoke the verification entry point. None has been run. Actual glyph
coverage, rendered pixels, engine versions and interactive authoring remain
unverified; do not close their acceptance based on source.

## Live Game debug-only disconnect (SOURCE)

The optional protocol-1 descriptor capability is
`"debugDisconnect":"debug-lease-v1"`. New clients explicitly `debug-attach`
with a fresh 32-lowercase-hex `debugOwner`; subsequent mutations carry this
ownership nonce. It is an ownership identity, not a credential. Session and
monotonic sequence validation remain unchanged.

A newly created Live Game session suppresses debug draws until an explicit
attachment, even if it receives a preview payload. Attachment applies only to
new transfers; it does not enable debug draws on instances already playing.

`debug-disconnect` is acknowledged only for the current owner. It clears only
Debug Text/Model resources for the exact current session/slot/generation, then
suppresses new debug scopes, including queued creates and future spawned
children. Session `Close()` performs the same cleanup even when normal lifetime
means it returns NotReady. No Stop, Destroy, NaturalEnd, native host mutation,
time/default reset, ordinary renderer removal or GPU stop is sent by cleanup.

Explicit takeover first clears the previous debug transfer. A late old-window
disconnect or replace cannot affect the new transfer, even though successful
Replace retains the target generation. Cleared components never resurrect from
cached definitions: a fresh transferred playback is required after attach.
Before the first lease, old ownerless protocol commands remain usable, but
debug is suppressed. After a lease has been claimed, mutations require its
current owner (or an explicit new attach).

Window disconnect, consent revocation, directory change and Dispose start a
finite cleanup on the captured client, independently of cancellation of an
in-flight replace. Ownership is registered before the initial attach can be
published, so closing during Connect is covered too.
Published requests are never overwritten/deleted; cleanup
waits for the old request before publishing its own. A connection generation
guards late UI completions. `LiveGameSpoolClient.DisconnectDebugAsync()` returns
the acknowledgement, or null for an old endpoint without the capability;
non-Ok/timeout/session-change results are failures. Both client and window model
expose `DebugDisconnectCompletion` so hosts can await actual completion instead
of interpreting Dispose as acknowledgement. EN/JA status distinguishes pending,
acknowledged, unsupported and failed cleanup.

An unresponsive/stopped engine, killed editor process or unavailable endpoint
cannot supply an acknowledgement. Do not claim remote pixels were cleared in
that case; no automatic force-stop of ordinary effects is attempted.

## Verification boundaries

`LiveGameGraphCompiler.Compile(source, options)` accepts the explicit option but
does not broaden the existing Restart-only numeric-root compatibility policy.
The Live Game window still does not opt into debug compilation automatically.
The explicit debug-only disconnect contract is now wired in SOURCE; its
compilation, live UI race behavior, engine object cleanup and rendered pixels
remain unexecuted/unverified. No acceptance awaiting the user's builds or
engine observations is closed by this source handoff.

## Files owned/touched by #65

- Assets: `Nodes/DebugText.json`, `Nodes/DebugModel.json`,
  `Translations/DebugDraw.en.json`, `Translations/DebugDraw.ja.json`.
- Models/Graph: additive UUID entries in `GraphNodeDefinition.cs`;
  four-row terminal routing in `GraphMatrixLowering.cs`.
- Services/Compilation: `PreviewDebugDraw.cs`, `VfxProgram.cs`,
  `SpawnableGroupPackage.cs`, `RenderProgramBindings.cs`.
- Services: `CompiledPreviewProtocol.cs`, `EditorNativePreviewSession.cs`,
  `EditorPlayback.cs`, `LiveGameGraphCompiler.cs`, `PreviewHost.cs`,
  `PreviewProtocol.cs`, `ResourceIdentityExport.cs`.
- ViewModels/Views: `MainViewModel.PreviewDebugDraw.cs`, `GraphNodeViewModel.cs`,
  `PreviewPaneView.axaml`, `GraphNodeView.axaml`.
- Core: `Runtime/include/liberafx/platform/LbDebugDrawQueue.h`,
  `Runtime/tests/DebugDrawQueueTests.cpp`, `Runtime/CMakeLists.txt`.
- Unity Runtime: `LiberaFxDebugDrawInstance.cs`, `LiberaFxRenderPayload.cs`,
  `LiberaFxRenderScope.cs`; Editor/Tests: `LiberaFxDebugDrawVerification.cs`.
- UE runtime: `Public/LibraDebugDrawScope.h`, `Private/LibraDebugDrawScope.cpp`,
  `Private/LibraSpawnableRenderState.cpp`; editor:
  `Private/LibraEditorDebugDraw.h`, `LibraEditorPreviewSession.cpp/.h`,
  `Private/Tests/LibraDebugDrawTests.cpp`.
- Tests: `PreviewDebugDrawTests.cs`, additive selector in `Program.cs`.
- `Tools/Converter/LiberaFx.Converter.csproj`, this handoff.

Additional disconnect-only source ownership:

- `Services/LiveGameSpoolClient.cs`, `ViewModels/LiveGameWindowViewModel.cs`,
  `Assets/Translations/LiveGame.en.json` and `.ja.json`.
- Unity Runtime: `LiveGameDebugLease.cs`,
  `LiberaFxSpawnableComponent.PreviewDebug.cs`, the minimal partial declaration
  and two scope creation arguments in `LiberaFxSpawnableComponent.cs`,
  `LiberaFxRenderScope.cs`, `LiberaFxLiveGameSession.cs`,
  `LiberaFxLiveGameEndpoint.cs`; both named Unity verification sources above.
- UE Runtime: `LibraPreviewDebugCleanup.cpp`, additive public declarations/fields
  in `LibraEffectComponent.h` and `LibraSpawnableComponent.h`,
  one pre-Play hook in `LibraEffectPackagePlayback.cpp`,
  `LibraSpawnableRenderState.h/.cpp`, `LibraLiveGameSession.h/.cpp`,
  `LibraLiveGameEndpointComponent.h/.cpp`, and the two named UE test sources.
- Tests: `LiveGameDebugDisconnectTests.cs`, its source link in
  `CommentImageSmokeTests.csproj`, a call in `PreviewDebugDrawTests.Run`.
  No new edit to compiler ownership files or `Tests/Program.cs` in this phase.
