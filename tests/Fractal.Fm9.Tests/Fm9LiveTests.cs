using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9LiveTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();
		private readonly Fm9PresetDump _metallica = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx")));
		private readonly FakeFm9 _fm9;

		public Fm9LiveTests()
		{
			_fm9 = new(_metallica);
			_fm9.Slots[505] = _metallica;
			_fm9.Preset = 505;
			_fm9.EditBuffer = _metallica;
		}

		public void Dispose() => _folder.Dispose();

		private Fm9Connection Connection() => new(() => _fm9, _folder.File("midi.lock"));

		private Fm9Live Live() => new(Connection(), _folder.File("state")) { Wait = (_, _) => Task.CompletedTask };

		private Fm9Device Device(Fm9Safeguards? safeguards = null) =>
			new(Connection(), new Fm9Settings { WorkFolder = _folder.Root, Safeguards = safeguards ?? new() }) { Wait = (_, _) => Task.CompletedTask };

		private IReadOnlyDictionary<string, string> Buffer(string channel) =>
			Fm9Presets.GetChannel(Write("buffer.syx", _fm9.EditBuffer!), new("Amp", 1), channel);

		[DictionaryFact]
		public async Task ASettingChangesLiveOnTheChannelTheBlockPlaysAndNothingIsStored()
		{
			IReadOnlyDictionary<string, string> channelB = Buffer("B");

			Fm9LiveResult result = await Live().SetAsync(new("Amp", 1), new Dictionary<string, string> { ["gain"] = "2.25" }, null, CancellationToken.None);

			Assert.Equal("A", result.Channel);
			Assert.Equal("2.25", Buffer("A")["Gain"]);
			Assert.Equal(channelB, Buffer("B"));
			Fm9LiveChange change = result.Changes.Single();
			Assert.Equal(("Gain", "2.25"), (change.Setting, change.After));
			Assert.NotEqual(change.Before, change.After);
			Assert.Equal(0, _fm9.Stores);
			Assert.True(_metallica.HasSameRawPatch(_fm9.Slots[505]));
		}

		[DictionaryFact]
		public async Task AnotherChannelIsSwitchedToFirstAsFm9EditDoes()
		{
			Fm9LiveResult result = await Live().SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "3" }, "C", CancellationToken.None);

			Assert.Equal(("C", "A"), (result.Channel, result.SwitchedFromChannel));
			Assert.Equal("3", Buffer("C")["Gain"]);
			Fm9UploadedPreset live = Fm9UploadedPreset.Read(_fm9.EditBuffer!);
			Assert.Equal(Fm9SceneChannel.C, live.GetSceneState(live.Find("Amp", 1)!, _fm9.Scene).Channel);
			Assert.Contains(result.Notes, note => note.Contains("now plays channel C", StringComparison.Ordinal));
		}

		[DictionaryFact]
		public async Task ANewModelGoesFirstSoTheFm9LoadsItsVoicingThenTheKnobsGoOverIt()
		{
			IReadOnlyList<string> models = Fm9Names.TypeLabels("Amp", "Amp Model");
			string current = Fm9Names.Model("Amp", Buffer("A"))!;
			int ordinal = Enumerable.Range(0, models.Count).First(index => models[index] != current && Fm9ModelDefaults.HasDefaults("Amp", index) && models[index].Length > 0);

			Fm9LiveResult result = await Live().SetAsync(new("Amp", 1),
				new Dictionary<string, string> { ["Amp Model"] = models[ordinal], ["Gain"] = "4" }, null, CancellationToken.None);

			Assert.Equal((58, 10, ordinal), _fm9.ParameterChanges[0]);
			Assert.Equal(models[ordinal], result.ModelAfter);
			IReadOnlyDictionary<string, string> after = Buffer("A");
			Assert.Equal("4", after["Gain"]);
			foreach ((string name, string value) in Fm9ModelDefaults.Settings("Amp", ordinal))
				Assert.Equal(value, after[name]);
			// Only the model and the asked-for setting travel: the voicing is the FM9's own.
			Assert.Equal(2, _fm9.ParameterChanges.Count);
		}

		[DictionaryFact]
		public async Task LiveSettingsAreReadForThePlayingChannel()
		{
			Fm9LiveBlockState state = await Live().GetAsync(new("Amp", 1), null, ["gain", "Master"], CancellationToken.None);

			Assert.Equal(("A", "A", false), (state.PlayingChannel, state.Channel, state.Bypassed));
			Assert.Equal(["Gain", "Master"], state.Settings.Keys);
			Assert.Equal(Fm9Names.Model("Amp", Buffer("A")), state.Model);
		}

		[DictionaryFact]
		public async Task ABlocksChannelAndBypassSwitchInTheCurrentScene()
		{
			_fm9.Scene = 3;

			Fm9LiveSwitchResult result = await Live().SetBlockAsync(new("Amp", 1), "D", true, CancellationToken.None);

			Assert.Equal((3, "D", true), (result.Scene, result.Channel, result.Bypassed));
			Fm9UploadedPreset live = Fm9UploadedPreset.Read(_fm9.EditBuffer!);
			Assert.Equal(Fm9SceneChannel.D, live.GetSceneState(live.Find("Amp", 1)!, 3).Channel);
			Assert.Equal(Fm9UploadedPreset.Read(_metallica).GetSceneState(Fm9UploadedPreset.Read(_metallica).Find("Amp", 1)!, 1),
				live.GetSceneState(live.Find("Amp", 1)!, 1));
		}

		[DictionaryFact]
		public async Task AScenesAreSelectedAndConfirmed()
		{
			Assert.Equal(6, await Live().SelectSceneAsync(6, CancellationToken.None));
			Assert.Equal(6, _fm9.Scene);
		}

		[DictionaryFact]
		public async Task TheEditBufferIsStoredWithItsLiveEditsEvenWhileUnsavedEditsAreGuarded()
		{
			await Live().SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "6" }, null, CancellationToken.None);
			Fm9PresetDump edited = _fm9.EditBuffer!;

			(Fm9StoreResult result, string file) = await Device(Fm9Safeguards.Careful).StoreEditBufferAsync(505, null, CancellationToken.None);

			Assert.True(result.Verified);
			Assert.True(edited.HasSameRawPatch(_fm9.Slots[505]));
			Assert.True(File.Exists(file));
			Assert.NotNull(result.Backup);
		}

		[DictionaryFact]
		public async Task SelectingAPresetOverUnsavedEditsIsRefusedWhileTheSafeguardIsOn()
		{
			await Live().SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "6" }, null, CancellationToken.None);

			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				Device(new Fm9Safeguards { RefuseLoadIfBufferChanged = true }).SelectAsync(500, 1, CancellationToken.None));
			Assert.Equal(505, _fm9.Preset);

			await Device().SelectAsync(500, 1, CancellationToken.None);
			Assert.Equal(500, _fm9.Preset);
		}

		[DictionaryFact]
		public async Task UndoWritesTheExactWordsBackNewestFirst()
		{
			Fm9Live live = Live();
			await live.SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "2.25" }, null, CancellationToken.None);
			await live.SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "3.5", ["Master"] = "1" }, null, CancellationToken.None);

			IReadOnlyList<string> undone = await live.UndoAsync(1, CancellationToken.None);

			Assert.Equal("2.25", Buffer("A")["Gain"]);
			Assert.Contains("Master", undone.Single(), StringComparison.Ordinal);
			await live.UndoAsync(1, CancellationToken.None);
			Assert.True(Fm9Presets.Load(Write("now.syx", _fm9.EditBuffer!)).Body.Bytes.Span.SequenceEqual(Fm9UploadedPreset.Read(_metallica).Body.Bytes.Span));
			await Assert.ThrowsAsync<InvalidOperationException>(() => live.UndoAsync(1, CancellationToken.None));
		}

		[DictionaryFact]
		public async Task AModelChangeIsUndoneByRestoringTheBufferFromJustBeforeIt()
		{
			Fm9Live live = Live();
			IReadOnlyList<string> models = Fm9Names.TypeLabels("Amp", "Amp Model");
			string current = Fm9Names.Model("Amp", Buffer("C"))!;
			string other = models.First(model => model.Length > 0 && model != current && Fm9ModelDefaults.HasDefaults("Amp", models.ToList().IndexOf(model)));
			await live.SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Amp Model"] = other, ["Gain"] = "4" }, "C", CancellationToken.None);

			await live.UndoAsync(1, CancellationToken.None);

			Assert.True(Fm9UploadedPreset.Read(_fm9.EditBuffer!).Body.Bytes.Span.SequenceEqual(Fm9UploadedPreset.Read(_metallica).Body.Bytes.Span));
		}

		[DictionaryFact]
		public async Task BlockSwitchesAreUndoneToo()
		{
			Fm9Live live = Live();
			await live.SetBlockAsync(new("Amp", 1), "D", true, CancellationToken.None);

			await live.UndoAsync(1, CancellationToken.None);

			Fm9UploadedPreset now = Fm9UploadedPreset.Read(_fm9.EditBuffer!), was = Fm9UploadedPreset.Read(_metallica);
			Assert.Equal(was.GetSceneState(was.Find("Amp", 1)!, 1), now.GetSceneState(now.Find("Amp", 1)!, 1));
		}

		[DictionaryFact]
		public async Task ANamedSnapshotComesBackAndTheRestoreCanItselfBeUndone()
		{
			Fm9Live live = Live();
			await live.SnapshotAsync("clean start", CancellationToken.None);
			await live.SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "1.5" }, null, CancellationToken.None);
			Fm9PresetDump edited = _fm9.EditBuffer!;

			await live.RestoreSnapshotAsync("Clean Start", CancellationToken.None);
			Assert.True(_metallica.HasSameRawPatch(_fm9.EditBuffer!));

			await live.UndoAsync(1, CancellationToken.None);
			Assert.True(edited.HasSameRawPatch(_fm9.EditBuffer!));
			Assert.Contains(live.History().Snapshots, snapshot => snapshot.Name == "clean start");
		}

		[DictionaryFact]
		public async Task HistoryStartsAgainWhenAnotherPresetIsShowing()
		{
			Fm9Live live = Live();
			await live.SetAsync(new("Amp", 1), new Dictionary<string, string> { ["Gain"] = "1.5" }, null, CancellationToken.None);
			Assert.Single(live.History().Steps);

			_fm9.Slots[500] = _metallica.WithPresetName("Another");
			await _fm9.SelectPresetAsync(500, 1, CancellationToken.None);

			await Assert.ThrowsAsync<InvalidOperationException>(() => live.UndoAsync(1, CancellationToken.None));
		}

		private string Write(string name, Fm9PresetDump dump)
		{
			string path = _folder.File(name);
			File.WriteAllBytes(path, dump.ToArray());
			return path;
		}
	}
}
