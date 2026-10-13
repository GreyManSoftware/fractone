using System.Globalization;
using Fractal.Application;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9
{
	public sealed record Fm9ChannelSummary(string Channel, string? Model, IReadOnlyList<int> Scenes);

	public sealed record Fm9BlockSummary(string Block, int Row, int Column, bool Editable, IReadOnlyList<Fm9ChannelSummary> Channels);

	public sealed record Fm9SceneBlock(string Block, string Channel, bool Bypassed);

	public sealed record Fm9SceneSummary(int Number, string Name, IReadOnlyList<Fm9SceneBlock> Blocks);

	public sealed record Fm9PresetSummary(string Name, IReadOnlyList<Fm9BlockSummary> Blocks, IReadOnlyList<Fm9SceneSummary> Scenes,
		IReadOnlyList<string> Modifiers, IReadOnlyList<string> Notes);

	public sealed record Fm9EditResult(string Output, string PresetName, IReadOnlyList<string> Changes);

	public sealed record Fm9SettingInfo(string Name, string Kind, double? Minimum, double? Maximum, IReadOnlyList<string> Choices);

	// Offline preset edits on .syx files, as FracAI's designer makes them: only what is asked changes, a new model or type
	// loads the voicing the FM9 would load, and every block keeps its cell and cables. Every edit writes a new file (never
	// the input) and reads it back before reporting.
	public static class Fm9Presets
	{
		public const int SceneCount = Fm9UploadedPreset.SceneCount;

		public static Fm9UploadedPreset Load(string path) => Fm9UploadedPreset.Read(Fm9PresetDump.Parse(File.ReadAllBytes(path)));

		public static Fm9PresetSummary Describe(string path) => Describe(Load(path));

		public static Fm9PresetSummary Describe(Fm9UploadedPreset preset)
		{
			ArgumentNullException.ThrowIfNull(preset);
			List<Fm9BlockSummary> blocks = [];
			Dictionary<Fm9UploadedBlock, Fm9PresetBlockSceneState[]> states = [];
			foreach (Fm9UploadedBlock block in preset.Blocks)
			{
				if (!block.IsEditable)
				{
					blocks.Add(new(Name(block), block.Row, block.Column, false, []));
					continue;
				}
				Fm9PresetBlockSceneState[] sceneStates = Enumerable.Range(1, SceneCount).Select(scene => preset.GetSceneState(block, scene)).ToArray();
				states[block] = sceneStates;
				List<Fm9ChannelSummary> channels = [];
				for (int channel = 0; channel < Fm9UploadedPreset.ChannelsOf(block); channel++)
				{
					int[] scenes = Enumerable.Range(1, SceneCount)
						.Where(scene => (int)sceneStates[scene - 1].Channel == channel && !sceneStates[scene - 1].Bypassed).ToArray();
					channels.Add(new(Fm9Names.Letter(channel), Fm9Names.Model(block.BlockType, preset.GetParameters(block, channel)), scenes));
				}
				blocks.Add(new(Name(block), block.Row, block.Column, true, channels));
			}
			Fm9SceneSummary[] sceneSummaries = Enumerable.Range(1, SceneCount).Select(scene => new Fm9SceneSummary(scene, preset.GetSceneName(scene),
				states.Where(entry => entry.Key.BlockType is not ("Input" or "Output"))
					.Select(entry => new Fm9SceneBlock(Name(entry.Key), Fm9Names.Letter((int)entry.Value[scene - 1].Channel), entry.Value[scene - 1].Bypassed))
					.ToArray())).ToArray();
			string[] modifiers = Fm9PresetModifiers.Read(preset.Body).Select(entry => entry.Modifier).Select(modifier =>
				string.Create(CultureInfo.InvariantCulture,
					$"{modifier.Source} -> {modifier.BlockType} {modifier.Instance} {modifier.Parameter} ({modifier.Start:0.#}-{modifier.End:0.#}%, channel {modifier.Channel})"))
				.ToArray();
			string[] notes = preset.Blocks.Where(block => !block.IsEditable && block.BlockType is not ("Input" or "Output"))
				.Select(block => $"{Name(block)} is shown but not editable here (its record has a shape this firmware-12 tooling does not know).")
				.ToArray();
			return new(preset.Name, blocks, sceneSummaries, modifiers, notes);
		}

		// Every setting of one block channel, by name, as display values.
		public static IReadOnlyDictionary<string, string> GetChannel(string path, Fm9BlockRef block, string channel)
		{
			Fm9UploadedPreset preset = Load(path);
			return new SortedDictionary<string, string>(preset.GetParameters(Editable(preset, block), Fm9Names.Channel(channel)).ToDictionary(), StringComparer.Ordinal);
		}

		// The settings a block type exposes, with their ranges or choices.
		public static IReadOnlyList<Fm9SettingInfo> ListSettings(string blockType)
		{
			string type = Fm9Names.BlockType(blockType);
			List<Fm9SettingInfo> settings = [];
			if (type == "Amp")
				settings.Add(new("Amp Model", "choice", null, null, Fm9Names.TypeLabels("Amp", "Amp Model")));
			if (type == "Cab")
			{
				settings.Add(new("Bank", "integer", 0, 4, []));
				settings.Add(new("Cab Type", "integer", 0, 1023, []));
			}
			foreach (Fm9TemplateParameterDefinition curated in Fm9BlockRecordTemplateProfile.FindFamilyDefinition(type)?.Parameters ?? [])
			{
				IReadOnlyList<string> labels = Fm9ModelDefaults.TypeSetting(type) == curated.Name ? Fm9Names.TypeLabels(type, curated.Name) : [];
				settings.Add(new(curated.Name, labels.Count > 0 ? "choice" : curated.IsDiscrete ? "integer" : "number",
					labels.Count > 0 ? null : curated.Minimum, labels.Count > 0 ? null : curated.Maximum, labels));
			}
			foreach (Fm9BlockSetting setting in Fm9BlockSettings.For(type))
				settings.Add(new(setting.Name, setting.IsChoice ? "choice" : setting.IsContinuous ? "number" : "integer",
					setting.Minimum, setting.Maximum, setting.Choices));
			return settings.DistinctBy(setting => setting.Name).ToArray();
		}

		// Writes only the given settings to one block channel. A new model or type loads its FM9 voicing first, then the given
		// settings are written over it; anything not given keeps the voicing (or, without a model change, its old value).
		public static Fm9EditResult SetChannel(string input, string output, Fm9BlockRef block, string channel,
			IReadOnlyDictionary<string, string> settings)
		{
			ArgumentNullException.ThrowIfNull(settings);
			if (settings.Count == 0) throw new ArgumentException("Give at least one setting to change.", nameof(settings));
			Fm9UploadedPreset preset = Load(input);
			Fm9UploadedBlock target = Editable(preset, block);
			int row = Fm9Names.Channel(channel);
			IReadOnlyDictionary<string, string> before = preset.GetParameters(target, row);
			ToneDesignProposal proposal = preset.ToProposal();
			string letter = Fm9Names.Letter(row);
			Dictionary<string, string> named = settings.ToDictionary(pair => Canonical(target, before, pair.Key),
				pair => ModelOrdinal(target.BlockType, Canonical(target, before, pair.Key), pair.Value), StringComparer.Ordinal);
			proposal = proposal with
			{
				Blocks = proposal.Blocks.Select(candidate => Same(candidate, target)
					? candidate.WithChannel(letter, named, null) : candidate).ToArray()
			};
			Fm9PresetDump edited = Fm9UploadedPreset.Apply(proposal);
			Fm9UploadedPreset check = Fm9UploadedPreset.Read(edited);
			IReadOnlyDictionary<string, string> after = check.GetParameters(check.Find(target.BlockType, target.Instance)!, row);
			List<string> changes = [];
			foreach ((string name, string value) in named)
			{
				string? written = after.GetValueOrDefault(name) ?? after.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
				if (written is null)
					throw new InvalidDataException($"{Name(target)} channel {letter} has no setting '{name}' after the edit.");
				if (!SameValue(written, value, target.BlockType, name))
					throw new InvalidDataException($"{Name(target)} channel {letter} {name} reads back as {written}, not {value}.");
				changes.Add($"{Name(target)} {letter}: {name} {before.GetValueOrDefault(name) ?? "(unset)"} -> {written}");
			}
			string? oldModel = Fm9Names.Model(target.BlockType, before), newModel = Fm9Names.Model(target.BlockType, after);
			if (oldModel != newModel)
				changes.Add($"{Name(target)} {letter}: now {newModel ?? "unknown model"} (was {oldModel ?? "unknown"}), with the FM9's voicing for it");
			return Save(edited, input, output, changes);
		}

		// Copies one block channel from a preset (this one or another) onto a channel of this one. When both records have
		// the same shape the channel's words are copied exactly, so even values this tooling cannot name (a user IR, for
		// instance) come across; otherwise every named setting is copied.
		public static Fm9EditResult CopyChannel(string input, string output, string source, Fm9BlockRef block,
			string fromChannel, string toChannel, Fm9BlockRef? sourceBlock = null)
		{
			Fm9UploadedPreset destination = Load(input);
			Fm9UploadedPreset origin = Path.GetFullPath(source) == Path.GetFullPath(input) ? destination : Load(source);
			Fm9UploadedBlock to = Editable(destination, block);
			Fm9UploadedBlock from = Editable(origin, sourceBlock ?? block);
			if (!from.BlockType.Equals(to.BlockType, StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException($"Cannot copy a {from.BlockType} channel onto a {to.BlockType}.");
			int fromRow = Fm9Names.Channel(fromChannel), toRow = Fm9Names.Channel(toChannel);
			if (fromRow >= Fm9UploadedPreset.ChannelsOf(from) || toRow >= Fm9UploadedPreset.ChannelsOf(to))
				throw new ArgumentException($"{Name(to)} does not have channel {(fromRow >= Fm9UploadedPreset.ChannelsOf(from) ? fromChannel : toChannel)}.");
			string label = $"{Name(to)} {Fm9Names.Letter(toRow)}";
			string originLabel = origin == destination ? $"channel {Fm9Names.Letter(fromRow)}" : $"{origin.Name} {Name(from)} {Fm9Names.Letter(fromRow)}";
			Fm9PresetDump edited;
			string how;
			if (from.Record!.ParameterColumns == to.Record!.ParameterColumns)
			{
				byte[] bytes = destination.Body.Bytes.ToArray();
				ReadOnlySpan<byte> sourceBytes = origin.Body.Bytes.Span;
				int length = to.Record.ParameterColumns * sizeof(ushort);
				sourceBytes.Slice(Fm9ChannelParameterStorage.GetOffset(from.Record, fromRow, 0), length)
					.CopyTo(bytes.AsSpan(Fm9ChannelParameterStorage.GetOffset(to.Record, toRow, 0), length));
				edited = Fm9PresetBodyEncoder.Reencode(destination.Dump, bytes);
				how = "every word copied exactly";
			}
			else
			{
				IReadOnlyDictionary<string, string> values = origin.GetParameters(from, fromRow);
				ToneDesignProposal proposal = destination.ToProposal();
				proposal = proposal with
				{
					Blocks = proposal.Blocks.Select(candidate => Same(candidate, to)
						? candidate.WithChannel(Fm9Names.Letter(toRow), new Dictionary<string, string>(values, StringComparer.Ordinal), null) : candidate).ToArray()
				};
				edited = Fm9UploadedPreset.Apply(proposal);
				how = $"{values.Count} named settings copied (the records differ in width)";
			}
			Fm9UploadedPreset check = Fm9UploadedPreset.Read(edited);
			string? model = Fm9Names.Model(to.BlockType, check.GetParameters(check.Find(to.BlockType, to.Instance)!, toRow));
			return Save(edited, input, output, [$"{label} = {originLabel}{(model is null ? "" : $" ({model})")}: {how}"]);
		}

		// Sets what one scene plays: optionally starting as a copy of another scene, then the given blocks' channels and
		// bypass states, and a new name. Blocks not mentioned keep the scene's (or the copied scene's) state.
		public static Fm9EditResult ComposeScene(string input, string output, int scene, int? copyFrom, string? name,
			IReadOnlyDictionary<string, string>? channels, IReadOnlyDictionary<string, bool>? bypassed)
		{
			if (scene is < 1 or > SceneCount) throw new ArgumentOutOfRangeException(nameof(scene), "Scenes are 1 to 8.");
			if (copyFrom is < 1 or > SceneCount) throw new ArgumentOutOfRangeException(nameof(copyFrom), "Scenes are 1 to 8.");
			Fm9UploadedPreset preset = Load(input);
			ToneDesignProposal proposal = preset.ToProposal();
			List<ToneDesignSceneProposal> scenes = proposal.Scenes.ToList();
			ToneDesignSceneProposal start = scenes[(copyFrom ?? scene) - 1];
			List<string> changes = [];
			if (copyFrom is int copied && copied != scene)
				changes.Add($"Scene {scene} starts as a copy of scene {copied} ('{start.Name}')");
			List<ToneDesignSceneBlockSetting> settings = start.BlockSettings.ToList();
			foreach ((string blockText, string channel) in channels ?? new Dictionary<string, string>())
			{
				Fm9UploadedBlock target = Editable(preset, Fm9BlockRef.Parse(blockText));
				int row = Fm9Names.Channel(channel);
				if (row >= Fm9UploadedPreset.ChannelsOf(target)) throw new ArgumentException($"{Name(target)} has no channel {channel}.");
				int index = settings.FindIndex(setting => Same(setting, target));
				if (index < 0) throw new InvalidDataException($"Scene {scene} does not list {Name(target)}.");
				settings[index] = settings[index] with { Channel = Fm9Names.Letter(row) };
				changes.Add($"Scene {scene}: {Name(target)} plays channel {Fm9Names.Letter(row)}");
			}
			foreach ((string blockText, bool off) in bypassed ?? new Dictionary<string, bool>())
			{
				Fm9UploadedBlock target = Editable(preset, Fm9BlockRef.Parse(blockText));
				int index = settings.FindIndex(setting => Same(setting, target));
				if (index < 0) throw new InvalidDataException($"Scene {scene} does not list {Name(target)}.");
				settings[index] = settings[index] with { IsBypassed = off };
				changes.Add($"Scene {scene}: {Name(target)} {(off ? "bypassed" : "engaged")}");
			}
			string sceneName = name ?? (copyFrom is int && copyFrom != scene ? scenes[scene - 1].Name : start.Name);
			if (name is not null)
			{
				Fm9PresetBodySceneAuthor.ValidateSceneName(name);
				changes.Add($"Scene {scene} named '{name}'");
			}
			scenes[scene - 1] = start with { Name = sceneName, BlockSettings = settings };
			if (changes.Count == 0) throw new ArgumentException("Nothing to change: give a scene to copy, channels, bypass states or a name.");
			return Save(Fm9UploadedPreset.Apply(proposal with { Scenes = scenes }), input, output, changes);
		}

		// Takes blocks out as the FM9 does: a shunt keeps each cell's cables and scenes stop listing the block.
		public static Fm9EditResult RemoveBlocks(string input, string output, IReadOnlyList<Fm9BlockRef> blocks)
		{
			if (blocks is not { Count: > 0 }) throw new ArgumentException("Name at least one block to remove.", nameof(blocks));
			Fm9UploadedPreset preset = Load(input);
			ToneDesignProposal proposal = preset.ToProposal();
			List<string> changes = [];
			foreach (Fm9BlockRef wanted in blocks)
			{
				Fm9UploadedBlock target = preset.Find(wanted.BlockType, wanted.Instance)
					?? throw new ArgumentException($"The preset has no {wanted}.");
				if (target.BlockType is "Input" or "Output") throw new NotSupportedException("Inputs and outputs carry the routing; change them on the FM9.");
				proposal = proposal with
				{
					Blocks = proposal.Blocks.Where(candidate => !Same(candidate, target)).ToArray(),
					PreservedBlocks = proposal.PreservedBlocks.Where(kept => !(kept.BlockType == target.BlockType && kept.Instance == target.Instance)).ToArray(),
					RemovedBlocks = [.. proposal.RemovedBlocks, new ToneDesignPreservedBlock(target.BlockType, target.Instance, target.Row, target.Column)],
					Shunts = [.. proposal.Shunts, new ToneDesignGridCell(target.Row, target.Column)],
					Scenes = proposal.Scenes.Select(scene => scene with
					{
						BlockSettings = scene.BlockSettings.Where(setting => !Same(setting, target)).ToArray()
					}).ToArray()
				};
				changes.Add($"Removed {Name(target)} from row {target.Row}, column {target.Column}; a shunt keeps its cables");
			}
			return Save(Fm9UploadedPreset.Apply(proposal), input, output, changes);
		}

		// Puts a new block, with the FM9's default settings, in a cell that holds a shunt (so it inherits that cell's cables).
		public static Fm9EditResult AddBlock(string input, string output, Fm9BlockRef block, int row, int column)
		{
			Fm9UploadedPreset preset = Load(input);
			if (preset.Find(block.BlockType, block.Instance) is not null) throw new ArgumentException($"The preset already has {block}.");
			if (!Fm9PresetBodyBlockInserter.CanInsert(block.BlockType, block.Instance))
				throw new NotSupportedException($"{block} cannot be added yet: its default record was never captured from the FM9.");
			ToneDesignProposal proposal = preset.ToProposal();
			proposal = proposal with
			{
				Blocks = [.. proposal.Blocks, new ToneDesignBlockProposal(block.BlockType, block.Instance, row, column, new Dictionary<string, string>())],
				Shunts = proposal.Shunts.Where(cell => cell.Row != row || cell.Column != column).ToArray()
			};
			return Save(Fm9UploadedPreset.Apply(proposal), input, output, [$"Added {block} at row {row}, column {column} with the FM9's default settings"]);
		}

		public static Fm9EditResult Rename(string input, string output, string name)
		{
			Fm9UploadedPreset preset = Load(input);
			Fm9PresetDump renamed = preset.Dump.WithPresetName(name);
			return Save(renamed, input, output, [$"Renamed '{preset.Name}' to '{renamed.PresetName}'"]);
		}

		public static string Name(Fm9UploadedBlock block) => block.Instance > 0 ? $"{block.BlockType} {block.Instance}" : block.BlockType;

		internal static Fm9UploadedBlock Editable(Fm9UploadedPreset preset, Fm9BlockRef block)
		{
			Fm9UploadedBlock found = preset.Find(block.BlockType, block.Instance)
				?? throw new ArgumentException($"The preset has no {block}. It has: {string.Join(", ", preset.Blocks.Select(Name))}.");
			return found.IsEditable ? found : throw new NotSupportedException($"{block} cannot be edited here; its record has a shape this tooling does not know.");
		}

		internal static Fm9EditResult Save(Fm9PresetDump edited, string input, string output, IReadOnlyList<string> changes)
		{
			string outputPath = Path.GetFullPath(output);
			if (string.Equals(outputPath, Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException("Write edits to a new file; the input is kept as it was.", nameof(output));
			byte[] bytes = edited.ToArray();
			_ = Fm9UploadedPreset.Read(Fm9PresetDump.Parse(bytes));
			Fm9Files.WriteAtomically(outputPath, bytes);
			return new(outputPath, edited.PresetName, changes);
		}

		// A setting name as the block spells it ("gain" -> "Gain"); unknown names are kept and refused by the writer.
		internal static string Canonical(Fm9UploadedBlock block, IReadOnlyDictionary<string, string> current, string name) =>
			current.Keys.FirstOrDefault(key => key.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
			?? Fm9BlockSettings.Find(block.BlockType, name.Trim())?.Name ?? name.Trim();

		// A model or type given by name ("USA IIC++") becomes the ordinal the record stores.
		internal static string ModelOrdinal(string blockType, string name, string value)
		{
			if (Fm9ModelDefaults.TypeSetting(blockType) != name || int.TryParse(value, CultureInfo.InvariantCulture, out _)) return value;
			int index = Fm9Names.TypeLabels(blockType, name).ToList().FindIndex(label => label.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
			return index >= 0 ? index.ToString(CultureInfo.InvariantCulture)
				: throw new ArgumentException($"{blockType} has no {name} called '{value}'. Use fm9 settings to list them.");
		}

		private static bool Same(ToneDesignBlockProposal block, Fm9UploadedBlock target) =>
			block.BlockType == target.BlockType && block.Instance == target.Instance;

		private static bool Same(ToneDesignSceneBlockSetting setting, Fm9UploadedBlock target) =>
			setting.BlockType == target.BlockType && setting.Instance == target.Instance;

		// Values match when equal as text, as numbers within the setting's resolution, or as a choice given by name.
		private static bool SameValue(string written, string asked, string blockType, string name)
		{
			if (string.Equals(written, asked.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
			if (double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
				double.TryParse(asked, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
				return Math.Abs(a - b) <= Math.Max(0.011, Math.Abs(b) * 0.002);
			if (Fm9BlockSettings.Find(blockType, name) is { IsChoice: true } setting &&
				int.TryParse(written, CultureInfo.InvariantCulture, out int ordinal) && ordinal < setting.Choices.Count)
				return string.Equals(setting.Choices[ordinal], asked.Trim(), StringComparison.OrdinalIgnoreCase);
			if (Fm9ModelDefaults.TypeSetting(blockType) == name && int.TryParse(written, CultureInfo.InvariantCulture, out int model))
				return Fm9Names.TypeLabels(blockType, name).ElementAtOrDefault(model) is string label &&
					string.Equals(label, asked.Trim(), StringComparison.OrdinalIgnoreCase);
			return false;
		}
	}

	public static class Fm9Files
	{
		public static void WriteAtomically(string path, ReadOnlySpan<byte> contents)
		{
			string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
			Directory.CreateDirectory(directory);
			string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}");
			try
			{
				using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					stream.Write(contents);
				File.Move(temporary, path, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary)) File.Delete(temporary);
			}
		}
	}
}
