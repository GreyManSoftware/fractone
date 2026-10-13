using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9BlockLibraryTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();

		public void Dispose() => _folder.Dispose();

		[DictionaryFact]
		public void ASavedFavouriteHoldsTheBlocksFourChannelsInFm9EditsFolder()
		{
			string preset = Fixtures.Path("metallica-eight-scenes.syx");
			Fm9BlockLibrary library = new(_folder.Root);

			Fm9LibraryEntry saved = library.Save(preset, new("Amp", 1), "Rhythm amps");

			Assert.Equal("Amplifier", Path.GetFileName(Path.GetDirectoryName(saved.Path)));
			Assert.Matches(@"^Rhythm amps_\d{8}_\d{6}\.blk$", Path.GetFileName(saved.Path));
			Fm9LibraryBlock block = Fm9BlockLibraryFormat.Read(File.ReadAllBytes(saved.Path));
			Assert.Equal(("Rhythm amps", "Amp", false, 4), (block.Name, block.BlockType, block.ChannelOnly, block.Rows));
			Fm9PresetSummary summary = Fm9Presets.Describe(preset);
			Assert.Equal(summary.Blocks.Single(entry => entry.Block == "Amp 1").Channels.Select(channel => channel.Model), saved.Models);
			Fm9LibraryEntry listed = library.List("amp").Single();
			Assert.Equal((saved.Path, saved.Name, saved.Channels), (listed.Path, listed.Name, listed.Channels));
			Assert.Equal(saved.Models, listed.Models);
		}

		[DictionaryFact]
		public void TheLibraryTypeIsFm9EditsDictionarySection()
		{
			Assert.Equal(10, Fm9BlockLibraryFormat.LibraryType("Amp"));
			Assert.Equal(11, Fm9BlockLibraryFormat.LibraryType("Cab"));
			Assert.Equal(35, Fm9BlockLibraryFormat.LibraryType("Gate/Expander"));
			Assert.Equal("Gate-Expander", Fm9BlockLibraryFormat.Folder("Gate/Expander"));
		}

		[DictionaryFact]
		public void AFileSurvivesAReadAndWriteByteForByte()
		{
			Fm9LibraryEntry saved = new Fm9BlockLibrary(_folder.Root).Save(Fixtures.Path("metallica-eight-scenes.syx"), new("Cab", 1), "Cab B", "B");
			byte[] original = File.ReadAllBytes(saved.Path);

			Assert.Equal(original, Fm9BlockLibraryFormat.Write(Fm9BlockLibraryFormat.Read(original)));
			Assert.Equal("B", saved.Channels);
		}

		[DictionaryFact]
		public void ACorruptedFileIsRejected()
		{
			Fm9LibraryEntry saved = new Fm9BlockLibrary(_folder.Root).Save(Fixtures.Path("metallica-eight-scenes.syx"), new("Amp", 1), "Amps");
			byte[] bytes = File.ReadAllBytes(saved.Path);
			bytes[200] ^= 0x01;

			Assert.Throws<InvalidDataException>(() => Fm9BlockLibraryFormat.Read(bytes));
		}

		[DictionaryFact]
		public void ApplyingAnAllChannelFavouriteReplacesEveryChannelAndNothingElse()
		{
			string source = Fixtures.Path("fm9-uploaded-usb-input.syx");
			string target = Fixtures.Path("metallica-eight-scenes.syx");
			Fm9LibraryEntry saved = new Fm9BlockLibrary(_folder.Root).Save(source, new("Amp", 1), "Borrowed");
			string output = _folder.File("applied.syx");

			Fm9EditResult result = Fm9BlockLibrary.Apply(saved.Path, target, output);

			foreach (string channel in new[] { "A", "B", "C", "D" })
				Assert.Equal(Fm9Presets.GetChannel(source, new("Amp", 1), channel), Fm9Presets.GetChannel(output, new("Amp", 1), channel));
			Assert.Equal(4, result.Changes.Count);
			Assert.Equal(Fm9Presets.Describe(target).Scenes.Select(scene => scene.Blocks), Fm9Presets.Describe(output).Scenes.Select(scene => scene.Blocks),
				new SceneBlocksComparer());
			Assert.Equal(Fm9Presets.GetChannel(target, new("Cab", 1), "A"), Fm9Presets.GetChannel(output, new("Cab", 1), "A"));
		}

		[DictionaryFact]
		public void ASingleChannelFavouriteGoesIntoTheChannelAsked()
		{
			string source = Fixtures.Path("fm9-uploaded-usb-input.syx");
			string target = Fixtures.Path("metallica-eight-scenes.syx");
			Fm9LibraryEntry saved = new Fm9BlockLibrary(_folder.Root).Save(source, new("Amp", 1), "Lead", "A");
			string output = _folder.File("lead.syx");

			Fm9BlockLibrary.Apply(saved.Path, target, output, toChannel: "C");

			Assert.Equal(Fm9Presets.GetChannel(source, new("Amp", 1), "A"), Fm9Presets.GetChannel(output, new("Amp", 1), "C"));
			foreach (string channel in new[] { "A", "B", "D" })
				Assert.Equal(Fm9Presets.GetChannel(target, new("Amp", 1), channel), Fm9Presets.GetChannel(output, new("Amp", 1), channel));
		}

		[DictionaryFact]
		public void AFavouriteOnlyGoesOnABlockOfItsOwnType()
		{
			Fm9LibraryEntry saved = new Fm9BlockLibrary(_folder.Root).Save(Fixtures.Path("metallica-eight-scenes.syx"), new("Amp", 1), "Amps");

			Assert.Throws<ArgumentException>(() => Fm9BlockLibrary.Apply(saved.Path, Fixtures.Path("metallica-eight-scenes.syx"),
				_folder.File("x.syx"), new Fm9BlockRef("Cab", 1)));
		}

		private sealed class SceneBlocksComparer : IEqualityComparer<IReadOnlyList<Fm9SceneBlock>>
		{
			public bool Equals(IReadOnlyList<Fm9SceneBlock>? x, IReadOnlyList<Fm9SceneBlock>? y) => x!.SequenceEqual(y!);
			public int GetHashCode(IReadOnlyList<Fm9SceneBlock> obj) => obj.Count;
		}
	}
}
