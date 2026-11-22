# Particle Sprite editor animations

CPU Sprite Draw and GPU billboard rendering can consume the Sprite editor's authored
sequence or composition. The editable source stays in the document; compilation
prepares a flat timeline for the adapters. The artist-facing instructions are in
[CPU particles](site/cpu-particles.md) and [GPU particles](site/gpu-particles.md).

## Playback contract

- Sequences preserve arbitrary cells, frame order, frame durations, the selected
  start/end range, FPS and loop setting. Each particle starts at its own age zero.
- Compositions preserve visible part order, each part's cells, and interpolated
  position, rotation and scale keys. Shorter parts hold their last cell until the
  composition loops. Hidden parts consume no draw geometry.
- Composition translations are normalized by sheet dimensions. Cell dimensions
  scale the particle's XY size before part rotation; particle rotation follows.
- Particle age uses effect time. Disabling the authored animation retains its
  editable source. Regular-grid Flipbook remains a separate option; the authored
  animation takes priority while enabled.
- Limits follow the Sprite editor: 32 parts, 512 cells, 4096 frames and 256 pose
  keys per part. GPU export rejects timestamps that collapse at float precision.
  Source texture dimensions must match the authored sheet.

CPU render metadata uses version 4 when a prepared Sprite timeline is present.
GPU executable payload version 4 carries renderer bindings version 2. Existing
particle buffers, compute packets, transport protocol and native ABI are unchanged.
Use matching updated adapters and shaders; older release binaries cannot read the
new render metadata.

The timeline is version 1, with a `partCount` and a flat `values` array. Addresses
refer to float4 records. CPU uses doubles; GPU adapters upload floats once per
renderer. Three header records per part identify its FPS, duration, frame/key
offsets and counts, sheet dimensions and loop/composition flags. Each frame and
transform key occupies two records. Runtime validation checks boundaries and
monotonic timestamps before sampling. Sampling performs binary searches without
per-particle allocations. GPU animation does not read particles back to the CPU.

Unity's existing single-material renderer binding also requires all billboard
steps in one program to share the authored animation; mismatches are rejected.

## Reproduce authoring and runtime checks

Use a new absolute fixture directory:

```powershell
dotnet run --project Tests/CommentImageSmokeTests.csproj -c Release -- --particle-sprite G:/LfxV/sprite-fixtures-new
cmake --build Runtime/build-native --config Release --target liberafx_particle_sprite_tests
Runtime/build-native/Release/liberafx_particle_sprite_tests.exe G:/LfxV/sprite-fixtures-new/sequence.bin G:/LfxV/sprite-fixtures-new/composition.bin
```

The authoring check compares sampling against the original Sprite editor data,
tests CPU/GPU application, undo/redo, saved graph reload and retained-draft
retargeting, and emits four compiled packages plus a synthetic atlas. Core checks
the same sampling oracle and malformed input handling. Windows CI additionally
runs the fixture parity check with AddressSanitizer. The MCP authoring transition
test sets Sprite parameters through the HTTP tools, compiles metadata 4 and undoes
the change.

Unreal automation accepts the same fixture directory with
`-LibraParticleSpriteFixtures=<directory>`:

- `Libra.Runtime.ParticleSpriteAnimation`: CPU package loading, explicit atlas and
  material bindings, scene updates, Stop and replay. This is scene application
  evidence, not a CPU framebuffer comparison.
- `Libra.GPU.Render.ParticleSpriteAnimation`: actual billboard raster pass with
  imported renderer data, red/blue frame selection, transformed gold composition
  part, and a clear framebuffer when all parts are hidden.
- `Libra.GPU.Render.BillboardFramebuffer`: existing ordinary billboard regression.

## Local observations, 2026-11-22

Windows, Radeon RX 6700 XT, isolated projects and the synthetic atlas above:

| Adapter | Boundary observed |
| --- | --- |
| Godot 4.7.2, Vulkan Mobile | Compiled CPU/GPU packages visibly change red → blue → final frame; compositions show the gold part and changed coverage; time scale zero holds pixels; Stop clears and replay restarts. |
| Unity 7000.0.0a16, D3D11 | CPU package rendering and CPU/GPU shader framebuffers in an isolated SRP; explicit command-buffer submission for GPU; authored frames/composition, zero delta, Stop and replay. |
| Unreal 5.7, D3D11 | CPU package scene application; GPU renderer import and actual raster framebuffer tests described above. |

Unity's batch-mode harness needed explicit GPU command-buffer submission to capture
the billboard. The unchanged ordinary `Graphics.DrawMeshInstancedIndirect` path did
not produce pixels in that harness. This does not certify ordinary Unity game
camera submission, Built-in, URP or HDRP. Packaged/cooked applications and other
engine versions were not tested by this acceptance run. Raw logs and captures stay
under the ignored local `build/` directory.
