---
title: Build your first fountain
---

# Build your first fountain

Make small glow particles rise, curve downward under gravity and disappear.
First explore the bundled sample, then build a simple version from an empty graph. Neither route requires code or JSON.

## Before starting

- Use an updated Preview containing `CpuFountain.lfxproj` and its matching engine integration.
- Finish the [engine connection walkthrough](production-workflow.md) so you can see rendered particles.
- Keep [Your first tour of the editor](workspace-tour.md) nearby if the panels are unfamiliar.

## Route A: explore the sample

### 1. Open your own copy

Choose **File → Open**, open `Assets/Samples/CpuFountain.lfxproj`, then **Save As** `MyFountain.lfxproj`.
The root graph contains `Fountain` and a guide comment. The node's footer identifies it as CPU Particle.

### 2. Change the amount

Select `Fountain`. In its Inspector, change **Default particles / second** from `300` to `60`.
Set the timeline to **Start = 0**, **End = 600**, **FPS = 60** and play.

**Check:** fewer particles are born and individual paths become easier to follow.
Capacity is a limit, not an emission rate; increasing capacity alone does not create more particles.

### 3. Change the upward speed

1. Double-click `Fountain`.
2. Choose **Spawn → Open stage graph**.
3. Select the sample node named `Upward velocity`.
4. Change the Y component of `b` from `5` to `3`; keep X and Z at `0`.

**Check:** the fountain reaches a lower height. Y is up in the effect's authoring space.
You do not rewrite this direction for Unity versus Unreal.
The separate `Horizontal spread` node controls sideways variation; change one setting at a time.

### 4. Change gravity

1. Use the CPU Particle breadcrumb to return to its Flow.
2. Choose **Update → Open stage graph**.
3. Select `Velocity + gravity`.
4. Change **Acceleration** Y from `-9.81` to `-4`.

**Check:** particles fall more gently. Zero disables gravity; negative Y pulls down and positive Y accelerates up.

### 5. Change size and lifetime

Return to Spawn. On `Glow diameter`, change **Size** from `(0.14, 0.14)` to `(0.08, 0.08)`.
On `Particle lifetime`, change **Life** from `3` to `1.5`.

**Check:** the particles are smaller and disappear sooner. The sample's lifetime-based fade also becomes shorter.
Spawn settings apply to particles born after the edit. Stop and return to the beginning for a clean comparison.

### 6. Find the glow texture

Return to the Flow and choose **Draw → Edit draw settings**.
Check **Texture source → Radial glow**. This generates a white image whose opacity falls linearly from the centre to its edge.
No imported image file is needed.

### 7. Inspect timing and movement

Set playback speed to `0.25`, inspect the arc and fade, then restore `1`.
Under **Preview motion**, select Circle or Sphere to see the effect while its owner moves.
Radius is in metres; Stationary removes the movement. Save your work.

You have now found the controls for emission, initial velocity, gravity, size and lifetime.

## Route B: build from an empty graph

### 1. Add CPU Particle

Choose **File → New**. Search the Node List for **CPU Particle** and drag it into the graph.
Set its Inspector values:

| Setting | Value |
| --- | --- |
| Capacity | `2048` |
| Default particles / second | `120` |
| Initial burst | `0` |

Leave the outer **Emitted total** input disconnected. The container uses its default rate when that input is unconnected.

### 2. Set the initial velocity

Open CPU Particle, then Spawn. Select the existing **Set Particle Velocity** node and set **Velocity** to `(0, 5, 0)`.
Keep its orange Signal connection from CPU Stage Event.

### 3. Set lifetime and size

Add these two nodes inside Spawn:

- **Set Particle Life**: Life `2`.
- **Set Particle Size**: Size `(0.1, 0.1)`.

Connect their orange pins in this order:

`CPU Stage Event → Set Particle Velocity → Set Particle Life → Set Particle Size`

Drag each preceding **Then** output to the next node's **Set** input.
Placing the nodes without these connections does not run them.

### 4. Add gravity

Return to CPU Particle's Flow and open Update.
On the existing **Move Particle** node, set **Acceleration** to `(0, -9.81, 0)`.
Keep the orange connection from CPU Stage Event to Move Particle.

### 5. Choose the appearance and play

Open Draw settings and choose **Texture source → Radial glow**.
Return to the parent graph, save, then play in the connected engine preview.

**Check:** 120 small particles per second travel upward, curve downward and disappear after two seconds.
They all start in the same vertical direction because this simple route does not add random sideways motion.
Compare Route A's sample when adding spread and fading.

## If the result is unexpected

| Symptom | Check |
| --- | --- |
| The graph opens but nothing is drawn | Engine connection, preview window and playback state. |
| Spawn/Update/Draw settings are missing | Double-click CPU Particle from its parent graph. |
| A newly added operation has no effect | Its orange Signal connection. |
| Particles do not rise | Spawn velocity has positive Y. |
| Particles never fall | Update acceleration has negative Y. |
| Particles disappear too early | Spawn Life; this is different from the timeline's End. |
| A setting is disabled | Node/parameter locks and incoming connections. |
| Capacity is exceeded | Reduce rate/lifetime or increase capacity. A useful estimate is rate × lifetime. |

Continue with the [CPU Particle guide](cpu-particles.md), [window tour](workspace-tour.md) or [node reference](node-reference.md).
