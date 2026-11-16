# LIBERA FX bytecode

The runtime accepts little-endian `LFXB` files. Header byte 6 is the endianness marker (`1` = little-endian); all multibyte integers and IEEE-754 binary64 values are little-endian. GUID bytes use RFC 4122/network byte order. Non-finite constants are invalid.

The wire layout is byte-packed and deliberately retains unaligned fields (including binary64 values in v1). The loader reads integers and doubles bytewise, so a valid file may begin at any byte address. Do not add padding to the wire format or cast its bytes to native structs. Wire alignment is independent of the alignment required by the runtime's caller-owned working buffers.

## Version 1

Version 1 remains the 12-byte header and 44-byte instruction format. It is the compiler output for scalar-only graphs and retains its prior scalar execution behavior.

## Version 2

Version 2 has a 16-byte header:

| Offset | Size | Meaning |
| --- | ---: | --- |
| 0 | 4 | `LFXB` |
| 4 | 2 | Version (`2`) |
| 6 | 1 | Endianness (`1`, little-endian) |
| 7 | 1 | Reserved, zero |
| 8 | 4 | Instruction count, at most 10,000 |
| 12 | 2 | Instruction size (`96`) |
| 14 | 2 | Reserved extension field, zero |

Each 96-byte instruction is:

| Offset | Size | Meaning |
| --- | ---: | --- |
| 0 | 16 | Nonzero, unique node GUID |
| 16 | 1 | Opcode (`1`–`39`; other values are reserved) |
| 17 | 1 | Output component count (`1`–`4`) |
| 18–19 | 2 | Connected A/B source component counts; zero when unconnected |
| 20 | 1 | Type flags: bit 0 output is Color, bit 1 A is Color, bit 2 B is Color |
| 21–22 | 2 | Selected A/B output lane; zero for single-output sources |
| 23 | 1 | Reserved, zero |
| 24–27 | 4 | A source instruction index or `0xffffffff` for the parameter default |
| 28–31 | 4 | B source instruction index or `0xffffffff` for the parameter default |
| 32–63 | 32 | Four A/default lanes |
| 64–95 | 32 | Four B/default lanes |

Sources must refer to an earlier instruction and have matching component counts and Color identity. BreakVector2/3/4 instructions preserve their vector inputs internally and expose stable scalar output lanes x/y/z/w; downstream edges encode the selected lane in the source-lane byte. Arithmetic defaults are componentwise. Scalar-to-vector conversion broadcasts; vector dimension extension copies existing lanes and fills every added lane from A/default lane 0; dimension reduction keeps the leading components. Mask selects exactly the output width from x/y/z/w (indices 0/1/2/3). Color conversion preserves RGBA, including HDR RGB values.

Opcodes: 1 Scalar, 2 Time, 3–6 scalar Add/Subtract/Multiply/Divide, 7–9 Vector2/3/4 constants, 10 Color constant, 11–22 vector Add/Subtract/Multiply/Divide grouped by width, 23–25 scalar broadcast, 26–28 BreakVector2/3/4 vector passthrough with component output pins, 29–34 dimension conversion, 35–37 masks, 38 Color-to-Vector4, 39 Vector4-to-Color.

Version 2 stores four doubles per result node, regardless of its output width. `Program::workBytes()` reports that result-work size; `Program::workAlignment()` reports the required work-buffer alignment (at least `alignof(double)`). `Program::parameterValueCount()` reports the optional caller-owned default-lane buffer size (eight doubles per node for v2, two for v1), and `Program::defaultsAlignment()` reports its required alignment (`alignof(double)`). The optional buffer is copied from immutable bytecode during `Instance::initialize`; `Instance::setParameter` updates its lanes only between jobs. The work and defaults buffers must be distinct, remain alive while used, and are never allocated or resized by the runtime. Pass buffers meeting these reported alignments; the bytecode itself does not need to be aligned.

Version 2 leaves header extension bytes and instruction opcodes 40–255 reserved. The loader rejects unknown versions, endianness, instruction sizes, and opcodes rather than interpreting them as known formats.

## Version 3: registered custom nodes

Version 3 is selected only when a compiled graph contains a project-defined native node. The fixed 16-byte header is unchanged except that the instruction size at offset 12 is `256`. The remainder is exactly `count * 256` bytes. Built-in opcodes (`1`–`39`) retain the v2 instruction in the first 96 bytes and require the remaining bytes to be zero. Custom opcode `40` is a parameter node; opcode `41` is an explicit signal node.

Custom records use these fields:

| Offset | Size | Meaning |
| --- | ---: | --- |
| 0 | 16 | Nonzero, unique node GUID |
| 16 | 1 | Opcode `40` (parameter) or `41` (signal) |
| 17 | 1 | Parameter output width (`1`–`4`); zero for a signal node |
| 18 | 1 | Numeric parameter input count (`0`–`4`) |
| 19 | 1 | Signal branch count (`0` for parameter nodes; `1`–`4` for signal nodes) |
| 20 | 1 | Parameter output Color flag (bit 0); zero for signals |
| 21–23 | 3 | Reserved, zero |
| 24 | 16 | Registered `TypeId`, RFC 4122/network byte order |
| 40 | 4 | Runtime ABI version, little-endian |
| 44 | 4 | Reserved, zero |
| 48 | 32 | Four 8-byte input records: source index (`0xffffffff` if default), source width, selected source lane, Color flag, reserved |
| 80 | 128 | Four sets of four little-endian binary64 input-default components |
| 208 | 16 | Four signal branch target indices (`0xffffffff` for an unconnected branch) |
| 224–255 | 32 | Reserved, zero |

Custom numeric inputs accept scalar, Vector2/3/4, or Color values as declared by the registered descriptor. Parameter dependencies must reference earlier records; signal targets must be forward references to signal records. These constraints reject parameter cycles and signal-flow cycles. Signal programs intentionally have no general loop instruction.

### Native registration and execution

Load v3 with `Program::load(bytes, size, registry, registryCount, bindings, bindingCount, out)`. `registry` is an immutable caller-owned `CustomNodeDescriptor` array; its descriptors and the caller-owned per-instruction `bindings` array must stay alive while the Program is in use. The loader resolves `TypeId` and ABI once and stores descriptor pointers in `bindings`; no registry lookup occurs during evaluation. Unknown types fail with `UnknownCustomNode`; descriptor/layout mismatches fail rather than being guessed.

`CustomNodeInvocation::inputs` and `constants` are flat groups of four doubles per input, indexed as `port * 4 + component`; `inputWidths` gives each port width. Inputs are resolved once from the topological schedule and shared in the result buffer. A parameter callback runs once per node per `updateAsync` on the scheduler worker. It must be pure with respect to graph topology, thread-safe for the descriptor's shared `context`, allocation-free, and must not call graph/UUID lookup APIs. Outputs and all components within their declared width must be finite.

Signal callbacks do not run in `updateAsync`. Bind an entry once with `Instance::bindSignal(nodeId, handle)`, then call `dispatchSignal(handle, time, instructionBudget)` after a successful update, on the owning thread. The node-ID overload is retained as a convenience; the handle overload does not scan nodes. The callback chooses a zero-based `selectedBranch`; the runtime follows the precompiled target. Dispatch uses only stack/caller-owned storage, executes synchronously, and stops on a terminal branch or when the budget is exceeded. Callback state is per Instance in the caller's work buffer; optional initialize/cleanup hooks are run on the owner thread. A cleanup hook is called even if its corresponding initialize hook reports failure, so it must tolerate partially initialized state. The caller must wait for jobs to finish before reinitializing or destroying an Instance.

For v3, `Program::workBytes()` includes four result doubles per instruction plus alignment padding and callback state; pass `ceil(workBytes()/sizeof(double))` as `workCount`. `Program::workAlignment()` is at least 16 and may be larger for over-aligned custom state. The optional defaults buffer still needs only `Program::defaultsAlignment()` (`alignof(double)`). `CustomNodeDescriptor::stateAlignment` declares the power-of-two state alignment (16 through 64; default 16); invalid values are rejected during load. State offsets are individually padded to that alignment, with overflow checked. `CustomNodeInvocation::inputs` and `constants` are 16-byte aligned; parameter-node `outputs` satisfy `workAlignment()`, and `state` satisfies its descriptor's `stateAlignment`. Every double pointer is also naturally aligned for `double`. `parameterValueCount()` reports sixteen doubles per instruction (four input ports × four lanes); provide that many separate, caller-owned defaults when overrides are needed. With no defaults buffer, bytecode defaults are read directly and overrides are unavailable. V1/v2 loading, work sizes, and execution are unchanged.

### JSON node metadata

Project node definitions opt in with `runtime: { "kind": "custom", "abiVersion": 1, "execution": "parameter" }` or `"signal"`. The definition's stable `typeId` is the native registration key. Per-port `category` may override legacy `portCategory`; data ports use Float/FloatX2/FloatX3/FloatX4/Color and signal execution ports use type `Signal`. A custom signal node may therefore declare both parameter inputs and exec pins. The compiler also emits the built-in Signal passthrough for ordinary Signal nodes and Group Signal boundaries; the standard native registry supplies its descriptor. Ordinary project-defined nodes without runtime declarations remain editor-only and built-in v1/v2 output remains unchanged. Standalone conversion supports these Signal graphs, but Spawnable and lifecycle graphs still require a package so host metadata is not discarded.

## Versions 4 and 5: curves and Signal fan-out

Version 4 retains the v3 instruction layout and adds opcode `42` for a scalar FCurve. Its record stores a byte offset and key count at offsets 224 and 228. Key records follow all instructions in instruction order; each 40-byte record contains four binary64 values (time in seconds, value, incoming slope, outgoing slope), an interpolation byte (0 step, 1 linear, 2 cubic), and seven zero bytes. Times must increase strictly. The loader bounds keys to 10,000 per curve and 100,000 per program.

Version 5 extends v4 for ordered Signal fan-out. For signal records only, bytes 224–255 contain four pairs of little-endian uint32 values: byte offset and count of extra targets for each branch. The first target remains at offset 208 as in v3. Extra targets are uint32 instruction indices in a contiguous table after all curve keys, ordered by instruction and branch; an empty list has both fields zero. Every target must be a forward reference to a Signal instruction, and the total extra target count is limited to 1,000,000. Other reserved fields remain zero. Older runtimes reject version 5 rather than silently dropping connections.

Dispatch visits the first target and each additional target in connection order, executing each branch depth-first. The instruction budget is shared across the whole dispatch. On error or budget exhaustion, remaining branches do not run and prior callbacks are not rolled back. `Program::workBytes()` includes the aligned caller-owned pending-target stack; no dispatch allocation is performed. Hosts must use the runtime-reported work size rather than deriving it from the node count.
