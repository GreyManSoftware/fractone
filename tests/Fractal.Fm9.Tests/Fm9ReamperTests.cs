using Fractal.Companion.Protocol;
using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9ReamperTests : IDisposable
	{
		private const int Rate = 44_100;
		private readonly TemporaryFolder _folder = new();
		private readonly List<int> _scenesPlayed = [];
		private FakeFm9 _fm9 = null!;

		public void Dispose() => _folder.Dispose();

		private Fm9Reamper Reamper(string fixture)
		{
			Fm9PresetDump preset = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path(fixture)));
			_fm9 = new(preset) { EditBuffer = preset, Preset = 505 };
			_fm9.Slots[505] = preset;
			Fm9Connection connection = new(() => _fm9, _folder.File("midi.lock"));
			Fm9Live live = new(connection, _folder.File("state")) { Wait = (_, _) => Task.CompletedTask };
			Fm9Device device = new(connection, new Fm9Settings { WorkFolder = _folder.Root }) { Wait = (_, _) => Task.CompletedTask };
			// The amp: darker and quieter than the DI, then the Output block's master and scene trim, as on the FM9.
			// Records which scene was playing.
			return new(live, device, async (dry, rate, options, token) =>
			{
				int scene = _fm9.Scene;
				_scenesPlayed.Add(scene);
				Fm9LiveBlockState output = await live.GetAsync(new("Output", 1), null, ["Level", $"Scene {scene} Level"], token);
				double decibels = output.Settings.Values.Sum(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
				float gain = (float)Math.Pow(10, decibels / 20);
				int total = dry[0].Length + (int)(options.TailSeconds * rate);
				float[][] wet = dry.Select(channel =>
				{
					float[] output = new float[total];
					float state = 0;
					for (int index = 0; index < channel.Length; index++) output[index] = gain * (state += 0.2f * ((channel[index] * 0.5f) - state));
					return output;
				}).ToArray();
				return new MultichannelAudioBuffer(rate, wet);
			}) { Wait = (_, _) => Task.CompletedTask };
		}

		private string Di(double silenceSeconds, double playingSeconds)
		{
			Random random = new(3);
			float[] samples = [.. new float[(int)(silenceSeconds * Rate)],
				.. Enumerable.Range(0, (int)(playingSeconds * Rate)).Select(_ => (float)((random.NextDouble() * 2) - 1) * 0.3f)];
			string path = _folder.File("di.wav");
			using FileStream stream = File.Create(path);
			WaveFileWriter.WritePcm24(new MultichannelAudioBuffer(Rate, [samples]), stream);
			return path;
		}

		[DictionaryFact]
		public async Task EachSceneIsSelectedThenRecordedAndMeasured()
		{
			Fm9Reamper reamper = Reamper("fm9-uploaded-usb-input.syx");

			Fm9ReampResult result = await reamper.RunAsync(Di(1, 6), [1, 3], _folder.File("reamp"), new Fm9ReampOptions(), CancellationToken.None);

			Assert.Equal([1, 3], _scenesPlayed);
			Assert.Equal([1, 3], result.Takes.Select(take => take.Scene));
			Assert.All(result.Takes, take => Assert.True(File.Exists(take.File)));
			Assert.InRange(result.TrimmedSeconds, 0.9, 1.0);
			Assert.All(result.Takes, take => Assert.InRange(take.Measurement!.ActiveSeconds, 5.5, 6.5));
			Assert.All(result.Takes, take => Assert.Equal(take.Measurement!.IntegratedLufs, take.Level!.Lufs));
		}

		[DictionaryFact]
		public async Task WithAStemEachSceneGetsItsOwnFittedEqChannel()
		{
			Fm9Reamper reamper = Reamper("fm9-uploaded-usb-input.syx");
			// The target is the bright DI itself, so each darkened take needs its top end back.
			string di = Di(0, 6);

			Fm9ReampResult result = await reamper.RunAsync(di, [1, 2], _folder.File("reamp"),
				new Fm9ReampOptions { Stem = di, Match = true }, CancellationToken.None);

			Assert.True(result.Matched is not null, string.Join(" ", result.Notes));
			Assert.All(result.Takes, take => Assert.NotNull(take.Comparison));
			Fm9PresetSummary matched = Fm9Presets.Describe(result.Matched!.Output);
			string first = matched.Scenes[0].Blocks.Single(block => block.Block == "Parametric EQ 1").Channel;
			string second = matched.Scenes[1].Blocks.Single(block => block.Block == "Parametric EQ 1").Channel;
			Assert.NotEqual(first, second);
		}

		[DictionaryFact]
		public async Task LevellingSetsThePresetSoItsLoudestScenePeaksAtTheTarget()
		{
			Fm9Reamper reamper = Reamper("fm9-uploaded-usb-input.syx");
			Fm9Live live = new(new Fm9Connection(() => _fm9, _folder.File("midi2.lock")), _folder.File("state2")) { Wait = (_, _) => Task.CompletedTask };
			double before = double.Parse((await live.GetAsync(new("Output", 1), null, ["Level"], CancellationToken.None)).Settings["Level"],
				System.Globalization.CultureInfo.InvariantCulture);

			Fm9ReampResult result = await reamper.RunAsync(Di(0, 4), [1, 2, 3, 4, 5, 6, 7, 8], _folder.File("reamp"),
				new Fm9ReampOptions { Level = true }, CancellationToken.None);

			Assert.Equal(Fm9PresetLevelling.DefaultPeakTarget, result.Levelling!.PredictedLoudestPeak);
			double after = double.Parse((await live.GetAsync(new("Output", 1), null, ["Level"], CancellationToken.None)).Settings["Level"],
				System.Globalization.CultureInfo.InvariantCulture);
			Assert.Equal(before + result.Levelling.MasterChange, after, 0.15);
			Assert.NotEmpty(result.Probes!);
			Assert.Equal(8, result.Takes.Count);
			Assert.Equal(1, _fm9.Scene);
		}

		[DictionaryFact]
		public async Task APresetWithoutTheUsbInputIsRefusedBeforeAnythingPlays()
		{
			Fm9Reamper reamper = Reamper("metallica-eight-scenes.syx");

			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				reamper.RunAsync(Di(0, 3), [1], _folder.File("reamp"), new Fm9ReampOptions(), CancellationToken.None));

			Assert.Empty(_scenesPlayed);
		}

		[Fact]
		public void ADiIsResampledToTheDeviceRateKeepingItsLength()
		{
			float[] samples = [.. Enumerable.Range(0, Rate).Select(index => (float)Math.Sin(2 * Math.PI * 440 * index / Rate))];

			float[][] resampled = Fm9Reamper.Resample([samples], Rate, 48_000);

			Assert.InRange(resampled[0].Length, 47_990, 48_010);
			Assert.InRange(resampled[0].Skip(1000).Take(46_000).Max(), 0.95f, 1.05f);
		}

		[Fact]
		public void TheFirstNoteIsFoundWithAShortLeadIn()
		{
			float[] samples = [.. new float[Rate * 2], .. Enumerable.Repeat(0.5f, Rate)];

			Assert.Equal((Rate * 2) - (Rate / 20), Fm9Reamper.FirstNote([samples, samples], Rate));
		}
	}
}
