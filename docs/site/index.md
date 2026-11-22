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
| [Workspace tour](workspace-tour.md) | Learn where each window is and how to select nodes, edit values, connect pins and preview an effect. |
| [Make your first fountain](first-fountain.md) | Follow a practical CPU particle tutorial, starting with the bundled sample or an empty graph. |
| [Release notes](release-notes.md) | Choose a version to review its changes, matching SDK requirements and verified environments. |
| [Production workflow: Unity, Unreal and Godot](production-workflow.md) | Install, launch, obtain engine resources, transfer previews, create engine assets and prepare distribution. |
| [Batch conversion](batch-conversion.md) | Automate supported CPU exports, resolve shared libraries, and fail safely before game builds. |
| [Getting started](getting-started.md) | Install the editor, open **Afterimage Gate**, and follow the signals that drive it. |
| [Glossary](glossary.md) | Look up words such as node, Signal, Spawnable, and runtime, with short examples. |
| [Editor window and panel reference](editor-reference.md) | Find tools, settings, and dialogs with screenshots and step-by-step examples. |
| [GPU Particle Authoring](gpu-particles.md) | Edit the GPU flow, visual kernels, typed DSL, and billboard settings. |
| [2D smoke, water and fire](fluid-2d.md) | Use the resource-first grid solvers, cell forces, dedicated renderers and generated outputs. |
| [Procedural resources](procedural-resources.md) | Create model geometry, GPU textures and materials from ordered procedural flows. |
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

## 2D fluids first

Development sources now support **2D smoke, side-view free-surface water,
top-down shallow water and fuel/temperature combustion**. The resource-first
Flow, cell forces, dedicated renderers and same-scope generated texture/field
consumers are described in the [fluid guide](fluid-2d.md), with actual editor
and engine captures. Use matching editor and SDK sources; published archives
have not been rebuilt with these changes.

The 2D-first work tracked in [issue #69](https://github.com/Libra-Works/LibraFX/issues/69) is complete; 3D fluids remain future work.
The procedural-resource guide covers generated models, textures, materials and
their bounded editor/runtime contracts. The coordinated Unity, Unreal and Godot renderer migration, CPU particle Flow
and GPU emitter shapes are included. Nested Spawnables and their CPU particles
have per-instance ownership and lifetime retention.
The portable resource-plan contract and declarations implement a foundation for
[issue #68](https://github.com/Libra-Works/LibraFX/issues/68). Arbitrary runtime
resource graphs and writable main-framebuffer access still require adapter work.

[Source repository](https://github.com/Libra-Works/LibraFX) ·
[MIT license](https://github.com/Libra-Works/LibraFX/blob/main/LICENSE)
