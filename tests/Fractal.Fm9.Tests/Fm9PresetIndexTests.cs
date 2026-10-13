using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9PresetIndexTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();
		private readonly Fm9PresetDump _metallica = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx")));
		private readonly Fm9PresetDump _usb = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("fm9-uploaded-usb-input.syx")));

		public void Dispose() => _folder.Dispose();

		[DictionaryFact]
		public void ABankFileIsSplitIntoItsPresetsWithTheirSlots()
		{
			string bank = _folder.File("bank-B.syx");
			File.WriteAllBytes(bank, [.. WithSlot(_metallica, 128), .. WithSlot(_usb, 129)]);

			Fm9PresetIndex index = Fm9PresetIndex.Build([bank]);

			Assert.Equal([(int?)128, 129], index.Presets.Select(preset => preset.Slot));
			Assert.Equal([_metallica.PresetName.Trim(), _usb.PresetName.Trim()], index.Presets.Select(preset => preset.Name));
		}

		[DictionaryFact]
		public void APresetReadOverMidiTakesItsSlotFromTheBackupsFileName()
		{
			// Presets read from the FM9 carry 0x7F where a bank file names the slot.
			string first = Write("007 First.syx", WithSlot(_metallica, 0x3F80));
			string second = Write("300 Second.syx", WithSlot(_usb, 0x3F80));

			Fm9PresetIndex index = Fm9PresetIndex.Build([first, second]);

			Assert.Equal([(int?)7, 300], index.Presets.Select(preset => preset.Slot));
		}

		[DictionaryFact]
		public void SearchFindsTheChannelsAModelIsPlayedOnAndTheirScenes()
		{
			Fm9PresetIndex index = Fm9PresetIndex.Build([Write("a.syx", WithSlot(_metallica, 7))]);
			Fm9BlockSummary amp = Fm9Presets.Describe(Fixtures.Path("metallica-eight-scenes.syx")).Blocks.Single(block => block.Block == "Amp 1");
			Fm9ChannelSummary played = amp.Channels.First(channel => channel.Scenes.Count > 0);

			IReadOnlyList<Fm9SearchHit> hits = index.Search(new Fm9IndexQuery { Model = played.Model });

			Fm9SearchHit hit = hits.First(candidate => candidate.Channel == played.Channel);
			Assert.Equal((7, "Amp 1"), (hit.Slot!.Value, hit.Block));
			Assert.Equal(played.Scenes, hit.Scenes);
			Assert.All(hits, candidate => Assert.NotEmpty(candidate.Scenes));
		}

		[DictionaryFact]
		public void ChannelsNoSceneEngagesAreFoundOnlyWhenAskedFor()
		{
			Fm9PresetIndex index = Fm9PresetIndex.Build([Write("a.syx", WithSlot(_metallica, 7))]);

			int played = index.Search(new Fm9IndexQuery { Block = "Amp 1" }).Count;
			int held = index.Search(new Fm9IndexQuery { Block = "Amp 1", PlayedOnly = false }).Count;

			Assert.Equal(4, held);
			Assert.True(played <= held);
		}

		[DictionaryFact]
		public void CabsCanBeFoundByBankAndNumber()
		{
			Fm9PresetIndex index = Fm9PresetIndex.Build([Write("a.syx", WithSlot(_metallica, 7)), Write("b.syx", WithSlot(_usb, 8))]);
			IReadOnlyDictionary<string, string> cab = Fm9Presets.GetChannel(Fixtures.Path("fm9-uploaded-usb-input.syx"), new("Cab", 1), "A");

			IReadOnlyList<Fm9SearchHit> hits = index.Search(new Fm9IndexQuery
			{
				CabBank = int.Parse(cab["Bank"]), CabType = int.Parse(cab["Cab Type"]), PlayedOnly = false
			});

			Assert.Contains(hits, hit => hit.Slot == 8 && hit.Block == "Cab 1" && hit.Channel == "A");
		}

		[DictionaryFact]
		public void TextSearchMatchesEveryWordAcrossPresetBlockAndModel()
		{
			Fm9PresetIndex index = Fm9PresetIndex.Build([Write("a.syx", WithSlot(_metallica, 7))]);
			string model = index.Presets[0].Blocks.Single(block => block.Block == "Amp 1").Channels[0].Model!;

			Assert.NotEmpty(index.Search(new Fm9IndexQuery { Text = $"amp {model.Split(' ')[0]}", PlayedOnly = false }));
			Assert.Empty(index.Search(new Fm9IndexQuery { Text = "amp no-such-model" }));
		}

		[DictionaryFact]
		public void AnIndexSurvivesSavingAndLoadingAndTakesUpdates()
		{
			Fm9PresetIndex index = Fm9PresetIndex.Build([Write("a.syx", WithSlot(_metallica, 7))]);
			string file = _folder.File("index.json");
			index.Save(file);

			Fm9PresetIndex loaded = Fm9PresetIndex.Load(file);
			loaded.Upsert(Fm9PresetIndex.Index(7, _usb, "after a store"));

			Assert.Equal(_usb.PresetName.Trim(), loaded.Presets.Single().Name);
			Assert.Equal(index.Presets[0].Blocks.Count, Fm9PresetIndex.Load(file).Presets[0].Blocks.Count);
		}

		private string Write(string name, byte[] bytes)
		{
			string path = _folder.File(name);
			File.WriteAllBytes(path, bytes);
			return path;
		}

		// The preset as a stored slot's dump: the header names the slot (high, low septets).
		private static byte[] WithSlot(Fm9PresetDump dump, int slot)
		{
			byte[][] frames = dump.Frames.Select(frame => frame.Bytes.ToArray()).ToArray();
			byte[] header = frames[0];
			header[6] = (byte)(slot >> 7);
			header[7] = (byte)(slot & 0x7F);
			byte checksum = 0;
			for (int index = 0; index < header.Length - 2; index++) checksum ^= header[index];
			header[^2] = (byte)(checksum & 0x7F);
			return frames.SelectMany(frame => frame).ToArray();
		}
	}
}
