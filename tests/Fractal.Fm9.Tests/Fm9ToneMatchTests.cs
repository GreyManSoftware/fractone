using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9ToneMatchTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		[DictionaryFact]
		public void AMatchWritesTheFittedBandsIntoAFreeEqChannelAndANewScene()
		{
			string input = Fixtures.Path("fm9-uploaded-usb-input.syx");
			// The take is bright noise; the target is the same noise darkened, so the fit must cut the top end.
			float[] noise = Noise(48_000 * 8, seed: 7);
			string wet = Wave("wet.wav", noise);
			string target = Wave("target.wav", LowPass(noise, 0.15f));
			Fm9PresetSummary before = Fm9Presets.Describe(input);
			string[] played = before.Blocks.Single(block => block.Block == "Parametric EQ 1").Channels
				.Where(channel => channel.Scenes.Count > 0).Select(channel => channel.Channel).ToArray();

			Fm9ToneMatchResult result = Fm9ToneMatch.MatchScene(new(input, _folder.File("matched.syx"), 1, wet, target)
			{
				NewScene = 8,
				NewSceneName = "Matched",
				Preview = _folder.File("preview.wav")
			});

			Assert.Equal("Matched", result.Status);
			Assert.True(result.DeviationAfterDecibels < result.DeviationBeforeDecibels);
			Assert.NotNull(result.Edit);
			Fm9PresetSummary after = Fm9Presets.Describe(result.Edit!.Output);
			Fm9SceneBlock eq = after.Scenes[7].Blocks.Single(block => block.Block == "Parametric EQ 1");
			Assert.False(eq.Bypassed);
			Assert.DoesNotContain(eq.Channel, played);
			Assert.Equal("Matched", after.Scenes[7].Name);
			IReadOnlyDictionary<string, string> bands = Fm9Presets.GetChannel(result.Edit.Output, new("Parametric EQ", 1), eq.Channel);
			Assert.Contains(Enumerable.Range(1, 5), band => double.Parse(bands[$"Gain {band}"], System.Globalization.CultureInfo.InvariantCulture) < -1);
			Assert.True(File.Exists(result.Preview));
			// The other scenes still sound as they did.
			Assert.Equal(before.Scenes.Take(7).Select(scene => scene.Blocks), after.Scenes.Take(7).Select(scene => scene.Blocks), new SequenceComparer());
		}

		private string Wave(string name, float[] samples)
		{
			string path = _folder.File(name);
			using FileStream stream = File.Create(path);
			WaveFileWriter.WritePcm24(new MultichannelAudioBuffer(48_000, [samples, samples]), stream);
			return path;
		}

		private static float[] Noise(int count, int seed)
		{
			Random random = new(seed);
			return Enumerable.Range(0, count).Select(_ => (float)((random.NextDouble() * 2) - 1) * 0.25f).ToArray();
		}

		private static float[] LowPass(float[] samples, float alpha)
		{
			float[] result = new float[samples.Length];
			float state = 0;
			for (int index = 0; index < samples.Length; index++)
				result[index] = state += alpha * (samples[index] - state);
			return result;
		}

		private sealed class SequenceComparer : IEqualityComparer<IReadOnlyList<Fm9SceneBlock>>
		{
			public bool Equals(IReadOnlyList<Fm9SceneBlock>? x, IReadOnlyList<Fm9SceneBlock>? y) => x!.SequenceEqual(y!);
			public int GetHashCode(IReadOnlyList<Fm9SceneBlock> obj) => obj.Count;
		}
	}
}
