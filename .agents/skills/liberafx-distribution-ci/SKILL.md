---
name: liberafx-distribution-ci
description: Build, verify or publish LIBERA FX releases and prereleases, maintain GitHub Actions and SDK artifacts, and complete versioned GitHub Pages release notes.
---

# LIBERA FX distribution CI

- Start with `.github/workflows/build.yml`, `Tools/Export-EngineSdk.ps1`, and `docs/sdk-distribution.md`. For a hosted failure, identify the exact run, commit, job, OS, and failing step. Distinguish compile, test, packaging, and upload failures; inspect the produced file or the failed command before blaming the build.
- Preserve `year.month.build`: derive year/month from workflow run creation in JST and build from run number, so reruns retain the version. Keep artifact names, editor About, macOS bundle metadata, and build/SDK manifests in sync without changing engine descriptors, ABI, SDK compatibility, or serialized format versions.
- Verify the consumer layout: archive contents, actual editor/converter entry points and executable bits on Unix, license, source commit, and SDK source hashes. Native outputs live separately from the source export. Stage outside the repository in a fresh directory; use `-AllowDirty` only for local development and never present it as a clean source release.
- Interpret coverage per product. Linux Clang Core compilation and CTest do not certify a console SDK. Unreal source installer checks do not run UnrealBuildTool or the engine; Unity and Godot native builds do not run the engines. An unsigned Apple Silicon `.app` is not signed or notarized. Check the current matrix before claiming support.
- Follow the user's validation authorization. Prefer checks at the failed boundary: use the CI compiler/configuration when fixing portability, and inspect an extracted artifact when fixing packaging. Scheduled or queued jobs are not successful runs.
- Keep ordinary CI separate from distribution. The current workflow retains artifacts on manual runs; it does not create GitHub Releases, publish to registries, deploy Pages, or push SDKs elsewhere. Do not add or trigger publication without separate approval.

## Prerelease delivery checklist

1. Confirm authorization to publish, the source commit, the previous release and the worktree. Never include unrelated changes. Read the actual commit range and current CI matrix; do not copy feature or platform claims from an earlier release.
2. Run `build.yml` with `workflow_dispatch` on the intended ref. Record the resulting run ID, source SHA, run number and creation time. Derive the version from its JST creation date and run number, and wait for every required job to succeed. A push-triggered run does not retain release artifacts.
3. Download that exact run's artifacts to a fresh directory outside the repository. Verify filenames, editor and converter entry points, license, matching version and full source SHA, Unix executable bits inside the desktop archives, SDK manifests and every source hash, install folders, hidden ownership files and native binaries actually included. Compare copy-ready files with their source/native counterparts where applicable. Do not confuse an outer Actions artifact ZIP with the inner desktop archive.
4. Package the verified engine and Core SDK artifacts without dropping hidden files. Create `SHA256SUMS.txt` for the final release assets and verify the checksums. Do not publish incomplete or mismatched assets.
5. Create a **draft prerelease**, not a stable release, with a new `v<version>` tag targeting the run's full source SHA. Upload the final assets and checksum file, then independently verify the draft's prerelease flag, target, asset names, sizes and server-side digests before publishing. After publication resolve the tag to the same SHA. If a release or tag already exists, stop and inspect it instead of overwriting it.
6. Publish the release-specific bilingual Pages notes below. Commit and push only this task's files; the documentation commit may follow the release source commit. Confirm the Documentation workflow and the actual English and Japanese published pages. Report any unfinished step explicitly.

### GitHub prerelease body template

Replace every placeholder with evidence for **this** release; omit inapplicable bullets and never present the template itself as verification. Keep both languages aligned. Link each claimed engine observation to its actual environment and distinguish a build, package inspection and real rendering. Do not carry past observations forward as if they were rerun.

```markdown
## LIBERA FX <version> Preview

<One or two sentences describing this release and the requirement to update the editor, converter and matching SDKs together.>

### Highlights

- **<User-facing feature>:** <what authors can do and any important limits>.
- **<Additional change>:** <what changed since the preceding release>.

### Installation and compatibility

- <Identify the editor downloads and the actual copy-ready SDKs, platforms, source-only packages and required engine rebuilds.>
- <Explain backup, migration and matching-version requirements; mention unsigned macOS and format compatibility where applicable.>

### Validation

[Packaging run <run-id>](<run-url>) passed <specific successful jobs> for source revision `<full-sha>`.
<State exactly what was inspected in the downloaded assets. List engine, OS, graphics backend and host version only for observations actually performed; explicitly name unverified rendering and platform boundaries.>

### Documentation

- [Release notes](<version-specific-English-Pages-URL>) / [リリースノート](<version-specific-Japanese-Pages-URL>)
- <Relevant user guides, if useful>

### 日本語の概要

<English summaryと同じ更新内容、導入・互換性、実際に確認した範囲と未確認事項を日本語で記載。>

Source: `<full-sha>` · [Packaging run](<run-url>)
```

### Versioned Pages template

Create separate English and Japanese pages, translating the same verified facts and retaining the appropriate language and links. Adapt the following structure for each language; placeholders are not factual claims.

```markdown
---
title: <version> Preview release notes
release_version: "<version>"
---

# <version> Preview

Released <actual-publication-date>. [Download this version](https://github.com/Libra-Works/LibraFX/releases/tag/v<version>) · [All release notes](../release-notes.md)

## What changed

- <Author-facing changes and links to the relevant guides>

## Upgrade together

- <Backups, matching editor/converter/SDK versions, migration and engine-specific installation>

## Verified for this release

- <Exact CI, asset-inspection and actual engine observations, with unverified boundaries>

Source: [<short-sha>](https://github.com/Libra-Works/LibraFX/commit/<full-sha>) · [Packaging run](<run-url>)
```

## Finish every release with versioned Pages notes

- Creating a release or prerelease also requires its dedicated English and Japanese Pages notes. Use the published tag, version, date, source revision and verified changes; do not infer released behavior from newer `main` sources. Follow the repository's existing release-body format.
- Create `docs/site/releases/<version>.md` and `docs/site/ja/releases/<version>.md`, with `release_version: "<version>"` frontmatter. Include the exact release download link, changes, matching editor/SDK and migration requirements, and actual verification boundaries. Link back to the release index. Use the `liberafx-documentation` skill for this part.
- Add the version once, newest first, to `docs/site/releases/versions.json` and both `release-notes.md` indexes. Keep past version pages; do not revive a generic “this update” page or overwrite old notes for a new release. Label corrections to a previous version explicitly.
- Run `Tools/Test-DocumentationLinks.ps1` and `Tools/Stage-ReleaseNotes.py <fresh-stage>`. Commit/push the documentation and verify the Documentation workflow's rendering, language links and Pages deployment. A published GitHub Release alone does not finish the delivery; report its version-specific Pages URL too, or explicitly report publication as pending if blocked.
- Respect the session's existing release and Pages authorization; do not ask again when it already covers these actions. Without publication authorization, prepare the notes and state the remaining deployment step.
