# SOURCE skeletal pose input

`Source` reads the current animated pose of one explicitly selected bone. It
has a `Matrix` output and a separate `Valid` (`Float`, 1 or 0) output. Always
gate use of the matrix on `Valid`; unavailable targets, replaced meshes and
missing bones produce `Valid = 0` and a zero matrix, never identity or a
different target.

The target is either the effect owner or a target registered under the exact
key saved on the node. There is no global scene-name search. A bone path is
relative to the registered skeletal target and uses exact `/`-separated
hierarchy segments. `Bone Local` is relative to that bone's parent, `Component`
is relative to the selected skeletal component, and `World` is engine world
space. Matrices use LIBERA FX's existing row-major storage, column-vector
transform convention; coordinates and units are not converted by the Core.

`SkeletalPoseSchema.Lower` expands SOURCE to hidden `InputProperty` nodes:
one Matrix property and one scalar validity property. The existing named-I/O
Matrix lowering binds its four runtime rows. Their versioned names carry the
serialized source selection so adapters can resolve it without modifying
bytecode. The current binding limit is 63 combined UTF-8 bytes for the
registered key and bone path (owner keys must be empty). SOURCE must currently
be in the effect root graph. It is explicitly rejected inside Spawnable Group
templates: current engine adapters publish only effect-root named inputs, not
generation-safe per-instance template inputs. Template SOURCE support requires
instance-scoped pose acquisition and publication; it is not silently left at
the compiled Game I/O default.

Engine adapters must:

* Register targets explicitly and hold weak object references with a
  generation that changes on replacement, destruction or mesh replacement.
  A source binding remains tied to the generation it first resolved; a later
  registration under the same key is not a silent retarget.
* Resolve owner targets only from the effect owner and keyed targets only from
  the explicit registry. Do not search the scene by object or bone name.
* Capture on Unity's main thread in `LateUpdate` after Animator pose evaluation,
  or on Unreal's game thread in a tick group with a prerequisite on the target
  skeletal mesh after animation/pose refresh. Copy all 16 matrix scalars and
  validity into immutable per-frame data before worker evaluation.

For Unity, add `LiberaFxSkeletalPoseTarget` directly to the intended
`SkinnedMeshRenderer` object, assign its renderer and skeleton root, then
register keyed targets with
`LiberaFxSkeletalPoseTarget.RegisterTarget("hero.skeleton", target)`. Keep the
returned `IDisposable` alive for the registration lifetime and dispose it to
unregister. Owner selection reads only the target component on the effect
owner's GameObject. Unity bone paths are relative to the configured skeleton
root, and the selected transform must still belong to the renderer's current
`bones` array.

For Unreal, add one `Libra Skeletal Pose Target` component to the intended
actor, assign its `Skeletal Mesh` component, and set `Registration Key` for
keyed acquisition. It registers that exact weak mesh reference while the
component is registered. Owner selection requires exactly one such target
component on the effect owner. Unreal bone paths include the full reference
skeleton path from its root (for example `root/spine/hand_r`), and the adapter
samples current component-space pose transforms on the game thread. Bone
Local is derived relative to the parent's pose matrix; World composes the
component-to-world matrix without decomposing the result into TRS. If a
keyed target first appears after playback starts, the adapter installs its
pose tick prerequisite and publishes unavailable until the following tick.

## Execution support boundary

SOURCE snapshot publication is supported only for the root of a compiled
`SpawnableGroupPackage` played by the engine package adapters:

* Unity `LiberaFxSpawnableComponent` acquires and publishes the Matrix and
  Valid inputs through `NativeSpawnableHost` in `LateUpdate` before the first
  and each subsequent native advance. Low-level `NativeSpawnableHost` alone
  does not resolve bones.
* Unreal `ULibraSpawnableComponent` publishes them on the game thread in its
  post-pose tick before the first and each subsequent native advance.
  `ULibraEffectComponent` uses this path only when playing a package, by
  delegating to `ULibraSpawnableComponent`. The core `SpawnableHost` alone
  does not resolve bones.

This is not a general raw-CPU program feature. `VfxProgram.Compile` rejects
SOURCE rather than producing a program whose hidden input defaults may be
mistaken for a resolved pose. SOURCE is also rejected by desktop
`render_scene` generation, editor-native preview, and live-game transfer:
those paths do not acquire engine bones and must not imply that a static
render scene is a live SOURCE preview. SOURCE inside Spawnable Group templates
is separately rejected until adapters publish per-instance pose inputs through
generation-checked instance APIs.

Unity 7 Play Mode and Unreal 5.7 automation have verified animated Matrix
values from compiled packages for owner and registered targets in all three
spaces. This verifies the values the effect receives, not the rendered
placement of an effect connected to those values.

To reproduce the Unreal check, run the managed `--source-batch-contracts`
tests with `LIBERAFX_SKELETAL_FIXTURES` set to the output directory and
`LIBERAFX_UNREAL_TEST_BONE_PATH` set to an exact child-bone path. Run
`Libra.Runtime.SkeletalPose.AnimatedPackage` in the test project with
`-LibraSkeletalFixture` pointing to the generated
`UnrealAnimatedSkeletalSpaces.lfxp`, `-LibraSkeletalBone` set to the same bone
path, and `-LibraSkeletalMesh` and `-LibraSkeletalAnimation` set to the Unreal
object paths of a mesh and clip that share a skeleton. Without these flags,
general automation runs skip this project-specific check.
