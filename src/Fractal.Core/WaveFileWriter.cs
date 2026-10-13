using System.Buffers.Binary;
using System.Text;

namespace Fractal.Core
{
	public static class WaveFileWriter
	{
		public const int Fm9SampleRate = 48_000;

		public static void WriteFm9CompatibleImpulseResponse(
			AudioBuffer impulseResponse,
			Stream stream)
		{
			ArgumentNullException.ThrowIfNull(impulseResponse);

			if (impulseResponse.SampleRate != Fm9SampleRate)
			{
				throw new ArgumentException(
					$"FM9 IR export requires a {Fm9SampleRate} Hz recording. Resample explicitly before exporting.",
					nameof(impulseResponse));
			}

			WritePcm24Mono(impulseResponse, stream);
		}

		public static void WritePcm24Mono(AudioBuffer audio, Stream stream)
		{
			ArgumentNullException.ThrowIfNull(audio);
			WritePcm24(new MultichannelAudioBuffer(audio.SampleRate, [audio.Samples]), stream);
		}

		public static void WritePcm24(MultichannelAudioBuffer audio, Stream stream)
		{
			ArgumentNullException.ThrowIfNull(audio);
			ArgumentNullException.ThrowIfNull(stream);
			audio.Validate();

			if (!stream.CanWrite)
			{
				throw new ArgumentException("The WAV output stream must be writable.", nameof(stream));
			}

			foreach (float sample in audio.Channels.SelectMany(channel => channel))
			{
				if (sample is < -1 or > 1)
				{
					throw new ArgumentOutOfRangeException(
						nameof(audio),
						"PCM export requires samples between -1 and 1. Adjust the signal level explicitly first.");
				}
			}

			int channelCount = audio.Channels.Count;
			uint dataLength = checked((uint)(audio.Channels[0].Count * channelCount * 3));
			uint paddingLength = dataLength & 1;
			uint riffLength = checked(36 + dataLength + paddingLength);
			using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(riffLength);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16U);
			writer.Write((ushort)1);
			writer.Write((ushort)channelCount);
			writer.Write((uint)audio.SampleRate);
			writer.Write((uint)(audio.SampleRate * channelCount * 3));
			writer.Write((ushort)(channelCount * 3));
			writer.Write((ushort)24);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(dataLength);

			for (int frame = 0; frame < audio.Channels[0].Count; frame++)
			{
				foreach (IReadOnlyList<float> channel in audio.Channels)
				{
					int value = (int)Math.Round(channel[frame] * 8_388_607);
					writer.Write((byte)(value & 0xFF));
					writer.Write((byte)((value >> 8) & 0xFF));
					writer.Write((byte)((value >> 16) & 0xFF));
				}
			}

			if (paddingLength == 1)
			{
				writer.Write((byte)0);
			}
		}
	}
}
