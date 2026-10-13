using System.Buffers.Binary;
using System.Text;

namespace Fractal.Core
{
	public static class WaveFileReader
	{
		private const ushort PcmFormat = 0x0001;
		private const ushort IeeeFloatFormat = 0x0003;
		private const ushort ExtensibleFormat = 0xFFFE;

		public static AudioBuffer ReadMono(string path)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(path);

			using FileStream stream = File.OpenRead(path);
			return ReadMono(stream);
		}

		public static AudioBuffer ReadMono(Stream stream)
		{
			return Read(stream).Audio;
		}

		public static WaveAudioFile Read(Stream stream)
		{
			(WaveFormat format, byte[] data) = ReadData(stream);
			int frameCount = data.Length / format.BlockAlign;
			float[] monoSamples = new float[frameCount];

			for (int frame = 0; frame < frameCount; frame++)
			{
				double channelSum = 0;
				int frameOffset = frame * format.BlockAlign;
				for (int channel = 0; channel < format.Channels; channel++)
				{
					channelSum += DecodeSample(data, frameOffset + channel * format.BytesPerSample, format);
				}
				monoSamples[frame] = (float)(channelSum / format.Channels);
			}
			return new WaveAudioFile(new AudioBuffer(format.SampleRate, monoSamples),
				format.Channels, format.BitsPerSample, format.IsIeeeFloat);
		}

		public static MultichannelAudioBuffer ReadChannels(Stream stream, double maximumDurationSeconds = double.PositiveInfinity)
		{
			if (double.IsNaN(maximumDurationSeconds) || maximumDurationSeconds <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(maximumDurationSeconds));
			}
			(WaveFormat format, byte[] data) = ReadData(stream);
			if (format.Channels is < 1 or > 2)
			{
				throw new NotSupportedException("Tone matching supports mono or stereo WAV files.");
			}
			int frameCount = data.Length / format.BlockAlign;
			if ((double)frameCount / format.SampleRate > maximumDurationSeconds)
			{
				throw new ArgumentException($"The recording exceeds the {maximumDurationSeconds:F0}-second duration limit.", nameof(stream));
			}
			float[][] channels = new float[format.Channels][];
			for (int channel = 0; channel < channels.Length; channel++)
			{
				channels[channel] = new float[frameCount];
				for (int frame = 0; frame < frameCount; frame++)
				{
					channels[channel][frame] = DecodeSample(
						data, frame * format.BlockAlign + channel * format.BytesPerSample, format);
				}
			}
			return new MultichannelAudioBuffer(format.SampleRate, channels);
		}

		// Reads only [start, start + length) of the recording, seeking past everything else, so a long upload is never
		// decoded (or even held) in full. The window is cut short at the end of the recording.
		public static MultichannelAudioBuffer ReadChannelWindow(Stream stream, double startSeconds, double lengthSeconds)
		{
			if (!double.IsFinite(startSeconds) || startSeconds < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(startSeconds));
			}
			if (!double.IsFinite(lengthSeconds) || lengthSeconds <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(lengthSeconds));
			}
			(WaveFormat format, long dataOffset, long dataLength) = LocateData(stream);
			if (format.Channels is < 1 or > 2)
			{
				throw new NotSupportedException("Tone matching supports mono or stereo WAV files.");
			}
			long totalFrames = dataLength / format.BlockAlign;
			long startFrame = (long)Math.Round(startSeconds * format.SampleRate);
			if (startFrame >= totalFrames)
			{
				throw new ArgumentException(
					$"The start time {startSeconds:0.##} s is after the end of the {(double)totalFrames / format.SampleRate:0.##}-second recording.",
					nameof(startSeconds));
			}
			int frameCount = checked((int)Math.Min(totalFrames - startFrame, (long)Math.Round(lengthSeconds * format.SampleRate)));
			stream.Position = dataOffset + startFrame * format.BlockAlign;
			byte[] data = new byte[checked(frameCount * format.BlockAlign)];
			stream.ReadExactly(data);
			float[][] channels = new float[format.Channels][];
			for (int channel = 0; channel < channels.Length; channel++)
			{
				channels[channel] = new float[frameCount];
				for (int frame = 0; frame < frameCount; frame++)
				{
					channels[channel][frame] = DecodeSample(
						data, frame * format.BlockAlign + channel * format.BytesPerSample, format);
				}
			}
			return new MultichannelAudioBuffer(format.SampleRate, channels);
		}

		private static (WaveFormat Format, byte[] Data) ReadData(Stream stream)
		{
			(WaveFormat format, long dataOffset, long dataLength) = LocateData(stream);
			stream.Position = dataOffset;
			byte[] data = new byte[checked((int)dataLength)];
			stream.ReadExactly(data);
			return (format, data);
		}

		private static (WaveFormat Format, long DataOffset, long DataLength) LocateData(Stream stream)
		{
			ArgumentNullException.ThrowIfNull(stream);

			if (!stream.CanRead || !stream.CanSeek)
			{
				throw new ArgumentException(
					"The WAV stream must support reading and seeking.",
					nameof(stream));
			}

			using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: true);

			if (ReadFourCharacterCode(reader) != "RIFF")
			{
				throw new InvalidDataException("The file is not a RIFF WAV file.");
			}

			ReadUInt32(reader);

			if (ReadFourCharacterCode(reader) != "WAVE")
			{
				throw new InvalidDataException("The RIFF file does not contain WAVE audio.");
			}

			WaveFormat? format = null;
			long? dataOffset = null;
			long dataLength = 0;

			while (reader.BaseStream.Position < reader.BaseStream.Length)
			{
				string chunkName = ReadFourCharacterCode(reader);
				uint chunkLength = ReadUInt32(reader);

				if (chunkLength > int.MaxValue)
				{
					throw new InvalidDataException($"The {chunkName} WAV chunk is too large to process.");
				}
				if (chunkLength > reader.BaseStream.Length - reader.BaseStream.Position)
				{
					throw new InvalidDataException($"The {chunkName} WAV chunk exceeds the available file data.");
				}

				if (chunkName == "data")
				{
					// Audio is skipped here and read later, only as much as the caller needs.
					dataOffset = reader.BaseStream.Position;
					dataLength = chunkLength;
					reader.BaseStream.Seek(chunkLength, SeekOrigin.Current);
				}
				else
				{
					byte[] chunk = ReadExactly(reader, checked((int)chunkLength));
					if (chunkName == "fmt ")
					{
						format = ParseFormat(chunk);
					}
				}

				if ((chunkLength & 1) == 1)
				{
					ReadExactly(reader, 1);
				}
			}

			if (format is null)
			{
				throw new InvalidDataException("The WAV file does not contain a format chunk.");
			}

			if (dataOffset is null)
			{
				throw new InvalidDataException("The WAV file does not contain an audio data chunk.");
			}

			if (dataLength % format.BlockAlign != 0)
			{
				throw new InvalidDataException("The WAV audio data is not aligned to complete sample frames.");
			}

			return (format, dataOffset.Value, dataLength);
		}

		private static WaveFormat ParseFormat(byte[] formatChunk)
		{
			if (formatChunk.Length < 16)
			{
				throw new InvalidDataException("The WAV format chunk is incomplete.");
			}

			ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk);
			ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.AsSpan(2));
			uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(formatChunk.AsSpan(4));
			ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.AsSpan(12));
			ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.AsSpan(14));

			if (formatTag == ExtensibleFormat)
			{
				if (formatChunk.Length < 40)
				{
					throw new InvalidDataException("The extensible WAV format chunk is incomplete.");
				}

				formatTag = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.AsSpan(24));
			}

			if (channels == 0 || sampleRate == 0 || sampleRate > int.MaxValue)
			{
				throw new InvalidDataException("The WAV format contains invalid channel or sample-rate information.");
			}

			int bytesPerSample = bitsPerSample / 8;

			if (bitsPerSample % 8 != 0 || bytesPerSample == 0)
			{
				throw new InvalidDataException("The WAV bit depth must be a whole number of bytes.");
			}

			if (blockAlign != channels * bytesPerSample)
			{
				throw new InvalidDataException("The WAV block alignment does not match its channel and bit-depth metadata.");
			}

			bool isPcm = formatTag == PcmFormat &&
				(bitsPerSample == 8 || bitsPerSample == 16 || bitsPerSample == 24 || bitsPerSample == 32);
			bool isFloat = formatTag == IeeeFloatFormat && bitsPerSample == 32;

			if (!isPcm && !isFloat)
			{
				throw new NotSupportedException(
					$"WAV format 0x{formatTag:X4} with {bitsPerSample} bits per sample is not supported.");
			}

			return new WaveFormat(
				(int)sampleRate,
				channels,
				blockAlign,
				bitsPerSample,
				isFloat);
		}

		private static float DecodeSample(
			IReadOnlyList<byte> data,
			int offset,
			WaveFormat format)
		{
			if (format.IsIeeeFloat)
			{
				int bits = BinaryPrimitives.ReadInt32LittleEndian(
					new[] { data[offset], data[offset + 1], data[offset + 2], data[offset + 3] });
				return BitConverter.Int32BitsToSingle(bits);
			}

			return format.BitsPerSample switch
			{
				8 => (data[offset] - 128) / 128f,
				16 => BinaryPrimitives.ReadInt16LittleEndian(
					new[] { data[offset], data[offset + 1] }) / 32768f,
				24 => Decode24BitPcm(data, offset),
				32 => BinaryPrimitives.ReadInt32LittleEndian(
					new[] { data[offset], data[offset + 1], data[offset + 2], data[offset + 3] }) / 2_147_483_648f,
				_ => throw new InvalidOperationException("The WAV format was not validated.")
			};
		}

		private static float Decode24BitPcm(IReadOnlyList<byte> data, int offset)
		{
			int value = data[offset] |
				(data[offset + 1] << 8) |
				(data[offset + 2] << 16);

			if ((value & 0x80_0000) != 0)
			{
				value |= unchecked((int)0xFF00_0000);
			}

			return value / 8_388_608f;
		}

		private static string ReadFourCharacterCode(BinaryReader reader)
		{
			return Encoding.ASCII.GetString(ReadExactly(reader, 4));
		}

		private static uint ReadUInt32(BinaryReader reader)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(reader, 4));
		}

		private static byte[] ReadExactly(BinaryReader reader, int length)
		{
			byte[] bytes = reader.ReadBytes(length);

			if (bytes.Length != length)
			{
				throw new InvalidDataException("The WAV file ended before the declared chunk length.");
			}

			return bytes;
		}

		private sealed record WaveFormat(
			int SampleRate,
			int Channels,
			int BlockAlign,
			int BitsPerSample,
			bool IsIeeeFloat)
		{
			public int BytesPerSample => BitsPerSample / 8;
		}
	}

	public sealed record WaveAudioFile(
		AudioBuffer Audio,
		int Channels,
		int BitsPerSample,
		bool IsIeeeFloat);
}
