# Runtime Enum branches

Enum Select provides typed, runtime-selected parameter branches. Its selector
must be connected to an Enum Value node of the same stable Enum type. Each case
pin is keyed by the Enum's integer value, not its localized display name.

An unconnected case uses the connected Default input when available; otherwise
it uses that case input's declared default value. An unknown integer Enum value
uses the connected Default input, or the Select node's declared Fallback when no
Default is connected.

## Execution and state

The compiler emits LFXB v8 with `LFEB` branch metadata only when a graph uses
Enum execution regions. Older LFXB versions remain unchanged and load through
their existing reader. The native loader validates metadata offsets, case
indices, stable Enum type IDs and guard chains before accepting the program.

Only the selected case's value dependencies and their guarded downstream
parameter/signal consumers execute. Inactive instructions do not evaluate
custom callbacks, update caller-owned node state, consume random values, invoke
Signal/Spawn callbacks or submit render work. A branch's caller-owned state is
retained while inactive. Its `Enum Branch Time` clock advances only while the
branch is active; ordinary `Time`, the parent instance lifetime, and already
spawned child instances remain global and continue independently.

Changing the Enum does not synthesize lifecycle Enter/Destroy/Reset events.
Selecting the same value again does not reset state. Reinitializing the native
Instance clears branch clocks and node state.

## Current support boundary

Enum branch closures containing GPU or rendering nodes are rejected by
compilation with an explicit unsupported-feature error. They are not lowered
to output-only selection, and inactive render instructions are not evaluated
as a workaround. Engine rendering/preview behavior for Enum branches is not
implemented by this runtime slice.

The focused native test source is `Runtime/tests/EnumBranchTests.cpp`; its CMake
target is `liberafx_enum_branch_tests`. `Tests/RuntimeEnumBranchTests.cs`
produces an isolated converter fixture when called with an absolute output
directory.
