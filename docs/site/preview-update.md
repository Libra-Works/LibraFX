---
title: Preview update and upgrade guide
---

# Preview update and upgrade guide

This page describes development sources following **2026.11.22 Preview**.
The fluid extensions require matching editor and SDK sources; published archives
have not been rebuilt with these changes. When using a published build, obtain
the editor and SDKs from the same [release](https://github.com/Libra-Works/LibraFX/releases).

## What is new

| Feature | Included behavior |
| --- | --- |
| [2D smoke, water and fire](fluid-2d.md) | Smoke, side-view free-surface water, top-down shallow water and fuel/temperature combustion; resource-first Flow, cell forces, dedicated renderers and generated drawing/particle consumers. |
| [Nested Spawnables and CPU particles](cpu-particles.md#spawnable-ownership) | Nested ownership, CPU systems inside Spawnables, immediate-parent Capture/Live and independent per-instance particles. |
| Lifetime and cleanup | Life Time stops a scope's emission; particles, descendants and scheduled work retain it until completion. Forcing a parent removes its subtree, not unrelated siblings. |
| [Engine rendering](engine-integration.md#rendering) | Engine-owned assets and direct/native/RID submissions instead of a scene renderer component for every ordinary Draw or CPU emitter. |
| Particle authoring | CPU Spawn / Update / Draw Flow and GPU Point / Box / Sphere / Circle emission shapes. |
| Editor navigation | Aligned node actions and node creation/search availability that follows the current graph scope. |

## Upgrade together

1. Back up projects and engine bindings. Keep the editable `.lfxproj` files.
2. Update the editor, converter **and** engine SDK. Follow the
   [engine installation guide](engine-integration.md); do not merge old native
   libraries with new scripts or leave duplicate Unity plugins.
3. Re-export `.lfxb` effects from their projects. Nested ownership or template
   CPU programs use **LFXP format 5 / package 13**; root-only CPU remains
   format 4 / package 12. Ordinary fluid scopes use GPU executable payload version 2;
   generated resource/draw references and explicit automatic-plane settings use version 3.
   Fluid shaders require the matching 512-byte uniform contract.
   Old SDKs must reject unsupported formats rather than omit their contents.
4. Test load, Play, natural completion, Stop and restart in a separate project.
   Preserve unchanged Unity `.meta` files and Godot `.gdextension.uid` files.
   Unreal's copy-ready plugin is source-only: rebuild the project's Editor target.

Portable Resource Identity projects still cannot use normal offline `.lfxb`
export; `.lfxrm` remains diagnostic-only. See the
[production workflow](production-workflow.md) for the supported paths and
[batch conversion](batch-conversion.md) for its current CPU-only scope.
These gaps are tracked in [#71](https://github.com/Libra-Works/LibraFX/issues/71)
and [#72](https://github.com/Libra-Works/LibraFX/issues/72).

The release's `build-info.json` and SDK `VERSION.json` identify the build and
source revision. Legacy `.lfxp` packages and raw `.lfxb` remain readable where
supported; importing a new package does not automatically migrate an existing
raw-program asset or its scene references.

## Verification boundaries

Local acceptance of this implementation includes real rendered pixels, retained
child CPU particles after parent/child deadlines, cleanup and restart in all
three engines. Fluid checks also exercise GPU fields and the output plane.

| Engine | Locally exercised environment |
| --- | --- |
| Unity | 7000-series Windows editor, D3D11; Built-in fallback for the new fluid and nested CPU checks. Ordinary rendering also has Built-in/URP coverage. |
| Unreal | UE 5.7 Windows, D3D11/SM5; Clang compile-only, installed-engine MSVC linking and real-RHI automation. |
| Godot | 4.7.2 Windows x64, Vulkan; production GDExtension rendering and lifetime checks. |

CI separately packages Windows x64, macOS Apple Silicon and Linux x64 editors,
tests portable Core with Clang, and builds/checks engine distributions. This is
not desktop UI acceptance on every OS, Unity 6 acceptance, console SDK validation,
or shipped-player certification. macOS packages remain unsigned and not notarized.
Unity/Godot included native binaries are Windows x64.

## Not included yet

Three-dimensional fluids, arbitrary runtime resource/pass graphs, writable engine
main targets, solid obstacles, shallow-water bathymetry and detached droplet
geometry remain future work. The current 2D cell-force editor supports eight
ordered nodes; it is not an arbitrary shader graph. Same-scope generated
color/field consumers are implemented; parent/child resource sharing is not.

[Issue #69](https://github.com/Libra-Works/LibraFX/issues/69) tracks the
**2D-first** work before 3D. See the [fluid guide](fluid-2d.md) for verified models,
engine environments and renderer limits. There is no claim of Niagara Fluids
feature parity. UE 5.8/5.9 compatibility remains separately tracked.
Unity's global transparent Sprite ordering limitation also remains; see the
[rendering guide](engine-integration.md#rendering).
