# World LookAt and ordinary Billboard Groups

World LookAt, Spherical Billboard and Tree Billboard are ordinary reusable
Groups made from existing arithmetic, Normalize, Cross, Dot and Matrix nodes.
No engine camera access, simulation state, implicit lifetime or new billboard
primitive is required.

## Contract

All positions share the caller's world coordinate system and units. Target is
an explicit target/camera **position**, not a view matrix or camera rotation.
The result is a row-major, column-vector **world** Matrix and a scalar Valid
(1 or 0).

Default mesh axes are +X forward and +Z up. Local Forward/Up let a +Z-forward,
+Y-up mesh be used without editing its asset. These local axes must be nonzero
and perpendicular; they are normalized, not used as scale.

Spherical uses Forward=normalize(Target-Position),
Right=normalize(normalize(WorldUp) cross Forward), Up=Forward cross Right.
Tree first removes the direction component along normalized World Up. Its mesh
Up therefore remains parallel to the supplied World Up, including Y-up scenes.
It is not hardcoded to world Z. A camera directly above/below a Tree is invalid.

Scale is mesh-local; Pivot is the mesh-local point placed exactly at Position:

`World = targetFrame * inverse(localAxisFrame) * Scale * Translate(-Pivot)`

Negative scale intentionally flips facing. A parent transform must be handled
explicitly: when the renderer already applies an owner transform, supply
`inverse(OwnerMatrix) * World` as the owner's local matrix. Do not apply a parent
twice. The no-pivot/default-axis path remains representable TRS; an additional
nonuniform scaled parent can produce unsupported renderer shear.

## Invalid inputs and rendering

Tolerance defaults to 1e-6 and is clamped to at least 1e-12. Direction and Up
lengths must exceed it; normalized Up/Forward cross length must also exceed it.
Coincident targets, near-zero directions, zero/parallel Up and invalid local
axes return **Valid=0 and the finite zero Matrix**. No alternative axis or prior
orientation is retained. Nonfinite numeric parameters are rejected by the
existing graph/runtime numeric contract.

Connect World Matrix to Decompose Transform, then Position/Rotation/Scale to
the renderer. Check Valid before submitting degenerate poses. Matrix-derived
rotations are tagged `liberafx-world` in render metadata; Unity applies the
same `Rz(Yaw) * Ry(-Pitch) * Rx(-Roll)` convention as native Matrix math.
Legacy raw Unity Euler inputs keep their previous behavior.

Renderer automatic Billboard must be **off** for precomputed orientation.
Conversion rejects an enabled automatic Sprite Billboard plus a decomposed
world rotation instead of applying two rotations silently.

Velocity-aligned/stretch and fixed-axis variants are future library additions,
not hidden modes in these Groups. These CPU numeric Groups do not imply GPU
particle graph support.

## Verification scope

The compiler/native fixtures cover valid and degenerate frames, custom local
axes, Y-up, scale and pivot. The moving-camera package also includes an RST
Transform and a shipped Euler Position Step Group. Standalone Converter output
has been exercised through the Unreal 5.7 world-render automation, including
component poses and offscreen pixels at three times.

Unity's production components agree with the expected positions, scales,
forward and up axes, including the tagged rotation convention. Its isolated
camera capture currently returns a black framebuffer on both D3D11 and D3D12;
Unity pixel verification is therefore **not complete**. The capture test
continues to fail rather than treating component creation as rendered output.

## 日本語

標準 Group の World LookAt／Spherical Billboard／Tree Billboard は、既存ノードの合成です。
Target に接続したカメラのワールド位置を明示入力します。返すのは View ではなく描画用 World 行列です。
初期の前向きはローカル +X、上向きは +Z。メッシュに合わせて Local Forward／Local Up を設定できます。
Tree は World Up に直交する平面へ視線方向を射影し、World Up を保ちます。
退化時は Valid=0 と有限なゼロ行列です。前フレームや代替軸への暗黙の切替はしません。
計算済みの回転を使う場合、描画側の自動 Billboard はオフにしてください。
