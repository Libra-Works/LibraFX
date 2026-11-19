---
title: Engine Integration
---

[Documentation home](index.md) · [Getting started](getting-started.md)

# Engine Integration

Download the matching SDK from [2026.11.8 Preview](https://github.com/Libra-Works/LibraFX/releases/tag/v2026.11.8).
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

## Play a first effect

Start with a CPU-only Spawnable package (`.lfxp`) without draw or GPU nodes.
This lets you verify loading and named Game I/O before setting up mesh or material
bindings. Save your `.lfxproj`, then compile it using the bundled `LiberaFxConverter`
from the `Converter` directory. For example, on Windows:

```powershell
.\LiberaFxConverter.exe convert C:\Effects\input.lfxproj C:\Effects\effect.lfxp
```

The editor's **Export VFX...** command is for `.lfxb`, not `.lfxp`. Give the graph
an observable output, such as a named Output Signal, so that a successful load
can be distinguished from an effect that never runs.

### Unity

Put the `.lfxp` under `Assets/`. Assign the imported asset to a
`LiberaFxSpawnableComponent`; enable **Play On Enable** and enter Play Mode,
or call `Play()` explicitly. Inspect `LastError` on failure and use
`GameIoEventReceived` to observe authored outputs.

### Unreal Engine

Build the project's Editor target after copying the plugin. Import the `.lfxp`
in the Content Browser and drag the imported asset into the level. This creates
an actor with a **Libra Effect** component. Enable **Auto Play** and start
Play In Editor, or call `Play()` explicitly. Use `OnGameIoEvent` to observe
authored outputs.

### Godot

Place the `.lfxp` under `res://`, add a `LibraEffectPlayer`, and call
`play_package(FileAccess.get_file_as_bytes("res://effect.lfxp"))` after the
player enters the scene tree. A result of `0` means the package loaded; wait
for the first process tick before firing a game input signal. Observe authored
outputs via `game_io_event`. Include `.lfxp` files in the export preset's
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
  and child programs of `.lfxp`. Use a native node when the graph needs the
  result within the current evaluation.

See the [SDK distribution reference](https://github.com/Libra-Works/LibraFX/blob/v2026.11.8/docs/sdk-distribution.md#project-defined-nodes-in-spawnable-packages)
for registration steps, working code examples, and the supported verification
matrix. The Preview's Windows bridge binaries do not certify other target
platforms, render backends, or console SDKs.
