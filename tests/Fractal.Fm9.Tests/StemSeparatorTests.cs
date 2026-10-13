using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	public sealed class StemSeparatorTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		private static AudioMeasurement Measured(double lufs, double sub = -28, double air = -35) => new(44_100, 2, 60, 60, lufs, lufs - 2, -3, 0,
		[
			new("sub", 20, 60, sub), new("lows", 60, 250, -2), new("mud", 250, 1000, -10), new("1-2k", 1000, 2000, -14),
			new("bite", 2000, 4000, -8), new("fizz", 4000, 8000, -15), new("air", 8000, 12000, air)
		]);

		[Fact]
		public void RawAudioRoundTripsChannelByChannel()
		{
			string path = _folder.File("audio.f32");
			float[][] audio = [[0.1f, -0.2f, 0.3f], [1f, 0f, -1f]];

			StemSeparator.WriteRaw(path, audio);

			Assert.Equal(audio, StemSeparator.ReadRaw(path, 3));
		}

		[Fact]
		public void ACleanGuitarStemReportsOnlyItsShareOfTheMix()
		{
			IReadOnlyList<string> notes = StemSeparator.Notes("guitar", Measured(-20), Measured(-14));

			Assert.Equal(["The guitar stem plays 6.0 dB below the mix."], notes);
		}

		[Fact]
		public void BassAndCymbalBleedInAGuitarStemAreReported()
		{
			IReadOnlyList<string> notes = StemSeparator.Notes("guitar", Measured(-20, sub: -15, air: -20), Measured(-14));

			Assert.Contains(notes, note => note.StartsWith("Bass or kick bleed", StringComparison.Ordinal));
			Assert.Contains(notes, note => note.StartsWith("Cymbal bleed", StringComparison.Ordinal));
		}

		[Fact]
		public void AStemFarBelowTheMixSaysThereIsLittleOfTheInstrument() =>
			Assert.Contains("little or no guitar", Assert.Single(StemSeparator.Notes("guitar", Measured(-67, sub: -7), Measured(-16))));

		[Fact]
		public void ASilentStemSaysTheInstrumentWasNotFound() =>
			Assert.Contains("found no guitar", Assert.Single(StemSeparator.Notes("guitar", null, Measured(-14))));

		[Fact]
		public void AnNvidiaGpuIsEstimatedFasterThanACpuAndFewerThreadsSlower()
		{
			Assert.True(StemSeparator.Estimate("cuda", 60, 1, 4) < StemSeparator.Estimate("cpu", 60, 1, 4));
			Assert.True(StemSeparator.Estimate("cpu", 60, 1, 16) < StemSeparator.Estimate("cpu", 60, 1, 2));
			Assert.True(StemSeparator.Estimate("cpu", 60, 2, 16) > StemSeparator.Estimate("cpu", 60, 1, 16));
		}

		[Fact]
		public async Task AFirstUsePlanSaysWhatItWillInstallAndWhere()
		{
			SeparationPlan plan = await new StemSeparator(_folder.File("separator")).PlanAsync(60, 1, cpu: true, CancellationToken.None);

			Assert.False(plan.Installed);
			Assert.Equal("cpu", plan.Device.Kind);
			Assert.Contains("CPU build", plan.Install);
			Assert.Contains(plan.Notes, note => note.Contains("slower than a GPU", StringComparison.Ordinal));
		}

		[Fact]
		public async Task MoreThanTheAnalysisWindowIsRefusedBeforeAnythingInstalls()
		{
			StemSeparator separator = new(_folder.File("separator"));

			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => separator.SeparateAsync("mix.wav", 0, 600, "stem.wav", "rest.wav",
				"guitar", 1, false, _ => { }, CancellationToken.None));
			Assert.False(Directory.Exists(separator.Folder));
		}
	}
}
