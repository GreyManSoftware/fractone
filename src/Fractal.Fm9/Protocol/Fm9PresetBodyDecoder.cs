namespace Fractal.Companion.Protocol
{
	public sealed record Fm9PresetGridCell(
		int Row,
		int Column,
		int? EffectId,
		ushort RouteMask);

	public sealed record Fm9PresetBody(
		ushort DecompressedSize,
		ushort CompressedSize,
		IReadOnlyList<Fm9PresetGridCell> GridCells,
		ReadOnlyMemory<byte> Bytes,
		IReadOnlyList<Fm9PresetBlockRecord> BlockRecords)
	{
		// The unit the body was written for, which sets its grid shape (and so where the record chain starts), its record
		// order and the Axe-Fx III's prefix words. Edits made with `with` keep it.
		public Gen3Device Device { get; init; } = Gen3Device.Fm9;
	}

	public sealed record Fm9PresetBlockRecord(
		int Offset,
		ushort ParameterColumns,
		ushort ParameterRows,
		int Length);

	public static class Fm9PresetBodyDecoder
	{
		private const int DecompressedSizeOffset = 0x48;
		private const int CompressedSizeOffset = 0x4A;
		private const int CompressedBodyOffset = 0x4C;
		private const int GridOffset = 0x104;
		private const int GridCellSize = 4;

		// The grid's shape is the device's: 6 x 14 on the FM9 and Axe-Fx III, 4 x 12 on the FM3; default the FM9.
		public static Fm9PresetBody Decode(Fm9PresetDump dump, Gen3Device? device = null)
		{
			ArgumentNullException.ThrowIfNull(dump);
			device ??= Gen3Device.Fm9;
			int GridRows = device.GridRows, GridColumns = device.GridColumns;
			ReadOnlySpan<byte> rawPatch = dump.RawPatch.Span;
			ushort decompressedSize = ReadUInt16(rawPatch, DecompressedSizeOffset);
			ushort compressedSize = ReadUInt16(rawPatch, CompressedSizeOffset);

			if (CompressedBodyOffset + compressedSize > rawPatch.Length)
			{
				throw new InvalidDataException("The FM9 raw preset patch has an invalid compressed-body size.");
			}

			byte[] body = Decompress(
				rawPatch.Slice(CompressedBodyOffset, compressedSize),
				decompressedSize);
			int requiredBodyLength = GridOffset + (GridRows * GridColumns * GridCellSize);

			if (body.Length < requiredBodyLength)
			{
				throw new InvalidDataException("The FM9 decompressed preset body is too small for its grid.");
			}

			List<Fm9PresetGridCell> gridCells = [];

			for (int column = 0; column < GridColumns; column++)
			{
				for (int row = 0; row < GridRows; row++)
				{
					int offset = GridOffset + ((column * GridRows + row) * GridCellSize);
					ushort effectId = ReadUInt16(body, offset);
					ushort routeMask = ReadUInt16(body, offset + sizeof(ushort));

					gridCells.Add(new Fm9PresetGridCell(
						row + 1,
						column + 1,
						effectId == 0 ? null : effectId,
						routeMask));
				}
			}

			return new Fm9PresetBody(
				decompressedSize,
				compressedSize,
				gridCells,
				body,
				ReadBlockRecords(body))
			{
				Device = device
			};
		}

		private static byte[] Decompress(ReadOnlySpan<byte> compressedData, int outputSize)
		{
			if (compressedData.IsEmpty || outputSize == 0)
			{
				throw new InvalidDataException("The FM9 preset body is empty.");
			}

			BitReader reader = new(compressedData);
			HuffmanNode root = ReadTree(reader, 0);
			byte[] output = new byte[outputSize];

			for (int index = 0; index < output.Length; index++)
			{
				HuffmanNode node = root;

				while (!node.IsLeaf)
				{
					node = reader.ReadBit() == 0 ? node.Left! : node.Right!;
				}

				output[index] = node.Symbol;
			}

			return output;
		}

		private static HuffmanNode ReadTree(BitReader reader, int depth)
		{
			if (depth > 256)
			{
				throw new InvalidDataException("The FM9 preset Huffman tree exceeds the supported depth.");
			}

			return reader.ReadBit() == 1
				? new HuffmanNode(reader.ReadByte())
				: new HuffmanNode(
					ReadTree(reader, depth + 1),
					ReadTree(reader, depth + 1));
		}

		private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
		{
			return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
		}

		private static IReadOnlyList<Fm9PresetBlockRecord> ReadBlockRecords(
			ReadOnlySpan<byte> body)
		{
			const int headerWordCount = 23;
			const int modifierColumnCount = 25;
			const int modifierRowCount = 1;
			int modifierRecordSize = (headerWordCount + modifierColumnCount) * sizeof(ushort);
			int startOffset = -1;

			for (int offset = 0x200; offset + (modifierRecordSize * 2) <= body.Length; offset += 2)
			{
				if (ReadUInt16(body, offset + 30) == modifierColumnCount &&
					ReadUInt16(body, offset + 32) == modifierRowCount &&
					ReadUInt16(body, offset + modifierRecordSize + 30) == modifierColumnCount &&
					ReadUInt16(body, offset + modifierRecordSize + 32) == modifierRowCount)
				{
					startOffset = offset;
					break;
				}
			}

			if (startOffset < 0)
			{
				throw new InvalidDataException("The FM9 preset body does not contain a block-record chain.");
			}

			List<Fm9PresetBlockRecord> records = [];

			for (int offset = startOffset; offset + (headerWordCount * sizeof(ushort)) <= body.Length;)
			{
				ushort columns = ReadUInt16(body, offset + 30);
				ushort rows = ReadUInt16(body, offset + 32);

				if (columns == 0 || rows == 0 || columns > 500 || rows > 8)
				{
					break;
				}

				int recordSize = (headerWordCount + (columns * rows)) * sizeof(ushort);

				if (offset + recordSize > body.Length)
				{
					break;
				}

				records.Add(new Fm9PresetBlockRecord(offset, columns, rows, recordSize));
				offset += recordSize;
			}

			return records;
		}

		private sealed class HuffmanNode
		{
			public HuffmanNode(byte symbol)
			{
				Symbol = symbol;
			}

			public HuffmanNode(HuffmanNode left, HuffmanNode right)
			{
				Left = left;
				Right = right;
			}

			public bool IsLeaf => Left is null;
			public byte Symbol { get; }
			public HuffmanNode? Left { get; }
			public HuffmanNode? Right { get; }
		}

		private sealed class BitReader
		{
			private readonly ReadOnlyMemory<byte> _bytes;
			private int _bitOffset;

			public BitReader(ReadOnlySpan<byte> bytes)
			{
				_bytes = bytes.ToArray();
			}

			public int ReadBit()
			{
				if (_bitOffset >= _bytes.Length * 8)
				{
					throw new InvalidDataException("The FM9 preset Huffman data ended unexpectedly.");
				}

				int byteIndex = _bitOffset / 8;
				int bitIndex = _bitOffset % 8;
				_bitOffset++;
				return (_bytes.Span[byteIndex] >> (7 - bitIndex)) & 1;
			}

			public byte ReadByte()
			{
				byte value = 0;

				for (int index = 0; index < 8; index++)
				{
					value = (byte)((value << 1) | ReadBit());
				}

				return value;
			}
		}
	}
}
