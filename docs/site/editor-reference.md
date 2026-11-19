---
title: Editor Window and Panel Reference
---

[Home](index.md) · [Graph authoring](authoring.md) · [Built-in nodes](node-reference.md)

# Editor Window and Panel Reference

Most editor tools are dockable panels rather than separate windows. Open them from
**Window** or **View**. Panels that read the graph normally follow the active
document tab. Menu labels below use the English UI.

## Authoring workspace

| Screen | Where to find it | How to use it |
| --- | --- | --- |
| Main window and document tabs | **File → New / Open** | Open multiple graphs, switch tabs, save the current document, and use **Save As** for an experimental copy. Docking a tool elsewhere does not change the document. |
| Graph canvas | Open a document | Place nodes, connect compatible pins, and enter a Group to see its child graph. Use the breadcrumb to return to the parent. See [Graph authoring](authoring.md). |
| Search anything | **Edit → Search anything** or `Ctrl+P` by default | Search across commands, nodes, and assets. Use the arrow keys to select a result and Enter to run or open it. |
| Outliner | **Window → Outliner** | Browse graph nodes and select one without locating it manually on the canvas. |
| Node List | **Window → Node List** | Search and drag nodes onto the graph. The catalog includes built-ins and definitions from the active project, not every known project definition. |
| Properties and inspectors 1–4 | **Window → Inspectors** | Edit the selected node's values, options, and connections. Open another slot to keep a second inspector available. What you can edit depends on the selected node. |
| F-Curve | **Window → F-Curve** | Edit numeric animation keys and interpolation. Choose the channel or curve first, edit its keys, and use the shared playhead to inspect timing. |
| Timeline | **Window → Timeline** | Set playback range, scrub or step, and author timed Signal keys. Preview playback and the F-Curve pane share the playhead. A preview loop does not define runtime lifetime. |
| Playback controls | Timeline and workspace transport | Play, pause, seek, or step. Use the engine connection status to distinguish moving the playhead from successfully displaying a frame in an engine. |
| Color editor | Open a color control in the inspector | Edit color channels and alpha. Editable color curves, where exposed, are opened from the corresponding curve control. |
| GPU workspace | Open a GPU Particle node's workspace | Edit the GPU subgraph and export authoring IR or shader source where offered. Source export is not proof of working GPU playback in an engine. |
| Sprite Animation | **Window → Sprite Animation** | Author sprite animation and composition data using the atlas, cells, and timeline. Previewing a composition in this pane is different from drawing a compiled package in an engine. |
| Graph Problems | **Window → Graph Problems** | Review graph diagnostics while editing. Resolve missing or incompatible definitions and connections before retrying compilation. |
| History | **Window → History** | Navigate this document's edit history and pin useful versions. Editing after moving back creates a branch; this does not create a Git commit. |

## Projects, assets, and shared groups

| Screen | Where to find it | How to use it |
| --- | --- | --- |
| Local projects | **Window → Local projects** | Find local effect project files and open them as documents. Use Settings for team/project profiles, which control node and group directories. |
| Content / engine assets | **View → Content** | Browse assets for the selected source or project. Where supported, drag a typed asset onto a compatible graph target or assign it through the inspector. A matching filename does not establish a binding. |
| Resources | **Window → Resources** | Inspect the active effect's resources and their engine references; use the resource action appropriate to the selected item. |
| Asset References | From an asset or resource's reference action | Inspect use sites before replacing a binding. Separate reported references from assets that merely look similar across sources. |
| Resource Identity | From resource or binding details | Inspect the binding's source and engine identifier. Preserve portable identity rather than substituting a machine-specific file path. |
| Configure Group Items | From a Group's inspector | Select the parameters or ports that should appear at the group boundary. Instances can edit exposed values without editing a locked shared asset. |
| Group Updates | When linked Group assets have available updates | Review versions and explicitly decide whether to update the selected instances or retain their saved revisions. |
| Revision history and graph diff | **Edit → Revision history and graph diff**, or from the resource version control action | Inspect Git-backed revisions and compare graph changes. This is separate from the editor's undo history. |
| Compare graph files | **Edit → Compare Graph Files** | Select saved graph files and inspect the comparison. Opening a diff does not merge or modify either file. |

## Engine connection, diagnostics, and assistance

| Screen | Where to find it | How to use it |
| --- | --- | --- |
| Engine Connection | **Window → Engine Connection** | Check preview transport and client status. The runtime debugger area can start or disconnect a supported CPU debug session, toggle breakpoints, and pause, step, resume, or locate the current node. Keep debug invitations private. |
| Preview connections | **View → Preview connections** | Manage authorized preview connections and inspect connected clients. A connected client or accepted transfer is not proof of rendering. Preview invitations are separate from debugger invitations. |
| Native preview profiler | **View → Native preview profiler** | Inspect timing samples and cost/history charts from the native preview. These timings do not benchmark other engines or target platforms. |
| Live Game Instances | **View → Live Game Instances** | Work with a connected, opted-in game session. Check the target instance and connection state before sending changes; this is separate from ordinary asset export. |
| Log | **View → Log** | Read recent editor messages when graph compilation, resource binding, or preview does not behave as expected. Check for private paths before sharing logs. |
| Crash Reports | **View → Crash Reports** | Review locally available crash information before choosing what to copy or share. |
| MCP | **View → MCP** | Inspect the local editor tool connection and setup information. Keep connection credentials private; external clients can use the bundled operator guide. |
| AI Chat | **View → AI Chat** | Configure a supported chat connection, review graph context or attached references, and inspect any proposed edit before approving it. Sending a prompt and approving a graph edit are distinct actions. |

## Settings, layouts, and dialogs

| Screen | Where to find it | How to use it |
| --- | --- | --- |
| Settings → Appearance | **Edit → Settings** | Set the editor language and node-name language independently, then choose the theme and accent. These settings do not rename nodes saved in a project. |
| Settings → Shortcuts | **Edit → Settings** | Inspect or change command bindings and graph/curve navigation controls. The shortcuts in these docs refer to defaults. |
| Settings → Servers | **Edit → Settings** | View or change the editor's MCP and preview ports. Update the corresponding client connection when needed. |
| Settings → Projects | **Edit → Settings** | Choose a profile and supply its node directory and group asset directory. Switching profiles changes future discovery, not the contents of open graphs. |
| Settings → Plugins / Inspectors | **Edit → Settings** | Manage extensions, inspector providers, default inspectors, and plugin commands. Review the trust prompt before enabling third-party code. |
| Window layouts | **Window → Window layouts** | Save, select, or manage docking presets, or restore the default layout from the layout menu. Layout changes do not edit the effect graph. |
| Startup setup | During initial setup | Choose the initial editor settings when prompted. These can be reviewed later in Settings. The splash screen only indicates startup. |
| Unsaved changes | Closing a modified document or workspace | Choose whether to save changes, discard them, or cancel the close. File open/save/export dialogs select the destination or source; the editor's VFX export creates `.lfxb`. |
| Getting started / About | **Help** menu | Open built-in help or check application details. See [Engine integration](engine-integration.md) for installing the current release SDK. |

Specialized confirmation dialogs appear inside these workflows, for example when
trusting an inspector provider, managing resource or sprite data, reviewing a
chat action, or copying diagnostic details. Review the selected target and action
before accepting. Operating-system file pickers vary by platform.
