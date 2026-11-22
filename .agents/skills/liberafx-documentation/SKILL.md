---
name: liberafx-documentation
description: Write or review LIBERA FX user documentation, versioned GitHub Pages release notes, tutorials, README copy, and editor or engine screenshots.
---

# LIBERA FX documentation

## Choose the destination

- Keep the root README a short English landing page: logo, one-line message, a real LibraDark editor screenshot with the graph prominent, and essential links. Put product explanations and operating instructions on the documentation site.
- Draft documentation in `docs/`. Start with `docs/README.md` and the relevant topic. Keep the existing language of the target document unless the user requests a translation.
- For a tutorial, lead with what the reader will make, then list prerequisites, exact steps using the visible UI labels, and what success looks like. Use a bundled sample rather than requiring users to edit a working project.
- Organize public navigation by reader task: start, create, integrate, reference, help. Keep `docs/site/_data/navigation.json` as the reviewed bilingual page allowlist; use `Tools/Stage-Documentation.py` to stage it. Preserve existing URLs and heading anchors when reorganizing.
- Keep beginner tutorials separate from SDK internals and historical migration notes. Name the documented release in the shared layout, review both languages, and caption screenshots with the relevant controls and expected result. Verify rendered local links and anchors with `Tools/Test-RenderedDocumentation.py`.

## Versioned release notes

For every release/prerelease, write dedicated English and Japanese notes under
`docs/site/releases/<version>.md` and `docs/site/ja/releases/<version>.md` with
`release_version: "<version>"` frontmatter. Base them on the actual published
GitHub release, date and source revision. Include its exact download link,
changes, update requirements and verified environments/limits, plus a link back
to `../release-notes.md`. Add the version newest first to
`docs/site/releases/versions.json` and both release indexes. Keep past versions;
do not use a generic “current update” page. Clearly label later corrections.

Release delivery includes publishing these notes with the session's existing
authorization and checking the Documentation workflow/deployment. Run
`Tools/Stage-ReleaseNotes.py <fresh-stage>` locally; CI uses its
`--verify-rendered` mode to check the index and language links. Report the
version-specific Pages URL, not just the GitHub release URL. If deployment
cannot finish, explicitly report it as pending.

## Verify the claims

1. Check current code, bundled samples and the relevant engine adapter's `USAGE.txt`; distinguish implemented, verified and not yet tested. Do not copy support claims from design notes without checking the implementation.
2. Name the editor OS, engine version, rendering backend or package type when that limits the evidence. Compilation, transfer, scene application and actual engine rendering are different outcomes.
3. Keep version terminology precise: distribution build numbers, native ABI, and file formats are independent.
4. Run `.\Tools\Test-DocumentationLinks.ps1` after editing README or `docs/`. It checks local Markdown and HTML links and images, not external URLs or heading anchors. Review the rendered result for image legibility and accessibility.

## Capture real screens

- Follow `docs/documentation-capture.md`. Use an existing editor build unless the user has authorized a build, and use an isolated absolute `LIBERAFX_DATA_ROOT`. Use LibraDark, record the language, and frame the graph so it is readable at page width.
- Prefer the scoped capture tool for allowlisted, unmodified samples. For full-window capture, capture only the isolated LIBERA FX window. Never capture other apps, private projects, credentials, or logs.
- If an engine result is needed, obtain approval to launch the engine and use a dedicated test project. Capture what the engine actually renders, not a graph view or generated mockup. A matching editor and engine image should show the same sample and be labeled separately.
- Store reviewed images needed by documentation under `docs/images/`. Keep raw captures, diagnostics and session settings outside the repository. Use descriptive alt text. A screenshot proves only what it visibly shows.

## Prepare for Pages without publishing

- Keep site copy independent of the private repository: use site-local links and assets, and check links again against the staged site layout.
- Publish from an explicit allowlist of reviewed public documents and images, never the repository root or all of `docs/` by default. Exclude source, internal handoff notes, private URLs, logs and user data.
- Drafting is not publication. Get explicit approval for the public contents and deployment before enabling Pages or deploying. Do not change repository visibility or push unless explicitly requested.
