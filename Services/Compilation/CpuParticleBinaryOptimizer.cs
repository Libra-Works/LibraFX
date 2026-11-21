namespace LiberaFx.Services.Compilation;

/// <summary>Offline CPU-kernel optimization of an already compiled effect package.</summary>
public static class CpuParticleBinaryOptimizer
{
    public static byte[] OptimizePackage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var package = SpawnableGroupPackageBinary.Deserialize(bytes);
        var root = OptimizePayload(package.CpuPrograms);
        var templates = package.Templates.Select(template => template with
            { CpuPrograms = OptimizePayload(template.CpuPrograms) }).ToArray();
        if (root.AsSpan().SequenceEqual(package.CpuPrograms) && templates.Zip(package.Templates)
            .All(pair => pair.First.CpuPrograms.AsSpan().SequenceEqual(pair.Second.CpuPrograms))) return bytes;
        return SpawnableGroupPackageBinary.Serialize(package with { CpuPrograms = root, Templates = templates });
    }

    private static byte[] OptimizePayload(byte[] bytes)
    {
        if (bytes.Length == 0) return bytes;
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        byte[] Read(int count)
        {
            if (count < 0 || count > input.Length - input.Position)
                throw new InvalidDataException("Truncated CPU particle payload.");
            return reader.ReadBytes(count);
        }
        int Integer()
        {
            if (input.Length - input.Position < 4) throw new InvalidDataException("Truncated CPU particle count.");
            return reader.ReadInt32();
        }
        byte[] Blob(int maximum)
        {
            var count = Integer();
            if (count < 0 || count > maximum) throw new InvalidDataException("Invalid CPU particle blob length.");
            return Read(count);
        }
        void WriteBlob(byte[] blob) { writer.Write(blob.Length); writer.Write(blob); }
        var version = Integer();
        if (version is not (1 or 2)) return bytes;
        var count = Integer();
        if (count < 0 || count > 1024) throw new InvalidDataException("Invalid CPU system count.");
        writer.Write(version); writer.Write(count);
        for (var system = 0; system < count; system++)
        {
            writer.Write(Read(60)); // ID, capacity, seed, queue, rate, burst, emitter source/component.
            for (var stage = 0; stage < 2; stage++)
            {
                writer.Write(Read(16)); // Entry UUID remains stable.
                var code = Blob(64 * 1024 * 1024);
                var ioCount = Integer();
                if (ioCount < 0 || ioCount > 256) throw new InvalidDataException("Invalid CPU IO count.");
                var io = Read(ioCount * 41);
                WriteBlob(ioCount == 0 ? VfxProgram.OptimizeCpuKernelBinary(code) : code);
                writer.Write(ioCount); writer.Write(io);
            }
            if (version == 2) WriteBlob(Blob(4096));
        }
        if (input.Position != input.Length) throw new InvalidDataException("Unexpected CPU particle payload tail.");
        return output.ToArray();
    }
}
