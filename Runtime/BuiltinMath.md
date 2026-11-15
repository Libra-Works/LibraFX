# Built-in math nodes

The Core registry exposes immutable, process-lifetime descriptors through
`liberafx::builtinNodes(count)`. The v3 `Program::load` overload with caller-owned
bindings resolves built-ins automatically when the caller registry does not contain
the type ID. A caller descriptor takes precedence, including ABI mismatches. The
binding storage, bytecode, and any caller-supplied descriptors must outlive the program.
Type IDs use the existing bytecode convention `Guid.TryWriteBytes(bigEndian: true)`.

Scalar operations are Abs, Floor, Round, Sign, Sqrt, Pow, Log (natural), Log10,
Sin, Cos, Tan, ATan, ATan2 (Y, X), DegToRad, RadToDeg, NormalizeRad,
NormalizeDeg, Min, Max, Clamp, Lerp, Mad, PI and TwoPI. Vector2/3/4 provide
Length, Dot, Normalize, Abs, Min, Max and Lerp; Vector3 also provides Cross.
Vector Lerp uses a scalar interpolation factor. Lerp extrapolates, and Mad is
multiply then add (not fused). Clamp rejects reversed bounds.
Floor rounds its input down to a multiple of its second input; zero multiple
returns `Result::DivideByZero`. ComposeVector4 combines four scalar inputs.

Round ties are away from zero. Angle normalization uses `[-π, π)` and
`[-180, 180)` respectively. A zero vector normalizes to zero; vector length and
normalization scale their inputs to avoid intermediate overflow. Non-finite
inputs, domain errors and non-finite results produce `Result::NonFinite`.
An unrepresentable length therefore fails even though normalization can succeed.
