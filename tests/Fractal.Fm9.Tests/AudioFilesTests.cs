using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	public sealed class AudioFilesTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Audio", name);

		private static double Frequency(MultichannelAudioBuffer audio)
		{
			IReadOnlyList<float> left = audio.Channels[0];
			int crossings = 0;
			for (int index = 1; index < left.Count; index++) if ((left[index - 1] < 0) != (left[index] < 0)) crossings++;
			return crossings / 2.0 / (left.Count / (double)audio.SampleRate);
		}

		[Theory]
		[InlineData("tone.wav", "WAV", false, 44_100)]
		[InlineData("tone.aiff", "AIFF", false, 44_100)]
		[InlineData("tone.flac", "FLAC", false, 44_100)]
		[InlineData("tone.mp3", "MP3", true, 44_100)]
		[InlineData("tone.m4a", "M4A (AAC)", true, 44_100)]
		[InlineData("tone-alac.m4a", "M4A (ALAC)", false, 44_100)]
		[InlineData("tone.aac", "AAC", true, 44_100)]
		[InlineData("tone.ogg", "OGG Vorbis", true, 44_100)]
		[InlineData("tone.opus", "Opus", true, 48_000)]
		public void EachCommonFormatDecodesToTheSameStereoTone(string file, string format, bool lossy, int rate)
		{
			MultichannelAudioBuffer audio = AudioFiles.Read(Fixture(file), out AudioSource source);

			Assert.Equal((format, lossy, rate, 2), (source.Format, source.Lossy, source.SampleRate, source.Channels));
			Assert.Equal(2, audio.Channels.Count);
			Assert.InRange(audio.Channels[0].Count / (double)audio.SampleRate, 0.95, 1.1);
			Assert.InRange(Frequency(audio), 430, 450);
			double wavPeak = AudioFiles.Read(Fixture("tone.wav")).Channels[0].Max(Math.Abs);
			Assert.InRange(20 * Math.Log10(audio.Channels[0].Max(Math.Abs) / wavPeak), -1, 1);
		}

		[Fact]
		public void AnMp3MeasuresLikeTheWavOfTheSameTone()
		{
			AudioMeasurement wav = AudioMeasurer.Measure(AudioFiles.Read(Fixture("tone.wav")));
			AudioMeasurement mp3 = AudioMeasurer.Measure(AudioFiles.Read(Fixture("tone.mp3")));

			// The encoder's start padding (about 25 ms) weighs on a 1 s clip's 400 ms loudness blocks: 0.33 dB here.
			Assert.InRange(mp3.IntegratedLufs - wav.IntegratedLufs, -0.5, 0.5);
			Assert.InRange(mp3.PeakDecibels - wav.PeakDecibels, -0.5, 0.5);
		}

		[Fact]
		public void TheFileIsReadByItsContentNotItsExtension()
		{
			string mislabelled = _folder.File("really an mp3.wav");
			File.Copy(Fixture("tone.mp3"), mislabelled);

			AudioFiles.Read(mislabelled, out AudioSource source);

			Assert.Equal("MP3", source.Format);
		}

		[Fact]
		public void AWindowReadsOnlyThatPart()
		{
			MultichannelAudioBuffer part = AudioFiles.Read(Fixture("tone.flac"), 0.25, 0.5, out _);

			Assert.Equal(22_050, part.Channels[0].Count);
		}

		[Theory]
		[InlineData("not-audio.mp3")]
		[InlineData("damaged.flac")]
		public void AFileThatIsNotUsableAudioNamesTheSupportedFormats(string file)
		{
			InvalidDataException error = Assert.Throws<InvalidDataException>(() => AudioFiles.Read(Fixture(file)));

			Assert.Contains("Supported: WAV, AIFF, FLAC, MP3", error.Message);
		}
	}
}
