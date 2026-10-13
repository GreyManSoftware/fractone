using System.Globalization;
using System.Text.Json;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9
{
	public sealed record Fm9IndexedChannel(string Channel, string? Model, IReadOnlyDictionary<string, string> Ids, IReadOnlyList<int> Scenes);

	public sealed record Fm9IndexedBlock(string Block, bool Editable, IReadOnlyList<Fm9IndexedChannel> Channels);

	public sealed record Fm9IndexedPreset(int? Slot, string Name, string Source, IReadOnlyList<string> Scenes, IReadOnlyList<Fm9IndexedBlock> Blocks);

	public sealed record Fm9PresetIndexDocument(DateTimeOffset Built, IReadOnlyList<Fm9IndexedPreset> Presets, IReadOnlyList<string> Skipped);

	public sealed record Fm9SearchHit(int? Slot, string Preset, string Block, string Channel, string? Model, IReadOnlyList<int> Scenes);

	public sealed record Fm9IndexQuery
	{
		// Matched against preset names, block names and model names (any word order, case ignored).
		public string? Text { get; init; }
		public string? Block { get; init; }
		public string? Model { get; init; }
		public int? CabBank { get; init; }
		public int? CabType { get; init; }
		// Only channels some scene actually plays (engaged); otherwise every channel the block holds.
		public bool PlayedOnly { get; init; } = true;
	}

	// Every preset's blocks, what each channel plays and which scenes play it, built from .syx files: single presets (as the
	// device backup writes them) or FM9-Edit / Fractal-Bot bank files of 128.
	public sealed class Fm9PresetIndex
	{
		private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		private readonly Dictionary<string, Fm9IndexedPreset> _presets = new(StringComparer.Ordinal);
		private readonly List<string> _skipped = [];

		public IReadOnlyList<Fm9IndexedPreset> Presets => _presets.Values.OrderBy(preset => preset.Slot ?? int.MaxValue).ThenBy(preset => preset.Source).ToArray();
		public IReadOnlyList<string> Skipped => _skipped;

		public static Fm9PresetIndex Build(IEnumerable<string> paths)
		{
			Fm9PresetIndex index = new();
			foreach (string path in paths)
			{
				IEnumerable<string> files = Directory.Exists(path)
					? Directory.EnumerateFiles(path, "*.syx", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
					: [path];
				foreach (string file in files)
					index.AddFile(file);
			}
			return index;
		}

		public void AddFile(string file)
		{
			List<(int? Slot, Fm9PresetDump Dump)> presets;
			try { presets = Split(File.ReadAllBytes(file)); }
			catch (InvalidDataException exception)
			{
				_skipped.Add($"{file}: {exception.Message}");
				return;
			}
			// A single preset without a slot in its header takes it from the backup's file name ("042 Name.syx").
			if (presets is [(null, Fm9PresetDump single)] &&
				System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"^(\d{3,4}) ") is { Success: true } named &&
				int.Parse(named.Groups[1].Value, CultureInfo.InvariantCulture) is int numbered and <= 511)
				presets = [(numbered, single)];
			foreach ((int? slot, Fm9PresetDump dump) in presets)
			{
				try { Upsert(Index(slot, dump, Path.GetFullPath(file))); }
				catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or InvalidOperationException)
				{
					_skipped.Add($"{(slot is null ? file : $"preset {slot}")} '{dump.PresetName}': {exception.Message}");
				}
			}
		}

		// Adds or replaces a preset (by slot, or by file for presets without one), e.g. after a store.
		public void Upsert(Fm9IndexedPreset preset)
		{
			ArgumentNullException.ThrowIfNull(preset);
			_presets[preset.Slot?.ToString(CultureInfo.InvariantCulture) ?? preset.Source] = preset;
		}

		public static Fm9IndexedPreset Index(int? slot, Fm9PresetDump dump, string source)
		{
			Fm9UploadedPreset preset = Fm9UploadedPreset.Read(dump);
			List<Fm9IndexedBlock> blocks = [];
			foreach (Fm9UploadedBlock block in preset.Blocks.Where(block => block.BlockType is not ("Input" or "Output")))
			{
				if (!block.IsEditable)
				{
					blocks.Add(new(Fm9Presets.Name(block), false, []));
					continue;
				}
				Fm9PresetBlockSceneState[] states = Enumerable.Range(1, Fm9Presets.SceneCount).Select(scene => preset.GetSceneState(block, scene)).ToArray();
				List<Fm9IndexedChannel> channels = [];
				for (int channel = 0; channel < Fm9UploadedPreset.ChannelsOf(block); channel++)
				{
					IReadOnlyDictionary<string, string> settings = preset.GetParameters(block, channel);
					int[] scenes = Enumerable.Range(1, Fm9Presets.SceneCount)
						.Where(scene => (int)states[scene - 1].Channel == channel && !states[scene - 1].Bypassed).ToArray();
					channels.Add(new(Fm9Names.Letter(channel), Fm9Names.Model(block.BlockType, settings), Ids(block.BlockType, settings), scenes));
				}
				blocks.Add(new(Fm9Presets.Name(block), true, channels));
			}
			return new(slot, preset.Name, source, Enumerable.Range(1, Fm9Presets.SceneCount).Select(preset.GetSceneName).ToArray(), blocks);
		}

		public IReadOnlyList<Fm9SearchHit> Search(Fm9IndexQuery query)
		{
			ArgumentNullException.ThrowIfNull(query);
			string[] words = (query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			string? blockType = query.Block is null ? null : Fm9BlockRef.Parse(query.Block).BlockType;
			int? instance = query.Block is not null && query.Block.Trim().Any(char.IsDigit) ? Fm9BlockRef.Parse(query.Block).Instance : null;
			List<Fm9SearchHit> hits = [];
			foreach (Fm9IndexedPreset preset in Presets)
				foreach (Fm9IndexedBlock block in preset.Blocks)
				{
					Fm9BlockRef reference = Fm9BlockRef.Parse(block.Block);
					if (blockType is not null && (reference.BlockType != blockType || (instance is int wanted && reference.Instance != wanted))) continue;
					foreach (Fm9IndexedChannel channel in block.Channels)
					{
						if (query.PlayedOnly && channel.Scenes.Count == 0) continue;
						if (query.Model is { } model && !(channel.Model?.Contains(model, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
						// Bank and IR slot only describe a legacy-mode cab; in DynaCab mode they are leftovers.
						if ((query.CabBank is not null || query.CabType is not null) && channel.Ids.GetValueOrDefault("Mode") == "1") continue;
						if (query.CabBank is int bank && channel.Ids.GetValueOrDefault("Bank") != bank.ToString(CultureInfo.InvariantCulture)) continue;
						if (query.CabType is int type && channel.Ids.GetValueOrDefault("Cab Type") != type.ToString(CultureInfo.InvariantCulture)) continue;
						string haystack = $"{preset.Name} {block.Block} {channel.Model}";
						if (!words.All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
						hits.Add(new(preset.Slot, preset.Name, block.Block, channel.Channel, channel.Model, channel.Scenes));
					}
				}
			return hits;
		}

		public void Save(string path) =>
			Fm9Files.WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(new Fm9PresetIndexDocument(DateTimeOffset.Now, Presets, _skipped), Json));

		public static Fm9PresetIndex Load(string path)
		{
			Fm9PresetIndex index = new();
			if (!File.Exists(path)) return index;
			Fm9PresetIndexDocument document = JsonSerializer.Deserialize<Fm9PresetIndexDocument>(File.ReadAllBytes(path), Json)
				?? throw new InvalidDataException("The preset index file is empty.");
			foreach (Fm9IndexedPreset preset in document.Presets) index.Upsert(preset);
			index._skipped.AddRange(document.Skipped);
			return index;
		}

		// One file's presets with their slots (from each preset's header; the edit buffer's has none).
		public static List<(int? Slot, Fm9PresetDump Dump)> Split(ReadOnlySpan<byte> bytes)
		{
			List<(int?, Fm9PresetDump)> presets = [];
			int start = -1;
			for (int index = 0; index < bytes.Length;)
			{
				if (bytes[index] != 0xF0) throw new InvalidDataException($"Expected SysEx at byte {index}.");
				int end = bytes[index..].IndexOf((byte)0xF7) + index;
				if (end < index) throw new InvalidDataException("A SysEx message has no end.");
				byte function = bytes[index + 5];
				if (function == 0x77) start = index;
				if (function == 0x79 && start >= 0)
				{
					Fm9PresetDump dump;
					try { dump = Fm9PresetDump.Parse(bytes[start..(end + 1)]); }
					catch (Fm9EmptyPresetSlotException) { start = -1; index = end + 1; continue; }
					byte high = bytes[start + 6], low = bytes[start + 7];
					// Bank files name each preset's slot; a preset read over MIDI or from the edit buffer carries 0x7F there.
					presets.Add((high == 0x7F ? null : (high << 7) | low, dump));
					start = -1;
				}
				index = end + 1;
			}
			return presets.Count > 0 ? presets : throw new InvalidDataException("No FM9 presets were found in the file.");
		}

		// The numbers behind what a channel plays, for exact searches: the amp model or block type ordinal, the cab's bank and type.
		private static IReadOnlyDictionary<string, string> Ids(string blockType, IReadOnlyDictionary<string, string> settings)
		{
			Dictionary<string, string> ids = new(StringComparer.Ordinal);
			foreach (string name in blockType == "Cab" ? ["Mode", "Bank", "Cab Type", "Dynacab Type1"] : Fm9ModelDefaults.TypeSetting(blockType) is string type ? [type] : Array.Empty<string>())
				if (settings.TryGetValue(name, out string? value)) ids[name] = value;
			return ids;
		}
	}
}
