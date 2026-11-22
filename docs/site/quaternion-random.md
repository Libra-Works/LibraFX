---
title: Quaternion and seeded random nodes
---

# Quaternion and seeded random nodes

Use Quaternion nodes to rotate a direction or interpolate orientation. Use seeded Random nodes to vary particle positions and directions while keeping results repeatable.

These are portable CPU parameter nodes. They are separate from GPU shader nodes and engine-specific quaternion types.

<details class="page-toc" markdown="1">
<summary>On this page</summary>

* TOC
{:toc}

</details>

## Quaternion representation and rotation convention

Quaternions use **FloatX4 in XYZW order**, Hamilton multiplication, with identity
`(0,0,0,1)`. FloatX4 is storage, not a new semantic type: connecting a general
vector4 does not prove it is a valid rotation. Use **Quaternion Compose XYZW**
or **Quaternion From Euler**, not a vector3-to-vector4 cast. The existing
**Break Vector4** has real named X/Y/Z/W outputs and reads these components
without changing representation. There is no implicit vector rotation:
use **Quaternion Rotate Vector**.

All authored angles here use **degrees, not radians**. Euler input/output is
`(RollX, PitchY, YawZ)`, exactly the existing `LbMatrixMath::makeTransform`
convention for column vectors:

`R = Rz(Yaw) * Ry(-Pitch) * Rx(-Roll)`.

Thus Euler `(90,0,0)` maps `(0,1,0)` to `(0,0,-1)`; Euler `(0,90,0)` maps
`(1,0,0)` to `(0,0,1)`; Euler `(0,0,90)` maps `(1,0,0)` to `(0,1,0)`.
Axis Angle instead uses the usual **right-handed positive** axis rotation.
To reproduce positive Euler Roll/Pitch with Axis Angle, negate the angle
about +X/+Y. Positive Euler Yaw uses the same sign about +Z.

| Node | Inputs and defaults | Output / behavior |
|---|---|---|
| Quaternion Identity | None | `(0,0,0,1)` |
| Quaternion Compose XYZW | X/Y/Z = 0, W = 1; four Float inputs | Raw XYZW, no normalization |
| Quaternion Normalize | Quaternion = identity | Unit XYZW; scaled normalization also handles very large/small finite inputs |
| Quaternion Conjugate | Quaternion = identity | `(-X,-Y,-Z,W)`; no normalization |
| Quaternion Inverse | Quaternion = identity | `conjugate(q)/dot(q,q)`; supports non-unit quaternions |
| Quaternion Multiply | A/B = identity | Raw Hamilton `A*B`; apply **B first, A second**; no normalization |
| Quaternion From Euler | Roll/Pitch/Yaw = `(0,0,0)` degrees | XYZW with the matrix convention above |
| Quaternion To Euler | Quaternion = identity | Canonical Roll/Pitch/Yaw degrees matching the existing matrix decomposition convention; equivalent angles need not match original numbers |
| Quaternion Axis Angle | Axis = `(0,0,1)`, Angle = 0 degrees | Normalizes axis; returns unit XYZW |
| Quaternion Slerp | From/To = identity, T = 0.5 | Normalizes endpoints; clamps T to `[0,1]`; shortest arc, including `q`/`-q`; normalized linear blend when dot >= 0.9995 |
| Quaternion Rotate Vector | Quaternion = identity, Vector = `(1,0,0)` | Normalizes quaternion; rotates FloatX3 with no translation or scaling |

Normalize, Inverse, To Euler, Slerp and Rotate Vector reject a zero quaternion
with `InvalidArgument`; Axis Angle rejects a zero axis **even at angle zero**.
Compose, Conjugate and Multiply are raw algebra and can produce zero: that
does not make zero a valid rotation. Conjugate is inverse only for unit length.
At Euler gimbal lock, To Euler uses Roll = 0 and derives the equivalent Yaw,
as existing matrix decomposition does. Quaternion sign is not canonical;
`q` and `-q` express the same rotation. Exactly 180-degree choices have
equally short arcs; endpoint representation can choose either direction.

All node inputs must be finite (`NonFinite` otherwise). Non-finite results
or an inverse too large for binary64 fail with `NonFinite`, not identity.
Floating-point underflow can produce zero. Large raw Multiply inputs may
overflow; normalize beforehand when the values represent rotations.

## Stateless seeded sampling

Every Random node exposes **Seed** and **Sample Index**, both defaulting to 0.
They are **Float pins carrying exact unsigned 32-bit integer values**
`0..4294967295`, not semantic Int32 ports. Fractional, negative or out-of-range
identifiers fail with `InvalidArgument`, rather than truncate or wrap.
Use **Int32 To Float** explicitly when starting with an Int32 value.

| Node | Other inputs/defaults | Output |
|---|---|---|
| Random Float | Min = 0, Max = 1 | Float range sample |
| Random Integer (Float), kind `RandomInt` | Min = 0, Max = 10 | Integer-valued Float; **both bounds inclusive**; signed 32-bit integer endpoints only |
| Random Vector3 | Min XYZ = `(0,0,0)`, Max XYZ = `(1,1,1)` | Box sample; three distinct component streams |
| Random Unit Sphere | None | Unit direction; equal-area Z/azimuth construction |
| Random In Sphere | None | Unit-ball position; cube-root radius for volume-weighted sampling |

Reversed bounds are swapped, independently per component for Vector3. Equal
bounds produce that value. RandomInt rejects fractional endpoints or values
outside `[-2147483648,2147483647]`. It uses unsigned rejection sampling before
range reduction, including the complete signed 32-bit range; it does not use a
biased `word % count` alone. Convert its output with **Float To Int32** when
an actual Int32 pin is needed; ABI v1 cannot describe semantic Int32 custom
ports, so this node deliberately does not pretend otherwise.

Float sampling uses `u = word / 2^32` and a convex endpoint blend, with
`0 <= u < 1`. Rounding can reach the upper endpoint. Sphere sampling is a
finite discrete approximation to continuous uniform distributions, not a
cryptographic source or a guarantee of statistical independence.

### Reproducibility and limits

Version-1 sampling hashes Seed, Sample Index and a component stream with
defined modulo-2^32 arithmetic. There is **no global RNG, hidden advancing
state, frame dependency, or evaluation-order dependency**. Unchanged inputs
give unchanged results on repeated evaluations. Give particles a stable
integer ID as Sample Index; changing the ID changes the sample. Merely
advancing time does not create a new sample. Fractional Time must not be
connected directly as Sample Index.

The integer hash/RandomInt mapping is portable across endian/OS/engine hosts
using this implementation and binary64 numeric transport. Float range blending
can differ in last bits with compiler floating-point contraction; sphere
`sin`, `cos`, `sqrt`, `cbrt`, and quaternion trigonometry also depend on the
platform math library. **Cross-platform floating-point bit identity is not
promised.** Integer word mapping must not change under these stable node IDs
without an explicit compatibility decision.

Two nodes with the same Seed/Index reuse the same underlying stream; scalar,
vector and sphere nodes are intentionally related, not independent streams.
Use different Seeds for unrelated properties. The effective hash is 32-bit:
different Seed/Index pairs can collide, values can repeat, and neither unique
samples nor exhaustive coverage of the unit ball is promised.

## Small authoring tasks

- Turn `(1,0,0)` around Z by 90 degrees using Axis Angle and Rotate Vector.
  Expect approximately `(0,1,0)`, not an engine transform or a four-channel cast.
- Interpolate two authored Euler orientations through From Euler → Slerp →
  To Euler when a draw node expects Euler degrees. Keep XYZW directly when
  rotating a direction; do not reconstruct low-level quaternion arithmetic.
- Give a stable particle ID to Sample Index on Random In Sphere, and keep Seed
  fixed to reproduce its starting position. A particle scheduler must provide
  the ID; adding a Random node alone does not install a particle simulation.

Source verification includes JSON/UUID/translation checks and new compilation
and native outcome test sources. Builds, execution, UI interaction, Undo/Redo,
copy/paste and engine pixels remain to be verified with the updated application.

The optional `QuaternionRandomCompilationTests.WriteNativeFixtures` helper
produces **internal compiler-to-native raw program fixtures** using
`VfxProgram.Compile(...).ToBinary()`. Despite their `.lfxb` filenames, they are
not unified `EffectBinary` containers. The native test reads them with
`Program::load`. This helper does not invoke the standalone converter executable;
neither fixture execution nor executable-converter integration has been verified.

## Re-exporting older binaries

Older converter output could overwrite custom-node defaults with branch metadata. Re-export an affected effect from its editable `.lfxproj`; a binary cannot recover overwritten values.

## Next steps

- [Connect to an engine and play the effect](production-workflow.md).
- [Check a missing preview or unexpected result](troubleshooting.md).
- [Look up a node](node-reference.md).
