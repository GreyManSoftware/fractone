using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9PresetsTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		[DictionaryFact]
		public void DescribingAPresetListsItsBlocksChannelsAndScenes()
		{
			Fm9PresetSummary summary = Fm9Presets.Describe(Fixtures.Path("metallica-eight-scenes.syx"));

			Assert.Contains(summary.Blocks, block => block.Block == "Amp 1" && block.Editable);
			Fm9BlockSummary amp = summary.Blocks.Single(block => block.Block == "Amp 1");
			Assert.Equal(["A", "B", "C", "D"], amp.Channels.Select(channel => channel.Channel));
			Assert.All(amp.Channels, channel => Assert.False(string.IsNullOrEmpty(channel.Model)));
			Assert.Equal(8, summary.Scenes.Count);
			// Every engaged scene of the amp appears under exactly one of its channels.
			int[] engaged = summary.Scenes.Where(scene => scene.Blocks.Any(block => block.Block == "Amp 1" && !block.Bypassed)).Select(scene => scene.Number).ToArray();
			Assert.Equal(engaged, amp.Channels.SelectMany(channel => channel.Scenes).Order());
		}

		[DictionaryFact]
		public void SettingAChannelWritesOnlyTheGivenSettingsToANewFile()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			byte[] original = File.ReadAllBytes(input);
			IReadOnlyDictionary<string, string> before = Fm9Presets.GetChannel(input, new("Amp", 1), "B");
			string output = _folder.File("gain.syx");

			Fm9EditResult result = Fm9Presets.SetChannel(input, output, new("Amp", 1), "B", new Dictionary<string, string> { ["gain"] = "6.5" });

			IReadOnlyDictionary<string, string> after = Fm9Presets.GetChannel(output, new("Amp", 1), "B");
			Assert.Equal("6.5", after["Gain"]);
			Assert.Equal(before.Where(pair => pair.Key != "Gain"), after.Where(pair => pair.Key != "Gain"));
			Assert.Equal(Fm9Presets.GetChannel(input, new("Amp", 1), "A"), Fm9Presets.GetChannel(output, new("Amp", 1), "A"));
			Assert.Contains(result.Changes, change => change.StartsWith("Amp 1 B: Gain ", StringComparison.Ordinal) && change.EndsWith("-> 6.5", StringComparison.Ordinal));
			Assert.Equal(original, File.ReadAllBytes(input));
		}

		[DictionaryFact]
		public void AnEditNeverOverwritesItsInput()
		{
			string input = _folder.Copy(Fixtures.Path("metallica-eight-scenes.syx"));

			Assert.Throws<ArgumentException>(() => Fm9Presets.Rename(input, input, "Other"));
		}

		[DictionaryFact]
		public void ChoosingAModelByNameLoadsTheVoicingTheFm9Would()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			string current = Fm9Names.Model("Amp", Fm9Presets.GetChannel(input, new("Amp", 1), "C"))!;
			IReadOnlyList<string> models = Fm9Names.TypeLabels("Amp", "Amp Model");
			int ordinal = Enumerable.Range(0, models.Count).First(index => models[index] != current && Fm9ModelDefaults.HasDefaults("Amp", index) &&
				!string.IsNullOrWhiteSpace(models[index]));
			string output = _folder.File("model.syx");

			Fm9EditResult result = Fm9Presets.SetChannel(input, output, new("Amp", 1), "C", new Dictionary<string, string> { ["Amp Model"] = models[ordinal] });

			IReadOnlyDictionary<string, string> after = Fm9Presets.GetChannel(output, new("Amp", 1), "C");
			Assert.Equal(models[ordinal], Fm9Names.Model("Amp", after));
			IReadOnlyDictionary<string, string> voicing = Fm9ModelDefaults.Settings("Amp", ordinal);
			Assert.NotEmpty(voicing);
			Assert.All(voicing, pair => Assert.Equal(pair.Value, after[pair.Key]));
			Assert.Contains(result.Changes, change => change.Contains("with the FM9's voicing", StringComparison.Ordinal));
		}

		[DictionaryFact]
		public void AnUnknownModelNameIsRefused()
		{
			Assert.Throws<ArgumentException>(() => Fm9Presets.SetChannel(Fixtures.Path("metallica-eight-scenes.syx"), _folder.File("x.syx"),
				new("Amp", 1), "A", new Dictionary<string, string> { ["Amp Model"] = "No Such Amp" }));
		}

		[DictionaryFact]
		public void CopyingAChannelCopiesEveryWordExactly()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			string output = _folder.File("copy.syx");

			Fm9EditResult result = Fm9Presets.CopyChannel(input, output, input, new("Amp", 1), "A", "D");

			Fm9UploadedPreset copied = Fm9Presets.Load(output);
			Fm9UploadedBlock amp = copied.Find("Amp", 1)!;
			int columns = amp.Record!.ParameterColumns * sizeof(ushort);
			Assert.True(copied.Body.Bytes.Span.Slice(Fm9ChannelParameterStorage.GetOffset(amp.Record, 0, 0), columns)
				.SequenceEqual(copied.Body.Bytes.Span.Slice(Fm9ChannelParameterStorage.GetOffset(amp.Record, 3, 0), columns)));
			Assert.Contains("every word copied exactly", result.Changes.Single(), StringComparison.Ordinal);
			Assert.Equal(Fm9Presets.GetChannel(input, new("Amp", 1), "B"), Fm9Presets.GetChannel(output, new("Amp", 1), "B"));
		}

		[DictionaryFact]
		public void CopyingAChannelFromAnotherPresetBringsItsModel()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			string source = Fixtures.Path("fm9-uploaded-usb-input.syx");
			string output = _folder.File("borrowed.syx");

			Fm9Presets.CopyChannel(input, output, source, new("Amp", 1), "A", "B");

			Assert.Equal(Fm9Presets.GetChannel(source, new("Amp", 1), "A"), Fm9Presets.GetChannel(output, new("Amp", 1), "B"));
		}

		[DictionaryFact]
		public void ComposingASceneCopiesAnotherThenChangesOnlyWhatIsAsked()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			Fm9PresetSummary before = Fm9Presets.Describe(input);
			string output = _folder.File("scene.syx");

			Fm9Presets.ComposeScene(input, output, 8, 1, "Lead", new Dictionary<string, string> { ["Amp"] = "D" },
				new Dictionary<string, bool> { ["Reverb"] = true });

			Fm9PresetSummary after = Fm9Presets.Describe(output);
			Fm9SceneSummary scene = after.Scenes[7];
			Assert.Equal("Lead", scene.Name);
			Assert.Equal("D", scene.Blocks.Single(block => block.Block == "Amp 1").Channel);
			Assert.True(scene.Blocks.Single(block => block.Block == "Reverb 1").Bypassed);
			Assert.Equal(before.Scenes[0].Blocks.Where(block => block.Block is not ("Amp 1" or "Reverb 1")),
				scene.Blocks.Where(block => block.Block is not ("Amp 1" or "Reverb 1")));
			Assert.Equal(before.Scenes.Take(7), after.Scenes.Take(7), SceneComparer.Instance);
		}

		[DictionaryFact]
		public void RemovingABlockLeavesAShuntSoItsCablesStay()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			Fm9UploadedBlock reverb = Fm9Presets.Load(input).Find("Reverb", 1)!;
			string output = _folder.File("removed.syx");

			Fm9Presets.RemoveBlocks(input, output, [new("Reverb", 1)]);

			Fm9UploadedPreset removed = Fm9Presets.Load(output);
			Assert.Null(removed.Find("Reverb", 1));
			Assert.True(removed.Body.GridCells.Single(cell => cell.Row == reverb.Row && cell.Column == reverb.Column).EffectId >= 1024);
			Assert.All(Fm9Presets.Describe(output).Scenes, scene => Assert.DoesNotContain(scene.Blocks, block => block.Block == "Reverb 1"));
		}

		[DictionaryFact]
		public void ABlockAddedIntoAShuntGetsTheFm9Defaults()
		{
			string input = Fixtures.Path("fm9-uploaded-shunts.syx");
			Fm9PresetGridCell shunt = Fm9Presets.Load(input).Body.GridCells.First(cell => cell.EffectId >= 1024);
			string output = _folder.File("added.syx");

			Fm9Presets.AddBlock(input, output, new("Delay", 1), shunt.Row, shunt.Column);

			Fm9UploadedBlock delay = Fm9Presets.Load(output).Find("Delay", 1)!;
			Assert.True(delay.IsEditable);
			Assert.Equal((shunt.Row, shunt.Column), (delay.Row, delay.Column));
		}

		[DictionaryFact]
		public void RenamingChangesOnlyTheName()
		{
			string input = Fixtures.Path("metallica-eight-scenes.syx");
			string output = _folder.File("renamed.syx");

			Fm9Presets.Rename(input, output, "Renamed Preset");

			Fm9UploadedPreset renamed = Fm9Presets.Load(output);
			Assert.Equal("Renamed Preset", renamed.Name);
			Assert.True(Fm9Presets.Load(input).Body.Bytes.Span.SequenceEqual(renamed.Body.Bytes.Span));
		}

		[Theory]
		[InlineData("fm9-uploaded-shunts.syx")]
		[InlineData("fm9-uploaded-usb-input.syx")]
		public void APresetTheFm9SavedReencodesToTheSameBytes(string fixture)
		{
			// Fractal's Huffman tie-break: an untouched body compresses exactly as the FM9 compressed it.
			Fm9PresetDump saved = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path(fixture)));

			Fm9PresetDump again = Fm9PresetBodyEncoder.Reencode(saved, Fm9PresetBodyDecoder.Decode(saved).Bytes.Span);

			Assert.True(saved.HasSameRawPatch(again));
		}

		[Theory]
		[InlineData(0x10, "an Axe-Fx III")]
		[InlineData(0x11, "an FM3")]
		public void APresetFromAnotherFractalModelIsRefusedByName(byte model, string device)
		{
			byte[] bytes = File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx"));
			bytes[4] = model;

			InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => Fm9PresetDump.Parse(bytes));

			Assert.Contains(device, refusal.Message, StringComparison.Ordinal);
		}

		[DictionaryFact]
		public void BlockNamesAcceptTheUsualSpellings()
		{
			Assert.Equal(new Fm9BlockRef("Amp", 2), Fm9BlockRef.Parse("amp 2"));
			Assert.Equal(new Fm9BlockRef("Amp", 2), Fm9BlockRef.Parse("Amp:2"));
			Assert.Equal(new Fm9BlockRef("Gate/Expander", 1), Fm9BlockRef.Parse("gate/expander"));
			Assert.Throws<ArgumentException>(() => Fm9BlockRef.Parse("Banjo"));
		}

		[DictionaryFact]
		public void ListingSettingsIncludesModelsAndDictionarySettings()
		{
			IReadOnlyList<Fm9SettingInfo> settings = Fm9Presets.ListSettings("amp");

			Assert.True(settings.Single(setting => setting.Name == "Amp Model").Choices.Count > 200);
			Assert.Contains(settings, setting => setting.Name == "Gain");
			Assert.Equal(settings.Count, settings.Select(setting => setting.Name).Distinct().Count());
		}

		private sealed class SceneComparer : IEqualityComparer<Fm9SceneSummary>
		{
			public static SceneComparer Instance { get; } = new();
			public bool Equals(Fm9SceneSummary? x, Fm9SceneSummary? y) =>
				x is not null && y is not null && x.Number == y.Number && x.Name == y.Name && x.Blocks.SequenceEqual(y.Blocks);
			public int GetHashCode(Fm9SceneSummary obj) => obj.Number;
		}
	}

	internal static class Fixtures
	{
		public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", name);
	}

	internal sealed class TemporaryFolder : IDisposable
	{
		public string Root { get; } = Directory.CreateTempSubdirectory("fm9-tests-").FullName;

		public string File(string name) => System.IO.Path.Combine(Root, name);

		public string Copy(string path)
		{
			string copy = File(System.IO.Path.GetFileName(path));
			System.IO.File.Copy(path, copy, overwrite: true);
			return copy;
		}

		public void Dispose()
		{
			try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
		}
	}
}
