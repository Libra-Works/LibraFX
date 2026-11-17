# Logical Signal nodes

These nodes are runtime graph operators, not editor-only conditions. Signal pins
and numeric pins remain distinct types; there is no implicit Signal-to-Float or
Float-to-Signal conversion.

## Scalar Compare

`Compare` accepts scalar `A` and `B`, and offers six exact operations:
`<`, `<=`, `==`, `!=`, `>=`, and `>`. Equality is ordinary scalar equality;
there is no implicit tolerance or approximate-equality mode.

Its named outputs are independent:

- `Condition` is a numeric Float containing `0` or `1` for the current update.
- `Event` is a Signal. In `False to True` mode it emits when the condition
  changes from false to true. The previous condition begins false, so a first
  evaluation that is true emits. In `True Each Update` mode it emits once on
  every update for which the condition is true.

For example, connect `Time` to `A`, set `B` to a threshold, and wire `Event` to
an event-driven branch while wiring `Condition` to a numeric effect parameter.

## Signal AND and Signal OR

Both nodes have two named Signal inputs (`A` and `B`), a `Reset` Signal input,
a named `Signal` output, and a named numeric `Pulse` output.

- `Signal AND / Same Update` waits until both distinct input slots arrive in
  one successful effect update.
- `Signal AND / Accumulate Until Both` remembers the first slot across updates
  until the other slot arrives, then clears its waiting mask.
- `Signal OR` emits when either slot arrives.

Each AND/OR node emits at most one Signal per successful update. Repeated
arrivals on one slot are idempotent for AND and cannot satisfy the other slot;
OR coalesces all arrivals in the update. Missing inputs do not fire. Arrivals
are processed FIFO. `Reset` clears the waiting mask and visible pulse at its
position in that order, but never clears the per-update emission guard: a
reset cannot enable a second emission in the same update. A reset before an
emission permits a later valid arrival sequence; a reset after an emission
clears the pulse but does not permit a second emission.

`Pulse` is a numeric `0`/`1` value. It is set in the same update as the
successful gate event, stays `1` through the rest of that update unless Reset
arrives, and returns to `0` at the next successful update. It is not a delayed
frame signal.

## Signal To Number (#62)

`Signal To Number` has `Signal` and `Reset` Signal inputs, a numeric `Value`
output, and a `Mode` enum:

- `Pulse` coalesces one or more arrivals in the current update to `1`, then
  returns to `0` on the next successful update.
- `Latch` sets `Value` to `1` on an arrival and holds it until Reset.
- `Counter` increments once for every received event, including repeated
  arrivals on the same input. Reset sets it to `0`. The count is exact through
  `2^53 - 1`; the next increment fails visibly with `CounterOverflow` rather
  than wrapping or losing integer precision.

Arrival order is significant: Reset clears the value immediately, so a later
Signal in the same update can set or increment it again; a Reset after a Signal
clears the result for that update. AND/OR's one-emission-per-update rule does
not apply to Counter's input events.

Use Compare to turn a numeric condition into a Signal. Use Signal To Number to
turn Signal arrivals into Pulse, Latch, or Counter values. The graph validator
rejects direct cross-category wiring.

## Runtime and state

Each node's state is allocated in caller-owned work memory per runtime
instruction and effect instance. Flattened instances of the same Group asset
therefore have independent state. Reusing an asset or updating another effect
does not share state. `Instance::initialize` resets all state.

After a successful `updateAsync`, the standard `NamedIo`,
`EffectLifecycle`, `GeneratorPool`, and `SpawnableHost` paths automatically
call `Instance::dispatchGeneratedSignals(maxInstructions)` before consuming
render-facing data. A caller using raw `Instance` APIs must make that call
explicitly. It drains comparison events and routes queued Signals in FIFO
order for that update's evaluation serial; repeated signal dispatches do not
create a new update tick.

After routing, the runtime refreshes only the compiler-marked late-pure
dependency chain for logical numeric outputs. The allowlist is limited to
Core-owned, stateless math and matrix operations, including Transform rows and
Decompose outputs. It does not rerun Signal To Number, Compare, other logical
state, arbitrary custom callbacks, lifetime capture, or RNG. Therefore a
Signal To Number value feeding Compare or an unsupported custom/stateful
numeric operation is rejected during compilation with an actionable error;
the graph is not accepted with stale same-tick values. Runtime also checks the
registered descriptor identity, ABI, and state size before re-evaluating an
allowlisted custom-backed matrix operation, so an external registry override
cannot become eligible by reusing its UUID. Exceeding the supplied event
instruction budget is an explicit runtime error, not a silent event drop.

Groups use the existing typed Signal and Float boundaries. Compilation rejects
a Group route if lowering cannot retain the destination slot identity and
per-flattened-instance state. This feature does not introduce a fixed End or
effect-lifetime policy. Signal To Number provides basic pulse/latch/counter
building blocks; a general-purpose Loop/For node and broader state-machine
primitives are not part of #35 or #62.

## Verification scope

The managed checks exercise real named pin controls, wiring, history,
project/group persistence, MCP discovery and creation. Both managed-compiler
and standalone-Converter bytecode run through the assertion-enabled native
tests, including guarded inactive branches and isolated Group/Instance state.
A positive FIFO budget limit and subsequent resume preserve exactly-once
delivery; a zero instruction budget is rejected as `InvalidArgument`.

The Converter's `logical-matrix.lfxp` fixture connects Compare to Counter,
Scalar To Vector3, Transform, Decompose Transform and Model Draw. The full
native package host and actual Unity components expose positions (1,1,1),
(2,2,2) and (3,3,3) in the same three update ticks. Unreal 5.7 also verifies
these component poses and offscreen pixels. Its plugin compiles with Clang
and the final verification build uses MSVC.

Unity's separate `RunPoses` check explicitly excludes pixel capture. The
default `Run` still asserts visible pixels and currently fails with a black
isolated framebuffer; Unity pixel verification is not complete.
