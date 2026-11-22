---
title: Troubleshooting
---

# Troubleshooting

Start with the symptom that matches your result. Change one setting at a time and
replay the same short range. Find panels in [Windows and panels](editor-reference.md).

## The timeline moves, but nothing appears

1. Check the engine preview connection. The graph timeline can run without a renderer.
2. Check the engine viewport, camera position and effect scale, not just a connection badge.
3. Open **Log** and resolve the first compilation, shader or missing-resource error.
4. Check particle capacity, emission rate/burst and lifetime. A zero rate with no burst emits nothing.
5. Check texture/material bindings. A local image preview does not import the image into an engine.

Follow [Connect and deliver](production-workflow.md) for connection and binding steps.

## Two pins will not connect

Connect an output to a compatible input. Value pins carry data; orange Signal pins
trigger work. Use an explicit conversion node for different value types, and
**Sequence** to send a Signal to several destinations. See [Graph fundamentals](authoring.md).

## A particle setting has no effect

- Open **Spawn** for initial values, **Update** for ongoing motion and **Draw** for appearance.
- Connect CPU setters to the stage event. Placing a setter alone does not execute it.
- Spawn changes apply to new particles. Stop and replay to compare initial settings.
- A connected **Emitted total** input supplies emission; it is not added to the container's fallback rate.

Use [the fountain exercise](first-fountain.md) to check each stage separately.

## A texture animation is not playing

Apply the Sprite editor data to the selected particle node and enable its Sprite
animation option. FPS, range and loop come from that editor. The bound texture
must have the authored dimensions. For regular-grid Flipbook, check the grid and
starting frame instead. See [CPU texture animation](cpu-particles.md).

## The effect ends or loops too soon

The preview range/loop and **Life Time** serve different purposes. Check both.
Continuously running root GPU/Fluid2D effects also need the appropriate
**AutoDestroyWhenIdle** setting; spawned effects need a Spawn Signal and lifetime.
See [time and lifetime](glossary.md#time).

## The exported effect does not play

Use the editor, converter and SDK from the same release. Export an executable
`.lfxb`, then import/load it and assign engine resources. A `.lfxrm` is a Resource
Identity diagnostic manifest, not a playable effect. Keep the original `.lfxproj`
when investigating an export failure. See [release notes](release-notes.md) and [batch conversion](batch-conversion.md).

## An engine cannot connect, or its resource list is empty

Check the selected engine/project and reconnect after restarting either editor.
Browsing capabilities differ by engine. An empty engine catalog does not imply
the document has no local resources. Follow [your engine's steps](production-workflow.md)
rather than copying another engine's binding names.

## Report a problem

Include the build number, engine version, reproduction steps, expected result and
first error. A small reproduction project is easier to inspect than a production
scene. Remove private paths and connection invitations before sharing logs.
