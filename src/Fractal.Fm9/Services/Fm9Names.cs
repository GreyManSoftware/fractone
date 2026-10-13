using System.Globalization;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9
{
	// A block as tools name it: its type and instance ("Amp", "Amp 2", "Amp:2"). Instance 1 when omitted.
	public sealed record Fm9BlockRef(string BlockType, int Instance)
	{
		public static Fm9BlockRef Parse(string text)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(text);
			string trimmed = text.Trim();
			int split = trimmed.LastIndexOfAny([' ', ':']);
			if (split > 0 && int.TryParse(trimmed[(split + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int instance))
				return new(Fm9Names.BlockType(trimmed[..split].Trim()), instance);
			return new(Fm9Names.BlockType(trimmed), 1);
		}

		public override string ToString() => $"{BlockType} {Instance}";
	}

	public static class Fm9Names
	{
		public const int ChannelCount = 4;

		// The canonical spelling of a block type ("amp" -> "Amp", "peq" stays unknown and is refused).
		public static string BlockType(string text)
		{
			string? known = Fm9BlockSettings.KnownBlockTypes().FirstOrDefault(type => string.Equals(type, text, StringComparison.OrdinalIgnoreCase));
			return known ?? throw new ArgumentException(
				$"Unknown block type '{text}'. Known types: {string.Join(", ", Fm9BlockSettings.KnownBlockTypes().Order(StringComparer.Ordinal))}.");
		}

		// Channel letter A-D to row index.
		public static int Channel(string text)
		{
			string letter = text?.Trim().ToUpperInvariant() ?? "";
			return letter.Length == 1 && letter[0] is >= 'A' and <= 'D' ? letter[0] - 'A'
				: throw new ArgumentException($"A channel is A, B, C or D, not '{text}'.");
		}

		public static string Letter(int channel) => ((char)('A' + channel)).ToString();

		// What a channel plays, by name: the amp model, the cab, or the type of any block that has one. Null when the block
		// has no type or the name is unknown.
		public static string? Model(string blockType, IReadOnlyDictionary<string, string> settings)
		{
			if (blockType.Equals("Cab", StringComparison.OrdinalIgnoreCase))
			{
				// In DynaCab mode the bank and IR slot are leftovers: the cab is the DynaCab type and mic.
				if (settings.GetValueOrDefault("Mode") == "1")
					return Choice("Cab", "Dynacab Type1", settings) is string dyna
						? $"DynaCab {dyna}{(Choice("Cab", "Dynacab Mic1", settings) is string mic ? $", {mic}" : "")}" : null;
				if (!settings.TryGetValue("Bank", out string? bankText) || !settings.TryGetValue("Cab Type", out string? typeText) ||
					!int.TryParse(bankText, CultureInfo.InvariantCulture, out int bank) || !int.TryParse(typeText, CultureInfo.InvariantCulture, out int type))
					return null;
				return CabName(bank, type);
			}
			string? setting = Fm9ModelDefaults.TypeSetting(blockType);
			if (setting is null || !settings.TryGetValue(setting, out string? value)) return null;
			if (!int.TryParse(value, CultureInfo.InvariantCulture, out int ordinal)) return value;
			IReadOnlyList<string> labels = TypeLabels(blockType, setting);
			return ordinal >= 0 && ordinal < labels.Count ? labels[ordinal] : null;
		}

		// A stored value as a person reads it: models, types and choices by name, numbers as they are.
		public static string? Display(string blockType, string name, string? value)
		{
			if (value is null || !int.TryParse(value, CultureInfo.InvariantCulture, out int ordinal)) return value;
			if (Fm9ModelDefaults.TypeSetting(blockType) == name)
				return TypeLabels(blockType, name).ElementAtOrDefault(ordinal) is { Length: > 0 } model ? model : value;
			return Fm9BlockSettings.Find(blockType, name) is { IsChoice: true } choice && ordinal >= 0 && ordinal < choice.Choices.Count
				? choice.Choices[ordinal] : value;
		}

		private static string? Choice(string blockType, string name, IReadOnlyDictionary<string, string> settings) =>
			settings.TryGetValue(name, out string? value) && int.TryParse(value, CultureInfo.InvariantCulture, out int ordinal) &&
			Fm9BlockSettings.Find(blockType, name) is { IsChoice: true } setting && ordinal >= 0 && ordinal < setting.Choices.Count
				? setting.Choices[ordinal] : null;

		// A cab by bank and position: the firmware's own name for factory cabs, otherwise the bank and slot.
		public static string CabName(int bank, int type) =>
			Catalog.Value.TryResolveCabName(bank, type, out string? name) ? name : $"Bank {bank} cab {type}";

		// Every name a block's type selector can take, in ordinal order.
		public static IReadOnlyList<string> TypeLabels(string blockType, string setting)
		{
			if (Fm9BlockSettings.Find(blockType, setting) is { IsChoice: true } choice) return choice.Choices;
			// Curated selectors (Amp Model, Drive Model) are named from FM9-Edit's dictionary entry for their column.
			int? id = blockType.Equals("Amp", StringComparison.OrdinalIgnoreCase) && setting == "Amp Model" ? 10
				: Fm9BlockRecordTemplateProfile.FindFamilyDefinition(blockType)?.Parameters
					.FirstOrDefault(parameter => parameter.Name == setting)?.ParameterId;
			string? family = Fm9HardwareBlockInstanceCatalog.Bundled.Instances
				.FirstOrDefault(instance => instance.BlockType.Equals(blockType, StringComparison.OrdinalIgnoreCase))?.ParameterFamily;
			return id is int column && family is not null && Fm9EffectDefinitions.Bundled.ForFamily(family)?.Find(column) is { } parameter
				? parameter.Labels : [];
		}

		private static readonly Lazy<Fm9Firmware12Catalog> Catalog = new(() => Fm9Firmware12Catalog.LoadBundledAsync().GetAwaiter().GetResult());
	}
}
