using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace LibraGpu.Core;

public abstract record DslExpression(TextSpan Span);
public sealed record DslLiteral(double Value, GpuType Type, TextSpan Source) : DslExpression(Source);
public sealed record DslName(string Name, bool IsAttribute, TextSpan Source) : DslExpression(Source);
public sealed record DslUnary(string Operator, DslExpression Value, TextSpan Source) : DslExpression(Source);
public sealed record DslBinary(string Operator, DslExpression Left, DslExpression Right, TextSpan Source) : DslExpression(Source);
public sealed record DslCall(string Name, IReadOnlyList<DslExpression> Arguments, string? Parameter, TextSpan Source) : DslExpression(Source);
public sealed record DslSwizzle(DslExpression Value, string Components, TextSpan Source) : DslExpression(Source);
public abstract record DslStatement(TextSpan Span);
public sealed record DslLet(string Name, DslExpression Value, TextSpan Source) : DslStatement(Source);
public sealed record DslAssign(string Attribute, string? Components, string Operator, DslExpression Value, TextSpan Source) : DslStatement(Source);
public sealed record GpuDslAst(IReadOnlyList<DslStatement> Statements);
public sealed record DslLoweringOptions
{
    public IReadOnlyDictionary<TextSpan, string>? NodeIdsBySpan { get; init; }
    public IReadOnlyDictionary<TextSpan, string>? WriteIdsBySpan { get; init; }
}
public sealed class DslSource
{
    public string Text { get; }
    public IReadOnlyDictionary<TextSpan, string> NodeIdsBySpan { get; }
    public IReadOnlyDictionary<TextSpan, string> WriteIdsBySpan { get; }
    internal DslSource(string text, Dictionary<TextSpan, string> nodes, Dictionary<TextSpan, string> writes)
    { Text = text; NodeIdsBySpan = new ReadOnlyDictionary<TextSpan, string>(nodes); WriteIdsBySpan = new ReadOnlyDictionary<TextSpan, string>(writes); }
}

/// <summary>
/// Independent bounded expression DSL. Both textual and visual frontends lower to KernelGraph.
/// Let bindings are immutable; attribute assignments are sequential and lower to final SSA stores.
/// Emit returns ID/span maps for this limited grammar, not arbitrary text-to-visual-layout reconstruction.
/// </summary>
public static class GpuDsl
{
    public static GpuDslAst Parse(string text, GpuLimits? limits = null) => new Parser(text, limits ?? new()).Parse();
    public static KernelGraph Compile(string text, string kernelId, KernelStage stage, GpuLimits? limits = null, DslLoweringOptions? options = null)
        => Lower(Parse(text, limits), kernelId, stage, limits, options);
    public static KernelGraph Lower(GpuDslAst ast, string kernelId, KernelStage stage, GpuLimits? limits = null, DslLoweringOptions? options = null)
        => new Lowerer(kernelId, stage, limits ?? new(), options ?? new()).Run(ast);

    private static GpuValidationException Error(string message, TextSpan span, string? kernel = null, string? node = null)
        => new([new("Dsl", message, kernel, node, null, span)]);
    private sealed record Token(string Text, int Start, int Length, bool String = false)
    { public TextSpan Span => new(Start, Length); }
    private sealed class Parser
    {
        private readonly List<Token> tokens = [];
        private readonly GpuLimits limits;
        private int index, depth;
        private Token Current => tokens[index];
        public Parser(string text, GpuLimits limits)
        {
            this.limits = limits;
            // Reuse the public limits guard without admitting source code to the shader.
            GpuCompiler.Validate(new GpuProgram(), limits);
            if (text is null || Encoding.UTF8.GetByteCount(text) > limits.MaxJsonBytes)
                throw Error("Text exceeds the byte limit.", new(0, 0));
            for (int i = 0; i < text.Length;)
            {
                int start = i;
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    if (i - start > 128) throw Error("Identifier too long.", new(start, i - start));
                    tokens.Add(new(text[start..i], start, i - start));
                }
                else if (char.IsAsciiDigit(c) || c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    bool dot = false;
                    while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.' && !dot))
                    { if (text[i] == '.') dot = true; i++; }
                    if (i < text.Length && text[i] is 'e' or 'E')
                    {
                        i++; if (i < text.Length && text[i] is '+' or '-') i++;
                        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    }
                    if (i < text.Length && text[i] is 'u' or 'i' or 'f') i++;
                    tokens.Add(new(text[start..i], start, i - start));
                }
                else if (c == '"')
                {
                    i++; int content = i;
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    if (i >= text.Length || text[i] != '"') throw Error("Only plain parameter-name strings are allowed.", new(start, i - start));
                    tokens.Add(new(text[content..i], start, ++i - start, true));
                }
                else if ("@.=;()+-*/, ".Contains(c))
                {
                    i++;
                    if (i < text.Length && text[i] == '=' && c is '+' or '-' or '*' or '/') i++;
                    tokens.Add(new(text[start..i], start, i - start));
                }
                else throw Error($"Unexpected character '{c}'.", new(i, 1));
                if (tokens.Count > Math.Min(262144, (long)limits.MaxNodesPerKernel * 32))
                    throw Error("Token limit exceeded.", new(start, i - start));
            }
            tokens.Add(new("", text.Length, 0));
        }
        private bool Take(string text)
        { if (Current.String || Current.Text != text) return false; index++; return true; }
        private Token Require(string text)
        { var t = Current; if (!Take(text)) throw Error($"Expected '{text}'.", t.Span); return t; }
        private string Name()
        {
            var t = Current;
            if (t.String || t.Text.Length == 0 || !(char.IsAsciiLetter(t.Text[0]) || t.Text[0] == '_'))
                throw Error("Expected identifier.", t.Span);
            index++; return t.Text;
        }
        private static TextSpan Through(int start, Token end) => new(start, end.Start + end.Length - start);
        public GpuDslAst Parse()
        {
            var statements = new List<DslStatement>();
            while (Current.Text.Length > 0)
            {
                int start = Current.Start;
                if (Take("let"))
                {
                    var name = Name(); Require("="); var value = Expression();
                    statements.Add(new DslLet(name, value, Through(start, Require(";"))));
                }
                else
                {
                    Require("@"); var attribute = Name(); string? components = null;
                    if (Take(".")) components = Name();
                    var op = Current.Text;
                    if (op is not ("=" or "+=" or "-=" or "*=" or "/=")) throw Error("Expected attribute assignment.", Current.Span);
                    index++; var value = Expression();
                    statements.Add(new DslAssign(attribute, components, op, value, Through(start, Require(";"))));
                }
                if (statements.Count > limits.MaxNodesPerKernel * 2L) throw Error("Statement limit exceeded.", Current.Span);
            }
            return new(statements.AsReadOnly());
        }
        private DslExpression Expression(int minimum = 0)
        {
            if (++depth > Math.Min(64, limits.MaxJsonDepth * 2)) throw Error("Expression nesting limit exceeded.", Current.Span);
            DslExpression left;
            var first = Current;
            if (Take("-") || Take("+"))
                left = new DslUnary(first.Text, Expression(30), new(first.Start, tokens[index - 1].Start + tokens[index - 1].Length - first.Start));
            else if (Take("("))
            { left = Expression(); Require(")"); }
            else if (Take("@"))
            { left = new DslName(Name(), true, Through(first.Start, tokens[index - 1])); }
            else if (first.Text is "true" or "false")
            { index++; left = new DslLiteral(first.Text == "true" ? 1 : 0, GpuType.Bool, first.Span); }
            else if (first.Text.Length > 0 && (char.IsAsciiDigit(first.Text[0]) || first.Text[0] == '.'))
            {
                index++;
                var suffix = first.Text[^1];
                var type = suffix == 'u' ? GpuType.UInt : suffix == 'i' ? GpuType.Int : GpuType.Float;
                var number = suffix is 'u' or 'i' or 'f' ? first.Text[..^1] : first.Text;
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                    throw Error("Invalid numeric literal.", first.Span);
                left = new DslLiteral(v, type, first.Span);
            }
            else
            {
                var name = Name();
                if (Take("("))
                {
                    var args = new List<DslExpression>(); string? parameter = null;
                    if (name == "param")
                    {
                        if (!Current.String) throw Error("param requires a fixed uniform-name string.", Current.Span);
                        parameter = Current.Text; index++; Require(")");
                    }
                    else if (!Take(")"))
                    {
                        do { args.Add(Expression()); if (args.Count > 4) throw Error("Function arity limit exceeded.", Current.Span); } while (Take(","));
                        Require(")");
                    }
                    left = new DslCall(name, args.AsReadOnly(), parameter, Through(first.Start, tokens[index - 1]));
                }
                else left = new DslName(name, false, first.Span);
            }
            while (Take("."))
            {
                var components = Name();
                left = new DslSwizzle(left, components, new(left.Span.Start, tokens[index - 1].Start + tokens[index - 1].Length - left.Span.Start));
            }
            while (true)
            {
                string op = Current.Text;
                int precedence = op is "+" or "-" ? 10 : op is "*" or "/" ? 20 : -1;
                if (precedence < minimum) break;
                index++; var right = Expression(precedence + 1);
                left = new DslBinary(op, left, right, new(left.Span.Start, right.Span.Start + right.Span.Length - left.Span.Start));
            }
            depth--; return left;
        }
    }

    private sealed class Lowerer
    {
        private readonly string id;
        private readonly KernelStage stage;
        private readonly GpuLimits limits;
        private readonly DslLoweringOptions options;
        private readonly List<GpuNode> nodes = [];
        private readonly Dictionary<string, GpuNode> locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GpuNode> attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AttributeWrite> writes = new(StringComparer.Ordinal);
        private readonly HashSet<string> reservedIds;
        private int sequence, depth;
        public Lowerer(string id, KernelStage stage, GpuLimits limits, DslLoweringOptions options)
        {
            this.id = id; this.stage = stage; this.limits = limits; this.options = options;
            if (options.NodeIdsBySpan?.Count > limits.MaxNodesPerKernel || options.WriteIdsBySpan?.Count > ParticleAbi.Attributes.Count)
                throw Error("ID mapping limits exceeded.", new(0, 0), id);
            reservedIds = options.NodeIdsBySpan?.Values.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        private GpuNode New(GpuNode node, TextSpan span, bool reuseId = true)
        {
            if (nodes.Count >= limits.MaxNodesPerKernel) throw Error("Lowered node limit exceeded.", span, id);
            string nodeId;
            if (reuseId && options.NodeIdsBySpan is not null && options.NodeIdsBySpan.TryGetValue(span, out var mapped))
            {
                nodeId = mapped;
                if (nodes.Any(n => n.Id == nodeId)) throw Error("Duplicate mapped expression ID.", span, id, nodeId);
            }
            else
            {
                do { nodeId = $"text_{sequence++:D6}"; } while (reservedIds.Contains(nodeId) || nodes.Any(n => n.Id == nodeId));
            }
            node = node with { Id = nodeId, SourceSpan = span }; nodes.Add(node); return node;
        }
        private GpuNode ReadAttribute(string name, TextSpan span)
        {
            if (!ValidLocal(name)) throw Error("Invalid attribute identifier.", span, id);
            if (attributes.TryGetValue(name, out var n) &&
                !(n.Op == GpuOp.ReadAttribute && !writes.ContainsKey(name) &&
                  options.NodeIdsBySpan is not null && options.NodeIdsBySpan.ContainsKey(span)))
                return n;
            var attr = ParticleAbi.Attributes.FirstOrDefault(a => a.Id == name) ?? throw Error($"Unknown attribute '{name}'.", span, id);
            n = New(new() { Op = GpuOp.ReadAttribute, Type = attr.Type, Attribute = name }, span); attributes[name] = n; return n;
        }
        private GpuNode Binary(string op, GpuNode a, GpuNode b, TextSpan span)
        {
            var type = a.Type == b.Type ? a.Type : a.Type == GpuType.Float && GpuOperationRegistry.Float(b.Type) ? b.Type :
                b.Type == GpuType.Float && GpuOperationRegistry.Float(a.Type) ? a.Type : throw Error("Only matching types or float scalar broadcast are supported.", span, id);
            return New(new()
            {
                Op = op switch { "+" => GpuOp.Add, "-" => GpuOp.Sub, "*" => GpuOp.Mul, "/" => GpuOp.Div, _ => throw Error("Unknown binary operator.", span, id) },
                Type = type,
                Inputs = [a.Id, b.Id]
            }, span);
        }
        private int[] Components(string text, GpuType type, TextSpan span, bool writing = false)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 4 || GpuOperationRegistry.Components(type) < 2 || !GpuOperationRegistry.Float(type))
                throw Error("Swizzle requires a float vector and 1–4 components.", span, id);
            var indices = text.Select(c => c switch { 'x' or 'r' => 0, 'y' or 'g' => 1, 'z' or 'b' => 2, 'w' or 'a' => 3, _ => -1 }).ToArray();
            if (indices.Any(i => i < 0 || i >= GpuOperationRegistry.Components(type)) || writing && indices.Distinct().Count() != indices.Length)
                throw Error("Invalid or duplicate write component.", span, id);
            return indices;
        }
        private GpuNode Component(GpuNode n, int index, TextSpan span, bool reuseId = false) => New(new() { Op = GpuOp.Component, Type = GpuType.Float, Inputs = [n.Id], ComponentIndex = index }, span, reuseId);
        private GpuNode Swizzle(GpuNode n, string text, TextSpan span)
        {
            var indices = Components(text, n.Type, span);
            if (indices.Length == 1) return Component(n, indices[0], span, true);
            var values = indices.Select(i => Component(n, i, span)).ToArray();
            return New(new() { Op = GpuOp.Compose, Type = VectorType(indices.Length), Inputs = values.Select(v => v.Id).ToList() }, span);
        }
        private static GpuType VectorType(int count) => count switch { 1 => GpuType.Float, 2 => GpuType.Float2, 3 => GpuType.Float3, 4 => GpuType.Float4, _ => throw new InvalidOperationException() };
        private GpuNode Expression(DslExpression e)
        {
            if (e?.Span is null) throw Error("Null AST expression/span.", new(0, 0), id);
            if (++depth > 64) throw Error("AST expression depth exceeded.", e.Span, id);
            GpuNode result = e switch
            {
                DslLiteral l => New(new() { Op = GpuOp.Constant, Type = l.Type, Value = [l.Value] }, e.Span),
                DslName { IsAttribute: true } n => ReadAttribute(n.Name, e.Span),
                DslName n => Name(n),
                DslBinary b => Binary(b.Operator, Expression(b.Left), Expression(b.Right), e.Span),
                DslUnary u => Unary(u),
                DslCall c => Call(c),
                DslSwizzle s => Swizzle(Expression(s.Value), s.Components, e.Span),
                _ => throw Error("Unsupported AST expression.", e.Span, id)
            };
            depth--; return result;
        }
        private GpuNode Name(DslName n)
        {
            if (!ValidLocal(n.Name)) throw Error("Invalid local/uniform identifier.", n.Span, id);
            if (locals.TryGetValue(n.Name, out var local)) return local;
            string uniform = n.Name == "dt" ? "deltaTime" : n.Name;
            if (!ParticleAbi.Uniforms.TryGetValue(uniform, out var type)) throw Error($"Undefined local/uniform '{n.Name}'.", n.Span, id);
            return New(new() { Op = GpuOp.ReadUniform, Type = type, Uniform = uniform }, n.Span);
        }
        private GpuNode Unary(DslUnary u)
        {
            int start = nodes.Count;
            var n = Expression(u.Value);
            if (u.Operator == "+") return n;
            if (u.Operator != "-" || n.Type is GpuType.Bool or GpuType.UInt) throw Error("Unsupported unary operator/type.", u.Span, id);
            if (n.Op == GpuOp.Constant && nodes.IndexOf(n) >= start)
            {
                // Preserve signed minimum integer literals without first admitting an out-of-range positive value.
                nodes.Remove(n);
                return New(new() { Op = GpuOp.Constant, Type = n.Type, Value = n.Value.Select(v => -v).ToList() }, u.Span);
            }
            var zero = New(new() { Op = GpuOp.Constant, Type = n.Type == GpuType.Int ? GpuType.Int : GpuType.Float, Value = [0] }, u.Span, false);
            return Binary("-", zero, n, u.Span);
        }
        private GpuNode Call(DslCall c)
        {
            if (c.Arguments is null) throw Error("Null function arguments.", c.Span, id);
            if (c.Name == "param")
            {
                if (c.Arguments.Count != 0 || c.Parameter is null || !ParticleAbi.Uniforms.TryGetValue(c.Parameter, out var t))
                    throw Error("Unknown fixed uniform parameter.", c.Span, id);
                return New(new() { Op = GpuOp.ReadUniform, Type = t, Uniform = c.Parameter }, c.Span);
            }
            if (c.Parameter is not null) throw Error("Unexpected function parameter payload.", c.Span, id);
            GpuOp op = c.Name switch
            {
                "sin" => GpuOp.Sin,
                "cos" => GpuOp.Cos,
                "clamp" => GpuOp.Clamp,
                "min" => GpuOp.Min,
                "max" => GpuOp.Max,
                "normalize" => GpuOp.Normalize,
                "length" => GpuOp.Length,
                "select" => GpuOp.Select,
                "random" => GpuOp.Random,
                "float2" or "float3" or "float4" => GpuOp.Compose,
                _ => throw Error($"Unknown safe function '{c.Name}'.", c.Span, id)
            };
            var descriptor = GpuOperationRegistry.Operations[op];
            if (c.Arguments.Count < descriptor.MinInputs || c.Arguments.Count > descriptor.MaxInputs) throw Error("Function arity mismatch.", c.Span, id);
            var constructorType = c.Name == "float2" ? GpuType.Float2 : c.Name == "float3" ? GpuType.Float3 : GpuType.Float4;
            if (op == GpuOp.Compose && c.Arguments.Count == GpuOperationRegistry.Components(constructorType) &&
                c.Arguments.All(a => a is DslLiteral { Type: GpuType.Float } or DslUnary { Operator: "+" or "-", Value: DslLiteral { Type: GpuType.Float } }))
            {
                var values = c.Arguments.Select(a => a is DslLiteral literal ? literal.Value :
                    a is DslUnary { Value: DslLiteral l } unary ? (unary.Operator == "-" ? -l.Value : l.Value) : throw new InvalidOperationException()).ToList();
                return New(new() { Op = GpuOp.Constant, Type = constructorType, Value = values }, c.Span);
            }
            var args = c.Arguments.Select(Expression).ToArray();
            GpuType type = op switch
            {
                GpuOp.Compose => constructorType,
                GpuOp.Length or GpuOp.Random => GpuType.Float,
                GpuOp.Select => args[1].Type,
                _ => args.Select(a => a.Type).OrderByDescending(GpuOperationRegistry.Components).First()
            };
            return New(new() { Op = op, Type = type, Inputs = args.Select(a => a.Id).ToList() }, c.Span);
        }
        public KernelGraph Run(GpuDslAst ast)
        {
            GpuCompiler.Validate(new GpuProgram(), limits);
            if (ast?.Statements is null || ast.Statements.Count > limits.MaxNodesPerKernel * 2L)
                throw Error("Null AST or statement limit exceeded.", new(0, 0), id);
            foreach (var statement in ast.Statements)
            {
                if (statement?.Span is null) throw Error("Null AST statement/span.", new(0, 0), id);
                if (statement is DslLet l)
                {
                    if (!ValidLocal(l.Name) || l.Name is "dt" or "true" or "false" or "let" ||
                        ParticleAbi.Uniforms.ContainsKey(l.Name) || !locals.TryAdd(l.Name, Expression(l.Value)))
                        throw Error("Local is duplicate or reserved.", l.Span, id);
                }
                else if (statement is DslAssign a)
                {
                    var abi = ParticleAbi.Attributes.FirstOrDefault(x => x.Id == a.Attribute) ?? throw Error("Unknown write attribute.", a.Span, id);
                    var value = Expression(a.Value);
                    if (a.Components is not null)
                    {
                        var original = ReadAttribute(a.Attribute, a.Span);
                        var indices = Components(a.Components, abi.Type, a.Span, true);
                        if (a.Operator != "=") value = Binary(a.Operator[..1], Swizzle(original, a.Components, a.Span), value, a.Span);
                        if (value.Type != VectorType(indices.Length)) throw Error("Component write type mismatch.", a.Span, id);
                        var components = new List<string>();
                        for (int i = 0; i < GpuOperationRegistry.Components(abi.Type); i++)
                        {
                            int replacement = Array.IndexOf(indices, i);
                            components.Add(replacement < 0 ? Component(original, i, a.Span).Id : indices.Length == 1 ? value.Id : Component(value, replacement, a.Span).Id);
                        }
                        value = New(new() { Op = GpuOp.Compose, Type = abi.Type, Inputs = components }, a.Span);
                    }
                    else if (a.Operator != "=") value = Binary(a.Operator[..1], ReadAttribute(a.Attribute, a.Span), value, a.Span);
                    if (value.Type != abi.Type) throw Error("Attribute write requires an exact type.", a.Span, id, value.Id);
                    attributes[a.Attribute] = value;
                    var writeId = options.WriteIdsBySpan is not null && options.WriteIdsBySpan.TryGetValue(a.Span, out var mapped) ? mapped : $"write_{a.Attribute}";
                    writes[a.Attribute] = new(writeId, a.Attribute, value.Id, a.Span);
                }
                else throw Error("Unsupported AST statement.", statement?.Span ?? new(0, 0), id);
            }
            var k = new KernelGraph { Id = id, Stage = stage, Nodes = nodes, Writes = writes.Values.OrderBy(w => w.Attribute, StringComparer.Ordinal).ToList() };
            var p = new GpuProgram { Id = "__dsl_program", Kernels = [k], Flow = [new() { Id = "__dsl_flow", Kind = stage == KernelStage.Spawn ? FlowKind.Spawn : FlowKind.Update, Kernel = id }] };
            var diagnostics = GpuCompiler.Validate(p, limits);
            if (diagnostics.Count != 0) throw new GpuValidationException(diagnostics);
            return k;
        }
        private static bool ValidLocal(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 128 &&
            (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    public static DslSource Emit(KernelGraph kernel, GpuLimits? limits = null)
    {
        var p = new GpuProgram { Id = "__dsl_program", Kernels = [kernel], Flow = [new() { Id = "__dsl_flow", Kind = kernel.Stage == KernelStage.Spawn ? FlowKind.Spawn : FlowKind.Update, Kernel = kernel.Id }] };
        var errors = GpuCompiler.Validate(p, limits);
        if (errors.Count > 0) throw new GpuValidationException(errors);
        var order = GpuCompiler.Topological(kernel.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal));
        var names = order.Select((n, i) => (n.Id, Name: $"r{i:D4}")).ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
        var text = new StringBuilder();
        var nodeMap = new Dictionary<TextSpan, string>();
        var writeMap = new Dictionary<TextSpan, string>();
        foreach (var n in order)
        {
            string[] args = n.Inputs.Select(i => names[i]).ToArray();
            string expression = n.Op switch
            {
                GpuOp.Constant => DslLiteralText(n),
                GpuOp.ReadAttribute => $"@{n.Attribute}",
                GpuOp.ReadUniform => $"param(\"{n.Uniform}\")",
                GpuOp.Add => $"{args[0]} + {args[1]}",
                GpuOp.Sub => $"{args[0]} - {args[1]}",
                GpuOp.Mul => $"{args[0]} * {args[1]}",
                GpuOp.Div => $"{args[0]} / {args[1]}",
                GpuOp.Component => $"{args[0]}.{"xyzw"[n.ComponentIndex!.Value]}",
                _ => $"{(n.Op == GpuOp.Compose ? ShaderEmitter.HlslType(n.Type) : n.Op.ToString().ToLowerInvariant())}({string.Join(", ", args)})"
            };
            text.Append("let ").Append(names[n.Id]).Append(" = ");
            nodeMap.Add(new(text.Length, expression.Length), n.Id);
            text.Append(expression).Append(";\n");
        }
        foreach (var w in kernel.Writes.OrderBy(w => w.Attribute, StringComparer.Ordinal))
        {
            int start = text.Length;
            text.Append('@').Append(w.Attribute).Append(" = ").Append(names[w.Value]).Append(';');
            writeMap.Add(new(start, text.Length - start), w.Id); text.Append('\n');
        }
        return new(text.ToString(), nodeMap, writeMap);
    }
    private static string DslLiteralText(GpuNode n)
    {
        string Literal(double value) => n.Type switch
        {
            GpuType.Bool => value == 0 ? "false" : "true",
            GpuType.Int => value.ToString("0", CultureInfo.InvariantCulture) + "i",
            GpuType.UInt => value.ToString("0", CultureInfo.InvariantCulture) + "u",
            _ => ((float)value).ToString("R", CultureInfo.InvariantCulture)
        };
        return n.Value.Count == 1 ? Literal(n.Value[0]) : $"{ShaderEmitter.HlslType(n.Type)}({string.Join(", ", n.Value.Select(Literal))})";
    }
}
