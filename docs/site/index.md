---
title: LIBERA FX Documentation
---

# LIBERA FX Documentation

LIBERA FX is a node-based editor for authoring effects that play in Unity,
Unreal Engine, and Godot. Build a graph in the editor, compile your project
with the standalone converter, and play it through a game-engine integration.

**Current Preview downloads:** [GitHub Releases](https://github.com/Libra-Works/LibraFX/releases).

| Start here | What you will learn |
| --- | --- |
| [Getting started](getting-started.md) | Install the editor, open **Afterimage Gate**, and follow the signals that drive it. |
| [Glossary](glossary.md) | Look up words such as node, Signal, Spawnable, and runtime, with short examples. |
| [Editor window and panel reference](editor-reference.md) | Find tools, settings, and dialogs with screenshots and step-by-step examples. |
| [GPU Particle Authoring](gpu-particles.md) | Edit the GPU flow, visual kernels, typed DSL, and billboard settings. |
| [CPU Particle Authoring](cpu-particles.md) | Author Spawn/Update particle graphs, emission, and CPU Property/Signal interactions. |
| [Flexible Plane Draw](flexible-plane-draw.md) | Build a ribbon from authored points or a bounded position history. |
| [Graph authoring guide](authoring.md) | Connect pins, animate with curves, and manage Spawnable lifetime and Group reuse. |
| [Built-in node reference](node-reference.md) | Browse nodes by category and subcategory, with their pins and default settings. |
| [Scalar math recipes](scalar-math-nodes.md) | Use the ten new math nodes, including wrapping, easing, thresholds, and inverse interpolation. Requires the updated runtime/SDK. |
| [Quaternions and random values](quaternion-random.md) | Compose rotations and sample repeatable random values using an explicit seed and sample index. |
| [Engine integration](engine-integration.md) | Install the matching SDK, play a compiled effect, and choose between script actions and native nodes. |
| [SDK distribution reference](https://github.com/Libra-Works/LibraFX/blob/main/docs/sdk-distribution.md) | Build from source, stage packages, and check engine and platform limits. |

[![Afterimage Gate in the LIBERA FX editor, with its graph, Timeline, and Sequence inspector visible](../images/afterimage-gate-editor.png)](../images/afterimage-gate-editor.png)

*The Afterimage Gate graph in the editor. The screenshot does not show engine rendering.*

## Future direction: fluids

Future work targets both gases such as smoke/fire and liquids such as water or
viscous fluids. Solver methods, CPU/GPU responsibilities and rendering remain
undecided. Development will start with **2D**, verify that implementation, and
only then extend it to **3D**. Fluid simulation is **not implemented or included in this Preview**;
particle and ribbon support does not imply a fluid solver.

The agreed order after the current Preview is the coordinated Unity, Unreal and
Godot rendering migration, CPU particle Flow authoring and GPU emitter shapes,
then 2D fluids followed by 3D fluids. A proposed Resources / Targets section for
GPU Flow is tracked in [issue #68](https://github.com/Libra-Works/LibraFX/issues/68);
it is a future design proposal, not an available feature.

[Source repository](https://github.com/Libra-Works/LibraFX) ·
[MIT license](https://github.com/Libra-Works/LibraFX/blob/main/LICENSE)
