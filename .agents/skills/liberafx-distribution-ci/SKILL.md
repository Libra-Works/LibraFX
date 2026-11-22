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

## Finish every release with versioned Pages notes

- Creating a release or prerelease also requires its dedicated English and Japanese Pages notes. Use the published tag, version, date, source revision and verified changes; do not infer released behavior from newer `main` sources. Follow the repository's existing release-body format.
- Create `docs/site/releases/<version>.md` and `docs/site/ja/releases/<version>.md`, with `release_version: "<version>"` frontmatter. Include the exact release download link, changes, matching editor/SDK and migration requirements, and actual verification boundaries. Link back to the release index. Use the `liberafx-documentation` skill for this part.
- Add the version once, newest first, to `docs/site/releases/versions.json` and both `release-notes.md` indexes. Keep past version pages; do not revive a generic “this update” page or overwrite old notes for a new release. Label corrections to a previous version explicitly.
- Run `Tools/Test-DocumentationLinks.ps1` and `Tools/Stage-ReleaseNotes.py <fresh-stage>`. Commit/push the documentation and verify the Documentation workflow's rendering, language links and Pages deployment. A published GitHub Release alone does not finish the delivery; report its version-specific Pages URL too, or explicitly report publication as pending if blocked.
- Respect the session's existing release and Pages authorization; do not ask again when it already covers these actions. Without publication authorization, prepare the notes and state the remaining deployment step.
