# Transform product order

`Transform` keeps its saved identity, Position/Rotation/Scale pins and Matrix output.
The Order inspector Enum selects **matrix multiplication order**, not application order:
`TRS = T * R * S` applies Scale, then Rotation, then Translation.
All six permutations are supported; stable values are TRS=0, TSR=1, RTS=2,
RST=3, STR=4 and SRT=5.

Matrices use row-major storage and column vectors. Rotation remains degrees
`(RollX, PitchY, YawZ)`, with `Rz(Yaw) * Ry(-Pitch) * Rx(-Roll)`, matching
the existing Unreal-compatible Transform convention.

Order is an authoring parameter, also publishable through Group exposed
parameters. Changing it recompiles the graph; it is not runtime Enum branching.
Files without Order and explicit TRS both lower to the original three-input
runtime operations and produce identical bytecode. Unknown Order values fail
conversion rather than silently selecting TRS.

## Rendering and decomposition

Matrix arithmetic, point/direction transforms and compiler-generated native
execution retain the full product. Moving Scale left of Rotation can create a
non-orthogonal basis under nonuniform scale. A downstream TRS-only renderer
cannot represent that shear.

`DecomposeTransform.Valid` remains authoritative: singular, nonfinite or sheared
matrices have finite zero outputs and Valid=0. No implicit approximation or
polar decomposition is introduced. Use representable TRS matrices at renderer
boundaries; all six orders remain useful for general matrix calculations.

EN/JA Order labels include the product/application distinction. Existing saved
TRS effects need no migration.
