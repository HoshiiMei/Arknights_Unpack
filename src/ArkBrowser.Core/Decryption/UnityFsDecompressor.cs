using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace ArkBrowser.Core.Decryption;

public static class UnityFsDecompressor
{
    public static void ConvertToUncompressed(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        using FileStream input = new(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        ConvertToUncompressed(input, output);
    }

    public static void ConvertToUncompressed(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string signature = ReadStringToNull(input);
        if (!ArkLz4Decryptor.IsStandardUnityBundle(Encoding.UTF8.GetBytes(signature)))
        {
            throw new InvalidDataException("The input is not a UnityFS bundle.");
        }

        uint version = ReadUInt32BigEndian(input);
        string unityVersion = ReadStringToNull(input);
        string unityRevision = ReadStringToNull(input);
        long originalSize = ReadInt64BigEndian(input);
        uint compressedBlocksInfoSize = ReadUInt32BigEndian(input);
        uint uncompressedBlocksInfoSize = ReadUInt32BigEndian(input);
        uint archiveFlags = ReadUInt32BigEndian(input);

        int headerEnd = Encoding.UTF8.GetByteCount(signature) + 1
            + sizeof(uint)
            + Encoding.UTF8.GetByteCount(unityVersion) + 1
            + Encoding.UTF8.GetByteCount(unityRevision) + 1
            + sizeof(long)
            + (sizeof(uint) * 3);

        int headerSize = version >= 7 ? Align(headerEnd, 16) : headerEnd;
        SkipBytes(input, headerSize - headerEnd);

        int compressedInfoSize = checked((int)compressedBlocksInfoSize);
        int uncompressedInfoSize = checked((int)uncompressedBlocksInfoSize);

        byte[] compressedInfoBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(compressedInfoSize, 1));
        byte[] uncompressedInfoBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(uncompressedInfoSize, 1));

        StorageBlock[] blocks;
        Node[] nodes;

        try
        {
            input.ReadExactly(compressedInfoBuffer, 0, compressedInfoSize);
            ArkLz4Decryptor.DecompressBlockInPlace(
                compressedInfoBuffer.AsSpan(0, compressedInfoSize),
                uncompressedInfoBuffer.AsSpan(0, uncompressedInfoSize),
                (int)archiveFlags);

            ReadOnlySpan<byte> infoSpan = uncompressedInfoBuffer.AsSpan(0, uncompressedInfoSize);
            SpanReader infoReader = new(infoSpan);
            infoReader.Skip(16);

            int blockCount = infoReader.ReadInt32BigEndian();
            blocks = new StorageBlock[blockCount];
            for (int i = 0; i < blocks.Length; i++)
            {
                uint uncompressedSize = infoReader.ReadUInt32BigEndian();
                uint compressedSize = infoReader.ReadUInt32BigEndian();
                ushort blockFlags = infoReader.ReadUInt16BigEndian();
                blocks[i] = new StorageBlock(uncompressedSize, compressedSize, blockFlags);
            }

            int nodeCount = infoReader.ReadInt32BigEndian();
            nodes = new Node[nodeCount];
            for (int i = 0; i < nodes.Length; i++)
            {
                long offset = infoReader.ReadInt64BigEndian();
                long size = infoReader.ReadInt64BigEndian();
                uint flags = infoReader.ReadUInt32BigEndian();
                string path = infoReader.ReadStringToNull();
                nodes[i] = new Node(offset, size, flags, path);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedInfoBuffer);
            ArrayPool<byte>.Shared.Return(uncompressedInfoBuffer);
        }

        int dataStart = headerSize + compressedInfoSize;
        if ((archiveFlags & 0x200) != 0)
        {
            dataStart = Align(dataStart, 16);
        }

        SkipBytes(input, dataStart - (headerSize + compressedInfoSize));

        byte[] newBlocksInfo = BuildUncompressedBlocksInfo(blocks, nodes);
        uint newArchiveFlags = archiveFlags & ~0x3Fu;
        long newDataStart = headerSize + newBlocksInfo.LongLength;
        if ((newArchiveFlags & 0x200) != 0)
        {
            newDataStart = Align((int)newDataStart, 16);
        }

        long totalUncompressedSize = 0;
        int maxCompressedSize = 0;
        int maxUncompressedSize = 0;
        foreach (StorageBlock block in blocks)
        {
            totalUncompressedSize += block.UncompressedSize;
            maxCompressedSize = Math.Max(maxCompressedSize, checked((int)block.CompressedSize));
            maxUncompressedSize = Math.Max(maxUncompressedSize, checked((int)block.UncompressedSize));
        }

        long newSize = newDataStart + totalUncompressedSize;

        WriteHeader(output, signature, version, unityVersion, unityRevision, newSize, newArchiveFlags, newBlocksInfo.Length, headerSize);
        output.Write(newBlocksInfo);
        WritePadding(output, newDataStart - headerSize - newBlocksInfo.Length);

        byte[] compressedBlockBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(maxCompressedSize, 1));
        byte[] uncompressedBlockBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(maxUncompressedSize, 1));

        try
        {
            foreach (StorageBlock block in blocks)
            {
                int compressedSize = checked((int)block.CompressedSize);
                int uncompressedSize = checked((int)block.UncompressedSize);

                input.ReadExactly(compressedBlockBuffer, 0, compressedSize);
                ArkLz4Decryptor.DecompressBlockInPlace(
                    compressedBlockBuffer.AsSpan(0, compressedSize),
                    uncompressedBlockBuffer.AsSpan(0, uncompressedSize),
                    block.Flags);

                output.Write(uncompressedBlockBuffer, 0, uncompressedSize);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedBlockBuffer);
            ArrayPool<byte>.Shared.Return(uncompressedBlockBuffer);
        }
    }

    private static byte[] BuildUncompressedBlocksInfo(StorageBlock[] blocks, Node[] nodes)
    {
        using MemoryStream stream = new();
        stream.Write(new byte[16]);

        WriteInt32BigEndian(stream, blocks.Length);
        foreach (StorageBlock block in blocks)
        {
            WriteUInt32BigEndian(stream, block.UncompressedSize);
            WriteUInt32BigEndian(stream, block.UncompressedSize);
            WriteUInt16BigEndian(stream, (ushort)(block.Flags & ~0x3F));
        }

        WriteInt32BigEndian(stream, nodes.Length);
        foreach (Node node in nodes)
        {
            WriteInt64BigEndian(stream, node.Offset);
            WriteInt64BigEndian(stream, node.Size);
            WriteUInt32BigEndian(stream, node.Flags);
            WriteStringToNull(stream, node.Path);
        }

        return stream.ToArray();
    }

    private static void WriteHeader(
        Stream output,
        string signature,
        uint version,
        string unityVersion,
        string unityRevision,
        long size,
        uint archiveFlags,
        int blocksInfoSize,
        int headerSize)
    {
        WriteStringToNull(output, signature);
        WriteUInt32BigEndian(output, version);
        WriteStringToNull(output, unityVersion);
        WriteStringToNull(output, unityRevision);
        WriteInt64BigEndian(output, size);
        WriteUInt32BigEndian(output, (uint)blocksInfoSize);
        WriteUInt32BigEndian(output, (uint)blocksInfoSize);
        WriteUInt32BigEndian(output, archiveFlags);

        int written = Encoding.UTF8.GetByteCount(signature) + 1
            + sizeof(uint)
            + Encoding.UTF8.GetByteCount(unityVersion) + 1
            + Encoding.UTF8.GetByteCount(unityRevision) + 1
            + sizeof(long)
            + (sizeof(uint) * 3);
        WritePadding(output, headerSize - written);
    }

    private static string ReadStringToNull(Stream stream)
    {
        using MemoryStream buffer = new();
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException("Unexpected end of data while reading a string.");
            }

            if (value == 0)
            {
                break;
            }

            buffer.WriteByte((byte)value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
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

    private static void WriteStringToNull(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32BigEndian(Stream stream, int value)
    {
        WriteUInt32BigEndian(stream, (uint)value);
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64BigEndian(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void SkipBytes(Stream stream, int count)
    {
        if (count == 0)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));
        try
        {
            stream.ReadExactly(buffer, 0, count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WritePadding(Stream output, long count)
    {
        if (count <= 0)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 4096));
        Array.Clear(buffer, 0, buffer.Length);
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                int toWrite = (int)Math.Min(remaining, buffer.Length);
                output.Write(buffer, 0, toWrite);
                remaining -= toWrite;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int Align(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    private sealed class StorageBlock
    {
        internal StorageBlock(uint uncompressedSize, uint compressedSize, ushort flags)
        {
            UncompressedSize = uncompressedSize;
            CompressedSize = compressedSize;
            Flags = flags;
        }

        internal uint UncompressedSize { get; }
        internal uint CompressedSize { get; }
        internal ushort Flags { get; }
    }

    private sealed class Node
    {
        internal Node(long offset, long size, uint flags, string path)
        {
            Offset = offset;
            Size = size;
            Flags = flags;
            Path = path;
        }

        internal long Offset { get; }
        internal long Size { get; }
        internal uint Flags { get; }
        internal string Path { get; }
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
            if (count < 0 || position + count > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of data.");
            }

            position += count;
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
                throw new EndOfStreamException("Unexpected end of data while reading a string.");
            }

            string value = Encoding.UTF8.GetString(data[start..position]);
            position++;
            return value;
        }

        internal uint ReadUInt32BigEndian()
        {
            const int size = sizeof(uint);
            if (position + size > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of data.");
            }

            uint value = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            position += size;
            return value;
        }

        internal int ReadInt32BigEndian()
        {
            return (int)ReadUInt32BigEndian();
        }

        internal long ReadInt64BigEndian()
        {
            const int size = sizeof(long);
            if (position + size > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of data.");
            }

            long value = BinaryPrimitives.ReadInt64BigEndian(data[position..]);
            position += size;
            return value;
        }

        internal ushort ReadUInt16BigEndian()
        {
            const int size = sizeof(ushort);
            if (position + size > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of data.");
            }

            ushort value = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            position += size;
            return value;
        }
    }
}
