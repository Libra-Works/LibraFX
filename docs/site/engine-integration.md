---
title: Engine Integration
---

[Documentation home](index.md) · [Getting started](getting-started.md)

# Engine Integration

See [engine and file terms](glossary.md#engine) for SDK, runtime, editable projects, and compiled effects.

Download the matching SDK from the latest [Preview release](https://github.com/Libra-Works/LibraFX/releases).
The unified `.lfxb` package workflow below requires the updated editor,
converter, and SDK from the same source revision. Do not mix these with
2026.11.8 or earlier SDKs. Older `.lfxp` packages remain supported by updated SDKs.

Existing raw-program assets do not automatically become package assets.
When replacing a legacy raw `.lfxb` with a new package export, import it as a new
asset and explicitly reassign its component references. Renaming a file or
reimporting it is not an asset-type migration. Unity also needs the matching
native plugin installed for import validation.
Close the engine before installing. Extract the archive and copy only the
top-level install folder shown below. Each folder includes `VERSION.json`.

| SDK | Install folder in your project | Included runtime |
| --- | --- | --- |
| LibraForUnity | `Packages/works.libra.liberafx` | Windows x64 native bridge |
| LibraForUnreal | `Plugins/LibraRuntimeForUnreal` | Source plugin, including the portable Core |
| LibraForGodot | `addons/liberafx` | Windows x64 Godot 4.7 GDExtension |

Back up any existing installation before updating. For Unity, keep existing
`.meta` files for unchanged assets and remove duplicate package or native
plugin registrations. For Godot, preserve any generated `.gdextension.uid`.
For Unreal, replace the installed folder rather than merging old and new files;
do not delete through a development junction.

### Verification boundaries

The updated Unreal adapter passed UE 5.7.4 Clang compilation (`-NoLink`),
MSVC Editor linking, and the isolated legacy raw import-routing automation.
The Clang check does not verify linking against an MSVC-built engine or a
console SDK. New CPU particle and Flexible Plane rendered results remain
unverified; engine compilation and import tests are not visual acceptance.

## Play a first effect

Start with a CPU-only effect package (`.lfxb`) without draw or GPU nodes.
This lets you verify loading and named Game I/O before setting up mesh or material
bindings. Save your `.lfxproj`, then compile it using the bundled `LiberaFxConverter`
from the `Converter` directory. For example, on Windows:

```powershell
.\LiberaFxConverter.exe convert C:\Effects\input.lfxproj C:\Effects\effect.lfxb
```

The editor's **Export VFX...** command creates the same `.lfxb` package. Give the graph
an observable output, such as a named Output Signal, so that a successful load
can be distinguished from an effect that never runs.

### Unity

Put the `.lfxb` under `Assets/`. Assign the imported asset to a
`LiberaFxSpawnableComponent`; enable **Play On Enable** and enter Play Mode,
or call `Play()` explicitly. Inspect `LastError` on failure and use
`GameIoEventReceived` to observe authored outputs.

### Unreal Engine

Build the project's Editor target after copying the plugin. Import the `.lfxb`
in the Content Browser and drag the imported asset into the level. This creates
an actor with a **Libra Effect** component. Enable **Auto Play** and start
Play In Editor, or call `Play()` explicitly. Use `OnGameIoEvent` to observe
authored outputs.

### Godot

Place the `.lfxb` under `res://`, add a `LibraEffectPlayer`, and call
`play_package(FileAccess.get_file_as_bytes("res://effect.lfxb"))` after the
player enters the scene tree. A result of `0` means the package loaded; wait
for the first process tick before firing a game input signal. Observe authored
outputs via `game_io_event`. Include `.lfxb` files in the export preset's
non-resource filter if you ship them with the game.

## Extend an effect

- **C# or GDScript:** On the root graph, use an `action.*` Output Signal. Bind it
  with `BindScriptAction` in Unity or `bind_script_action` in Godot. The handler
  runs on the engine thread and can write a reply to a named Input Property
  for the next evaluation. Existing Model Draw and Sprite nodes can handle
  rendering; this path does not require C++ or a custom renderer.
- **Project C++ nodes:** Register the descriptor matching the node JSON's UUID,
  ABI, and ports. Unreal registers descriptors at module startup. Unity and
  Godot build project descriptors into their native bridge. This supports root
  and child programs of the `.lfxb` package. Use a native node when the graph needs the
  result within the current evaluation.

See the [SDK distribution reference](https://github.com/Libra-Works/LibraFX/blob/main/docs/sdk-distribution.md#project-defined-nodes-in-spawnable-packages)
for registration steps, working code examples, and the supported verification
matrix. The Preview's Windows bridge binaries do not certify other target
platforms, render backends, or console SDKs.
