using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace ArkBrowser.Core.Decryption;

public sealed record UnityFsHeader(
	string Signature,
	uint Version,
	string UnityVersion,
	string UnityRevision,
	long OriginalSize,
	uint ArchiveFlags,
	int HeaderSize,
	int DataStart);

public sealed record UnityFsEntry(string Path, long Offset, long Size, uint Flags);

public sealed class UnityFsReader : IDisposable
{
	private readonly Stream stream;
	private readonly bool leaveOpen;
	private readonly StorageBlock[] blocks;
	private readonly UnityFsEntry[] entries;
	private bool disposed;

	public UnityFsHeader Header { get; }
	public IReadOnlyList<UnityFsEntry> Entries => entries;
	internal IReadOnlyList<StorageBlock> Blocks => blocks;

	private UnityFsReader(Stream stream, bool leaveOpen, UnityFsHeader header, StorageBlock[] blocks, UnityFsEntry[] entries)
	{
		this.stream = stream;
		this.leaveOpen = leaveOpen;
		Header = header;
		this.blocks = blocks;
		this.entries = entries;
	}

	public static UnityFsReader Open(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		return Open(stream, leaveOpen: false);
	}

	public static UnityFsReader Open(Stream stream, bool leaveOpen = true)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if (!stream.CanRead)
		{
			throw new ArgumentException("The stream must be readable.", nameof(stream));
		}

		(UnityFsHeader header, StorageBlock[] blocks, UnityFsEntry[] entries) = ReadIndex(stream);
		return new UnityFsReader(stream, leaveOpen, header, blocks, entries);
	}

	public Stream OpenEntry(int index)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if ((uint)index >= (uint)entries.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		UnityFsEntry entry = entries[index];
		return new SliceStream(new BlockVirtualStream(stream, blocks, Header.DataStart), entry.Offset, entry.Size);
	}

	public Stream OpenAllData()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		return new BlockVirtualStream(stream, blocks, Header.DataStart);
	}

	public void WriteUncompressedTo(Stream output)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		ArgumentNullException.ThrowIfNull(output);

		byte[] newBlocksInfo = BuildUncompressedBlocksInfo(blocks, entries);
		uint newArchiveFlags = Header.ArchiveFlags & ~0x3Fu;
		long newDataStart = Header.HeaderSize + newBlocksInfo.LongLength;
		if ((newArchiveFlags & 0x200) != 0)
		{
			newDataStart = Align((int)newDataStart, 16);
		}

		long totalUncompressedSize = 0;
		foreach (StorageBlock block in blocks)
		{
			totalUncompressedSize += block.UncompressedSize;
		}

		long newSize = newDataStart + totalUncompressedSize;

		WriteHeader(
			output,
			Header.Signature,
			Header.Version,
			Header.UnityVersion,
			Header.UnityRevision,
			newSize,
			newArchiveFlags,
			newBlocksInfo.Length,
			Header.HeaderSize);

		output.Write(newBlocksInfo);
		WritePadding(output, newDataStart - Header.HeaderSize - newBlocksInfo.Length);

		using Stream dataStream = OpenAllData();
		dataStream.CopyTo(output);
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			if (!leaveOpen)
			{
				stream.Dispose();
			}
		}
	}

	private static (UnityFsHeader Header, StorageBlock[] Blocks, UnityFsEntry[] Entries) ReadIndex(Stream stream)
	{
		string signature = ReadStringToNull(stream);
		if (!ArkLz4Decryptor.IsStandardUnityBundle(Encoding.UTF8.GetBytes(signature)))
		{
			throw new InvalidDataException("The input is not a UnityFS bundle.");
		}

		uint version = ReadUInt32BigEndian(stream);
		string unityVersion = ReadStringToNull(stream);
		string unityRevision = ReadStringToNull(stream);
		long originalSize = ReadInt64BigEndian(stream);
		uint compressedBlocksInfoSize = ReadUInt32BigEndian(stream);
		uint uncompressedBlocksInfoSize = ReadUInt32BigEndian(stream);
		uint archiveFlags = ReadUInt32BigEndian(stream);

		int headerEnd = Encoding.UTF8.GetByteCount(signature) + 1
			+ sizeof(uint)
			+ Encoding.UTF8.GetByteCount(unityVersion) + 1
			+ Encoding.UTF8.GetByteCount(unityRevision) + 1
			+ sizeof(long)
			+ sizeof(uint) * 3;

		int headerSize = version >= 7 ? Align(headerEnd, 16) : headerEnd;
		SkipBytes(stream, headerSize - headerEnd);

		int compressedInfoSize = checked((int)compressedBlocksInfoSize);
		int uncompressedInfoSize = checked((int)uncompressedBlocksInfoSize);

		byte[] compressedInfoBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(compressedInfoSize, 1));
		byte[] uncompressedInfoBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(uncompressedInfoSize, 1));

		StorageBlock[] blocks;
		UnityFsEntry[] entries;

		try
		{
			stream.ReadExactly(compressedInfoBuffer, 0, compressedInfoSize);
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
			entries = new UnityFsEntry[nodeCount];
			for (int i = 0; i < entries.Length; i++)
			{
				long offset = infoReader.ReadInt64BigEndian();
				long size = infoReader.ReadInt64BigEndian();
				uint flags = infoReader.ReadUInt32BigEndian();
				string path = infoReader.ReadStringToNull();
				entries[i] = new UnityFsEntry(path, offset, size, flags);
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

		UnityFsHeader header = new(
			signature,
			version,
			unityVersion,
			unityRevision,
			originalSize,
			archiveFlags,
			headerSize,
			dataStart);

		return (header, blocks, entries);
	}

	private static byte[] BuildUncompressedBlocksInfo(StorageBlock[] blocks, UnityFsEntry[] entries)
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

		WriteInt32BigEndian(stream, entries.Length);
		foreach (UnityFsEntry entry in entries)
		{
			WriteInt64BigEndian(stream, entry.Offset);
			WriteInt64BigEndian(stream, entry.Size);
			WriteUInt32BigEndian(stream, entry.Flags);
			WriteStringToNull(stream, entry.Path);
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
			+ sizeof(uint) * 3;

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

	internal sealed class StorageBlock
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

	private sealed class BlockVirtualStream : Stream
	{
		private readonly Stream baseStream;
		private readonly StorageBlock[] blocks;
		private readonly long[] blockStarts;
		private readonly long[] compressedStarts;
		private byte[]? cachedBuffer;
		private int cachedBlockIndex = -1;
		private int cachedBufferLength;
		private long position;
		private bool disposed;

		public BlockVirtualStream(Stream baseStream, StorageBlock[] blocks, long dataStart)
		{
			this.baseStream = baseStream;
			this.blocks = blocks;

			blockStarts = new long[blocks.Length];
			compressedStarts = new long[blocks.Length];
			long uncompressedPosition = 0;
			long compressedPosition = dataStart;
			for (int i = 0; i < blocks.Length; i++)
			{
				blockStarts[i] = uncompressedPosition;
				compressedStarts[i] = compressedPosition;
				uncompressedPosition += blocks[i].UncompressedSize;
				compressedPosition += blocks[i].CompressedSize;
			}

			Length = uncompressedPosition;
		}

		public override bool CanRead => !disposed;
		public override bool CanSeek => !disposed;
		public override bool CanWrite => false;
		public override long Length { get; }
		public override long Position
		{
			get => position;
			set => position = Math.Clamp(value, 0, Length);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			ArgumentNullException.ThrowIfNull(buffer);
			if ((uint)offset > (uint)buffer.Length)
			{
				throw new ArgumentOutOfRangeException(nameof(offset));
			}

			if ((uint)count > (uint)(buffer.Length - offset))
			{
				throw new ArgumentOutOfRangeException(nameof(count));
			}

			return Read(buffer.AsSpan(offset, count));
		}

		public override int Read(Span<byte> buffer)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			if (position >= Length || buffer.IsEmpty)
			{
				return 0;
			}

			long remaining = Length - position;
			int requested = (int)Math.Min((long)buffer.Length, remaining);
			int totalRead = 0;
			while (requested > 0)
			{
				int blockIndex = FindBlock(position);
				EnsureBlock(blockIndex);

				int offsetInBlock = (int)(position - blockStarts[blockIndex]);
				int available = cachedBufferLength - offsetInBlock;
				int toCopy = Math.Min(requested, available);
				cachedBuffer.AsSpan(offsetInBlock, toCopy).CopyTo(buffer.Slice(totalRead, toCopy));

				position += toCopy;
				totalRead += toCopy;
				requested -= toCopy;
			}

			return totalRead;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			long newPosition = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => position + offset,
				SeekOrigin.End => Length + offset,
				_ => throw new ArgumentOutOfRangeException(nameof(origin)),
			};

			position = Math.Clamp(newPosition, 0, Length);
			return position;
		}

		public override void Flush()
		{
		}

		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ReturnBuffer();
				disposed = true;
			}

			base.Dispose(disposing);
		}

		private int FindBlock(long target)
		{
			int low = 0;
			int high = blocks.Length - 1;
			while (low <= high)
			{
				int middle = (low + high) / 2;
				if (target < blockStarts[middle])
				{
					high = middle - 1;
				}
				else if (middle + 1 < blocks.Length && target >= blockStarts[middle + 1])
				{
					low = middle + 1;
				}
				else
				{
					return middle;
				}
			}

			return blocks.Length - 1;
		}

		private void EnsureBlock(int blockIndex)
		{
			if (cachedBlockIndex == blockIndex && cachedBuffer is not null)
			{
				return;
			}

			ReturnBuffer();

			StorageBlock block = blocks[blockIndex];
			int uncompressedSize = checked((int)block.UncompressedSize);
			int compressedSize = checked((int)block.CompressedSize);
			int compressionType = block.Flags & 0x3F;

			byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(uncompressedSize, 1));
			baseStream.Position = compressedStarts[blockIndex];

			if (compressionType == 0)
			{
				if (compressedSize != uncompressedSize)
				{
					ArrayPool<byte>.Shared.Return(outputBuffer);
					throw new InvalidDataException("Uncompressed block size does not match the expected size.");
				}

				baseStream.ReadExactly(outputBuffer, 0, uncompressedSize);
			}
			else
			{
				byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(compressedSize, 1));
				try
				{
					baseStream.ReadExactly(compressedBuffer, 0, compressedSize);
					ArkLz4Decryptor.DecompressBlockInPlace(
						compressedBuffer.AsSpan(0, compressedSize),
						outputBuffer.AsSpan(0, uncompressedSize),
						block.Flags);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(compressedBuffer);
				}
			}

			cachedBuffer = outputBuffer;
			cachedBufferLength = uncompressedSize;
			cachedBlockIndex = blockIndex;
		}

		private void ReturnBuffer()
		{
			if (cachedBuffer is not null)
			{
				ArrayPool<byte>.Shared.Return(cachedBuffer);
				cachedBuffer = null;
			}

			cachedBlockIndex = -1;
			cachedBufferLength = 0;
		}
	}

	private sealed class SliceStream : Stream
	{
		private readonly Stream parent;
		private readonly long origin;
		private readonly long length;
		private long position;

		public SliceStream(Stream parent, long origin, long length)
		{
			ArgumentNullException.ThrowIfNull(parent);
			if (origin < 0 || length < 0 || origin + length > parent.Length)
			{
				throw new ArgumentOutOfRangeException(nameof(origin));
			}

			this.parent = parent;
			this.origin = origin;
			this.length = length;
		}

		public override bool CanRead => parent.CanRead;
		public override bool CanSeek => parent.CanSeek;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position
		{
			get => position;
			set => position = Math.Clamp(value, 0, length);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			ArgumentNullException.ThrowIfNull(buffer);
			if ((uint)offset > (uint)buffer.Length)
			{
				throw new ArgumentOutOfRangeException(nameof(offset));
			}

			if ((uint)count > (uint)(buffer.Length - offset))
			{
				throw new ArgumentOutOfRangeException(nameof(count));
			}

			return Read(buffer.AsSpan(offset, count));
		}

		public override int Read(Span<byte> buffer)
		{
			long remaining = length - position;
			if (remaining <= 0 || buffer.IsEmpty)
			{
				return 0;
			}

			int toRead = (int)Math.Min(buffer.Length, remaining);
			parent.Position = origin + position;
			int read = parent.Read(buffer.Slice(0, toRead));
			position += read;
			return read;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			long newPosition = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => position + offset,
				SeekOrigin.End => length + offset,
				_ => throw new ArgumentOutOfRangeException(nameof(origin)),
			};

			position = Math.Clamp(newPosition, 0, length);
			return position;
		}

		public override void Flush()
		{
		}

		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				parent.Dispose();
			}

			base.Dispose(disposing);
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

