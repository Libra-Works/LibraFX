---
title: GPU Particle Authoring
---

[Home](index.md) · [Editor reference](editor-reference.md) · [Built-in nodes](node-reference.md)

# GPU Particle Authoring

Use **GPU Particle** for large particle fields. Set initial values in **Spawn**, motion in **Update**, and appearance in **RenderBillboard**. Double-click the container to open its workspace.

The outer graph controls effect timing and Signals; GPU kernels calculate particle values. For grid-based smoke, water or fire, use [Fluid2D](fluid-2d.md) in the same workspace. See [GPU terms](glossary.md#gpu) when you need a definition.

<details class="page-toc" markdown="1">
<summary>On this page</summary>

* TOC
{:toc}

</details>

## Try the bundled samples

These are editable starting points, not templates requiring an imported image.
Use a LIBERA FX build containing the samples and a matching engine integration
that supports GPU particles and Fluid2D. Connect the engine preview as described
in the [production workflow](production-workflow.md); compiling alone does not
show the result. The expected appearances below are design intentions, not
screenshots or verified engine results.

| Sample in `Assets/Samples` | What it demonstrates |
| --- | --- |
| `GpuCurlTurbulence.lfxproj` | A small blue particle cloud following Curl Noise, with Turbulence changing its speed. Start here. |
| `GpuFluidVelocity.lfxproj` | Particles sampling a Fluid2D smoke solver's velocity output. |
| `GpuCurl100k.lfxproj` | The larger Curl Noise example: capacity 100,000 and 12,500 particles per second. Increase load only after checking your target GPU. |

### Curl and turbulence: follow a moving field

1. Use **File → Open** to open `Assets/Samples/GpuCurlTurbulence.lfxproj`,
   then **Save As** to save a working copy. Double-click `GpuCurlTurbulence`.
2. The flow has Spawn, Update and RenderBillboard. The capacity is `4096`;
   **Particles per second** is `240`. Spawn uses a sphere of radius `1.5`,
   lifetime `12` seconds, size `0.025` and a blue color. RenderBillboard uses
   `builtin:white`; no texture file is needed.
3. Set preview FPS to `60` and range to `0–1200`, connect the engine and play.
   The intended result is a gradually filling, drifting blue cloud. Capacity is
   a limit, not the number of immediately visible particles; allow several
   seconds for emission and the one-second fade-in.
4. Open **Update → Open kernel**, select **Typed DSL**, and paste the following
   into the text editor. Press **Apply text → IR**. **Graph → text** generates
   equivalent expressions with generated names, not necessarily these names.

```text
let frequency = 0.65;
let strength = 1.2;
let drift = float3(0, 0.3, 0);
let animatedPosition = @Position * frequency + float3(0, time * 0.1, 0);
let variation = turbulence(animatedPosition, 31u, 3i, 0.5);
@Velocity = curlnoise(animatedPosition, 17u, 3i, 0.5) * strength * (0.35 + 0.65 * variation) + drift;
@Position += @Velocity * dt;
@Color.a = 0.4 * clamp(@Age, 0, 1) * clamp(@Lifetime - @Age, 0, 1);
```

`curlnoise` supplies a vector direction; scalar `turbulence` varies its
strength, and `drift` adds upward motion. Try `strength = 0.5` for gentler
motion or `frequency = 1.2` for a finer field. Apply the text and restart
preview to compare. Set the multiplier `(0.35 + 0.65 * variation)` to `1` to
compare without turbulence. This is a velocity field, not a fluid simulation.

<span id="gpu-fluid-velocity"></span>

### Fluid2D velocity: carry particles with the solver

1. Open `Assets/Samples/GpuFluidVelocity.lfxproj` and save a separate copy.
   The main graph contains `Fluid2D velocity source` and `Particles following
   fluid` in the same scope. No graph wire is needed: the generated resource
   binding is stored on the particle container.
2. Open `Particles following fluid`. In its flow settings, `fluidVelocity` is a
   FlowMap bound to the fluid node's `VelocityOutput`, using raw velocity and
   generated-plane coordinates. The fluid solver runs before the consumer.
   To rebind it, use **Generated output in this scope**, select the fluid node
   and `VelocityOutput`, then **Use generated output**.
3. Open **Update → Open kernel**. The particle velocity comes from
   `sampleFlowMap("fluidVelocity", @Position)`; Position advances using
   velocity and `dt`. Spawn places particles near the smoke source, with
   capacity `2048`, `100` per second and a `4`-second lifetime.
4. Connect a compatible engine, set preview to `60` FPS and `0–600`, and play.
   The intended result is yellow particles carried by the smoke velocity.
   The 64 × 64 fluid's output plane is off so the particles are easier to
   see; simulation still runs. Turn **Output plane: Auto / On / Off** on in
   the fluid workspace to compare with the smoke.

For a slower response, replace the Update text with the following and apply it:

```text
@Velocity = sampleFlowMap("fluidVelocity", @Position) * 0.5;
@Position += @Velocity * dt;
@Color.a = 0.8 * clamp(@Age, 0, 1) * clamp(@Lifetime - @Age, 0, 1);
```

Outside the producer domain, sampling clamps to its edge. Both transforms
must be invertible; moving the producer and consumer apart changes the sampled
region. Parent/child resource sharing and same-frame cycles are unsupported.
For density sampling and other output details, see
[Fluid2D: Drive another GPU particle system](fluid-2d.md#drive-another-gpu-particle-system).

## Open the particle program

1. In **Node List → Core / GPU**, add **GPU Particle** to the main graph.
2. Double-click **GPU Particle**. The
   breadcrumb changes from the CPU graph to the GPU workspace.
3. Use **Open kernel** on a Spawn or Update card to edit its calculations.
   **Flow** returns to the flow; **Main graph** returns to the main graph.

![GPU Particle workspace showing the Spawn flow card, GPU palette, and renderer properties](images/editor/gpu-flow.png)

This image shows an earlier UI and starter program, not the samples above.
Its status message does not describe the current engine integrations.

The center lists the authored execution order, **not** a trace of a running
effect. Read the cards in this order:

| Phase | What to edit |
| --- | --- |
| **Spawn** | Initial particle attributes, such as Position, Velocity, Lifetime, Size, and Color. In the starter program, `emitterPosition` is a host uniform, not an output from another phase. |
| **Update** | Per-step simulation. Read the current attributes, compute new values, and write the attributes that should persist for the next step. Use `deltaTime` for a rate expressed per second. |
| **RenderBillboard** | Drawing of the simulated state. Set the texture, material preset, and draw-size multiplier in the renderer properties. A draw-size multiplier changes the rendered size, not the simulated Size attribute. |

### Choose the initial Spawn shape

The **Spawn position shape** selector is on the Spawn flow card. It controls
where each new particle is initially placed; it does not change billboard draw
geometry. Choose **Point**, **Box (volume)**, **Sphere (uniform volume)**, or
**Circle (XY circumference)**. Box uses non-negative half-extents; Sphere and
Circle use a non-negative radius. Sphere samples volume uniformly (radius is
distributed by the cube root of a uniform sample); Circle samples its XY
circumference and keeps Z unchanged.

The shape adds a seeded, per-particle offset to the value written to Spawn's
**Position** attribute. If Spawn has no Position write, the offset is centered
on the host-provided `emitterPosition`. Point adds no offset. Existing GPU
documents without a saved shape keep their previous Spawn position behavior
and are shown as Point when opened. Authored shape settings remain in the
portable JSON IR; compilation lowers sampling to existing typed `Random` and
math operations for register packets and all generated shader formats, without
a new GPU opcode.
After editing Box half-extents or a Sphere/Circle radius, use **Apply shape
dimensions** on the Spawn card to save the values.

Set **Particle capacity** at program level and apply the change with the
checkmark. Capacity limits the particle storage; it is not an emission rate.

## Use Sprite editor animation and layered parts

Open **Window → Sprite Animation** and author the cells, frame order, individual
durations or layered composition. Select **GPU Particle** in the same graph and
choose **Apply to selected node**. **Load selected node settings** reopens the
authored data. No animation kernel needs to be built by hand.

The updated billboard renderer samples that timeline on the GPU using each
particle's age. Visible parts retain their position/rotation/scale keys and
order inside each particle. The parts share its simulation state; they do not
create more simulated particles or require particle readback. More parts still
mean more vertices and overdraw. Transparent particles are not globally sorted.

In the GPU renderer settings, **Use Sprite editor animation** toggles the
authored data without deleting it; apply the renderer settings to save the change.
The editor's range, loop and FPS settings control playback, and effect time
scaling controls particle age. The source texture binding must match the
authored image dimensions. Frame/key times that collapse at GPU float precision
are rejected explicitly.

This requires GPU executable payload **4**, renderer bindings **2**, and matching
updated native adapters and billboard shaders. The particle buffer layout and
compute-kernel format are unchanged. Older Preview binaries are not updated by
publishing this documentation. See [CPU Sprite animation](cpu-particles.md)
for the authoring workflow and the distinction from a regular-grid Flipbook.

Unity separates GPU simulation updates from camera submission, retaining the last
visible state while time is held. Ordinary camera rendering, Stop and replay were
checked in Windows / Unity 7000.0.0a16 / D3D11 Play Mode. Unity 7 alpha needs
`-enable-builtin-rp-fallback` when testing the bundled shaders.

## Edit a kernel

![Spawn kernel graph with a typed operation palette, value nodes, and attribute writes](images/editor/gpu-kernel.png)

This earlier UI image illustrates the graph layout, not the current sample.

1. Open a Spawn kernel and select the value driving the **Lifetime** write.
   For a first experiment, change its Float constant, then compile. This
   changes particle lifetime, not the lifetime of the enclosing CPU effect.
2. To add a calculation, select a type in **GPU palette**, add the operation,
   and connect the output to a compatible input. Select a node to inspect its
   settings. If a node has no downstream use and drives no write, it will not
   update particle state.
3. In **Attribute writes**, assign the computed value to the attribute you
   intend to update. For example, an Update kernel can read Position and
   Velocity, calculate `Position + Velocity × deltaTime`, and write Position.
   Check **Diagnostics** for mismatched types, missing inputs, or invalid flow.

**Kernel Graph** and **Typed DSL** edit the same program; they are not two
independently executed kernels. The text tab is a restricted typed language,
not HLSL, VEX, or a general scripting console. Use **Graph → text** to regenerate
text from the visual graph, and **Apply text → IR** to apply a text edit.
Invalid text is kept as a draft without replacing the last valid graph.

![Typed DSL showing the starter Spawn kernel's uniform, constants, and attribute assignments](images/editor/gpu-dsl.png)

This earlier UI image shows the starter Spawn text, not the Update examples above.

## Compile, export, and verify in the engine

Run **Compile** and fix diagnostics before exporting. The workspace can export
portable IR or supported shader source; retain the matching metadata when
exporting a shader. **A successful compile or export does not run the GPU.**
Use the target engine's integration to resolve bindings, build or import the
backend artifacts, and verify simulated motion and rendering. Do not treat
standalone shader source or legacy raw bytecode as a complete GPU effect.
The editor exports GPU effects in the same `.lfxb` package as CPU effects;
the standalone converter uses the same compiler for CPU, GPU and Fluid2D effects. Use an engine SDK from the same release. See [Engine integration](engine-integration.md).

Follow [Animate particle textures](sprite-animation.md) for the steps to cut cells, arrange frames and apply them.

## Next steps

- [Connect to an engine and play the effect](production-workflow.md).
- [Check a missing preview or unexpected result](troubleshooting.md).
- [Look up a node](node-reference.md).
