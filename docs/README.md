# LIBERA FX Documentation

Read the user documentation in
[English](https://libra-works.github.io/LibraFX/) or
[Japanese](https://libra-works.github.io/LibraFX/ja/).
The Markdown sources are also available on GitHub:
[English](site/index.md) and [Japanese](site/ja/index.md).

## For effect artists

Follow the public guides in order, or choose a topic from the grouped navigation:

| Task | English | 日本語 |
| --- | --- | --- |
| Install and start | [Getting started](site/getting-started.md) | [導入と最初の起動](site/ja/getting-started.md) |
| Find the controls | [Workspace tour](site/workspace-tour.md) | [はじめての画面操作](site/ja/workspace-tour.md) |
| Make an effect | [First fountain](site/first-fountain.md) | [最初の噴水](site/ja/first-fountain.md) |
| Animate particles | [Sprite animation](site/sprite-animation.md) | [パーティクルのアニメーション](site/ja/sprite-animation.md) |
| Connect an AI client | [MCP guide](site/mcp-guide.md) | [MCP クライアント接続](site/ja/mcp-guide.md) |
| Use it in a game | [Production workflow](site/production-workflow.md) | [ゲームへの組み込み](site/ja/production-workflow.md) |
| Solve a problem | [Troubleshooting](site/troubleshooting.md) | [困ったときの確認](site/ja/troubleshooting.md) |

The site has the step-by-step workflows. The repository references below describe
implementation contracts and development evidence for SDK contributors.

## SDK integration and performance

- [Particle performance baseline and shared scheduling requirements](particle-performance.md)
- [Prepared playback, shared simulation and persistent particle SoA](runtime-shared-simulation.md)
- [Particle Sprite editor animations and engine acceptance](particle-sprite-animation.md)

- [SDK distribution and compatibility](sdk-distribution.md)
- [Unity integration](../Runtime/Integrations/Unity/USAGE.txt), [Unreal integration](../Runtime/Integrations/Unreal/USAGE.txt), and [Godot integration](../Runtime/Integrations/Godot/USAGE.txt)

## Formats and authoring contracts

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

The [navigation manifest](site/_data/navigation.json) is the explicit bilingual
page allowlist. The [Pages workflow](../.github/workflows/pages.yml) stages it
with `Tools/Stage-Documentation.py` and copies only approved images. Adding a
file elsewhere in `docs/` does not publish it. Review new public content before
adding it to the manifest. Run `Tools/Test-RenderedDocumentation.py` on Jekyll's
output to check local links, images, languages and heading anchors.

Both languages share the Bootstrap layout in `site/_layouts/bilingual.html`.
Keep `site/assets/css/libradark.css` aligned with the editor's LibraDark
palette. The Bootstrap CSS URL is pinned and its integrity hash must be
updated together with the version. The site does not load Bootstrap JavaScript.

The workflow generates both built-in node references from catalog definitions,
enum options, the standard module manifest, translations, and the supplemental explanations in
[`Tools/Generate-NodeReference.py`](../Tools/Generate-NodeReference.py). Internal
runtime nodes are excluded. When adding a built-in authoring node, supply
translated help text or a bilingual supplemental explanation.
