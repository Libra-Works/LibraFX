using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class Int32CompilationTests
{
    public static void Run()
    {
        var int32 = Definition("Int32");
        var operations = new[] { "Int32Add", "Int32Subtract", "Int32Multiply", "Int32Divide" };
        var fixtureDirectory = Environment.GetEnvironmentVariable("LIBERAFX_INT32_FIXTURE_DIR");
        if (!string.IsNullOrWhiteSpace(fixtureDirectory))
            Directory.CreateDirectory(fixtureDirectory);

        foreach (var kind in operations)
        {
            var left = Node(int32,
                new NodeParameterSnapshot("value", "int", JsonSerializer.SerializeToElement(14)));
            var right = Node(int32,
                new NodeParameterSnapshot("value", "int", JsonSerializer.SerializeToElement(3)));
            var operation = Node(Definition(kind));
            var program = VfxProgram.Compile(new GraphSnapshot([left, right, operation],
            [
                new(left.Id, operation.Id, "a"),
                new(right.Id, operation.Id, "b")
            ]));
            var bytes = program.ToBinary();
            VerifyInt32TypeTable(bytes, GraphValueType.Int32, GraphValueType.Int32, GraphValueType.Int32);
            Require(program.BinaryVersion == 10,
                $"{kind} must use the explicitly typed bytecode version.");
            if (!string.IsNullOrWhiteSpace(fixtureDirectory))
                File.WriteAllBytes(Path.Combine(fixtureDirectory, $"{kind}.lfxb"), bytes);
        }

        var floatNode = Node(Definition("Float"),
            new NodeParameterSnapshot("value", "float", JsonSerializer.SerializeToElement(3.9)));
        var toInt = Node(Definition("FloatToInt32"));
        var toFloat = Node(Definition("Int32ToFloat"));
        var conversion = VfxProgram.Compile(new GraphSnapshot([floatNode, toInt, toFloat],
        [
            new(floatNode.Id, toInt.Id, "a"),
            new(toInt.Id, toFloat.Id, "a")
        ]));
        VerifyInt32TypeTable(conversion.ToBinary(),
            GraphValueType.Float, GraphValueType.Int32, GraphValueType.Float);
        if (!string.IsNullOrWhiteSpace(fixtureDirectory))
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "Conversions.lfxb"), conversion.ToBinary());

        var absolute = Node(GraphNodeDefinition.Core.Single(item => item.Name == "Abs"));
        var mixed = VfxProgram.Compile(new GraphSnapshot([floatNode, toInt, toFloat, absolute],
        [
            new(floatNode.Id, toInt.Id, "a"),
            new(toInt.Id, toFloat.Id, "a"),
            new(toFloat.Id, absolute.Id, "a")
        ]));
        VerifyInt32TypeTable(mixed.ToBinary(),
            GraphValueType.Float, GraphValueType.Int32, GraphValueType.Float, GraphValueType.Float);
        if (!string.IsNullOrWhiteSpace(fixtureDirectory))
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "Mixed.lfxb"), mixed.ToBinary());

        var groupInput = Node(Definition("GroupInputInt32"));
        var groupOutput = Node(Definition("GroupOutputInt32"));
        var groupDefinition = Definition("GraphGroup");
        var grouped = new GraphNodeSnapshot(Guid.NewGuid(), groupDefinition.Kind, "Int32 boundary",
            0, 0, groupDefinition.PortType, groupDefinition.PortCategory,
            ChildGraph: new GraphSnapshot([groupInput, groupOutput],
                [new(groupInput.Id, groupOutput.Id)]));
        var groupProgram = VfxProgram.Compile(new GraphSnapshot([grouped], []));
        VerifyInt32TypeTable(groupProgram.ToBinary(), GraphValueType.Int32, GraphValueType.Int32);

        var badTarget = Node(Definition("Int32Add"));
        var mismatched = new GraphSnapshot([floatNode, badTarget], [new(floatNode.Id, badTarget.Id, "a")]);
        try
        {
            VfxProgram.Compile(mismatched);
            throw new InvalidOperationException("Float was accepted as an Int32 input.");
        }
        catch (InvalidDataException) { }

        var invalidLiteral = Node(int32,
            new NodeParameterSnapshot("value", "int", JsonSerializer.SerializeToElement(1.5)));
        try
        {
            VfxProgram.Compile(new GraphSnapshot([invalidLiteral], []));
            throw new InvalidOperationException("A fractional Int32 literal was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static void VerifyInt32TypeTable(byte[] bytes, params GraphValueType[] expectedTypes)
    {
        var instructionCount = expectedTypes.Length;
        var tableSize = 8 + instructionCount * 8;
        var offset = bytes.Length - tableSize;
        Require(offset >= 16 && bytes.AsSpan(offset, 4).SequenceEqual("LFTY"u8),
            "The compiler must append the v10 value-type table.");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)) == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6)) == 8,
            "The value-type table schema must be explicit.");
        Require(Enumerable.Range(0, instructionCount).All(index =>
                bytes[offset + 8 + index * 8] == (byte)expectedTypes[index]),
            "Compiler output and group boundary lowering must preserve exact semantic type tags.");
    }

    private static GraphNodeDefinition Definition(string kind) =>
        GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);

    private static GraphNodeSnapshot Node(GraphNodeDefinition definition, params NodeParameterSnapshot[] parameters) =>
        new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0,
            definition.PortType, definition.PortCategory, Parameters: parameters);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
