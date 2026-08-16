using System.Buffers.Binary;
using System.Text;
using ArkBrowser.Core.Decryption;

namespace ArkBrowser.Core.Serialization;

public sealed record StreamingInfo(ulong Offset, uint Size, string Path)
{
    public bool IsSet => Path.Length > 0 || Size > 0;
}

public sealed record Texture2DInfo(
    string Name,
    int Width,
    int Height,
    int TextureFormat,
    int CompleteImageSize,
    StreamingInfo? StreamingInfo,
    int InlineImageOffset,
    int InlineImageSize);

public static class Texture2DReader
{
    public static Texture2DInfo Read(Stream objectStream)
    {
        ArgumentNullException.ThrowIfNull(objectStream);
        if (!objectStream.CanSeek || objectStream.Length > int.MaxValue)
        {
            throw new ArgumentException("Texture2D object stream must be seekable and smaller than 2 GB.", nameof(objectStream));
        }

        objectStream.Position = 0;
        byte[] buffer = new byte[(int)objectStream.Length];
        objectStream.ReadExactly(buffer);
        return Read(buffer);
    }

    public static Texture2DInfo Read(ReadOnlySpan<byte> objectData)
    {
        SpanReader reader = new(objectData);

        string name = reader.ReadAlignedString();
        _ = reader.ReadInt32LittleEndian(); // m_ForcedFallbackFormat
        _ = reader.ReadByte();              // m_DownscaleFallback
        _ = reader.ReadByte();              // m_IsAlphaChannelOptional
        reader.Align(4);

        int width = reader.ReadInt32LittleEndian();
        int height = reader.ReadInt32LittleEndian();
        int completeImageSize = reader.ReadInt32LittleEndian();
        _ = reader.ReadInt32LittleEndian(); // m_MipsStripped
        int textureFormat = reader.ReadInt32LittleEndian();
        _ = reader.ReadInt32LittleEndian(); // m_MipCount

        _ = reader.ReadByte(); // m_IsReadable
        _ = reader.ReadByte(); // m_IsPreProcessed
        _ = reader.ReadByte(); // m_IgnoreMasterTextureLimit
        _ = reader.ReadByte(); // m_StreamingMipmaps
        reader.Align(4);

        _ = reader.ReadInt32LittleEndian(); // m_StreamingMipmapsPriority
        _ = reader.ReadInt32LittleEndian(); // m_ImageCount
        _ = reader.ReadInt32LittleEndian(); // m_TextureDimension

        // GLTextureSettings
        _ = reader.ReadInt32LittleEndian(); // m_FilterMode
        _ = reader.ReadInt32LittleEndian(); // m_Aniso
        _ = reader.ReadSingleLittleEndian(); // m_MipBias
        _ = reader.ReadInt32LittleEndian(); // m_WrapU
        _ = reader.ReadInt32LittleEndian(); // m_WrapV
        _ = reader.ReadInt32LittleEndian(); // m_WrapW

        _ = reader.ReadInt32LittleEndian(); // m_LightmapFormat
        _ = reader.ReadInt32LittleEndian(); // m_ColorSpace

        int platformBlobSize = reader.ReadInt32LittleEndian();
        reader.Skip(platformBlobSize);

        int inlineImageSize = reader.ReadInt32LittleEndian();
        int inlineImageOffset = reader.Position;
        reader.Skip(inlineImageSize);

        ulong streamOffset = reader.ReadUInt64LittleEndian();
        uint streamSize = reader.ReadUInt32LittleEndian();
        string streamPath = reader.ReadAlignedString();

        StreamingInfo? streamingInfo = streamPath.Length > 0
            ? new StreamingInfo(streamOffset, streamSize, streamPath)
            : null;

        return new Texture2DInfo(
            name,
            width,
            height,
            textureFormat,
            completeImageSize,
            streamingInfo,
            inlineImageOffset,
            inlineImageSize);
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

        internal int Position => position;

        internal void Skip(int count)
        {
            if (count < 0 || (long)position + count > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of Texture2D object data.");
            }

            position += count;
        }

        internal void Align(int alignment)
        {
            int alignedPosition = (position + alignment - 1) & ~(alignment - 1);
            if (alignedPosition > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of Texture2D object data.");
            }

            position = alignedPosition;
        }

        internal ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || (long)position + count > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of Texture2D object data.");
            }

            ReadOnlySpan<byte> value = data.Slice(position, count);
            position += count;
            return value;
        }

        internal string ReadAlignedString()
        {
            int byteCount = ReadInt32LittleEndian();
            if (byteCount < 0)
            {
                throw new InvalidDataException("Texture2D string length cannot be negative.");
            }

            string value = Encoding.UTF8.GetString(ReadBytes(byteCount));
            Align(4);
            return value;
        }

        internal byte ReadByte()
        {
            if (position >= data.Length)
            {
                throw new EndOfStreamException("Unexpected end of Texture2D object data.");
            }

            return data[position++];
        }

        internal int ReadInt32LittleEndian()
        {
            const int size = sizeof(int);
            return BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(size));
        }

        internal uint ReadUInt32LittleEndian()
        {
            const int size = sizeof(uint);
            return BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(size));
        }

        internal ulong ReadUInt64LittleEndian()
        {
            const int size = sizeof(ulong);
            return BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(size));
        }

        internal float ReadSingleLittleEndian()
        {
            const int size = sizeof(float);
            return BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(size));
        }
    }
}

public sealed class BundleResourceResolver
{
    private readonly UnityFsReader reader;

    public BundleResourceResolver(UnityFsReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
    }

    public Stream? OpenStream(StreamingInfo streamingInfo)
    {
        ArgumentNullException.ThrowIfNull(streamingInfo);
        if (!streamingInfo.IsSet)
        {
            return null;
        }

        for (int i = 0; i < reader.Entries.Count; i++)
        {
            UnityFsEntry entry = reader.Entries[i];
            if (entry.IsSerializedFile)
            {
                continue;
            }

            if (streamingInfo.Path.EndsWith(entry.Path, StringComparison.OrdinalIgnoreCase)
                || streamingInfo.Path.EndsWith("/" + entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                Stream resourceEntry = reader.OpenEntry(i);
                return UnityFsReader.CreateReadOnlySlice(
                    resourceEntry,
                    checked((long)streamingInfo.Offset),
                    streamingInfo.Size,
                    leaveOpen: false);
            }
        }

        throw new InvalidDataException($"No resource entry matches streaming path '{streamingInfo.Path}'.");
    }
}
