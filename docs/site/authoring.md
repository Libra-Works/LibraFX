---
title: Graph Authoring Guide
---

[Home](index.md) · [Editor reference](editor-reference.md) · [Built-in nodes](node-reference.md)

# Graph Authoring Guide

New to node graphs? Start with [nodes, pins, and Signals](glossary.md#graphs).

<details class="page-toc" markdown="1">
<summary>On this page</summary>

* TOC
{:toc}

</details>

## Connect values and signals

**Parameter** pins carry typed data, such as a Float, Vector3, Color, or Matrix.
**Signal** pins carry execution events, not numeric values. Connect compatible
pins; when the types differ, choose an explicit conversion where the catalog
provides one. To fan out a Signal, insert **Sequence** and connect one destination
to each output. A Signal input can receive multiple independent arrivals.

Try this on a copy of [Afterimage Gate](afterimage-gate.md): find the Delay
between Sequence and the cyan afterimages, change its duration, and replay the
same time range. The second cue moves while the first remains at ignition.

Nodes that define variable pins (such as **Flexible Plane Point List**) show
**+** / **-** controls on both the node and the inspector. Increasing the count
adds numbered pins up to the node's limit; decreasing it also disconnects any
edges on the removed pins as a single undoable edit.

## Use time, curves, and lifetime

- **Time** follows the current scope; inside a Spawnable instance it is local to
  that instance. **Effect Age** reads the whole effect's time.
- **F-Curve** maps an input to a numeric value. Use it for animation, not to
  keep an effect alive. Select the curve in the inspector and edit keys in the F-Curve panel.
- **Life Time** sets the lifetime deadline. Reaching it does not mean every
  child or pending continuation is already gone. Use the appropriate natural
  end or force operation for the scope you intend to stop.
- **Timeline** Signal keys are timed arrivals. Keep the distinction between
  the graph's authored keys and the editor's playback range, speed, and loop.

## Choose a group

| Need | Use |
| --- | --- |
| Organize reusable graph logic and expose selected pins | **Graph Group** and its Group Input / Group Output nodes |
| Spawn independent child instances with local clocks and lifetime | **Spawnable Group** and its Spawn input/output boundary |
| Share a reusable revision | A Group asset in the active project's Group directory; inspect upgrades before applying them |
| Use a bundled motion or orientation building block | Open the Node List and browse the standard groups; inspect their exposed ports before connecting them |

Do not use a normal Group as an instance generator. A locked shared asset protects
its definition, not the exposed values you set on a particular instance.

## Move from graph to engine

Save source projects as `.lfxproj`. The editor's **Export VFX...** produces a
`.lfxp` effect package, also used by the converter for simple and Spawnable
graphs alike. Bind actual textures, materials, and meshes in the engine project.
Use an engine SDK from the same release as the editor. The compiled `.lfxp` package is the single supported format for production playback and Live Preview.
Named root or instance Game I/O is distinct from internal graph connections.
See [Engine integration](engine-integration.md) for installation, output events,
and extending your effect with script actions or C++ nodes.

If playback fails, isolate the problem: confirm that the graph compiles, the
engine loads the package, authored outputs are observable, and render assets
are bound. Only then treat a visible engine frame as confirmation of rendering.

## Next steps

- [Connect to an engine and play the effect](production-workflow.md).
- [Check a missing preview or unexpected result](troubleshooting.md).
- [Look up a node](node-reference.md).
