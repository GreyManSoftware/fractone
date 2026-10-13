namespace Fractal.Companion.Protocol
{
	public static class Fm9PresetBodyEncoder
	{
		private const int DecompressedSizeOffset = 0x48;
		private const int CompressedSizeOffset = 0x4A;
		private const int CompressedBodyOffset = 0x4C;

		public static Fm9PresetDump Reencode(
			Fm9PresetDump source,
			ReadOnlySpan<byte> body)
		{
			ArgumentNullException.ThrowIfNull(source);

			if (body.IsEmpty || body.Length > ushort.MaxValue)
			{
				throw new ArgumentOutOfRangeException(
					nameof(body),
					"An FM9 decompressed preset body must contain 1 through 65,535 bytes.");
			}

			byte[] compressedBody = Compress(body);
			byte[] rawPatch = source.RawPatch.ToArray();

			if (CompressedBodyOffset + compressedBody.Length > rawPatch.Length)
			{
				throw new InvalidOperationException(
					"The compressed FM9 preset body does not fit in the source patch.");
			}

			WriteUInt16(rawPatch, DecompressedSizeOffset, checked((ushort)body.Length));
			WriteUInt16(rawPatch, CompressedSizeOffset, checked((ushort)compressedBody.Length));
			Array.Clear(rawPatch, CompressedBodyOffset, rawPatch.Length - CompressedBodyOffset);
			compressedBody.CopyTo(rawPatch, CompressedBodyOffset);
			rawPatch[4] = 0;
			rawPatch[5] = 0;
			WriteUInt16(rawPatch, 4, Fm9PresetDump.CalculateCrc(rawPatch));

			return Fm9PresetDump.CreateFromRawPatch(source.Frames[0], rawPatch);
		}

		private static byte[] Compress(ReadOnlySpan<byte> body)
		{
			Dictionary<byte, int> frequencies = [];

			foreach (byte value in body)
			{
				frequencies[value] = frequencies.TryGetValue(value, out int count) ? count + 1 : 1;
			}

			// Fractal's tie-break, so an untouched preset re-encodes to Fractal's own bytes: leaves start in descending symbol
			// order, and a merged node sorts before every node of equal weight, the newest first (FracCompat, 2026-10-08:
			// reproduced 1,280 of 1,280 factory and FM9 compressed streams).
			int order = 0;
			List<WeightedNode> nodes = frequencies
				.OrderByDescending(pair => pair.Key)
				.Select(pair => new WeightedNode(new HuffmanNode(pair.Key), pair.Value, order++))
				.ToList();
			int merged = 0;
			HuffmanNode root;

			if (nodes.Count == 1)
			{
				HuffmanNode onlyNode = nodes[0].Node;
				root = new HuffmanNode(onlyNode, new HuffmanNode((byte)(onlyNode.Symbol + 1)));
			}
			else
			{
				while (nodes.Count > 1)
				{
					nodes.Sort((left, right) =>
					{
						int countComparison = left.Count.CompareTo(right.Count);
						return countComparison != 0
							? countComparison
							: left.Order.CompareTo(right.Order);
					});
					WeightedNode left = nodes[0];
					WeightedNode right = nodes[1];
					nodes.RemoveRange(0, 2);
					nodes.Add(new WeightedNode(
						new HuffmanNode(left.Node, right.Node),
						left.Count + right.Count,
						-(++merged)));
				}

				root = nodes[0].Node;
			}

			BitWriter writer = new();
			WriteTree(root, writer);
			Dictionary<byte, IReadOnlyList<int>> codebook = [];
			BuildCodebook(root, [], codebook);

			foreach (byte value in body)
			{
				foreach (int bit in codebook[value])
				{
					writer.WriteBit(bit);
				}
			}

			return writer.ToArray();
		}

		private static void WriteTree(HuffmanNode node, BitWriter writer)
		{
			if (node.IsLeaf)
			{
				writer.WriteBit(1);
				writer.WriteByte(node.Symbol);
				return;
			}

			writer.WriteBit(0);
			WriteTree(node.Left!, writer);
			WriteTree(node.Right!, writer);
		}

		private static void BuildCodebook(
			HuffmanNode node,
			IReadOnlyList<int> prefix,
			IDictionary<byte, IReadOnlyList<int>> codebook)
		{
			if (node.IsLeaf)
			{
				codebook[node.Symbol] = prefix.Count == 0 ? [0] : prefix;
				return;
			}

			BuildCodebook(node.Left!, [.. prefix, 0], codebook);
			BuildCodebook(node.Right!, [.. prefix, 1], codebook);
		}

		private static void WriteUInt16(byte[] destination, int offset, ushort value)
		{
			destination[offset] = (byte)value;
			destination[offset + 1] = (byte)(value >> 8);
		}

		private sealed record WeightedNode(HuffmanNode Node, int Count, int Order);

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

		private sealed class BitWriter
		{
			private readonly List<byte> _bytes = [];
			private byte _currentByte;
			private int _bitCount;

			public void WriteBit(int bit)
			{
				_currentByte = (byte)((_currentByte << 1) | (bit & 1));
				_bitCount++;

				if (_bitCount == 8)
				{
					_bytes.Add(_currentByte);
					_currentByte = 0;
					_bitCount = 0;
				}
			}

			public void WriteByte(byte value)
			{
				for (int bit = 7; bit >= 0; bit--)
				{
					WriteBit((value >> bit) & 1);
				}
			}

			public byte[] ToArray()
			{
				if (_bitCount > 0)
				{
					_bytes.Add((byte)(_currentByte << (8 - _bitCount)));
				}

				return _bytes.ToArray();
			}
		}
	}
}
