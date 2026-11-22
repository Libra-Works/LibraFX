---
title: "Production workflow: Unity, Unreal and Godot"
---

# Production workflow: Unity, Unreal and Godot

This is the **development process**, not the node Flow window:

**Install SDK → launch LIBERA FX → obtain/bind engine resources → author and
preview → export → import and play without LIBERA FX → automate conversion.**

Choose [Unreal](#unreal), [Unity](#unity) or [Godot](#godot).
For unattended builds, see [Batch conversion](batch-conversion.md).
The [installation reference](engine-integration.md) and
[release notes](release-notes.md) cover versions and platform limits.

## What moves between the tools?

| Item | Purpose |
| --- | --- |
| `.lfxproj` | Editable source and history. Keep it in source control; it is not the game's runtime payload. |
| Engine mesh, material and texture | Stay in the engine project. Resource browsing transfers references and metadata, not copies of an engine's assets into LIBERA FX. |
| Live preview | A temporary, authenticated editor connection. Useful while authoring; not a saved runtime asset. |
| `.lfxb` | Compiled effect package to import/load for offline gameplay. Includes the supported CPU/GPU payloads, not arbitrary engine resource files. |
| Engine asset/bindings | Unity imported assets and serialized references, Unreal `.uasset` and cookable overrides, or Godot resources plus explicit player bindings. |

### Important current differences

| Operation | Unreal | Unity | Godot |
| --- | --- | --- | --- |
| Browse an engine asset catalog in Content | Supported | No full catalog | No full catalog |
| Exact resource/property lookup through Resource Identity | Supported | Supported | No desktop mapping integration |
| Desktop live preview | Dedicated Unreal preview window | Opt-in isolated Unity preview window | Script connection to a dedicated player |
| Create a playable effect for the game | Import `.lfxb` | Import `.lfxb` and assign engine bindings | Load `.lfxb` bytes and register resources |

**Portable Resource Identity mapping is not yet a complete offline export
pipeline.** It supports verified Unreal/Unity live delivery, but normal
**Export VFX...** and converter `.lfxb` export reject registered identity
metadata. `.lfxrm` is a diagnostic mapping manifest, **not an importable effect**.
Do not delete mappings to silence an export error. Use a deliberately maintained
legacy-binding project for the currently supported offline package path.
This limitation also applies to the Unity mapping steps below.
See the [identity specification](https://github.com/Libra-Works/LibraFX/blob/main/docs/resource-identity.txt).

[Issue #71](https://github.com/Libra-Works/LibraFX/issues/71) tracks unifying
these workflows and completing mapped offline export.

<span id="unreal"></span>

## Unreal Engine

### 1. Install in the engine project

Close Unreal. Extract the matching `LibraForUnreal` archive and copy its
`LibraRuntimeForUnreal` folder to `<Project>\Plugins\LibraRuntimeForUnreal`.
Rebuild the project's **Editor target** and open the project. This is a source
plugin, not a precompiled plugin for every Unreal version. See the
[installation and verification reference](engine-integration.md).

### 2. Launch LIBERA FX and connect

Start `LiberaFx.exe`, create/open an effect with **File → New / Open**, then save
the `.lfxproj` in your team's authoring folder. In Unreal open
**Window → LIBERA FX Preview → Connect to Libra**.

On Windows, a single valid local editor is discovered; with multiple instances,
use **Select Libra** to choose explicitly. Manual fallback: in LIBERA FX open
**View → Preview connections**, click **Copy manual connection information**,
then click **Paste Connection** in Unreal's preview header.
Keep this temporary invitation private. Restarting LIBERA FX requires reconnecting.

### 3. Obtain the engine's resources

In LIBERA FX open **View → Content → Connected engine assets**. Select the
intended Unreal peer, search by name/type, and select a result to obtain its
thumbnail and material details. Use **Add Model** / **Add Sprite** when available,
or select the destination node, choose **Assignment target**, then **Assign**.
Wait for details before assigning; with multiple texture/material targets,
choose the exact slot or resource. Undo/Redo applies to the graph edit.

The engine resolves its `/Game` or `/Engine` references. A file with the same
name in another project is not automatically equivalent. Local PNG/JPEG
previews in Content are not imported engine textures.
Resource Identity registration is a separate, optional mapping operation with
the offline-export limitation described above.

### 4. Author and transfer the preview

Edit the graph, connect compatible pins and change the timeline/playback.
The connected Unreal preview receives compiled data and renders in its isolated
scene; it does not edit your gameplay level. Watch **Preview connections** and
**View → Log** for compilation, binding or shader failures. Check actual motion
and pixels in Unreal, not just a connection indicator.

For GPU authoring, follow [GPU particles](gpu-particles.md) or
[2D smoke and ink](fluid-2d.md). Unreal GPU forward seeking is not supported;
backward seeking resets simulation rather than reconstructing an arbitrary
requested time. A compilation failure is not a successful preview update.

### 5. Create the engine asset and verify gameplay

For an offline-exportable project, save it and use **File → Export VFX...** to
write `.lfxb`. Import that file in Unreal's **Content Browser**, then drag the
imported asset into the level. It creates an actor with the public
**Libra Effect** component. Enable **Auto Play**, enter PIE, and check playback.
Blueprint/C++ can instead call `Play()` and consume `OnGameIoEvent`.

The imported asset's **LIBERA FX Resource Overrides** can assign project-local
Static Mesh, Texture2D and Material objects to the delivered keys. Save the
`.uasset`; these are cookable references, not edits to the source graph.
Component-level bindings take priority over package overrides.

Set the asset's editor-only **Source Project Path** to its `.lfxproj` for
**Open LIBERA FX**. This is distinct from the `.lfxb` reimport filename.
On Windows, register LIBERA FX as the `.lfxproj` handler or set
`LIBERAFX_EDITOR_EXECUTABLE` before starting Unreal. If LIBERA FX is already
running, open the source in its existing window; launch arguments are not forwarded.

### 6. Iterate and prepare distribution

Re-export the same package, reimport in Unreal, and restart playback. Review
bindings if resource keys change; overrides are not inferred for a new key.
Verify Play, natural completion, Stop and replay with LIBERA FX disconnected.
Cook the imported effect and referenced engine resources; the user's machine
must not need the original `.lfxproj` or its authoring path.
Automate CPU/GPU and Fluid2D exports using the [batch converter](batch-conversion.md),
then run the project's normal Unreal import/cook pipeline.

<span id="unity"></span>

## Unity

### 1. Install in the engine project

Close Unity. Extract `LibraForUnity` and copy `works.libra.liberafx` to
`<Project>\Packages\works.libra.liberafx`. The copy-ready package already includes
the Windows x64 native bridge; do not install a second copy under `Assets`.
Reopen Unity and resolve package/native-load errors first. Other OS/CPU targets
need matching native binaries. See [installation](engine-integration.md).

### 2. Launch and pair the editors

Start LIBERA FX and save your `.lfxproj`. In Unity choose
**Tools → LiberaFx → Resources → Connect to LIBERA FX**.
In LIBERA FX use **View → Preview connections → Copy manual connection information**.
From that session's JSON, enter the Unity window's:

| Unity field | Value |
| --- | --- |
| WSS endpoint | `wss://<host>:<port>/preview?protocol=3`, using the invitation's host/port |
| Preview token | `accessToken` |
| TLS SHA-256 fingerprint | `certificateFingerprint` |

Enable **Render desktop preview in Unity before Connect** if you want rendering,
then click **Connect**. A resource-only connection does not render.
These are preview credentials, not MCP or debugger credentials; do not save
the invitation in assets, scripts, logs or source control.

### 3. Obtain exact Unity resource bindings

Unity does **not** expose the Unreal-style full Content catalog. For mapped
live authoring:

1. Choose **Tools → LiberaFx → Resources → Initialize or Resolve Project Identity**.
   If this is a clone, deliberately keep the shared identity or fork it.
2. Select a saved Mesh, Texture2D or Material in Unity and choose
   **Copy Selected Asset Binding** in the same Resources menu.
3. In LIBERA FX open **View → Content → Resource identity**, select the Unity
   editor, and **Read project identity**. Choose the node and resource location,
   paste the exact GUID/local-file-ID binding, and **Query this exact binding**.
4. On success, use **Register and assign selected location**. For materials,
   read the exact schema and explicitly bind properties; names are not guessed.

This gives mapped live delivery, **not currently a directly exportable `.lfxb`**.
For the offline legacy-binding path, use the graph/package's exact authored
lookup keys and explicit Unity object assignments in step 5 instead.

### 4. Author and transfer the preview

With the rendering option enabled at connection time, graph updates send a
compiled package and verified project bindings to the Unity window's isolated
preview. Timeline changes advance that preview; backward seeks restart and
replay, with a 0–120 second range. It is not your gameplay scene.
Inspect the window's diagnostics and the rendered result.

Use **Deliver Unity runtime bindings** in Resource identity when you explicitly
want to write/update `Assets\LiberaFx\LiberaFxBindings.asset`.
Preview bindings alone are transient; delivery alone does not compile an
offline effect or bypass the mapping-export limitation.
This WSS mapping preview does not support SOURCE skeletal input, manual Editor
Send or GPU resource mappings. It is separate from the Live Game connection.

### 5. Create the engine asset and verify gameplay

For an offline-exportable legacy-binding project, **File → Export VFX...** writes
`.lfxb`. Copy it under Unity's `Assets` and let the importer create a
`LiberaFxSpawnableAsset`. Do not rename a new package to `.bytes`.

Create **Assets → Create → LiberaFx → Unity Bindings**. In **Render Assets**,
map each exact authored `assetIdentity` to its Unity Mesh/Texture and required
default materials; use **Materials** and explicit shader property mappings where
needed. These keys are opaque lookup keys, not guessed Unity asset paths.
Attach `LiberaFxSpawnableComponent` to a GameObject and assign **Effect** and
**Engine Bindings**. Enable **Play On Enable** and enter Play Mode, or call
`Play()` yourself. Inspect `LastError` and `GameIoEventReceived`.
GPU packages also need the artifacts/bindings in the [GPU guide](gpu-particles.md).

### 6. Iterate and prepare distribution

Update the exported `.lfxb`, wait for reimport, and restart playback.
Keep unchanged `.meta` files and serialized references. Verify load, Play,
completion, Stop and replay without the desktop connection.
Build the imported asset and referenced Unity resources with the normal game
pipeline; include matching native plugins for each target.
The [batch converter](batch-conversion.md) creates executable effect packages, not
Unity scenes, binding assets or Player builds.

<span id="godot"></span>

## Godot

### 1. Install in the engine project

Close Godot. Extract `LibraForGodot` and copy `liberafx` to
`<Project>\addons\liberafx`. Keep the `.gdextension` and its `bin` folder together.
The supplied extension targets **Godot 4.7 / Windows x64**; other platforms need
a matching build. Reopen Godot and confirm that `LibraEffectPlayer` is available.
This is a GDExtension, not an EditorPlugin with a connection toolbar.

### 2. Launch LIBERA FX and prepare the scene

Start LIBERA FX, open/save the effect's `.lfxproj`, and make a Godot test scene
with a `LibraEffectPlayer`, Camera3D and appropriate lighting/environment.
For live preview use a **dedicated, idle player**; do not connect one already
used by gameplay. Both the player and connection Node must be in the scene tree.

### 3. Bind resources in Godot

There is no full Godot asset catalog or desktop Resource Identity mapping path.
Load Godot Mesh/Texture2D/Material objects and bind them while the player is stopped:

```gdscript
$LibraEffectPlayer.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new())
$LibraEffectPlayer.set_texture_binding("/Game/FX/T_Atlas.T_Atlas", preload("res://atlas.png"))
```

The strings are **example authored lookup keys**, not Godot paths or automatic
Unreal imports. Replace them with the exact identities in your effect.
Use `set_material_binding(identity, material)` for material overrides.
A missing binding rejects playback; no placeholder is substituted.

### 4. Connect and transfer the preview

In LIBERA FX open **View → Preview connections** and use
**Copy manual connection information** and **Copy public TLS certificate for Godot**.
In the running Godot scene add the provided connection script:

```gdscript
var desktop_preview: Node

func _ready() -> void:
    desktop_preview = preload("res://addons/liberafx/liberafx_live_preview.gd").new()
    add_child(desktop_preview)
    # Register this effect's resources before connecting.

func connect_desktop(invitation_json: String, public_pem: String) -> Error:
    return desktop_preview.connect_package_local(
        $LibraEffectPlayer, invitation_json, public_pem)
```

Pass the current invitation and certificate through your temporary local
connection UI/in-memory caller. Do not hardcode the invitation or persist it in
the scene. Check the returned error and connect `preview_error` /
`frame_rejected` for diagnostics. Keep `liberafx_package_preview.gd` beside the
connection script; the copy-ready folder does this already.

After connecting, author/play in LIBERA FX and inspect the Godot viewport.
Do not separately play, stop or advance the dedicated player while attached.
Backward seeks rebuild/replay up to 120 seconds; catch-up is bounded and not
frame-exact GPU reconstruction. See the
[Godot live-preview reference](https://github.com/Libra-Works/LibraFX/blob/main/Runtime/Integrations/Godot/LIVE_PREVIEW.txt).

### 5. Load the offline effect

Export an offline-compatible `.lfxb` and place it in the Godot project.
On a gameplay player in the scene tree, set the same explicit bindings before:

```gdscript
var result = $LibraEffectPlayer.play_package(
    FileAccess.get_file_as_bytes("res://effects/firework.lfxb"))
if result != 0:
    push_error("LIBERA FX package load failed: %s" % result)
```

`0` means the package was admitted, not proof of rendered pixels. Observe
`game_io_event` for authored outputs. Wait for the first process tick before
sending gameplay input signals. Disconnect live preview before reusing its player.

### 6. Iterate and prepare distribution

Re-export, replace the package and restart playback; resource bindings remain
the Godot project's responsibility. Add `*.lfxb` to the export preset's
**non-resource include filter** when loading packages through FileAccess.
Include the matching GDExtension binary and bound resources in the game export;
do not depend on the authoring project's local path.
Use the [batch converter](batch-conversion.md) for CPU/GPU and Fluid2D packages,
then the normal Godot export pipeline.

## Before handing an effect to the game team

Verify the correct engine project/bindings, visible playback, natural completion,
Stop/restart and playback **with LIBERA FX disconnected**. An accepted preview
transfer is not a shipped asset. For a launcher waiting for future game signals
or a continuously running root GPU effect, review **End automatically when idle**
rather than assuming timeline looping controls runtime lifetime.

Keep editable sources, group/node libraries and engine bindings in source
control. Never commit preview/debug invitations or tokens. Console SDKs,
every rendering backend and exported players are not all verified; use the
[release notes](release-notes.md) for the actual acceptance scope.
