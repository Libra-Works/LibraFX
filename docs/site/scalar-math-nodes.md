---
title: Scalar math nodes
---

# Scalar math nodes

These portable Float nodes can drive other parameter inputs, for example to
turn effect age into a repeating phase or a smooth opacity fade.

**Runtime requirement:** These nodes require a matching updated LIBERA FX
runtime/SDK. The published 2026.11.8 downloads have not been rebuilt or
released with them; an older runtime cannot execute their custom node IDs.

| Node | Result |
|---|---|
| Modulo | Floating-point remainder `fmod(Value, Divisor)`, with the dividend's sign. A zero divisor fails. |
| Fraction | `Value - floor(Value)`; `-1.25` produces `0.75`. |
| Ceil | Integer toward positive infinity: `-1.25` produces `-1`. |
| Truncate | Integer toward zero: `-1.25` produces `-1`. |
| Exp / Exp2 | `e^Value` / `2^Value`. |
| Smoothstep | With `t = clamp((Value - Low) / (High - Low), 0, 1)`, produces `t*t*(3-2*t)`; Low must be strictly less than High. |
| Inverse Lerp | `(Value - Start) / (End - Start)`, without clamping. Equal endpoints fail; reversed endpoints are allowed. |
| Saturate | Value clamped to `[0, 1]`. |
| Step | `0` below Edge, otherwise `1`, including at Edge. |

Use Inverse Lerp followed by Lerp to map an input range to an output range;
add Saturate between them for a clamped remap. All inputs must be finite.
Non-finite calculations, including overflow, fail with `NonFinite`; Modulo
and Inverse Lerp report `DivideByZero` for a zero divisor. Smoothstep reports
`InvalidArgument` for equal or reversed bounds. Underflow may produce zero.
