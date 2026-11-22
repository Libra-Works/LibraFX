---
title: Getting Started
---

[Documentation home](index.md) · [Engine integration](engine-integration.md)

# Getting Started

If terms like node, pin, or Signal are new to you, see the [graph glossary](glossary.md#graphs).

Open the bundled **Afterimage Gate** project and follow the two cues that create
a violet ring and a delayed burst of cyan streaks. You can inspect the graph
without a game engine; to see the effect render, use the Unreal preview.

## Install the editor

Download a package from the latest [Preview release](https://github.com/Libra-Works/LibraFX/releases).
Update the editor and SDKs together; the [release notes](release-notes.md)
lists the new features, package formats and known limits.
For the complete install-to-game process, follow the
[Unity / Unreal / Godot production workflow](production-workflow.md) and
[batch conversion guide](batch-conversion.md).

| Platform | How to start |
| --- | --- |
| Windows x64 | Run the MSI and open **LIBERA FX** from the Start menu, or extract the entire portable ZIP and open `LiberaFx.exe` at its root. Keep the adjacent `Editor` and `Converter` directories. |
| macOS (Apple Silicon) | Extract the ZIP and open the editor `.app`. The app is unsigned and not notarized. |
| Linux x64 | Extract the tarball and run `Editor/LiberaFx` from a graphical session with the required Avalonia system dependencies. |

## Read the sample graph

1. In LIBERA FX, open `Assets/Samples/AfterimageGate.lfxproj` from the installed editor's assets.
   To experiment, save a copy before making changes.
2. Find **Ignite**, then follow **Sequence**. One branch starts the violet ring.
   The other passes through a **Delay** of 0.55 seconds before spawning the cyan streaks.
3. Open a Spawnable Group and look for **Time** and **Life Time**. Local Time drives
   the animation; Life Time sets when an instance reaches its lifetime deadline.
4. To find a node, asset, or command, use Anything Search (`Ctrl+P` by default).
   The editor UI language and built-in node-name language can be selected independently.

## Watch the sample in Unreal

Install the matching [Unreal adapter](engine-integration.md#unreal-engine) in a
test project. In Unreal, open **Window → LIBERA FX Preview** and choose
**Connect to Libra**. In LIBERA FX, set the Timeline to 60 FPS and **End** to
144, then play or scrub. Around frames 54–63, look for the ring and cyan streaks
together. The live preview uses Unreal's stock sphere mesh and its own unlit
material. Standalone playback needs explicit asset bindings.
New offline exports use `.lfxb` packages and require the updated editor and SDK;
legacy releases used `.lfxp` for packages.

For the preview's native-library prerequisite and camera framing, see the
[bundled sample notes](https://github.com/Libra-Works/LibraFX/blob/main/Assets/Samples/AfterimageGate.txt).
