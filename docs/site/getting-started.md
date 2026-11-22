---
title: Install and start
---

# Install and start

Go from a download to your first editable effect. Explore graphs before connecting
an engine; set up an engine preview when you want to see particle rendering.

<span id="install-the-editor"></span>

## 1. Install the editor

Download your OS package from [GitHub Releases](https://github.com/Libra-Works/LibraFX/releases).
Choose the editor and SDKs from the **same release**. Read the [release notes](release-notes.md)
when updating an existing project.

| Platform | Open the editor |
| --- | --- |
| Windows x64 | Install the MSI and open **LIBERA FX** from Start, or extract the whole portable ZIP and open its top-level `LiberaFx.exe`. Keep `Editor` and `Converter` together. |
| macOS, Apple Silicon | Extract the ZIP and open the `.app`. The Preview app is unsigned and not notarized. |
| Linux x64 | Extract the tarball and run `Editor/LiberaFx` in a desktop environment with Avalonia's required system dependencies. |

SDK installation is separate. The included Unity/Godot native bridges target Windows
x64; Unreal's source plugin needs an Editor build. See [SDK installation](engine-integration.md).

## 2. Open a sample and save a copy

1. Choose **File → Open** and open `Assets/Samples/CpuFountain.lfxproj` from the package.
2. Choose **File → Save As** and save a copy in your working folder.
3. Select a node to show its values in an Inspector. Use [the workspace tour](workspace-tour.md)
   to locate panels, connect pins and return from child graphs.

Keep the `.lfxproj`: it is the editable project. An exported `.lfxb` is the package
loaded by the game, not the file to use for further graph editing.

## 3. Choose a preview route

Follow [Connect and deliver](production-workflow.md) for your engine:

| Engine | Start with |
| --- | --- |
| Unreal Engine | The dedicated **LIBERA FX Preview** window and **Connect to Libra**. |
| Unity | The SDK's explicitly enabled editor preview and project-local asset bindings. |
| Godot | The SDK player and scripted preview connection. |

A connection badge means the editors can communicate. Confirm the image and movement
in the engine viewport too. Textures, meshes and materials need their engine bindings.

<span id="read-the-sample-graph"></span>
<span id="watch-the-sample-in-unreal"></span>

## 4. Make your first change

Continue with [Make a fountain](first-fountain.md). It shows where to change emission,
velocity, gravity, size and lifetime, and what each change should look like.
Then try [Afterimage Gate](afterimage-gate.md) to follow delayed Signals and child lifetimes.
If nothing appears, start with [Troubleshooting](troubleshooting.md).
