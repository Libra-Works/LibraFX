---
title: Glossary
---

# Glossary

Start here when a guide uses an unfamiliar word. Names in **bold** may also
appear in the editor or Node List.

[Graphs](#graphs) · [Time and lifetime](#time) · [Groups](#groups) ·
[Engine and files](#engine) · [Assets and drawing](#assets) ·
[GPU particles](#gpu) · [CPU particles](#cpu) · [Editor tools](#editor)

<span id="graphs"></span>

## Graphs

| Term | What it means |
| --- | --- |
| VFX | Visual effects, such as a spark, dust puff, or an afterimage. |
| Graph / node | A graph is the effect's diagram. A node is one box that does a job, such as waiting, adding values, or asking the engine to draw something. |
| Pin / port / connection | A pin is a connection point on a node, also called a port. A line connects an output to an input. An exposed port can be connected from outside its group. |
| Parameter / default | A parameter is a value used by a node. A default is the value supplied initially; for example, a Delay can start with `0.55` seconds. |
| Type | The kind of value a pin accepts: **Float** is a number that can have a fraction, **Int32** is a whole number, **Bool** is true or false, and **Vector3** is three numbers, often X, Y, Z. **Color** includes color channels and alpha (opacity). |
| Matrix / transform | A transform describes placement, orientation, or scale. A Matrix can represent such a transform so that several operations can be applied together. |
| Quaternion | Four numbers describing a rotation, not four Euler angles. Normalizing keeps it suitable for rotation; multiplication combines rotations, and Slerp blends between them. See [quaternion conventions](quaternion-random.md). |
| Seed / sample index | The seed selects a repeatable random sequence; the index selects a sample from it. Explicit values let a particle keep its own result rather than changing whenever evaluation order changes. See [random nodes](quaternion-random.md). |
| Signal / Sequence | A **Signal** says “do this now”; it does not carry a numeric value. A Signal output connects to one destination. Use **Sequence** to run several branches in order, one connection per output. |

For a worked example, see [Graph authoring](authoring.md).

<span id="time"></span>

## Time and lifetime

| Term | What it means |
| --- | --- |
| Frame / FPS / playhead | A frame is a numbered step on the timeline; FPS is frames per second. At 60 FPS, 120 frames represent two seconds. The playhead marks the current preview position. |
| Timeline key / F-Curve key | A timed Signal key triggers an event. An **F-Curve** key defines a value at a point on a curve, such as opacity `0.5` at one second. Interpolation determines the values between keys. |
| Effect Age / local time / delta time | Effect Age is time since the whole effect started. Local time inside a spawned instance starts when that instance starts. Delta time is the amount of time elapsed in one update. |
| Life Time / Destroy | **Life Time** sets a deadline; it is not the preview loop length or an F-Curve. Children and scheduled work may still need to finish after the deadline. The **Destroy** Signal marks the end of an instance's lifetime when that event is exposed by the graph. |

<span id="groups"></span>

## Groups

| Term | What it means |
| --- | --- |
| Root / child graph | The root graph is the top-level diagram. A child graph is a diagram inside a group. |
| Graph Group | A reusable collection of nodes with selected pins exposed to the outside. It does not create independent running objects. |
| Spawnable Group / instance | **Spawn** creates a running copy called an instance, with its own local time and lifetime. Several instances can run at once; a burst can start while another burst is still playing. |
| Group asset / revision / lock | A Group asset stores a reusable definition. A revision is a saved version of that definition. Locking protects the shared structure without locking the exposed values you set on individual instances. |

<span id="engine"></span>

## Engines and files

| Term | What it means |
| --- | --- |
| Engine / host / runtime | Unity, Unreal Engine, or Godot can act as the host that loads the effect and supplies game data. The LIBERA FX runtime evaluates the compiled effect; the engine draws it. |
| SDK / integration | The SDK is the set of files needed to use LIBERA FX in an engine project. The integration connects that engine to the runtime. |
| Compile / Converter / bytecode | Compiling translates the editable graph into instructions for the runtime. The Converter can do this without opening the editor. Those instructions are called bytecode. Successful compilation does not mean the effect has played or drawn a frame. |
| `.lfxproj` / `.lfxb` / legacy `.lfxp` | `.lfxproj` is the editable project. New `.lfxb` exports are complete effect packages for simple graphs and graphs with children or timed Signals. `.lfxp` is the old package extension and remains readable, as do old raw-bytecode `.lfxb` files. Use the matching updated SDK for new exports, and keep the `.lfxproj` for future edits. |
| Game I/O / script action | Game I/O is the named exchange of values and events between a game and an effect. A supported `action.*` output can call a game script; it is not a script node inside the editor. |
| Native node / ABI / UUID | A native node runs code compiled for the runtime, such as C++. The ABI is the binary-level contract with that code. A UUID is a stable identifier used to match the node definition, independent of its display name. |

See [Engine integration](engine-integration.md) for a first playback example.

<span id="assets"></span>

## Assets and drawing

| Term | What it means |
| --- | --- |
| Asset / binding | An asset is a file or object such as a texture or model. A binding tells the integration which asset to use. Two files with the same name are not necessarily the same asset. |
| Texture / mesh / material | A texture supplies image data, a mesh describes a shape, and a material controls how a surface looks when drawn. |
| Sprite / atlas / cell | A sprite is a flat image used for drawing. An atlas packs several images into one texture; a cell is the rectangular part of that texture used for a frame or part. |
| Rendering / preview | Rendering produces visible pixels. Preview lets you inspect the effect before shipping. A connected preview client or a successful transfer does not prove that the engine drew the effect. |
| Draw call / batching / instancing | A draw call asks the GPU to draw something. Batching groups compatible work; instancing draws copies of the same mesh using different transforms or other supported per-copy values. Different materials or transparent ordering can prevent grouping. |
| SceneProxy / Graphics / RenderingServer | Engine-side drawing interfaces used by Unreal, Unity, and Godot respectively. They can accept rendering data without creating a full scene component for every drawn item. Their use alone does not guarantee fewer draw calls or better performance. |
| Ribbon / point history | A ribbon is a strip joining a sequence of points. A point history records recent positions, so moving one input can leave a trail. [Flexible Plane Draw](flexible-plane-draw.md) turns these points into visible triangles. |

<span id="gpu"></span>

## GPU particles

| Term | What it means |
| --- | --- |
| CPU / GPU | The CPU handles general game and graph execution. The GPU can do many similar calculations in parallel, such as updating the positions of thousands of particles. |
| Flow / pass | A Flow shows processing stages and their relationships. A pass is one processing step, such as updating particle positions or drawing them. A stage relationship is not a CPU Signal connection. |
| Kernel / attribute / uniform | A kernel is a calculation run over particles. An attribute is a value stored per particle, such as Velocity. A uniform supplies a common value to the kernel, such as the emitter position or `deltaTime`. |
| Resource / buffer / render target | A resource is data used by the GPU. A buffer stores values such as particle positions. A render target receives drawing output; a RenderTexture keeps that output in a texture instead of displaying it directly. Not every resource is a render target. |
| Ping-pong buffers / synchronization | Ping-pong uses two storage areas: read the old state from one and write the new state to the other, then swap their roles. Synchronization ensures later GPU work can safely use earlier results. Automatic resource management in the proposed Resources / Targets editor is future work, not an available feature. |
| Spawn / Update / billboard | GPU **Spawn** sets initial particle values; **Update** changes them over time. **RenderBillboard** draws the resulting particles as flat shapes facing the camera. GPU Spawn initializes particles; a CPU Spawnable Group creates child graph instances. |
| Typed DSL / IR / shader | The typed DSL is the small language used to author a GPU kernel. IR is the intermediate representation shared by the graph and text workflows. A shader is code intended to run on the GPU; exported source alone is not a playable effect. |

See [GPU Particle Authoring](gpu-particles.md) for screenshots of both kernel editors.

<span id="cpu"></span>

## CPU particles

| Term | What it means |
| --- | --- |
| Emitter / rate / burst | An emitter creates particles. A rate spreads creation over time; a burst creates a requested group at an event. A shape determines where they begin. |
| Spawn / Update graph | Spawn sets a new particle's starting values. Update changes each live particle over time. These are CPU child graphs, not GPU shaders; CPU Property and Signal exchange uses the runtime's scoped interfaces. |
| Pool / capacity / batch | A pool reserves storage for a maximum number of live particles. A batch groups particle drawing work; creating many individual Draw nodes is not the same as a particle system. |
| Event queue | A bounded list carries notifications from simulation to its owner thread. Queueing keeps particle calculation separate from engine or game callbacks. |

See [CPU Particle Authoring](cpu-particles.md) for the current supported nodes,
interaction rules and limits.

<span id="editor"></span>

## Editor tools

| Term | What it means |
| --- | --- |
| Dock / inspector / outliner | A dock is a panel that can be placed alongside other panels. The inspector shows editable properties; the outliner lists nodes already in the graph. |
| Diagnostic / log / profiler | A diagnostic identifies a problem such as an incompatible pin. The log records messages over time. A profiler reports work and timings from a supported running preview. |
| MCP | Model Context Protocol is how compatible clients call editor tools. Connecting to those tools is separate from permission to send project data to a model. Keep access credentials private. |

See the [Editor reference](editor-reference.md) for where to find each tool.
