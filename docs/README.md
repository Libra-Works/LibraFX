# LIBERA FX Documentation

Read the documentation for the current Preview in
[English](https://libra-works.github.io/LibraFX/) or
[Japanese](https://libra-works.github.io/LibraFX/ja/).
The Markdown sources are also available on GitHub:
[English](site/index.md) and [Japanese](site/ja/index.md).

## References in this repository

- [Particle performance baseline and shared scheduling requirements](particle-performance.md)

- Artist introduction: [editor tour](site/workspace-tour.md) and [first fountain](site/first-fountain.md).
- 制作者向け入門：[はじめての画面操作](site/ja/workspace-tour.md)、[最初の噴水を作る](site/ja/first-fountain.md)。
- [SDK distribution and compatibility](sdk-distribution.md)
- [Unity integration](../Runtime/Integrations/Unity/USAGE.txt), [Unreal integration](../Runtime/Integrations/Unreal/USAGE.txt), and [Godot integration](../Runtime/Integrations/Godot/USAGE.txt)
- [Bytecode format](../Runtime/BYTECODE.md), [GPU backends](gpu-backends.md), and [math backend measurements](../Runtime/MATH-BACKEND-MEASUREMENTS.md)
- [Procedural models, textures, materials and shared resources](procedural-resources.md)
- [GPU resource plan compiler contract](gpu-resource-plan.md)
- [Signal timeline](signal-timeline.md), [type conversions](type-conversions.md), [logical signals](logical-signals.md), and [runtime enum branches](runtime-enum-branches.md)
- [Asset references](asset-references.md), [resource workspace](resource-workspace.md), [transform order](transform-order.md), [Look At and billboards](lookat-billboards.md), and [standard modules](standard-modules.md)
- Japanese references: [introduction](../イントロダクション.md), [overview](../概要書.md), [getting started](getting-started.md), [lifetime](lifetime.md), and [small example](small-example.md)

## Maintaining the site

Update both language versions when published workflows or support limits change.
Distinguish compilation, engine playback, and rendering evidence. Use the
[documentation capture guide](documentation-capture.md) for sample screenshots.

The [Pages workflow](../.github/workflows/pages.yml) stages an explicit list of
pages and images. Adding a file here does not add it to the site. Review any
new public content before adding it to the list.

Both languages share the Bootstrap layout in `site/_layouts/bilingual.html`.
Keep `site/assets/css/libradark.css` aligned with the editor's LibraDark
palette. The Bootstrap CSS URL is pinned and its integrity hash must be
updated together with the version. The site does not load Bootstrap JavaScript.

The workflow generates both built-in node references from catalog definitions,
enum options, the standard module manifest, translations, and the supplemental explanations in
[`Tools/Generate-NodeReference.py`](../Tools/Generate-NodeReference.py). Internal
runtime nodes are excluded. When adding a built-in authoring node, supply
translated help text or a bilingual supplemental explanation.
