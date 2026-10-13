using System.Text;
using Concentus;
using Concentus.Oggfile;
using Fractal.Core;
using NAudio.Wave;

namespace Fractal.Fm9
{
	public sealed record AudioSource(string Format, bool Lossy, int SampleRate, int Channels);

	// Reads any common audio file into float channels at its own sample rate, choosing the decoder from the file's own
	// bytes rather than its extension: WAV and AIFF directly, OGG Vorbis and Opus with managed decoders, and MP3, AAC/M4A
	// and FLAC through Windows Media Foundation (Windows 10 and later; macOS and Linux need another decoder for those).
	public static class AudioFiles
	{
		public const string Supported = "WAV, AIFF, FLAC, MP3, M4A (AAC or ALAC), AAC, OGG Vorbis and Opus";

		public static MultichannelAudioBuffer Read(string path) => Read(path, 0, null, out _);

		public static MultichannelAudioBuffer Read(string path, out AudioSource source) => Read(path, 0, null, out source);

		// The window [start, start + length) in seconds; length null reads to the end.
		public static MultichannelAudioBuffer Read(string path, double start, double? length, out AudioSource source)
		{
			if (!File.Exists(path)) throw new FileNotFoundException("The audio file was not found.", path);
			if (start < 0) throw new ArgumentOutOfRangeException(nameof(start), "The start can't be negative.");
			byte[] head = new byte[4096];
			int read;
			using (FileStream probe = File.OpenRead(path)) read = probe.Read(head, 0, head.Length);
			string kind = Identify(head[..read]);
			if (kind == "M4A (AAC)" && Mp4Codec(path) == "alac") kind = "M4A (ALAC)";
			try
			{
				return kind switch
				{
					"WAV" => ReadWave(path, start, length, out source),
					"AIFF" => Decode(new AiffFileReader(path).ToSampleProvider(), new("AIFF", false, 0, 0), start, length, out source),
					"OGG Vorbis" => ReadVorbis(path, start, length, out source),
					"Opus" => ReadOpus(path, start, length, out source),
					"MP3" or "AAC" or "M4A (AAC)" or "M4A (ALAC)" or "FLAC" => ReadMediaFoundation(path, kind, start, length, out source),
					_ => throw new InvalidDataException($"'{Path.GetFileName(path)}' isn't an audio file Fractone can read. Supported: {Supported}.")
				};
			}
			catch (Exception exception) when (exception is not (InvalidDataException or PlatformNotSupportedException or ArgumentException or FileNotFoundException))
			{
				throw new InvalidDataException($"'{Path.GetFileName(path)}' looks like {kind} but couldn't be decoded ({exception.Message.Trim()}); it may be damaged. Supported: {Supported}.", exception);
			}
		}

		// The container and codec from the first bytes.
		internal static string Identify(byte[] head)
		{
			bool At(int offset, string text) => head.Length >= offset + text.Length && Encoding.ASCII.GetString(head, offset, text.Length) == text;
			if (At(0, "RIFF") && At(8, "WAVE") || At(0, "RF64") || At(0, "BW64")) return "WAV";
			if (At(0, "FORM") && (At(8, "AIFF") || At(8, "AIFC"))) return "AIFF";
			if (At(0, "fLaC")) return "FLAC";
			if (At(0, "OggS"))
			{
				string text = Encoding.ASCII.GetString(head);
				if (text.Contains("OpusHead", StringComparison.Ordinal)) return "Opus";
				if (text.Contains("vorbis", StringComparison.Ordinal)) return "OGG Vorbis";
				if (text.Contains("fLaC", StringComparison.Ordinal)) return "OGG FLAC";
				return "OGG";
			}
			if (At(4, "ftyp")) return "M4A (AAC)";
			if (At(0, "ID3")) return "MP3";
			if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return "AAC";
			if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "MP3";
			return "unknown";
		}

		// The codec in an MP4 file's sample description ('stsd' box, which may sit at the end of the file): "mp4a" or "alac".
		internal static string? Mp4Codec(string path)
		{
			byte[] bytes = File.ReadAllBytes(path);
			ReadOnlySpan<byte> marker = "stsd"u8;
			for (int at = bytes.AsSpan().IndexOf(marker); at >= 0 && at + 20 <= bytes.Length;)
			{
				string codec = Encoding.ASCII.GetString(bytes, at + 16, 4);
				if (codec is "mp4a" or "alac") return codec;
				int next = bytes.AsSpan(at + 4).IndexOf(marker);
				at = next < 0 ? -1 : at + 4 + next;
			}
			return null;
		}

		private static MultichannelAudioBuffer ReadWave(string path, double start, double? length, out AudioSource source)
		{
			try
			{
				using FileStream stream = File.OpenRead(path);
				MultichannelAudioBuffer audio = length is double seconds || start > 0
					? Fractal.Core.WaveFileReader.ReadChannelWindow(stream, start, length ?? double.MaxValue / 4)
					: Fractal.Core.WaveFileReader.ReadChannels(stream);
				source = new("WAV", false, audio.SampleRate, audio.Channels.Count);
				return audio;
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
			{
				// Encodings the plain reader doesn't take (extensible headers, 8-bit, A-law...): NAudio's reader does.
				return Decode(new NAudio.Wave.WaveFileReader(path).ToSampleProvider(), new("WAV", false, 0, 0), start, length, out source);
			}
		}

		private static MultichannelAudioBuffer ReadMediaFoundation(string path, string kind, double start, double? length, out AudioSource source)
		{
			if (!OperatingSystem.IsWindows())
				throw new PlatformNotSupportedException($"{kind} is decoded with Windows Media Foundation; on this system convert it to WAV, AIFF or OGG first.");
			bool lossy = kind is not ("FLAC" or "M4A (ALAC)");
			using MediaFoundationReader reader = new(path);
			return Decode(reader.ToSampleProvider(), new(kind, lossy, 0, 0), start, length, out source);
		}

		private static MultichannelAudioBuffer ReadVorbis(string path, double start, double? length, out AudioSource source)
		{
			using NVorbis.VorbisReader vorbis = new(path);
			return Decode(new PullSource(vorbis.SampleRate, vorbis.Channels, (buffer, count) => vorbis.ReadSamples(buffer, 0, count)),
				new("OGG Vorbis", true, 0, 0), start, length, out source);
		}

		private static MultichannelAudioBuffer ReadOpus(string path, double start, double? length, out AudioSource source)
		{
			using FileStream stream = File.OpenRead(path);
			// Opus always decodes at 48 kHz; stereo keeps a stereo file's image and doubles a mono one.
			IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48_000, 2);
			OpusOggReadStream ogg = new(decoder, stream);
			Queue<float> pending = new();
			return Decode(new PullSource(48_000, 2, (buffer, count) =>
			{
				while (pending.Count < count && ogg.HasNextPacket)
				{
					short[]? packet = ogg.DecodeNextPacket();
					if (packet is null) continue;
					foreach (short sample in packet) pending.Enqueue(sample / 32768f);
				}
				int filled = 0;
				while (filled < count && pending.Count > 0) buffer[filled++] = pending.Dequeue();
				return filled;
			}), new("Opus", true, 0, 0), start, length, out source);
		}

		// Reads interleaved floats into channels, skipping to the window.
		private static MultichannelAudioBuffer Decode(ISampleProvider provider, AudioSource kind, double start, double? length, out AudioSource source)
		{
			int rate = provider.WaveFormat.SampleRate, channels = provider.WaveFormat.Channels;
			long skip = (long)Math.Round(start * rate) * channels;
			long wanted = length is double seconds ? (long)Math.Round(seconds * rate) * channels : long.MaxValue;
			List<float>[] output = Enumerable.Range(0, channels).Select(_ => new List<float>()).ToArray();
			float[] buffer = new float[Math.Max(1, rate / 10) * channels];
			long position = 0, kept = 0;
			int count;
			while (kept < wanted && (count = provider.Read(buffer, 0, buffer.Length - (buffer.Length % channels))) > 0)
			{
				for (int index = 0; index < count && kept < wanted; index++, position++)
				{
					if (position < skip) continue;
					output[(int)(position % channels)].Add(buffer[index]);
					kept++;
				}
			}
			int frames = output.Min(channel => channel.Count);
			if (frames == 0) throw new InvalidDataException(start > 0 ? "The start is past the end of the audio." : "The file holds no audio.");
			source = kind with { SampleRate = rate, Channels = channels };
			return new MultichannelAudioBuffer(rate, output.Select(channel => channel.Take(frames).ToArray()).ToArray());
		}

		private sealed class PullSource(int rate, int channels, Func<float[], int, int> read) : ISampleProvider
		{
			public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

			public int Read(float[] buffer, int offset, int count)
			{
				if (offset == 0) return read(buffer, count);
				float[] scratch = new float[count];
				int filled = read(scratch, count);
				Array.Copy(scratch, 0, buffer, offset, filled);
				return filled;
			}
		}
	}
}
