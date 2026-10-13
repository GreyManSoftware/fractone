namespace Fractal.Companion.Protocol
{
	public sealed class Fm9EmptyPresetSlotException()
		: Exception("The FM9 preset slot is empty (erased flash), so it holds no preset to read.");

	public sealed class Fm9PresetDump
	{
		private const byte PresetDumpHeaderFunction = 0x77;
		private const byte PresetDumpDataFunction = 0x78;
		private const byte PresetDumpFooterFunction = 0x79;
		private const int DataFrameCount = 8;
		private const int WordsPerDataFrame = 1_024;
		private const int RawPatchLength = DataFrameCount * WordsPerDataFrame * sizeof(ushort);
		private const int PresetNameOffset = 0x08;
		private const int PresetNameLength = 32;
		private readonly IReadOnlyList<FractalSysExFrame> _frames;
		private readonly byte[] _rawPatch;

		private Fm9PresetDump(
			IReadOnlyList<FractalSysExFrame> frames,
			byte[] rawPatch)
		{
			_frames = frames;
			_rawPatch = rawPatch;
		}

		public IReadOnlyList<FractalSysExFrame> Frames => _frames;
		public ushort FormatVersion => (ushort)(_rawPatch[0] | (_rawPatch[1] << 8));
		public ushort RawPatchXor => CalculateRawPatchXor(_rawPatch);
		internal ReadOnlyMemory<byte> RawPatch => _rawPatch;
		public string PresetName
		{
			get
			{
				string value = System.Text.Encoding.ASCII.GetString(
					_rawPatch,
					PresetNameOffset,
					PresetNameLength);
				int terminatorIndex = value.IndexOf('\0');

				return (terminatorIndex >= 0 ? value[..terminatorIndex] : value).TrimEnd();
			}
		}

		public static Fm9PresetDump Parse(ReadOnlySpan<byte> bytes)
		{
			if (bytes.IsEmpty)
			{
				throw new InvalidDataException("An FM9 preset dump cannot be empty.");
			}

			// Other Fractal gen-3 units share the format but not the record layouts: say so rather than call the file corrupt.
			if (bytes.Length > 5 && bytes[0] == 0xF0 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x74 && bytes[4] != 0x12)
			{
				string device = bytes[4] switch { 0x10 => "an Axe-Fx III", 0x11 => "an FM3", _ => $"another Fractal device (model 0x{bytes[4]:X2})" };
				throw new InvalidDataException($"This preset is from {device}; these tools read and write FM9 presets only so far.");
			}

			List<FractalSysExFrame> frames = [];
			int offset = 0;

			while (offset < bytes.Length)
			{
				if (bytes[offset] != 0xF0)
				{
					throw new InvalidDataException(
						$"Expected an FM9 SysEx frame at byte offset {offset}.");
				}

				int terminatorOffset = bytes[(offset + 1)..].IndexOf((byte)0xF7);

				if (terminatorOffset < 0)
				{
					throw new InvalidDataException(
						$"The FM9 SysEx frame at byte offset {offset} has no terminator.");
				}

				int frameLength = terminatorOffset + 2;
				byte[] frameBytes = bytes.Slice(offset, frameLength).ToArray();

				if (!FractalSysExCodec.IsValidFm9Frame(frameBytes))
				{
					throw new InvalidDataException(
						$"The FM9 SysEx frame at byte offset {offset} is invalid.");
				}

				frames.Add(new FractalSysExFrame(frameBytes));
				offset += frameLength;
			}

			ValidateDumpFrameOrder(frames);
			byte[] rawPatch = ReassembleRawPatch(frames);
			ValidateFooter(frames[^1], rawPatch);
			ValidateCrc(rawPatch);
			return new Fm9PresetDump(frames, rawPatch);
		}

		public byte[] ToArray()
		{
			int byteCount = _frames.Sum(frame => frame.Bytes.Length);
			byte[] bytes = new byte[byteCount];
			int offset = 0;

			foreach (FractalSysExFrame frame in _frames)
			{
				frame.Bytes.Span.CopyTo(bytes.AsSpan(offset));
				offset += frame.Bytes.Length;
			}

			return bytes;
		}

		public bool HasSameRawPatch(Fm9PresetDump other)
		{
			ArgumentNullException.ThrowIfNull(other);
			return _rawPatch.AsSpan().SequenceEqual(other._rawPatch);
		}

		public bool TargetsEditBuffer
		{
			get
			{
				ReadOnlySpan<byte> header = _frames[0].Bytes.Span;
				return header.Length > 9 && header[6] == 0x7F && header[7] == 0x7F;
			}
		}

		public Fm9PresetDump ForEditBufferImport()
		{
			if (TargetsEditBuffer)
			{
				return this;
			}

			byte[] headerPayload = _frames[0].Bytes.Span[6..^2].ToArray();

			if (headerPayload.Length < 2)
			{
				throw new InvalidDataException("The FM9 preset header payload is too short.");
			}

			headerPayload[0] = 0x7F;
			headerPayload[1] = 0x7F;
			return CreateFromRawPatch(
				CreateFrame(PresetDumpHeaderFunction, headerPayload),
				_rawPatch.ToArray());
		}

		public Fm9PresetDump WithPresetName(string presetName)
		{
			if (string.IsNullOrWhiteSpace(presetName) || presetName.Length > 31 ||
				presetName.Any(character => character is < ' ' or > '~'))
			{
				throw new ArgumentException(
					"An FM9 preset name must contain 1 to 31 printable ASCII characters.",
					nameof(presetName));
			}

			byte[] rawPatch = _rawPatch.ToArray();
			byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(presetName);
			nameBytes.CopyTo(rawPatch, PresetNameOffset);
			rawPatch[PresetNameOffset + nameBytes.Length] = 0;
			rawPatch[4] = 0;
			rawPatch[5] = 0;
			ushort crc = CalculateCrc(rawPatch);
			rawPatch[4] = (byte)crc;
			rawPatch[5] = (byte)(crc >> 8);

			return CreateFromRawPatch(_frames[0], rawPatch);
		}

		private static void ValidateDumpFrameOrder(IReadOnlyList<FractalSysExFrame> frames)
		{
			if (frames.Count != DataFrameCount + 2 ||
				frames[0].Bytes.Span[5] != PresetDumpHeaderFunction ||
				frames[^1].Bytes.Span[5] != PresetDumpFooterFunction)
			{
				throw new InvalidDataException(
					"An FM9 preset dump must contain a 0x77 header, at least one 0x78 data frame, and a 0x79 footer.");
			}

			for (int index = 1; index < frames.Count - 1; index++)
			{
				if (frames[index].Bytes.Span[5] != PresetDumpDataFunction)
				{
					throw new InvalidDataException(
						$"Expected FM9 preset data function 0x78 at frame {index}.");
				}
			}
		}

		private static byte[] ReassembleRawPatch(IReadOnlyList<FractalSysExFrame> frames)
			{
				byte[] rawPatch = new byte[RawPatchLength];
				int rawPatchOffset = 0;

				for (int frameIndex = 1; frameIndex <= DataFrameCount; frameIndex++)
				{
					ReadOnlySpan<byte> frame = frames[frameIndex].Bytes.Span;
					ReadOnlySpan<byte> payload = frame[6..^2];

					if (payload.Length != 2 + (WordsPerDataFrame * 3) ||
						payload[0] != 0x00 ||
						payload[1] != 0x08)
					{
						throw new InvalidDataException(
							$"FM9 preset data frame {frameIndex} has an unexpected payload layout.");
					}

					ReadOnlySpan<byte> packedWords = payload[2..];

					for (int wordIndex = 0; wordIndex < WordsPerDataFrame; wordIndex++)
					{
						int packedOffset = wordIndex * 3;
						byte lowSeptet = packedWords[packedOffset];
						byte middleSeptet = packedWords[packedOffset + 1];
						byte highSeptet = packedWords[packedOffset + 2];

						if (highSeptet > 0x03)
						{
							throw new InvalidDataException(
								$"FM9 preset data frame {frameIndex} contains an invalid packed word.");
						}

						ushort value = (ushort)(
							lowSeptet |
							(middleSeptet << 7) |
							(highSeptet << 14));
						rawPatch[rawPatchOffset++] = (byte)value;
						rawPatch[rawPatchOffset++] = (byte)(value >> 8);
					}
				}

				if (rawPatch[2] != 0x55 || rawPatch[3] != 0xAA)
				{
					// A never-used slot reads back as erased flash: every byte 0xFF.
					if (rawPatch.AsSpan().IndexOfAnyExcept((byte)0xFF) < 0)
						throw new Fm9EmptyPresetSlotException();
					throw new InvalidDataException("The FM9 raw preset patch has an invalid magic value.");
				}

				return rawPatch;
		}

		private static void ValidateFooter(FractalSysExFrame footer, ReadOnlySpan<byte> rawPatch)
		{
				ReadOnlySpan<byte> payload = footer.Bytes.Span[6..^2];

				if (payload.Length != 3 || payload[2] > 0x03)
				{
					throw new InvalidDataException("The FM9 preset dump footer has an invalid payload.");
				}

				ushort encodedXor = (ushort)(
					payload[0] |
					(payload[1] << 7) |
					(payload[2] << 14));
				ushort expectedXor = CalculateRawPatchXor(rawPatch);

				if (encodedXor != expectedXor)
				{
					throw new InvalidDataException(
						"The FM9 preset dump footer does not match the raw patch XOR.");
				}
		}

		private static ushort CalculateRawPatchXor(ReadOnlySpan<byte> rawPatch)
		{
				ushort result = 0;

				for (int offset = 0; offset < rawPatch.Length; offset += sizeof(ushort))
				{
					result ^= (ushort)(rawPatch[offset] | (rawPatch[offset + 1] << 8));
				}

				return result;
		}

		internal static Fm9PresetDump CreateFromRawPatch(
				FractalSysExFrame header,
				byte[] rawPatch)
		{
				List<FractalSysExFrame> frames =
				[
					header
				];

				for (int chunkIndex = 0; chunkIndex < DataFrameCount; chunkIndex++)
				{
					byte[] payload = new byte[2 + (WordsPerDataFrame * 3)];
					payload[1] = 0x08;
					int rawOffset = chunkIndex * WordsPerDataFrame * sizeof(ushort);

					for (int wordIndex = 0; wordIndex < WordsPerDataFrame; wordIndex++)
					{
						ushort value = (ushort)(
							rawPatch[rawOffset] |
							(rawPatch[rawOffset + 1] << 8));
						int packedOffset = 2 + (wordIndex * 3);
						payload[packedOffset] = (byte)(value & 0x7F);
						payload[packedOffset + 1] = (byte)((value >> 7) & 0x7F);
						payload[packedOffset + 2] = (byte)((value >> 14) & 0x03);
						rawOffset += sizeof(ushort);
					}

					frames.Add(CreateFrame(PresetDumpDataFunction, payload));
				}

				ushort rawPatchXor = CalculateRawPatchXor(rawPatch);
				frames.Add(CreateFrame(
					PresetDumpFooterFunction,
					[
						(byte)(rawPatchXor & 0x7F),
						(byte)((rawPatchXor >> 7) & 0x7F),
						(byte)((rawPatchXor >> 14) & 0x03)
					]));

				ValidateDumpFrameOrder(frames);
				ValidateFooter(frames[^1], rawPatch);
				return new Fm9PresetDump(frames, rawPatch);
		}

		private static FractalSysExFrame CreateFrame(byte function, IReadOnlyList<byte> payload)
		{
				List<byte> frame =
				[
					0xF0,
					0x00,
					0x01,
					0x74,
					0x12,
					function,
					.. payload
				];
				byte checksum = 0xF0;

				foreach (byte value in frame.Skip(1))
				{
					checksum ^= value;
				}

				frame.Add((byte)(checksum & 0x7F));
				frame.Add(0xF7);
				return new FractalSysExFrame(frame.ToArray());
		}

		// The patch stores its CRC at bytes 4-5, computed with those two bytes zeroed.
		private static void ValidateCrc(byte[] rawPatch)
		{
			if (rawPatch.Length < 6)
			{
				throw new InvalidDataException("The FM9 preset patch is too short to carry a CRC.");
			}

			ushort stored = (ushort)(rawPatch[4] | (rawPatch[5] << 8));
			byte[] unsigned = rawPatch.ToArray();
			unsigned[4] = 0;
			unsigned[5] = 0;
			ushort calculated = CalculateCrc(unsigned);

			if (stored != calculated)
			{
				throw new InvalidDataException(
					$"The FM9 preset CRC 0x{stored:X4} does not match its contents (0x{calculated:X4}).");
			}
		}

		internal static ushort CalculateCrc(ReadOnlySpan<byte> rawPatch)
		{
				ushort crc = 0xAA55;

				foreach (byte value in rawPatch)
				{
					crc ^= (ushort)(value << 8);

					for (int bit = 0; bit < 8; bit++)
					{
						crc = (crc & 0x8000) != 0
							? (ushort)((crc << 1) ^ 0x1021)
							: (ushort)(crc << 1);
					}
				}

				return crc;
		}
	}
}
