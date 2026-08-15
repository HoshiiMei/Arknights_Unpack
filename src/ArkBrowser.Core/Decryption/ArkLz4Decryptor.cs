using System.Buffers;
using K4os.Compression.LZ4;

namespace ArkBrowser.Core.Decryption;

public enum ArkCompressionType
{
    None = 0,
    Lzma = 1,
    Lz4 = 2,
    Lz4HC = 3,
    ArkLz4 = 4
}

public static class ArkLz4Decryptor
{
    private static readonly byte[] UnityFsMagic = "UnityFS"u8.ToArray();

    public static bool IsStandardUnityBundle(ReadOnlySpan<byte> header)
    {
        return header.Length >= UnityFsMagic.Length
            && header[..UnityFsMagic.Length].SequenceEqual(UnityFsMagic);
    }

    public static void DecompressBlock(ReadOnlySpan<byte> compressed, Span<byte> output, int flags)
    {
        ArkCompressionType compressionType = (ArkCompressionType)(flags & 0x3F);

        switch (compressionType)
        {
            case ArkCompressionType.None:
                CopyNone(compressed, output);
                break;

            case ArkCompressionType.Lz4:
            case ArkCompressionType.Lz4HC:
                DecodeLz4(compressed, output);
                break;

            case ArkCompressionType.ArkLz4:
                byte[] rented = ArrayPool<byte>.Shared.Rent(compressed.Length);
                try
                {
                    Span<byte> buffer = rented.AsSpan(0, compressed.Length);
                    compressed.CopyTo(buffer);
                    DecompressArkLz4InPlace(buffer, output);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                break;

            case ArkCompressionType.Lzma:
                throw new NotSupportedException("LZMA compressed blocks are not supported yet.");

            default:
                throw new NotSupportedException($"Unsupported block compression type: {compressionType}.");
        }
    }

    internal static void DecompressBlockInPlace(Span<byte> compressed, Span<byte> output, int flags)
    {
        ArkCompressionType compressionType = (ArkCompressionType)(flags & 0x3F);

        switch (compressionType)
        {
            case ArkCompressionType.None:
                CopyNone(compressed, output);
                break;

            case ArkCompressionType.Lz4:
            case ArkCompressionType.Lz4HC:
                DecodeLz4(compressed, output);
                break;

            case ArkCompressionType.ArkLz4:
                DecompressArkLz4InPlace(compressed, output);
                break;

            case ArkCompressionType.Lzma:
                throw new NotSupportedException("LZMA compressed blocks are not supported yet.");

            default:
                throw new NotSupportedException($"Unsupported block compression type: {compressionType}.");
        }
    }

    private static void CopyNone(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        if (compressed.Length != output.Length)
        {
            throw new InvalidDataException(
                $"Uncompressed block size mismatch: expected {output.Length}, got {compressed.Length}.");
        }

        compressed.CopyTo(output);
    }

    private static void DecodeLz4(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        int written = LZ4Codec.Decode(compressed, output);

        if (written != output.Length)
        {
            throw new InvalidDataException($"LZ4 decompression wrote {written} bytes, expected {output.Length}.");
        }
    }

    private static void DecompressArkLz4InPlace(Span<byte> data, Span<byte> output)
    {
        PreprocessArkLz4Block(data, output.Length);
        DecodeLz4(data, output);
    }

    private static void PreprocessArkLz4Block(Span<byte> data, int uncompressedSize)
    {
        int position = 0;
        int compressedSize = data.Length;
        long outputPosition = 0;

        while (position < compressedSize)
        {
            byte token = data[position];
            int literalLength = token & 0x0F;
            int matchLength = (token >> 4) & 0x0F;

            data[position] = (byte)((literalLength << 4) | matchLength);
            position++;

            if (literalLength == 0x0F)
            {
                (int value, int nextPosition) = ReadLongLength(data, position);
                literalLength += value;
                position = nextPosition;
            }

            outputPosition += literalLength;
            position += literalLength;

            if ((long)uncompressedSize - outputPosition < 12 || position + 1 >= compressedSize)
            {
                break;
            }

            byte low = data[position];
            byte high = data[position + 1];
            int offset = (low << 8) | high;
            data[position] = (byte)(offset & 0xFF);
            data[position + 1] = (byte)(offset >> 8);
            position += 2;

            if (matchLength == 0x0F)
            {
                if (position >= compressedSize)
                {
                    break;
                }

                (int value, int nextPosition) = ReadLongLength(data, position);
                matchLength += value;
                position = nextPosition;
            }

            matchLength += 4;
            outputPosition += matchLength;
        }
    }

    private static (int Value, int NextPosition) ReadLongLength(ReadOnlySpan<byte> data, int position)
    {
        int result = 0;

        while (position < data.Length)
        {
            byte value = data[position++];
            result += value;

            if (value != byte.MaxValue)
            {
                return (result, position);
            }
        }

        throw new EndOfStreamException("Failed to read long length: unexpected end of data.");
    }
}
