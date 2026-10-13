using System.Globalization;
using System.Text;
using Fractal.Companion.Protocol;

namespace Fractal.Companion.Catalog
{
	// One editable setting of a block: a name unique within the block, its record column and FM9-Edit's definition.
	public sealed record Fm9BlockSetting(string Name, int Id, Fm9EffectParameter Definition)
	{
		public bool IsChoice => Definition.Kind == Fm9ParameterKind.Choice;
		public bool IsContinuous => Definition.Kind is Fm9ParameterKind.Linear or Fm9ParameterKind.Logarithmic;
		public double? Minimum => IsContinuous ? Definition.DisplayMinimum : IsChoice ? null : Definition.Minimum;
		public double? Maximum => IsContinuous ? Definition.DisplayMaximum : IsChoice ? null : Definition.Maximum;
		public IReadOnlyList<string> Choices => IsChoice ? Definition.Labels : [];

		// The stored word as proposal text; null when it is not a value this setting can hold (e.g. a choice or shift older
		// firmware stored in another form), which leaves it exactly as uploaded. Signed settings (pitch shifts, -24..+24)
		// are 16-bit two's complement: writing -12 through the FM9 itself stores 65524, -1 stores 65535 (2026-10-04).
		public string? Read(int wire)
		{
			if (IsContinuous) return Fm9SettingText.Number(Definition.ToDisplay(wire));
			int last = IsChoice ? Definition.Labels.Count - 1 : (int)Math.Round(Definition.Maximum);
			int first = IsChoice ? 0 : (int)Math.Round(Definition.Minimum);
			int value = first < 0 && wire > short.MaxValue ? wire - 65536 : wire;
			return value >= first && value <= last ? value.ToString(CultureInfo.InvariantCulture) : null;
		}

		public int Write(string value)
		{
			// A choice may be given by its FM9 name ("1/8 DOT") as well as its position.
			if (IsChoice && Definition.Labels.ToList().FindIndex(label => string.Equals(label, value?.Trim(), StringComparison.OrdinalIgnoreCase)) is >= 0 and int named)
				return named;
			if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
				throw new InvalidDataException(IsChoice ? $"{Name} must be one of its listed options." : $"{Name} must be a number.");
			if (IsContinuous) return Definition.ToWire(number);
			int ordinal = (int)Math.Round(number);
			int wire = ordinal < 0 ? ordinal + 65536 : ordinal;
			if (ordinal != number || Read(wire) is null)
				throw new InvalidDataException(IsChoice ? $"{Name} must be one of its listed options." : $"{Name} must be a whole number from {Minimum} to {Maximum}.");
			return wire;
		}
	}

	// Every setting FracAI can edit on each FM9 block, from FM9-Edit's parameter dictionary. Settings the designer already
	// curates (the Ride template's names, the Amp model and output EQ, the Cab's bank, type and cuts) keep their own names
	// and handling; this adds the rest. Names come from the community catalogue's display labels, made unique per block.
	public static class Fm9BlockSettings
	{
		// Amp and Cab settings handled by Fm9PresetBodyParameterEditor's model, output EQ and cab code.
		private static readonly IReadOnlyDictionary<string, int[]> SpecialIds = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
		{
			["Amp"] = [10, 61, 62, 63, 64, 65, 98, 116, 132],
			["Cab"] = [0, 4, 62, 66]
		};
		// Names the designer already uses for the special settings, so a generated label cannot collide with them.
		private static readonly IReadOnlyDictionary<string, string[]> SpecialNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
		{
			["Amp"] = ["Amp Model", "Output EQ Enabled", "Output EQ Location", "Output EQ Type", "Output EQ 80 Hz", "Output EQ 240 Hz",
				"Output EQ 750 Hz", "Output EQ 2200 Hz", "Output EQ 6600 Hz"],
			["Cab"] = ["Bank", "Cab Type", "Low Cut", "High Cut", "Cab identity"]
		};

		private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<Fm9BlockSetting>>> All = new(Build);
		private static readonly Lazy<IReadOnlyList<Fm9KnownBlock>> Known = new(LoadKnownBlocks);
		private static readonly Lazy<IReadOnlyDictionary<int, Fm9KnownBlock>> ByEffectId = new(() =>
			Known.Value.ToDictionary(block => block.EffectId));

		private sealed record Fm9KnownBlock(int EffectId, string BlockType, int Instance, string Family, int? Columns, int? Rows);

		// The block type and instance an effect id places on the grid: the hardware-probed instances plus the Megatap Delay
		// the effect-id sweep found (the MIDI block stays out: it has no safe bounds).
		public static (string BlockType, int Instance)? Identify(int effectId) =>
			ByEffectId.Value.TryGetValue(effectId, out Fm9KnownBlock? block) ? (block.BlockType, block.Instance) : null;

		public static IReadOnlyList<string> KnownBlockTypes() => Known.Value.Select(block => block.BlockType).Distinct().ToArray();

		public static int? EffectIdOf(string blockType, int instance) =>
			Known.Value.FirstOrDefault(block => block.Instance == instance &&
				string.Equals(block.BlockType, blockType, StringComparison.OrdinalIgnoreCase))?.EffectId;

		private static IReadOnlyList<Fm9KnownBlock> LoadKnownBlocks()
		{
			Dictionary<string, Fm9HardwareRecordLayout> layouts = Fm9HardwareRecordCatalog.LoadBundledAsync().GetAwaiter().GetResult().Layouts
				.GroupBy(layout => layout.BlockType, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
			List<Fm9KnownBlock> blocks = Fm9HardwareBlockInstanceCatalog.Bundled.Instances.Select(instance => new Fm9KnownBlock(
				instance.EffectId, instance.BlockType, instance.Instance, instance.ParameterFamily,
				layouts.GetValueOrDefault(instance.BlockType)?.ParameterColumns, layouts.GetValueOrDefault(instance.BlockType)?.ParameterRows)).ToList();
			foreach (Fm9HardwareEffectIdDiscovery found in Fm9HardwareEffectIdDiscoveryCatalog.LoadBundledAsync().GetAwaiter().GetResult().Discoveries)
			{
				if (found.ParameterFamily == "MIDIBLOCK" || blocks.Any(block => block.EffectId == found.EffectId)) continue;
				blocks.Add(new(found.EffectId, found.BlockType, found.Instance, found.ParameterFamily, found.ParameterColumns, found.ParameterRows));
			}
			return blocks;
		}

		// The record shape firmware 12 writes for this block type (captured on hardware); null when it was never captured.
		public static (int Columns, int Rows)? RecordShape(string blockType) =>
			Known.Value.FirstOrDefault(block => block.Columns is not null &&
				string.Equals(block.BlockType, blockType, StringComparison.OrdinalIgnoreCase)) is { } known
				? (known.Columns!.Value, known.Rows!.Value) : null;

		public static IReadOnlyList<Fm9BlockSetting> For(string blockType) =>
			All.Value.TryGetValue(blockType, out IReadOnlyList<Fm9BlockSetting>? settings) ? settings : [];

		public static Fm9BlockSetting? Find(string blockType, string name) =>
			For(blockType).FirstOrDefault(setting => string.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase));

		public static bool HasFamily(string blockType) => All.Value.ContainsKey(blockType);

		private static IReadOnlyDictionary<string, IReadOnlyList<Fm9BlockSetting>> Build()
		{
			Fm9Firmware12Catalog catalog = Fm9Firmware12Catalog.LoadBundledAsync().GetAwaiter().GetResult();
			Dictionary<string, IReadOnlyList<Fm9BlockSetting>> result = new(StringComparer.OrdinalIgnoreCase);
			foreach (IGrouping<string, Fm9KnownBlock> type in Known.Value
				.GroupBy(instance => instance.BlockType, StringComparer.OrdinalIgnoreCase))
			{
				string family = type.First().Family;
				Fm9EffectSection? section = Fm9EffectDefinitions.Bundled.ForFamily(family);
				if (section is null) continue;
				Fm9TemplateBlockDefinition? curated = Fm9BlockRecordTemplateProfile.FindFamilyDefinition(type.Key);
				HashSet<int> skipped = [.. curated?.Parameters.Select(parameter => parameter.ParameterId) ?? [],
					.. SpecialIds.GetValueOrDefault(type.Key) ?? []];
				HashSet<string> used = new(
					(curated?.Parameters.Select(parameter => parameter.Name) ?? []).Concat(SpecialNames.GetValueOrDefault(type.Key) ?? []),
					StringComparer.OrdinalIgnoreCase);
				Dictionary<int, Fm9ParameterDescriptor> names = catalog.GetParametersForFamily(family)
					.GroupBy(parameter => parameter.ParameterId).ToDictionary(group => group.Key, group => group.First());
				List<Fm9BlockSetting> settings = [];
				foreach (Fm9EffectParameter parameter in section.Parameters.Where(parameter => parameter.Id < 60000 &&
					!skipped.Contains(parameter.Id) &&
					parameter.Kind is Fm9ParameterKind.Linear or Fm9ParameterKind.Logarithmic or Fm9ParameterKind.Choice or Fm9ParameterKind.Integer &&
					(parameter.Kind is not (Fm9ParameterKind.Linear or Fm9ParameterKind.Logarithmic) || parameter.DisplayMaximum > parameter.DisplayMinimum) &&
					(parameter.Kind != Fm9ParameterKind.Logarithmic || parameter.DisplayMinimum > 0)))
				{
					// A setting nobody can identify is not worth editing blind; it stays as uploaded.
					if (Label(names.GetValueOrDefault(parameter.Id), family) is not string label) continue;
					// Bare band frequencies: the Graphic EQ's depend on its type, so they are numbered; other blocks' are fixed.
					string name = !System.Text.RegularExpressions.Regex.IsMatch(label, "^[0-9.]+[kK]?$") ? label
						: type.Key.Equals("Graphic EQ", StringComparison.OrdinalIgnoreCase) ? $"Band {parameter.Id + 1}" : $"EQ {label} Hz";
					string unique = name;
					for (int suffix = 2; !used.Add(unique); suffix++)
						unique = $"{name} {suffix}";
					settings.Add(new(unique, parameter.Id, parameter));
				}
				result[type.Key] = settings;
			}
			return result;
		}

		// "LowCutFrequency" -> "Low Cut Frequency"; a missing or "None" label falls back to the internal name without its family.
		private static string? Label(Fm9ParameterDescriptor? descriptor, string family)
		{
			string? label = descriptor?.DisplayLabel;
			if (string.IsNullOrWhiteSpace(label) || label.Equals("None", StringComparison.OrdinalIgnoreCase))
			{
				string? internalName = descriptor?.Name;
				if (string.IsNullOrWhiteSpace(internalName)) return null;
				string bare = internalName.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase) ? internalName[(family.Length + 1)..] : internalName;
				return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(bare.Replace('_', ' ').ToLowerInvariant());
			}
			StringBuilder builder = new();
			for (int index = 0; index < label.Length; index++)
			{
				char character = label[index];
				if (index > 0 && char.IsUpper(character) && char.IsLower(label[index - 1])) builder.Append(' ');
				builder.Append(character);
			}
			return builder.ToString().Trim();
		}
	}

	internal static class Fm9SettingText
	{
		// About the FM9's own display precision: the device stores 16-bit steps, so 90 Hz reads back as 90.001 and 7 as 6.997.
		// Display rounding is safe because a value nobody changed is never written back.
		public static string Number(double value)
		{
			double magnitude = Math.Abs(value);
			int decimals = magnitude < 1 ? 3 : magnitude < 100 ? 2 : magnitude < 1_000 ? 1 : 0;
			string text = Math.Round(value, decimals).ToString("0.###", CultureInfo.InvariantCulture);
			return text == "-0" ? "0" : text;
		}
	}
}
