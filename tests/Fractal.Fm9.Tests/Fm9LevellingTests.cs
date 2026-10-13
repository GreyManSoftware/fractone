using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9LevellingTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		private static readonly Dictionary<int, double> Flat = Enumerable.Range(1, 8).ToDictionary(scene => scene, _ => 0.0);

		[Fact]
		public void EachSceneMovesByTheGapToAnAbsoluteTarget()
		{
			IReadOnlyList<Fm9LevelPlan> plan = Fm9Levelling.Plan([new(1, -16, -6), new(2, -21.5, -9)], "-18", null, Flat);

			Assert.Equal([-2.0, 3.5], plan.Select(step => step.NewTrim));
			Assert.All(plan, step => Assert.Empty(step.Notes));
		}

		[Fact]
		public void MatchingASceneUsesItsMeasuredLoudnessAndOffsetsAddOnTop()
		{
			IReadOnlyList<Fm9LevelPlan> plan = Fm9Levelling.Plan([new(1, -17, -6), new(3, -19, -8), new(5, -18, -7)], "match scene 1",
				new Dictionary<int, double> { [5] = 2 }, Flat);

			Assert.Equal([0.0, 2.0, 3.0], plan.Select(step => step.NewTrim));
		}

		[Fact]
		public void ExistingTrimsAreTheStartingPoint()
		{
			Dictionary<int, double> trims = new(Flat) { [2] = -3 };

			Fm9LevelPlan step = Fm9Levelling.Plan([new(2, -20, -10)], "-18", null, trims).Single();

			Assert.Equal((-3.0, -1.0), (step.CurrentTrim, step.NewTrim));
		}

		[Fact]
		public void ATrimStopsAtItsLimitAndABoostThatWouldClipIsFlagged()
		{
			IReadOnlyList<Fm9LevelPlan> plan = Fm9Levelling.Plan([new(1, -45, -30), new(2, -24, -2)], "-18", null, Flat);

			Assert.Equal(20, plan[0].NewTrim);
			Assert.Contains(plan[0].Notes, note => note.Contains("short of the target", StringComparison.Ordinal));
			Assert.Equal(4.0, plan[1].PredictedPeakDecibels);
			Assert.Contains(plan[1].Notes, note => note.Contains("master Level", StringComparison.Ordinal));
		}

		[Fact]
		public void UnclearTargetsAreRefused()
		{
			Assert.Throws<ArgumentException>(() => Fm9Levelling.Plan([new(1, -18, -6)], "loud", null, Flat));
			Assert.Throws<ArgumentException>(() => Fm9Levelling.Plan([new(1, -18, -6)], "match 4", null, Flat));
		}

		[DictionaryFact]
		public async Task TrimsLandOnTheOutputBlockLiveAndCanBeUndone()
		{
			Fm9PresetDump metallica = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx")));
			FakeFm9 fm9 = new(metallica) { EditBuffer = metallica, Preset = 505 };
			fm9.Slots[505] = metallica;
			Fm9Live live = new(new Fm9Connection(() => fm9, _folder.File("midi.lock")), _folder.File("state")) { Wait = (_, _) => Task.CompletedTask };

			Fm9LiveResult result = await live.SetAsync(new("Output", 1), new Dictionary<string, string>
			{
				[Fm9Levelling.TrimSetting(3)] = "-2.5", [Fm9Levelling.TrimSetting(5)] = "1.5"
			}, null, CancellationToken.None);

			Assert.Equal(["-2.5", "1.5"], result.Changes.Select(change => change.After));
			await live.UndoAsync(1, CancellationToken.None);
			Assert.True(metallica.HasSameRawPatch(fm9.EditBuffer!));
		}
	}
}
