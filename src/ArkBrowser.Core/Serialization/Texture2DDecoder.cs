using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Dxt;
using AssetRipper.TextureDecoder.Etc;
using AssetRipper.TextureDecoder.Rgb.Formats;

namespace ArkBrowser.Core.Serialization;

public static class Texture2DDecoder
{
    public static byte[] DecodeToRgba32(Texture2DInfo texture, ReadOnlySpan<byte> compressedData)
    {
        int pixelCount = checked(texture.Width * texture.Height);
        byte[] rgba = new byte[pixelCount * 4];
        DecodeToRgba32(texture.TextureFormat, compressedData, texture.Width, texture.Height, rgba);
        return rgba;
    }

    public static void DecodeToRgba32(int textureFormat, ReadOnlySpan<byte> input, int width, int height, Span<byte> output)
    {
        switch (textureFormat)
        {
            // ASTC RGB
            case 48: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 4, 4, output); break;
            case 49: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 5, 5, output); break;
            case 50: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 6, 6, output); break;
            case 51: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 8, 8, output); break;
            case 52: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 10, 10, output); break;
            case 53: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 12, 12, output); break;

            // ASTC RGBA
            case 54: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 4, 4, output); break;
            case 55: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 5, 5, output); break;
            case 56: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 6, 6, output); break;
            case 57: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 8, 8, output); break;
            case 58: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 10, 10, output); break;
            case 59: AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(input, width, height, 12, 12, output); break;

            // DXT / BC
            case 10: DxtDecoder.DecompressDXT1<ColorRGBA<byte>, byte>(input, width, height, output); break;
            case 11: DxtDecoder.DecompressDXT3<ColorRGBA<byte>, byte>(input, width, height, output); break;
            case 12: DxtDecoder.DecompressDXT5<ColorRGBA<byte>, byte>(input, width, height, output); break;

            // ETC / ETC2
            case 45: EtcDecoder.DecompressETC2<ColorRGBA<byte>, byte>(input, width, height, output); break;
            case 46: EtcDecoder.DecompressETC2A1<ColorRGBA<byte>, byte>(input, width, height, output); break;
            case 47: EtcDecoder.DecompressETC2A8<ColorRGBA<byte>, byte>(input, width, height, output); break;

            // Raw RGBA32
            case 4:
                if (input.Length < output.Length)
                {
                    throw new InvalidDataException("RGBA32 pixel data is shorter than expected.");
                }

                input[..output.Length].CopyTo(output);
                break;

            default:
                throw new NotSupportedException($"Unsupported texture format {textureFormat}.");
        }
    }
}