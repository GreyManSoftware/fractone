using System.Globalization;
using System.Text;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9
{
	// One FM9-Edit block-library file (.blk): a block's four channel rows with a name, saved as "all channels" or as one
	// channel. Little-endian header: u16 version 5, u16 device 0x12, u16 firmware major, 16-byte id, u32 library type (FM9-Edit's
	// dictionary section of the block family: Amp 10, Cab 11, Gate/Expander 35), u32 channel-only flag, u32 channel, u32 name
	// length, name, u32 0, u32 1, u32 payload length; the payload is the FM9's block-dump SysEx (0x74 header with effect id and
	// word count, 0x75 chunks of up to 256 words as three septets, 0x76 end); then 8 zero bytes and the XOR of every earlier
	// byte (& 0x7F). Rebuilding a user's FM9-Edit favourite from the preset it came from gave FM9-Edit's file byte for byte.
	public sealed record Fm9LibraryBlock(
		string Name,
		string BlockType,
		int EffectId,
		int FirmwareMajor,
		Guid Id,
		bool ChannelOnly,
		int Channel,
		int Rows,
		IReadOnlyList<int> Words)
	{
		public int Columns => Words.Count / Rows;

		public IReadOnlyList<int> Row(int channel) => Words.Skip(channel * Columns).Take(Columns).ToArray();
	}

	public sealed record Fm9LibraryEntry(string Path, string Name, string BlockType, string Channels, IReadOnlyList<string?> Models);

	public static class Fm9BlockLibraryFormat
	{
		private const ushort Version = 5;
		private const ushort Fm9Device = 0x12;
		private const int ChunkWords = 256;

		public static Fm9LibraryBlock Read(ReadOnlySpan<byte> bytes)
		{
			if (bytes.Length < 0x26 + 12 + 9) throw new InvalidDataException("The block-library file is too short.");
			if (Xor7(bytes[..^1]) != bytes[^1]) throw new InvalidDataException("The block-library file's checksum does not match.");
			if (U16(bytes, 0) != Version || U16(bytes, 2) != Fm9Device)
				throw new InvalidDataException($"This is not an FM9 block-library file (version {U16(bytes, 0)}, device 0x{U16(bytes, 2):X2}).");
			int firmware = U16(bytes, 4);
			Guid id = new(bytes.Slice(6, 16));
			bool channelOnly = U32(bytes, 26) != 0;
			int channel = U32(bytes, 30);
			int nameLength = U32(bytes, 34);
			string name = Encoding.UTF8.GetString(bytes.Slice(0x26, nameLength));
			int payloadLength = U32(bytes, 0x26 + nameLength + 8);
			int payloadAt = 0x26 + nameLength + 12;
			ReadOnlySpan<byte> payload = bytes.Slice(payloadAt, payloadLength);
			int effect = -1, expected = -1;
			List<int> words = [];
			for (int index = 0; index < payload.Length;)
			{
				int end = payload[index..].IndexOf((byte)0xF7) + index;
				if (end < index) throw new InvalidDataException("A block-library SysEx message has no end.");
				ReadOnlySpan<byte> message = payload[index..(end + 1)];
				if (Xor7(message[..^2]) != message[^2]) throw new InvalidDataException("A block-library SysEx message's checksum does not match.");
				switch (message[5])
				{
					case 0x74:
						effect = message[6] | (message[7] << 7);
						expected = message[8] | (message[9] << 7);
						break;
					case 0x75:
						int count = message[6] | (message[7] << 7);
						for (int word = 0; word < count; word++)
							words.Add(message[8 + (3 * word)] | (message[9 + (3 * word)] << 7) | (message[10 + (3 * word)] << 14));
						break;
				}
				index = end + 1;
			}
			if (effect < 0 || words.Count != expected) throw new InvalidDataException("The block-library payload is incomplete.");
			string blockType = Fm9BlockSettings.Identify(effect)?.BlockType ?? throw new InvalidDataException($"Unknown block (effect {effect}).");
			int rows = Fm9BlockSettings.RecordShape(blockType)?.Rows ?? Fm9Names.ChannelCount;
			if (words.Count % rows != 0) throw new InvalidDataException($"The {blockType} words do not divide into {rows} channels.");
			return new(name, blockType, effect, firmware, id, channelOnly, channel, rows, words);
		}

		public static byte[] Write(Fm9LibraryBlock block)
		{
			ArgumentNullException.ThrowIfNull(block);
			List<byte> payload = [.. Message(0x74, [(byte)(block.EffectId & 0x7F), (byte)(block.EffectId >> 7),
				(byte)(block.Words.Count & 0x7F), (byte)(block.Words.Count >> 7)])];
			foreach (int[] chunk in block.Words.Chunk(ChunkWords))
				payload.AddRange(Message(0x75, [(byte)(chunk.Length & 0x7F), (byte)(chunk.Length >> 7),
					.. chunk.SelectMany(word => new[] { (byte)(word & 0x7F), (byte)((word >> 7) & 0x7F), (byte)(word >> 14) })]));
			payload.AddRange(Message(0x76, []));
			byte[] name = Encoding.UTF8.GetBytes(block.Name);
			List<byte> file = [];
			void Add16(int value) => file.AddRange(BitConverter.GetBytes((ushort)value));
			void Add32(int value) => file.AddRange(BitConverter.GetBytes(value));
			Add16(Version);
			Add16(Fm9Device);
			Add16(block.FirmwareMajor);
			file.AddRange(block.Id.ToByteArray());
			Add32(LibraryType(block.BlockType));
			Add32(block.ChannelOnly ? 1 : 0);
			Add32(block.Channel);
			Add32(name.Length);
			file.AddRange(name);
			Add32(0);
			Add32(1);
			Add32(payload.Count);
			file.AddRange(payload);
			file.AddRange(new byte[8]);
			file.Add(Xor7(file.ToArray()));
			return [.. file];
		}

		// FM9-Edit files each block family under its dictionary section number (Amp 10, Cab 11, Gate/Expander 35 seen in
		// FM9-Edit's own files; the rest follow the same table).
		public static int LibraryType(string blockType)
		{
			string family = Fm9HardwareBlockInstanceCatalog.Bundled.Instances
				.FirstOrDefault(instance => instance.BlockType.Equals(blockType, StringComparison.OrdinalIgnoreCase))?.ParameterFamily
				?? throw new NotSupportedException($"No library type is known for {blockType}.");
			return Fm9EffectDefinitions.SectionOf(family) ?? throw new NotSupportedException($"No library type is known for {blockType}.");
		}

		// FM9-Edit's library folder for a block type: its long name, with " / " written as "-" (Amplifier, Cabinet and
		// Gate-Expander seen; the rest from FM9-Edit's own block names).
		public static string Folder(string blockType) => blockType switch
		{
			"Amp" => "Amplifier",
			"Cab" => "Cabinet",
			"Gate/Expander" => "Gate-Expander",
			"Pan/Tremolo" => "Tremolo-Panner",
			"Volume/Pan" => "Volume-Pan",
			"Pitch" => "Pitch Shifter",
			"Wah" => "Wahwah",
			"Send" => "Feedback Send",
			"Return" => "Feedback Return",
			_ => blockType.Replace('/', '-')
		};

		private static byte[] Message(byte function, IReadOnlyList<byte> data)
		{
			List<byte> message = [0xF0, 0x00, 0x01, 0x74, 0x12, function, .. data];
			message.Add(Xor7(message.ToArray()));
			message.Add(0xF7);
			return [.. message];
		}

		private static byte Xor7(ReadOnlySpan<byte> bytes)
		{
			int sum = 0;
			foreach (byte value in bytes) sum ^= value;
			return (byte)(sum & 0x7F);
		}

		private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BitConverter.ToUInt16(bytes[offset..]);
		private static int U32(ReadOnlySpan<byte> bytes, int offset) => BitConverter.ToInt32(bytes[offset..]);
	}

	// The user's FM9-Edit block library: list favourites, save a preset's block as one, and apply one to a preset.
	public sealed class Fm9BlockLibrary
	{
		private readonly string _folder;

		public Fm9BlockLibrary(string folder) => _folder = folder ?? throw new ArgumentNullException(nameof(folder));

		public IReadOnlyList<Fm9LibraryEntry> List(string? blockType = null)
		{
			if (!Directory.Exists(_folder)) return [];
			string? type = blockType is null ? null : Fm9Names.BlockType(blockType);
			List<Fm9LibraryEntry> entries = [];
			foreach (string file in Directory.EnumerateFiles(_folder, "*.blk", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
			{
				Fm9LibraryBlock block;
				try { block = Fm9BlockLibraryFormat.Read(File.ReadAllBytes(file)); }
				catch (InvalidDataException) { continue; }
				if (type is not null && block.BlockType != type) continue;
				entries.Add(new(file, block.Name, block.BlockType, block.ChannelOnly ? Fm9Names.Letter(block.Channel) : "all",
					Enumerable.Range(0, block.Rows).Select(channel => Model(block, channel)).ToArray()));
			}
			return entries;
		}

		// Saves a block of a preset to the library, all four channels or (with a channel) as a single-channel favourite.
		public Fm9LibraryEntry Save(string preset, Fm9BlockRef block, string name, string? channel = null, int firmwareMajor = 12)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(name);
			Fm9UploadedPreset source = Fm9Presets.Load(preset);
			Fm9UploadedBlock found = Fm9Presets.Editable(source, block);
			Fm9PresetBlockRecord record = found.Record!;
			List<int> words = [];
			for (int row = 0; row < record.ParameterRows; row++)
				for (int id = 0; id < record.ParameterColumns; id++)
					words.Add(BitConverter.ToUInt16(source.Body.Bytes.Span[Fm9ChannelParameterStorage.GetOffset(record, row, id)..]));
			int? single = channel is null ? null : Fm9Names.Channel(channel);
			Fm9LibraryBlock saved = new(name.Trim(), found.BlockType, found.EffectId, firmwareMajor, Guid.NewGuid(), single is not null, single ?? 0,
				record.ParameterRows, words);
			string folder = Path.Combine(_folder, Fm9BlockLibraryFormat.Folder(found.BlockType));
			string file = Path.Combine(folder, $"{Fm9Device.FileName(name)}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.blk");
			byte[] bytes = Fm9BlockLibraryFormat.Write(saved);
			_ = Fm9BlockLibraryFormat.Read(bytes);
			Fm9Files.WriteAtomically(file, bytes);
			return new(file, saved.Name, saved.BlockType, single is null ? "all" : Fm9Names.Letter(single.Value),
				Enumerable.Range(0, saved.Rows).Select(index => Model(saved, index)).ToArray());
		}

		// Applies a favourite to a block of the same type in a preset. An all-channel favourite replaces every channel, or
		// with a channel only that one (from the favourite's same channel unless another is named); a single-channel
		// favourite goes into the channel it was saved from unless another is named. Scenes and cables are untouched.
		public static Fm9EditResult Apply(string libraryFile, string input, string output, Fm9BlockRef? block = null,
			string? toChannel = null, string? fromChannel = null)
		{
			Fm9LibraryBlock favourite = Fm9BlockLibraryFormat.Read(File.ReadAllBytes(libraryFile));
			Fm9UploadedPreset preset = Fm9Presets.Load(input);
			Fm9UploadedBlock target = Fm9Presets.Editable(preset, block ?? new(favourite.BlockType, 1));
			if (target.BlockType != favourite.BlockType)
				throw new ArgumentException($"'{favourite.Name}' is a {favourite.BlockType} favourite; {Fm9Presets.Name(target)} is a {target.BlockType}.");
			Fm9PresetBlockRecord record = target.Record!;
			if (favourite.Columns > record.ParameterColumns || favourite.Rows != record.ParameterRows)
				throw new NotSupportedException($"'{favourite.Name}' was saved by newer firmware ({favourite.Columns} settings per channel; this preset's block has {record.ParameterColumns}).");
			int source = fromChannel is not null ? Fm9Names.Channel(fromChannel) : favourite.ChannelOnly ? favourite.Channel : -1;
			int destination = toChannel is not null ? Fm9Names.Channel(toChannel) : source;
			if (source < 0 && destination >= 0) source = destination;
			byte[] bytes = preset.Body.Bytes.ToArray();
			List<string> changes = [];
			IEnumerable<(int From, int To)> rows = destination < 0
				? Enumerable.Range(0, record.ParameterRows).Select(row => (row, row))
				: [(source, destination)];
			foreach ((int from, int to) in rows)
			{
				IReadOnlyList<int> words = favourite.Row(from);
				for (int id = 0; id < words.Count; id++)
					BitConverter.TryWriteBytes(bytes.AsSpan(Fm9ChannelParameterStorage.GetOffset(record, to, id)), checked((ushort)words[id]));
				changes.Add($"{Fm9Presets.Name(target)} {Fm9Names.Letter(to)} = '{favourite.Name}' channel {Fm9Names.Letter(from)}" +
					(Model(favourite, from) is string model ? $" ({model})" : ""));
			}
			if (favourite.Columns < record.ParameterColumns)
				changes.Add($"The favourite came from older firmware; the {record.ParameterColumns - favourite.Columns} newer settings per channel kept the preset's values.");
			return Fm9Presets.Save(Fm9PresetBodyEncoder.Reencode(preset.Dump, bytes), input, output, changes);
		}

		private static string? Model(Fm9LibraryBlock block, int channel)
		{
			if (channel >= block.Rows) return null;
			IReadOnlyList<int> row = block.Row(channel);
			Dictionary<string, string> settings = new(StringComparer.Ordinal);
			if (block.BlockType == "Amp" && row.Count > 10) settings["Amp Model"] = row[10].ToString(CultureInfo.InvariantCulture);
			if (block.BlockType == "Cab" && row.Count > 4)
			{
				settings["Bank"] = row[0].ToString(CultureInfo.InvariantCulture);
				settings["Cab Type"] = row[4].ToString(CultureInfo.InvariantCulture);
			}
			foreach (Fm9BlockSetting setting in Fm9BlockSettings.For(block.BlockType).Where(setting => setting.Id < row.Count))
				if (setting.Read(row[setting.Id]) is string value) settings[setting.Name] = value;
			if (block.BlockType == "Drive" && Fm9BlockRecordTemplateProfile.FindFamilyDefinition("Drive")?.Parameters
				.FirstOrDefault(parameter => parameter.Name == "Drive Model") is { } selector && selector.ParameterId < row.Count)
				settings["Drive Model"] = row[selector.ParameterId].ToString(CultureInfo.InvariantCulture);
			return Fm9Names.Model(block.BlockType, settings);
		}
	}
}
