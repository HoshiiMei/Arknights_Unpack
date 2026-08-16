using System.Buffers.Binary;
using System.Text;
using ArkBrowser.Core.Decryption;

namespace ArkBrowser.Core.Serialization;

public sealed class SerializedFileHeaderInfo
{
    public int MetadataSize { get; init; }
    public long FileSize { get; init; }
    public int Generation { get; init; }
    public long DataOffset { get; init; }
    public bool Endianness { get; init; }
    public string UnityVersion { get; init; } = string.Empty;
    public uint TargetPlatform { get; init; }
    public bool EnableTypeTree { get; init; }
}

public sealed class SerializedTypeInfo
{
    public TypeTreeInfo? TypeTree { get; init; }
    public int TypeId { get; init; }
    public bool IsStripped { get; init; }
    public short ScriptTypeIndex { get; init; }
    public byte[]? ScriptId { get; init; }
    public byte[] OldTypeHash { get; init; } = [];
}

public sealed class TypeTreeInfo
{
    public IReadOnlyList<TypeTreeNode> Nodes { get; init; } = [];
    public IReadOnlyDictionary<uint, string> CustomStrings { get; init; } = new Dictionary<uint, string>();

    public bool TryGetString(uint offset, out string value)
    {
        return CustomStrings.TryGetValue(offset, out value!);
    }
}

public sealed class TypeTreeNode
{
    public int Version { get; init; }
    public byte Level { get; init; }
    public byte TypeFlags { get; init; }
    public uint TypeStrOffset { get; init; }
    public uint NameStrOffset { get; init; }
    public int ByteSize { get; init; }
    public int Index { get; init; }
    public uint MetaFlag { get; init; }
    public ulong RefTypeHash { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool HasBuiltInTypeName { get; init; }
    public bool HasBuiltInName { get; init; }
}

public sealed class SerializedObjectInfo
{
    public long PathId { get; init; }
    public long ByteStart { get; init; }
    public int ByteSize { get; init; }
    public int SerializedTypeIndex { get; init; }
    public int TypeId { get; init; }
    public short ScriptTypeIndex { get; init; }
    public bool IsStripped { get; init; }
}

public sealed class SerializedFileInfo
{
    public SerializedFileHeaderInfo Header { get; init; } = new();
    public IReadOnlyList<SerializedTypeInfo> Types { get; init; } = [];
    public IReadOnlyList<SerializedObjectInfo> Objects { get; init; } = [];
}

public static class SerializedFileParser
{
    private const int SupportedGeneration = 22;
    private const int TypeTreeNodeSize = 32;
    private const int Hash128Size = 16;

    public static SerializedFileInfo Parse(Stream entryStream)
    {
        ArgumentNullException.ThrowIfNull(entryStream);
        if (!entryStream.CanRead || !entryStream.CanSeek)
        {
            throw new ArgumentException("The serialized file stream must be readable and seekable.", nameof(entryStream));
        }

        entryStream.Position = 0;

        SerializedFileHeaderInfo header = ReadHeader(entryStream);
        if (header.Generation != SupportedGeneration)
        {
            throw new NotSupportedException($"Only serialized file generation {SupportedGeneration} is currently supported.");
        }

        if (header.MetadataSize <= 0 || header.MetadataSize > entryStream.Length)
        {
            throw new InvalidDataException("Invalid serialized file metadata size.");
        }

        byte[] metadata = new byte[header.MetadataSize];
        entryStream.ReadExactly(metadata);

        return ParseMetadata(metadata, header, entryStream.Length);
    }

    public static Stream OpenObject(Stream entryStream, SerializedObjectInfo objectInfo)
    {
        ArgumentNullException.ThrowIfNull(entryStream);
        ArgumentNullException.ThrowIfNull(objectInfo);

        return UnityFsReader.CreateReadOnlySlice(entryStream, objectInfo.ByteStart, objectInfo.ByteSize, leaveOpen: true);
    }

    private static SerializedFileHeaderInfo ReadHeader(Stream stream)
    {
        _ = ReadInt32BigEndian(stream);
        _ = ReadUInt32BigEndian(stream);
        int generation = ReadInt32BigEndian(stream);
        _ = ReadUInt32BigEndian(stream);

        bool endianness = false;
        if (generation >= 9)
        {
            int endiannessByte = stream.ReadByte();
            if (endiannessByte < 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading endianness.");
            }

            endianness = endiannessByte != 0;
            AlignStream(stream, 4);
        }

        if (generation < 22)
        {
            throw new NotSupportedException($"Serialized file generation {generation} is not supported yet.");
        }

        int metadataSize = ReadInt32BigEndian(stream);
        long fileSize = ReadInt64BigEndian(stream);
        long dataOffset = ReadInt64BigEndian(stream);
        Span<byte> unknown = stackalloc byte[sizeof(long)];
        stream.ReadExactly(unknown);

        return new SerializedFileHeaderInfo
        {
            MetadataSize = metadataSize,
            FileSize = fileSize,
            Generation = generation,
            DataOffset = dataOffset,
            Endianness = endianness,
        };
    }

    private static SerializedFileInfo ParseMetadata(ReadOnlySpan<byte> metadata, SerializedFileHeaderInfo header, long entryLength)
    {
        SpanReader reader = new(metadata);

        string unityVersion = reader.ReadStringToNull();
        uint targetPlatform = reader.ReadUInt32LittleEndian();
        bool enableTypeTree = reader.ReadByte() != 0;

        int typeCount = reader.ReadInt32LittleEndian();
        if (typeCount < 0)
        {
            throw new InvalidDataException("Serialized type count cannot be negative.");
        }

        SerializedTypeInfo[] types = new SerializedTypeInfo[typeCount];
        for (int i = 0; i < types.Length; i++)
        {
            int typeId = reader.ReadInt32LittleEndian();
            bool isStripped = reader.ReadByte() != 0;
            short scriptTypeIndex = reader.ReadInt16LittleEndian();

            bool readScriptId = typeId == -1 || typeId == 114 || scriptTypeIndex >= 0;
            byte[]? scriptId = readScriptId ? reader.ReadBytes(Hash128Size).ToArray() : null;
            byte[] oldTypeHash = reader.ReadBytes(Hash128Size).ToArray();

            TypeTreeInfo? typeTree = null;
            if (enableTypeTree)
            {
                typeTree = ReadTypeTree(ref reader);

                int typeDependencyCount = reader.ReadInt32LittleEndian();
                if (typeDependencyCount < 0)
                {
                    throw new InvalidDataException("Type dependency count cannot be negative.");
                }

                reader.Skip(checked(typeDependencyCount * sizeof(int)));
            }

            types[i] = new SerializedTypeInfo
            {
                TypeId = typeId,
                IsStripped = isStripped,
                ScriptTypeIndex = scriptTypeIndex,
                ScriptId = scriptId,
                OldTypeHash = oldTypeHash,
                TypeTree = typeTree,
            };
        }

        int objectCount = reader.ReadInt32LittleEndian();
        if (objectCount < 0)
        {
            throw new InvalidDataException("Serialized object count cannot be negative.");
        }

        SerializedObjectInfo[] objects = new SerializedObjectInfo[objectCount];
        for (int i = 0; i < objects.Length; i++)
        {
            reader.Align(4);

            long pathId = reader.ReadInt64LittleEndian();
            long byteStartRelative = reader.ReadInt64LittleEndian();
            int byteSize = reader.ReadInt32LittleEndian();
            int serializedTypeIndex = reader.ReadInt32LittleEndian();

            if ((uint)serializedTypeIndex >= (uint)types.Length)
            {
                throw new InvalidDataException($"Serialized type index {serializedTypeIndex} is out of range.");
            }

            long byteStart = checked(header.DataOffset + byteStartRelative);
            if (byteSize < 0 || byteStart < 0 || byteStart + byteSize > entryLength)
            {
                throw new InvalidDataException($"Object {pathId} has an invalid data range.");
            }

            SerializedTypeInfo type = types[serializedTypeIndex];
            objects[i] = new SerializedObjectInfo
            {
                PathId = pathId,
                ByteStart = byteStart,
                ByteSize = byteSize,
                SerializedTypeIndex = serializedTypeIndex,
                TypeId = type.TypeId,
                ScriptTypeIndex = type.ScriptTypeIndex,
                IsStripped = type.IsStripped,
            };
        }

        return new SerializedFileInfo
        {
            Header = new SerializedFileHeaderInfo
            {
                MetadataSize = header.MetadataSize,
                FileSize = header.FileSize,
                Generation = header.Generation,
                DataOffset = header.DataOffset,
                Endianness = header.Endianness,
                UnityVersion = unityVersion,
                TargetPlatform = targetPlatform,
                EnableTypeTree = enableTypeTree,
            },
            Types = types,
            Objects = objects,
        };
    }

    private static TypeTreeInfo ReadTypeTree(ref SpanReader reader)
    {
        int nodeCount = reader.ReadInt32LittleEndian();
        int stringBufferSize = reader.ReadInt32LittleEndian();
        if (nodeCount < 0 || stringBufferSize < 0)
        {
            throw new InvalidDataException("Type tree node count or string buffer size cannot be negative.");
        }

        long nodeBytes = (long)nodeCount * TypeTreeNodeSize;
        if (nodeBytes > int.MaxValue)
        {
            throw new InvalidDataException("Type tree is too large.");
        }

        TypeTreeNode[] nodes = new TypeTreeNode[nodeCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            int version = reader.ReadUInt16LittleEndian();
            byte level = reader.ReadByte();
            byte typeFlags = reader.ReadByte();
            uint typeStrOffset = reader.ReadUInt32LittleEndian();
            uint nameStrOffset = reader.ReadUInt32LittleEndian();
            int byteSize = reader.ReadInt32LittleEndian();
            int index = reader.ReadInt32LittleEndian();
            uint metaFlag = reader.ReadUInt32LittleEndian();
            ulong refTypeHash = reader.ReadUInt64LittleEndian();

            nodes[i] = new TypeTreeNode
            {
                Version = version,
                Level = level,
                TypeFlags = typeFlags,
                TypeStrOffset = typeStrOffset,
                NameStrOffset = nameStrOffset,
                ByteSize = byteSize,
                Index = index,
                MetaFlag = metaFlag,
                RefTypeHash = refTypeHash,
                HasBuiltInTypeName = (typeStrOffset & 0x80000000u) != 0,
                HasBuiltInName = (nameStrOffset & 0x80000000u) != 0,
            };
        }

        ReadOnlySpan<byte> stringBuffer = reader.ReadBytes(stringBufferSize);
        Dictionary<uint, string> customStrings = ParseStringBuffer(stringBuffer);

        for (int i = 0; i < nodes.Length; i++)
        {
            TypeTreeNode node = nodes[i];
            nodes[i] = new TypeTreeNode
            {
                Version = node.Version,
                Level = node.Level,
                TypeFlags = node.TypeFlags,
                TypeStrOffset = node.TypeStrOffset,
                NameStrOffset = node.NameStrOffset,
                ByteSize = node.ByteSize,
                Index = node.Index,
                MetaFlag = node.MetaFlag,
                RefTypeHash = node.RefTypeHash,
                HasBuiltInTypeName = node.HasBuiltInTypeName,
                HasBuiltInName = node.HasBuiltInName,
                TypeName = node.HasBuiltInTypeName ? string.Empty : (customStrings.TryGetValue(node.TypeStrOffset, out string? typeName) ? typeName : string.Empty),
                Name = node.HasBuiltInName ? string.Empty : (customStrings.TryGetValue(node.NameStrOffset, out string? nodeName) ? nodeName : string.Empty),
            };
        }

        return new TypeTreeInfo
        {
            Nodes = nodes,
            CustomStrings = customStrings,
        };
    }

    private static Dictionary<uint, string> ParseStringBuffer(ReadOnlySpan<byte> buffer)
    {
        Dictionary<uint, string> strings = [];
        int position = 0;
        while (position < buffer.Length)
        {
            uint offset = (uint)position;
            int start = position;
            while (position < buffer.Length && buffer[position] != 0)
            {
                position++;
            }

            if (position >= buffer.Length)
            {
                throw new InvalidDataException("Type tree string buffer is not null-terminated.");
            }

            strings[offset] = Encoding.UTF8.GetString(buffer[start..position]);
            position++;
        }

        return strings;
    }

    private static int ReadInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static long ReadInt64BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt64BigEndian(buffer);
    }

    private static void AlignStream(Stream stream, int alignment)
    {
        long position = stream.Position;
        long remainder = position % alignment;
        if (remainder != 0)
        {
            stream.Position = position + alignment - remainder;
        }
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;

        internal SpanReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            position = 0;
        }

        internal void Skip(int count)
        {
            if (count < 0 || (long)position + count > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of metadata.");
            }

            position += count;
        }

        internal void Align(int alignment)
        {
            int alignedPosition = (position + alignment - 1) & ~(alignment - 1);
            if (alignedPosition > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of metadata while aligning.");
            }

            position = alignedPosition;
        }

        internal string ReadStringToNull()
        {
            int start = position;
            while (position < data.Length && data[position] != 0)
            {
                position++;
            }

            if (position >= data.Length)
            {
                throw new EndOfStreamException("Unexpected end of metadata while reading a string.");
            }

            string value = Encoding.UTF8.GetString(data[start..position]);
            position++;
            return value;
        }

        internal byte ReadByte()
        {
            if (position >= data.Length)
            {
                throw new EndOfStreamException("Unexpected end of metadata.");
            }

            return data[position++];
        }

        internal ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || (long)position + count > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of metadata.");
            }

            ReadOnlySpan<byte> value = data.Slice(position, count);
            position += count;
            return value;
        }

        internal ushort ReadUInt16LittleEndian()
        {
            const int size = sizeof(ushort);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(size));
            return value;
        }

        internal short ReadInt16LittleEndian()
        {
            const int size = sizeof(short);
            short value = BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(size));
            return value;
        }

        internal int ReadInt32LittleEndian()
        {
            const int size = sizeof(int);
            int value = BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(size));
            return value;
        }

        internal uint ReadUInt32LittleEndian()
        {
            const int size = sizeof(uint);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(size));
            return value;
        }

        internal ulong ReadUInt64LittleEndian()
        {
            const int size = sizeof(ulong);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(size));
            return value;
        }

        internal long ReadInt64LittleEndian()
        {
            const int size = sizeof(long);
            long value = BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(size));
            return value;
        }
    }
}






