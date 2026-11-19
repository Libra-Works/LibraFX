# Private documentation capture

Pages publishing remains disabled. No public workflow, repository visibility
change or automatic upload is installed by this feature.

On Windows, the guarded PowerShell runner starts an existing editor build with
fresh settings, opens an approved bundled sample over loopback MCP and saves
only that graph's PNG and metadata to a new local directory:

```powershell
.\Tools\Capture-PrivateDocumentation.ps1 -EditorExe C:\isolated\LiberaFx.exe `
  -OutputDirectory C:\isolated\docs-capture -Sample SolarSystem.lfxproj
```

Use the same display scaling when comparing captures. Review the image and
metadata before copying an approved image into private documentation; the
runner never publishes anything.

Start a dedicated editor with an **absolute isolated** `LIBERAFX_DATA_ROOT` and
`LIBERAFX_MCP_DEBUG=1`. Keep real user documents/settings out of that session.
Use the same language, theme, window dimensions and display scaling each time.
Connect to its authenticated loopback MCP endpoint using the bundled operator
skill; never put the token into documentation or logs.

Open an unchanged bundled `Fireworks.lfxproj`, `SolarSystem.lfxproj` or
`SolarSystemMatrix.lfxproj` using `libera_graph_open_project`, and return to the
root graph. `libera_graph_capture_documentation(sample, frameAll: true)` returns:

- A PNG of **only the active graph viewport**, not other panes/windows/apps.
- Sample SHA-256, language, actual theme, dimensions, zoom, offsets and graph state.
- An explicit `enginePlaybackVerified: false`.

The tool rejects other documents, edited sample graphs, nested scopes and
non-isolated sessions. It reuses the bounded graph capture path, writes no files,
and does not change graph/history. Framing is an explicit viewport-only option.
Save returned images into an explicitly chosen private documentation image
directory through the authorized client. This task does not generate or claim
screenshots when the editor has not actually been run.

Update the relevant Markdown alongside the feature, review wording against
actual metadata/behavior and check all local links/image files exist. A graph
capture does not replace Unity/Unreal playback screenshots: obtain those through
their guarded engine verification paths after permission to launch the engines.
Do not capture private user projects/assets or attach logs/source automatically.

Run `.\Tools\Test-DocumentationLinks.ps1` from PowerShell 7 to check local
Markdown/image targets without fetching external URLs. This detects missing
files and paths outside the repository; it does not certify external URLs or
heading anchors.

Before any future Pages deployment, explicitly approve the exact documents and
images, exclude internal/source/user content, then separately approve push,
public deployment and verification of the deployed pages. Local staging is not
publication.
