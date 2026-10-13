namespace Fractal.Fm9.Tests
{
	public sealed class Fm9PresetLevellingTests
	{
		private static Fm9SceneLevelState Scene(int scene, double lufs, double peak, double trim = 0, double master = 0) => new(scene, lufs, peak, trim, master);

		// Scenes 3-8 measured like scene 1, so the plan covers the whole preset.
		private static Fm9SceneLevelState[] Preset(params Fm9SceneLevelState[] first) =>
			[.. first, .. Enumerable.Range(first.Length + 1, 8 - first.Length).Select(scene => Scene(scene, first[0].Lufs, first[0].PeakDecibels))];

		[Fact]
		public void TheLoudestSceneEndsUpPeakingAtTheTargetWithTheScenesMatched()
		{
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan([Scene(1, -20, -8), Scene(2, -24, -14)], "match 1", null, -9);

			// Scene 2 comes up 4 dB to match scene 1, then everything comes down 1 dB so scene 1 peaks at -9.
			Assert.Equal(-9, plan.PredictedLoudestPeak);
			Assert.Equal(plan.Scenes[0].TargetLufs, plan.Scenes[1].TargetLufs);
			Assert.Equal(-21, plan.Scenes[0].TargetLufs);
		}

		[Fact]
		public void WithEverySceneTheMasterTakesTheSharedMoveSoTheTrimsStayCentred()
		{
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan(Preset(Scene(1, -20, -8), Scene(2, -24, -14)), "match 1", null, -9);

			// Seven scenes need -1 dB and scene 2 needs +3: the master takes -1, scene 2's trim the rest.
			Assert.Equal(-1, plan.MasterChange);
			Assert.Equal(4, plan.Scenes[1].NewTrim);
			Assert.All(plan.Scenes.Where(scene => scene.Scene != 2), scene => Assert.Equal(0, scene.NewTrim));
		}

		[Fact]
		public void LevellingOneSceneUsesOnlyItsOwnTrim()
		{
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan([Scene(2, -14, -3)], null, null, -9);

			Assert.Equal(0, plan.MasterChange);
			Assert.Equal(-6, Assert.Single(plan.Scenes).NewTrim);
			Assert.Equal(-9, plan.PredictedLoudestPeak);
		}

		[Fact]
		public void KeepingThePresetLevelOnlyBalancesTheScenes()
		{
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan([Scene(1, -20, -8), Scene(2, -24, -14)], "match 1", null, null);

			Assert.Equal(0, plan.MasterChange);
			Assert.Equal([0.0, 4.0], plan.Scenes.Select(scene => scene.NewTrim));
		}

		[Fact]
		public void ExistingTrimsAndOffsetsAreTakenIntoAccount()
		{
			// Scene 2 already had +3 dB; the lead wants 2 dB more than scene 1.
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan([Scene(1, -20, -10), Scene(2, -19, -9, trim: 3)], null, new Dictionary<int, double> { [2] = 2 }, null);

			Assert.Equal(-18, plan.Scenes[1].TargetLufs);
			Assert.Equal(4, plan.Scenes[1].NewTrim);
		}

		[Fact]
		public void ATrimBeyondItsRangeIsCappedAndSaysSo()
		{
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan([Scene(1, -10, -1), Scene(2, -40, -30)], "match 1", null, null);

			Assert.Equal(20, plan.Scenes[1].NewTrim);
			Assert.Contains("short", Assert.Single(plan.Scenes[1].Notes), StringComparison.Ordinal);
		}

		[Fact]
		public void TheMasterStopsAtItsLimitAndTheTrimsCarryTheRest()
		{
			Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan(Preset(Scene(1, -40, -30, master: 18)), "match 1", null, -9);

			Assert.Equal(2, plan.MasterChange);
			Assert.All(plan.Scenes, scene => Assert.Equal(19, scene.NewTrim));
			Assert.Contains(plan.Notes, note => note.Contains("master Level stops", StringComparison.Ordinal));
		}
	}
}
