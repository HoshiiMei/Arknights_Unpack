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

		using UnityFsReader reader = UnityFsReader.Open(input, leaveOpen: true);
		reader.WriteUncompressedTo(output);
	}
}