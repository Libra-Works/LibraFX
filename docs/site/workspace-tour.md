---
title: Your first tour of the editor
---

# Your first tour of the editor

Open a sample and follow the steps below to learn which window does what. You do not need to write code or JSON.
Then try [Build your first fountain](first-fountain.md).

## 1. Open a sample and save your own copy

1. Choose **File → Open**.
2. Open `Assets/Samples/CpuFountain.lfxproj` from the editor distribution.
3. Choose **File → Save As** and save a copy in your own working folder.

**Check:** a document tab opens and the central graph contains a node named `Fountain`.
If the file is missing, use an updated Preview that includes this sample.

## 2. Select something in the graph

![The LIBERA FX workspace with the graph in the centre and editing panels around it](images/editor/workspace.png)

This is an example workspace layout; the graph contents depend on the sample you open.

1. Click `Fountain` once.
2. Look for its selection outline.
3. Compare its title with the type label at the bottom. Renaming a node changes the title;
   the type label still tells you that it is a **CPU Particle**.

Pan and zoom gestures are configurable. If moving around feels unfamiliar, check the graph controls under
**Edit → Settings** rather than assuming a particular mouse button.

## 3. Change a value in an Inspector

An Inspector is the settings sheet for the selected item.

1. Open an Inspector from **Window → Inspectors**.
2. Select `Fountain` and find **Default particles / second**.
3. Change `300` to `60`, then press Enter or click another field.
4. Choose **Edit → Undo** to restore the value.

**Check:** the setting changes and Undo restores it. Check the visual difference in the engine preview.
If a field cannot be edited, check the Inspector's pinned selection, node/parameter locks and incoming connections.
A connected field gets its value from the other node.

## 4. Enter a particle node and return

1. Double-click `Fountain`.
2. Find the **Spawn**, **Update** and **Draw** cards.
3. On Spawn, choose **Open stage graph**.
4. Look at the breadcrumbs above the graph to see your current location.
5. Select the parent CPU Particle breadcrumb to return to its Flow; select its parent to return to the root graph.

Spawn sets up new particles. Update moves living particles. Draw controls their appearance.

## 5. Find and connect nodes

1. Open **Window → Node List**.
2. Search for `CPU` or `Velocity` in the node display language you use.
3. Drag a node into the graph.
4. Click its star to add a frequently used node to favourites.

**Edit → Search anything**, normally `Ctrl+P`, also searches commands and assets.
Drag from one round pin to a compatible pin to connect them.
Orange Signal connections determine when operations run; value connections provide numbers, colours and other data.
An operation that sets a particle attribute needs a Signal connection as well as its values.

## 6. Browse content

Content lists reusable resources; the Node List lists graph-building tools.

1. Open the content/assets panel from the **Window** menu.
2. Select **Project and local resources**.
3. Choose **Local**, then use the folder button to select `Assets/Samples`.
4. Click Refresh and select `CpuFountain`.
5. Inspect its graph on the right and choose **Open effect**.

Selecting an image previews the image. Selecting a group lets you insert it into the current graph.
For engine assets, select a connected editor in **Connected engine assets**, search, select an asset,
then explicitly assign it to the intended target. Seeing an asset in the list does not assign it.

Engine results use pages of 32; physical folders use pages of 128. Details and thumbnails are requested for the selected asset.

## 7. Play and inspect the result

1. Open **Window → Timeline**.
2. Set **Start = 0**, **End = 600**, **FPS = 60** for a ten-second range.
3. Open the connected game engine's preview window.
4. Play, pause and move the playhead.
5. Set playback speed to `0.25` to inspect timing slowly; `1` restores normal speed.

A moving playhead does not by itself show rendered particles. Complete the
[engine connection steps](production-workflow.md) to see the result.

## 8. Find the next tool

| Task | Window | First action |
| --- | --- | --- |
| Undo the last edit | Edit → Undo | Undo; use Redo to reapply. |
| Return to an older state | History | Select an entry. Click a compressed block to reveal its entries. |
| Find a node by name | Outliner | Select it from the list and inspect its hierarchy. |
| Animate a value over time | F-Curve | Select a curve and move its keys. |
| Choose a colour | Inspector colour field | Open its colour swatch and change colour/opacity. |
| Inspect generated textures or shapes | Procedural Flow | Select a stage and adjust its local preview. |
| Investigate a missing result | Engine connection, Log | Check the connection and the latest message. |
| Investigate slow work | Profiler | Compare operations with the measurements. |
| Find where an asset is used | Asset References | Select a resource and inspect its uses. |
| Personalise the editor | Settings | Change appearance, language or shortcuts. |

See the [window reference](editor-reference.md) for individual panels.
Continue with [Build your first fountain](first-fountain.md) to edit Spawn, Update and Draw yourself.
