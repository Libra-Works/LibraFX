using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public static class SpawnableGroupPackageBinary
{
    private const ushort FormatVersion = 3;
    private const ushort HeaderSize = 24;
    private const int MaximumPackageBytes = 512 * 1024 * 1024;
    private const int MaximumBlobBytes = 256 * 1024 * 1024;
    private const int MaximumMetadataItems = 100_000;
    private static readonly byte[] Magic = "LFXP"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] EmptyGpuPrograms = "{\"version\":1,\"programs\":[]}"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] Serialize(SpawnableGroupPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Version is not (SpawnableGroupPackage.FormatVersion or 12 or 13))
            throw new InvalidDataException("Unsupported Spawnable package version.");
        ValidateHierarchy(package.Templates);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(package.Version == 13 ? (ushort)5 :
            package.CpuPrograms.Length == 0 ? FormatVersion : (ushort)4);
        writer.Write(HeaderSize);
        writer.Write(0L);
        writer.Write(package.Version);
        writer.Write(0u);

        writer.Write(package.AutoDestroyWhenIdle);
        writer.Write(package.HasDeferredSignals);
        writer.Write(package.HasChildDeferredSignals);
        WriteOptionalGuid(writer, package.OnStartNodeId);
        WriteOptionalGuid(writer, package.OnDestroyNodeId);
        WriteGameIoPorts(writer, package.GameIoPorts);
        WriteBlob(writer, package.ParentBytecode);

        WriteCount(writer, package.Templates.Count);
        foreach (var template in package.Templates)
            WriteTemplate(writer, template);

        WriteCount(writer, package.DestroyRoutes.Count);
        foreach (var route in package.DestroyRoutes)
        {
            WriteGuid(writer, route.SourceContainerId);
            WriteGuid(writer, route.TargetContainerId);
            WriteOptionalGuid(writer, route.DelayNodeId);
            WriteCaptures(writer, route.Captures);
        }

        WriteCount(writer, package.SignalRoutes.Count);
        foreach (var route in package.SignalRoutes)
        {
            WriteGuid(writer, route.SourceContainerId);
            WriteGuid(writer, route.SourceNodeId);
            WriteGuid(writer, route.TargetNodeId);
        }
        WriteBlob(writer, package.RootRenderConfiguration);
        WriteBlob(writer, JsonSerializer.SerializeToUtf8Bytes(package.RootGpuContainers, JsonOptions));
        WriteGpuPrograms(writer, package.RootGpuPrograms);
        foreach (var template in package.Templates)
            WriteGpuPrograms(writer, template.GpuPrograms);
        if (package.Version >= 12)
        {
            WriteBlob(writer, package.CpuPrograms);
        }
        if (package.Version == 13)
        {
            foreach (var template in package.Templates)
            {
                WriteOptionalGuid(writer, template.ParentContainerId);
                WriteBlob(writer, template.CpuPrograms);
            }
        }

        writer.Flush();
        if (stream.Length > MaximumPackageBytes)
            throw new InvalidDataException("Spawnable binary package exceeds the size limit.");
        stream.Position = 8;
        writer.Write(stream.Length);
        writer.Flush();
        return stream.ToArray();
    }

    public static SpawnableGroupPackage Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || bytes.Length > MaximumPackageBytes)
            throw new InvalidDataException("Invalid Spawnable binary package size.");
        if (!bytes[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid Spawnable binary package signature.");
        try { return DeserializePayload(bytes); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated Spawnable binary package.", ex); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid GPU container metadata.", ex); }
    }

    private static SpawnableGroupPackage DeserializePayload(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        _ = reader.ReadBytes(4);
        var formatVersion = reader.ReadUInt16();
        if (formatVersion is not (1 or 2 or FormatVersion or 4 or 5) || reader.ReadUInt16() != HeaderSize)
            throw new InvalidDataException("Unsupported Spawnable binary package format.");
        if (reader.ReadInt64() != bytes.Length)
            throw new InvalidDataException("Spawnable binary package length mismatch.");
        var packageVersion = reader.ReadInt32();
        if ((formatVersion == 1 ? packageVersion != 9 :
             formatVersion == 2 ? packageVersion != 10 :
             formatVersion == 4 ? packageVersion != 12 :
             formatVersion == 5 ? packageVersion != 13 :
             packageVersion != SpawnableGroupPackage.FormatVersion) ||
            reader.ReadUInt32() != 0)
            throw new InvalidDataException("Unsupported or malformed Spawnable package metadata.");

        var autoDestroyWhenIdle = ReadBoolean(reader);
        var hasDeferredSignals = ReadBoolean(reader);
        var hasChildDeferredSignals = ReadBoolean(reader);
        var onStartNodeId = ReadOptionalGuid(reader);
        var onDestroyNodeId = ReadOptionalGuid(reader);
        var gameIoPorts = ReadGameIoPorts(reader);
        var parentBytecode = ReadBlob(reader);

        var templates = new SpawnableTemplate[ReadCount(reader)];
        for (var index = 0; index < templates.Length; index++)
            templates[index] = ReadTemplate(reader);

        var destroyRoutes = new SpawnableDestroyRoute[ReadCount(reader)];
        for (var index = 0; index < destroyRoutes.Length; index++)
        {
            var sourceContainerId = ReadGuid(reader);
            var targetContainerId = ReadGuid(reader);
            var delayNodeId = ReadOptionalGuid(reader);
            destroyRoutes[index] = new(sourceContainerId, targetContainerId, ReadCaptures(reader), delayNodeId);
        }

        var signalRoutes = new SpawnableSignalRoute[ReadCount(reader)];
        for (var index = 0; index < signalRoutes.Length; index++)
            signalRoutes[index] = new(ReadGuid(reader), ReadGuid(reader), ReadGuid(reader));

        var rootRenderConfiguration = formatVersion == 1 ? [] : ReadBlob(reader);
        var rootGpuContainers = formatVersion == 1 ? [] :
            JsonSerializer.Deserialize<GraphNodeSnapshot[]>(ReadBlob(reader), JsonOptions)
                ?? throw new InvalidDataException("Invalid root GPU container metadata.");
        var rootGpuPrograms = formatVersion < 3 ? [] : ReadGpuPrograms(reader);
        if (formatVersion >= 3)
            for (var index = 0; index < templates.Length; index++)
                templates[index] = templates[index] with { GpuPrograms = ReadGpuPrograms(reader) };
        var cpuPrograms = formatVersion >= 4 ? ReadBlob(reader) : [];
        if (formatVersion == 5)
        {
            for (var index = 0; index < templates.Length; index++)
            {
                var parentContainerId = ReadOptionalGuid(reader);
                var templateCpuPrograms = ReadBlob(reader);
                templates[index] = templates[index] with
                {
                    ParentContainerId = parentContainerId,
                    CpuPrograms = templateCpuPrograms
                };
            }
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing data in Spawnable binary package.");
        ValidateHierarchy(templates);
        return new(parentBytecode, Array.AsReadOnly(templates))
        {
            AutoDestroyWhenIdle = autoDestroyWhenIdle,
            HasDeferredSignals = hasDeferredSignals,
            HasChildDeferredSignals = hasChildDeferredSignals,
            OnStartNodeId = onStartNodeId,
            OnDestroyNodeId = onDestroyNodeId,
            GameIoPorts = Array.AsReadOnly(gameIoPorts),
            DestroyRoutes = Array.AsReadOnly(destroyRoutes),
            SignalRoutes = Array.AsReadOnly(signalRoutes),
            RootRenderConfiguration = rootRenderConfiguration,
            RootGpuContainers = Array.AsReadOnly(rootGpuContainers),
            RootGpuPrograms = rootGpuPrograms,
            CpuPrograms = cpuPrograms
        };
    }

    private static void WriteTemplate(BinaryWriter writer, SpawnableTemplate template)
    {
        WriteGuid(writer, template.ContainerId);
        WriteGuid(writer, template.SignalTypeId);
        WriteBlob(writer, template.Bytecode);
        WriteBlob(writer, template.RenderConfiguration);
        WriteBlob(writer, JsonSerializer.SerializeToUtf8Bytes(template.GpuContainers, JsonOptions));

        WriteCount(writer, template.Inputs.Count);
        foreach (var input in template.Inputs)
        {
            if (input.Width is < 1 or > 4)
                throw new InvalidDataException("Invalid Spawnable input width.");
            WriteString(writer, input.PortId);
            WriteGuid(writer, input.TemplateNodeId);
            WriteString(writer, input.PortType);
            writer.Write(input.Width);
            writer.Write(input.IsColor);
            writer.Write(input.IsLive);
        }

        WriteOptionalRecords(writer, template.Outputs, (output) =>
        {
            WriteString(writer, output.PortId);
            WriteGuid(writer, output.TemplateNodeId);
            WriteString(writer, output.PortType);
        });
        WriteOptionalRecords(writer, template.LiveInputs, capture => WriteCapture(writer, capture));
        WriteOptionalRecords(writer, template.CapturedInputs, capture => WriteCapture(writer, capture));
        WriteOptionalRecords(writer, template.SignalInputs, signal => WriteSignalPort(writer, signal));
        WriteOptionalRecords(writer, template.SignalOutputs, signal => WriteSignalPort(writer, signal));

        writer.Write(template.Lifecycle is not null);
        if (template.Lifecycle is { } lifecycle)
        {
            WriteOptionalGuid(writer, lifecycle.OnStartNodeId);
            WriteOptionalGuid(writer, lifecycle.LifeNodeId);
            WriteOptionalGuid(writer, lifecycle.OwnerDestroyNodeId);
        }
    }

    private static SpawnableTemplate ReadTemplate(BinaryReader reader)
    {
        var containerId = ReadGuid(reader);
        var signalTypeId = ReadGuid(reader);
        var bytecode = ReadBlob(reader);
        var renderConfiguration = ReadBlob(reader);
        var gpuJson = ReadBlob(reader);
        var gpuContainers = JsonSerializer.Deserialize<GraphNodeSnapshot[]>(gpuJson, JsonOptions)
            ?? throw new InvalidDataException("Invalid GPU container metadata.");

        var inputs = new SpawnableInput[ReadCount(reader)];
        for (var index = 0; index < inputs.Length; index++)
        {
            var portId = ReadString(reader);
            var templateNodeId = ReadGuid(reader);
            var portType = ReadString(reader);
            var width = reader.ReadInt32();
            if (width is < 1 or > 4) throw new InvalidDataException("Invalid Spawnable input width.");
            inputs[index] = new(portId, templateNodeId, portType, width, ReadBoolean(reader), ReadBoolean(reader));
        }

        var outputs = ReadOptionalRecords(reader, () =>
            new SpawnableOutput(ReadString(reader), ReadGuid(reader), ReadString(reader)));
        var liveInputs = ReadOptionalRecords(reader, () => ReadCapture(reader));
        var capturedInputs = ReadOptionalRecords(reader, () => ReadCapture(reader));
        var signalInputs = ReadOptionalRecords(reader, () => ReadSignalPort(reader));
        var signalOutputs = ReadOptionalRecords(reader, () => ReadSignalPort(reader));
        SpawnableLifecycle? lifecycle = null;
        if (ReadBoolean(reader))
            lifecycle = new(ReadOptionalGuid(reader), ReadOptionalGuid(reader), ReadOptionalGuid(reader));

        return new(containerId, signalTypeId, bytecode, Array.AsReadOnly(inputs), renderConfiguration,
            Array.AsReadOnly(gpuContainers), lifecycle, outputs, liveInputs, capturedInputs, signalInputs, signalOutputs);
    }

    private static void ValidateHierarchy(IReadOnlyList<SpawnableTemplate> templates)
    {
        var byId = new Dictionary<Guid, SpawnableTemplate>();
        foreach (var template in templates)
        {
            if (template.ContainerId == Guid.Empty || !byId.TryAdd(template.ContainerId, template))
                throw new InvalidDataException("Spawnable template IDs must be nonempty and unique.");
        }
        foreach (var template in templates)
        {
            if (template.ParentContainerId is not { } parentId) continue;
            if (parentId == template.ContainerId || !byId.ContainsKey(parentId))
                throw new InvalidDataException("A Spawnable template has an invalid parent container reference.");
        }

        foreach (var template in templates)
        {
            var visiting = new HashSet<Guid>();
            var cursor = template.ContainerId;
            var depth = 0;
            while (true)
            {
                if (!visiting.Add(cursor))
                    throw new InvalidDataException("Spawnable template ownership contains a cycle.");
                if (++depth > 12)
                    throw new InvalidDataException("The Spawnable template hierarchy is too deep.");
                if (byId[cursor].ParentContainerId is not { } parent) break;
                cursor = parent;
            }
        }
    }

    private static void WriteGameIoPorts(BinaryWriter writer, IReadOnlyList<GameIoPort> ports)
    {
        WriteCount(writer, ports.Count);
        foreach (var port in ports)
        {
            if ((byte)port.Kind > (byte)GameIoPortKind.OwnerMatrix || port.Width is < 0 or > 16)
                throw new InvalidDataException("Invalid Game I/O descriptor.");
            WriteGuid(writer, port.NodeId);
            WriteString(writer, port.Name);
            writer.Write((byte)port.Kind);
            writer.Write(port.Width);
            writer.Write(port.IsColor);
            if (port.RuntimeNodeIds is null) writer.Write(-1);
            else
            {
                WriteCount(writer, port.RuntimeNodeIds.Count);
                foreach (var nodeId in port.RuntimeNodeIds) WriteGuid(writer, nodeId);
            }
        }
    }

    private static GameIoPort[] ReadGameIoPorts(BinaryReader reader)
    {
        var ports = new GameIoPort[ReadCount(reader)];
        for (var index = 0; index < ports.Length; index++)
        {
            var nodeId = ReadGuid(reader);
            var name = ReadString(reader);
            var kind = reader.ReadByte();
            if (kind > (byte)GameIoPortKind.OwnerMatrix) throw new InvalidDataException("Invalid Game I/O port kind.");
            var width = reader.ReadInt32();
            if (width is < 0 or > 16) throw new InvalidDataException("Invalid Game I/O port width.");
            var isColor = ReadBoolean(reader);
            var runtimeCount = reader.ReadInt32();
            if (runtimeCount < -1 || runtimeCount > MaximumMetadataItems)
                throw new InvalidDataException("Invalid Game I/O runtime node count.");
            Guid[]? runtimeNodeIds = null;
            if (runtimeCount >= 0)
            {
                runtimeNodeIds = new Guid[runtimeCount];
                for (var node = 0; node < runtimeCount; node++) runtimeNodeIds[node] = ReadGuid(reader);
            }
            ports[index] = new(nodeId, name, (GameIoPortKind)kind, width, isColor,
                runtimeNodeIds is null ? null : Array.AsReadOnly(runtimeNodeIds));
        }
        return ports;
    }

    private static void WriteCaptures(BinaryWriter writer, IReadOnlyList<SpawnableRouteCapture> captures)
    {
        WriteCount(writer, captures.Count);
        foreach (var capture in captures) WriteCapture(writer, capture);
    }

    private static SpawnableRouteCapture[] ReadCaptures(BinaryReader reader)
    {
        var captures = new SpawnableRouteCapture[ReadCount(reader)];
        for (var index = 0; index < captures.Length; index++) captures[index] = ReadCapture(reader);
        return captures;
    }

    private static void WriteCapture(BinaryWriter writer, SpawnableRouteCapture capture)
    {
        if (capture.Width is < 1 or > 4 || capture.SourceComponent is < 0 or > 3 ||
            capture.SourceComponent + capture.Width > 4)
            throw new InvalidDataException("Invalid Destroy capture mapping.");
        WriteGuid(writer, capture.TargetNodeId);
        writer.Write(capture.Width);
        WriteOptionalGuid(writer, capture.SourceContainerId);
        WriteGuid(writer, capture.SourceNodeId);
        writer.Write(capture.SourceComponent);
    }

    private static SpawnableRouteCapture ReadCapture(BinaryReader reader)
    {
        var target = ReadGuid(reader);
        var width = reader.ReadInt32();
        if (width is < 1 or > 4) throw new InvalidDataException("Invalid Destroy capture width.");
        var sourceContainer = ReadOptionalGuid(reader);
        var sourceNode = ReadGuid(reader);
        var component = reader.ReadInt32();
        if (component is < 0 or > 3 || component + width > 4)
            throw new InvalidDataException("Invalid Destroy capture component.");
        return new(target, width, sourceContainer, sourceNode, component);
    }

    private static void WriteSignalPort(BinaryWriter writer, SpawnableSignalPort signal)
    {
        WriteString(writer, signal.PortId);
        WriteGuid(writer, signal.TemplateNodeId);
        WriteGuid(writer, signal.ProxyNodeId);
    }

    private static SpawnableSignalPort ReadSignalPort(BinaryReader reader) =>
        new(ReadString(reader), ReadGuid(reader), ReadGuid(reader));

    private static void WriteOptionalRecords<T>(BinaryWriter writer, IReadOnlyList<T>? records, Action<T> write)
    {
        writer.Write(records is not null);
        if (records is null) return;
        WriteCount(writer, records.Count);
        foreach (var record in records) write(record);
    }

    private static IReadOnlyList<T>? ReadOptionalRecords<T>(BinaryReader reader, Func<T> read)
    {
        if (!ReadBoolean(reader)) return null;
        var records = new T[ReadCount(reader)];
        for (var index = 0; index < records.Length; index++) records[index] = read();
        return Array.AsReadOnly(records);
    }

    private static void WriteBlob(BinaryWriter writer, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBlobBytes || writer.BaseStream.Position > MaximumPackageBytes - bytes.Length - sizeof(int))
            throw new InvalidDataException("Spawnable binary package blob exceeds the size limit.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteGpuPrograms(BinaryWriter writer, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try { _ = StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid UTF-8 in GPU executable payload.", ex);
        }
        WriteBlob(writer, bytes.Length == 0 ? EmptyGpuPrograms : bytes);
    }

    private static byte[] ReadGpuPrograms(BinaryReader reader)
    {
        var bytes = ReadBlob(reader);
        try { _ = StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid UTF-8 in GPU executable payload.", ex);
        }
        return bytes;
    }

    private static byte[] ReadBlob(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumBlobBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid Spawnable binary package blob length.");
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new InvalidDataException("Truncated Spawnable binary package.");
        return bytes;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteBlob(writer, bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var bytes = ReadBlob(reader);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 in Spawnable binary package.", ex); }
    }

    private static void WriteOptionalGuid(BinaryWriter writer, Guid? value)
    {
        writer.Write(value.HasValue);
        if (value is { } id) WriteGuid(writer, id);
    }

    private static Guid? ReadOptionalGuid(BinaryReader reader) => ReadBoolean(reader) ? ReadGuid(reader) : null;

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray(bigEndian: true));

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new InvalidDataException("Truncated GUID in Spawnable binary package.");
        return new Guid(bytes, bigEndian: true);
    }

    private static void WriteCount(BinaryWriter writer, int count)
    {
        if (count < 0 || count > MaximumMetadataItems)
            throw new InvalidDataException("Spawnable binary package item count exceeds the limit.");
        writer.Write(count);
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumMetadataItems)
            throw new InvalidDataException("Invalid Spawnable binary package item count.");
        return count;
    }

    private static bool ReadBoolean(BinaryReader reader)
    {
        var value = reader.ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid boolean in Spawnable binary package.")
        };
    }
}
