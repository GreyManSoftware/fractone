using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9DiffTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		[DictionaryFact]
		public void APresetComparedWithItselfHasNoDifferences()
		{
			Fm9UploadedPreset preset = Fm9Presets.Load(Fixtures.Path("metallica-eight-scenes.syx"));

			Assert.True(Fm9Diff.Compare(preset, preset).Same);
		}

		[DictionaryFact]
		public void SettingChangesAreListedByBlockAndChannelInDisplayUnits()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			string output = _folder.File("edited.syx");
			Fm9Presets.SetChannel(input, output, new("Amp", 1), "B", new Dictionary<string, string> { ["Gain"] = "3.25", ["Amp Model"] = "Brit 800 Mod" });

			Fm9DiffResult diff = Fm9Diff.Compare(Fm9Presets.Load(input), Fm9Presets.Load(output));

			Fm9DiffChannel amp = Assert.Single(diff.Blocks);
			Assert.Equal(("Amp 1", "B"), (amp.Block, amp.Channel));
			Assert.Contains(amp.Changes, change => change.StartsWith("Gain ", StringComparison.Ordinal) && change.EndsWith("-> 3.25", StringComparison.Ordinal));
			Assert.Contains(amp.Changes, change => change.StartsWith("Amp Model ", StringComparison.Ordinal) && change.EndsWith("-> Brit 800 Mod", StringComparison.Ordinal));
			Assert.Empty(diff.Grid);
			Assert.Empty(diff.Scenes);
		}

		[DictionaryFact]
		public void SceneGridAndNameChangesAreListed()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			string scene = _folder.File("scene.syx"), removed = _folder.File("removed.syx"), renamed = _folder.File("renamed.syx");
			Fm9Presets.ComposeScene(input, scene, 2, null, "Verse", new Dictionary<string, string> { ["Amp"] = "D" }, null);
			Fm9Presets.RemoveBlocks(scene, removed, [new("Reverb", 1)]);
			Fm9Presets.Rename(removed, renamed, "Other Name");

			Fm9DiffResult diff = Fm9Diff.Compare(Fm9Presets.Load(input), Fm9Presets.Load(renamed));

			Assert.Contains(diff.Scenes, line => line.StartsWith("Scene 2: Amp 1 plays ", StringComparison.Ordinal) && line.EndsWith("-> D", StringComparison.Ordinal));
			Assert.Contains(diff.Scenes, line => line.StartsWith("Scene 2 name:", StringComparison.Ordinal));
			Assert.Contains(diff.Grid, line => line.StartsWith("Removed Reverb 1", StringComparison.Ordinal));
			Assert.Contains(diff.Preset, line => line.Contains("'Other Name'", StringComparison.Ordinal));
		}

		[DictionaryFact]
		public void SoundOnlyHidesNamesAndChannelsNoScenePlays()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			Fm9UploadedPreset preset = Fm9Presets.Load(input);
			Fm9UploadedBlock cab = preset.Find("Cab", 1)!;
			string unplayed = Enumerable.Range(0, 4).Select(Fm9Names.Letter)
				.First(letter => Enumerable.Range(1, 8).All(scene => Fm9Names.Letter((int)preset.GetSceneState(cab, scene).Channel) != letter));
			string edited = _folder.File("edited.syx"), renamed = _folder.File("renamed.syx");
			Fm9Presets.SetChannel(input, edited, new("Cab", 1), unplayed, new Dictionary<string, string> { ["Low Cut"] = "120" });
			Fm9Presets.Rename(edited, renamed, "Cosmetic");

			Assert.False(Fm9Diff.Compare(preset, Fm9Presets.Load(renamed)).Same);
			Assert.True(Fm9Diff.Compare(preset, Fm9Presets.Load(renamed), soundOnly: true).Same);
		}
	}
}
