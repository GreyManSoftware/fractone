using System.Globalization;
using System.Text;
using Fractal.Application;
using Fractal.Companion.Protocol;

namespace Fractal.Companion.Catalog
{
	// The FM9's effect blocks for tone design, described and checked from FM9-Edit's own parameter dictionary.
	public sealed class Fm9ToneDesignEffectCatalog : IToneDesignEffectCatalog
	{
		private const int MaximumListedChoices = 90;
		private static readonly Lazy<string> Description = new(Describe);

		public string DescribeForPrompt() => Description.Value;

		public IReadOnlyList<string> ModifierTargets(string blockType) => Protocol.Fm9PresetModifiers.Targets(blockType);

		public bool IsSetting(string blockType, string name) => Fm9BlockSettings.Find(blockType, name.Trim()) is not null;

		public double? AmpLevel(int model) => Fm9AmpLevels.For(model);

		public string DescribeSettings(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			StringBuilder text = new();
			foreach (ToneDesignBlockProposal block in proposal.Blocks.OrderBy(block => block.Row).ThenBy(block => block.Column))
			{
				string? typeSetting = Fm9ModelDefaults.TypeSetting(block.BlockType);
				string? modelA = typeSetting is null ? null : block.Parameters.GetValueOrDefault(typeSetting);
				foreach (string channel in block.GetAuthoredChannels())
				{
					ToneDesignBlockProposal view = block.ForChannel(channel);
					// A channel on channel A's model has the same defaults, so only its own values are listed.
					bool full = channel == ToneDesignChannels.A || (typeSetting is not null && view.Parameters.GetValueOrDefault(typeSetting) != modelA);
					DescribeChannel(text, view, block.Channels.Count == 0 ? null : channel, full);
				}
			}
			return text.ToString();
		}

		private static void DescribeChannel(StringBuilder text, ToneDesignBlockProposal block, string? channel, bool full)
		{
			IReadOnlyList<Fm9BlockSetting> settings = Fm9BlockSettings.For(block.BlockType);
			if (Fm9PresetBodyBlockInserter.DefaultChannelWords(block.BlockType, block.Instance) is not { } defaults)
				return;
			int[] words = [.. defaults];
			string? typeSetting = Fm9ModelDefaults.TypeSetting(block.BlockType);
			string? chosen = typeSetting is null ? null : block.Parameters.GetValueOrDefault(typeSetting);
			if (chosen is not null && Fm9ModelDefaults.TypeOrdinal(block.BlockType, chosen) is int model &&
				Fm9ModelDefaults.For(block.BlockType, model) is { } voicing)
				foreach ((int id, int word) in voicing)
					if (id < words.Length) words[id] = word;
			text.Append(Environment.NewLine).Append("## ").Append(block.BlockType).Append(' ').Append(block.Instance)
				.Append(channel is null ? string.Empty : $" channel {channel}")
				.Append(chosen is null ? string.Empty : $" ({typeSetting} {chosen})").Append(Environment.NewLine);
			if (!full)
			{
				text.Append("Same model as channel A: every setting has channel A's default; this channel's own values:").Append(Environment.NewLine);
				foreach ((string name, string value) in block.Parameters.Where(pair => pair.Key != typeSetting))
					text.Append("- ").Append(name).Append(": current ").Append(value).Append(Environment.NewLine);
				return;
			}
			string? modelName = block.ModelSelection?.Name ?? Fm9QualifiedModelCatalog.Choices.FirstOrDefault(choice =>
				choice.BlockType == block.BlockType && choice.Bank is null &&
				choice.Ordinal.ToString(CultureInfo.InvariantCulture) == chosen)?.Name;
			foreach (string note in Fm9ModelNotes.For(block.BlockType, modelName))
				text.Append("Note: ").Append(note).Append(Environment.NewLine);
			// The listed parameters the first step set, with what they do.
			foreach ((string name, string value) in block.Parameters.Where(pair => Fm9BlockSettings.Find(block.BlockType, pair.Key) is null))
			{
				string? meaning = Fm9SettingGuide.Describe(block.BlockType, name);
				text.Append("- ").Append(name).Append(": current ").Append(value)
					.Append(meaning is null ? string.Empty : $" — {meaning}").Append(Environment.NewLine);
			}
			foreach (Fm9BlockSetting setting in settings)
			{
				if (setting.Name == typeSetting) continue;
				string? device = setting.Id < words.Length ? setting.Read(words[setting.Id]) : null;
				string shown = setting.IsChoice && device is not null && int.TryParse(device, out int option) && option < setting.Choices.Count
					? setting.Choices[option] : device ?? "?";
				string? current = block.Parameters.GetValueOrDefault(setting.Name);
				string? meaning = Fm9SettingGuide.Describe(block.BlockType, setting.Name);
				text.Append("- ").Append(Format(setting)).Append("; default ").Append(shown)
					.Append(current is null ? string.Empty : $"; current {current}")
					.Append(meaning is null ? string.Empty : $" — {meaning}").Append(Environment.NewLine);
			}
		}

		// Any block's dictionary settings checked against the FM9 (unknown names stay for the quality check to report;
		// curated settings are the contract's own and pass through untouched).
		public ToneDesignBlockProposal RepairSettings(ToneDesignBlockProposal block, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(block);
			ArgumentNullException.ThrowIfNull(adjustments);
			if (!block.Parameters.Keys.Any(name => Fm9BlockSettings.Find(block.BlockType, name.Trim()) is not null))
				return block;
			Dictionary<string, string> kept = new(StringComparer.Ordinal);
			foreach ((string name, string value) in block.Parameters)
			{
				if (Fm9BlockSettings.Find(block.BlockType, name.Trim()) is not { } setting)
				{
					kept[name] = value;
					continue;
				}
				if (Checked(block.BlockType, setting, value, adjustments) is string valid)
					kept[setting.Name] = valid;
			}
			return block with { Parameters = kept };
		}

		private static string? Checked(string blockType, Fm9BlockSetting setting, string value, List<string> adjustments)
		{
			if (setting.IsChoice)
			{
				int? ordinal = Choice(setting, value);
				if (ordinal is null) adjustments.Add($"{blockType} {setting.Name}: dropped '{value}' (not one of its options)");
				return ordinal?.ToString(CultureInfo.InvariantCulture);
			}
			if (!double.TryParse(Numeric(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || !double.IsFinite(amount))
			{
				adjustments.Add($"{blockType} {setting.Name}: dropped '{value}' (not a number)");
				return null;
			}
			double bounded = Math.Clamp(amount, setting.Minimum ?? amount, setting.Maximum ?? amount);
			if (!setting.IsContinuous) bounded = Math.Round(bounded);
			if (bounded != amount) adjustments.Add($"{blockType} {setting.Name} {value} → {Fm9SettingText.Number(bounded)}");
			return Fm9SettingText.Number(bounded);
		}

		public ToneDesignBlockProposal Repair(ToneDesignBlockProposal effect, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(effect);
			ArgumentNullException.ThrowIfNull(adjustments);
			Dictionary<string, string> kept = new(StringComparer.Ordinal);
			foreach ((string name, string value) in effect.Parameters)
			{
				// A second Drive takes the Drive 1 settings (model, drive, tone, level, mix) like the Ride chain's.
				if (CuratedDriveSetting(effect, name) is { } curated)
				{
					if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
					{
						adjustments.Add($"{effect.BlockType} {effect.Instance}: dropped {curated.Name} '{value}'");
						continue;
					}
					double clamped = Math.Clamp(number, curated.Minimum, curated.Maximum);
					if (clamped != number) adjustments.Add($"{effect.BlockType} {effect.Instance} {curated.Name} {value} → {Fm9SettingText.Number(clamped)}");
					kept[curated.Name] = curated.IsDiscrete ? ((int)Math.Round(clamped)).ToString(CultureInfo.InvariantCulture) : Fm9SettingText.Number(clamped);
					continue;
				}
				Fm9BlockSetting? setting = Fm9BlockSettings.Find(effect.BlockType, name.Trim());
				if (setting is null)
				{
					adjustments.Add($"{effect.BlockType}: dropped unknown setting '{name}'");
					continue;
				}
				if (setting.IsChoice)
				{
					int? ordinal = Choice(setting, value);
					if (ordinal is null) adjustments.Add($"{effect.BlockType} {setting.Name}: dropped '{value}' (not one of its options)");
					else kept[setting.Name] = ordinal.Value.ToString(CultureInfo.InvariantCulture);
					continue;
				}
				if (!double.TryParse(Numeric(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || !double.IsFinite(amount))
				{
					adjustments.Add($"{effect.BlockType} {setting.Name}: dropped '{value}' (not a number)");
					continue;
				}
				double bounded = Math.Clamp(amount, setting.Minimum ?? amount, setting.Maximum ?? amount);
				if (!setting.IsContinuous) bounded = Math.Round(bounded);
				if (bounded != amount) adjustments.Add($"{effect.BlockType} {setting.Name} {value} → {Fm9SettingText.Number(bounded)}");
				kept[setting.Name] = Fm9SettingText.Number(bounded);
			}
			return effect with { Parameters = kept };
		}

		private static Fm9TemplateParameterDefinition? CuratedDriveSetting(ToneDesignBlockProposal effect, string name) =>
			string.Equals(effect.BlockType, "Drive", StringComparison.OrdinalIgnoreCase)
				? Fm9BlockRecordTemplateProfile.FindFamilyDefinition("Drive")?.Parameters
					.FirstOrDefault(parameter => string.Equals(parameter.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
				: null;

		// An option by position or by name, tolerating case, spacing and "dotted"/"triplet" for the FM9's DOT/TRIP.
		private static int? Choice(Fm9BlockSetting setting, string value)
		{
			string wanted = Key(value);
			int index = setting.Choices.ToList().FindIndex(label => Key(label) == wanted);
			if (index >= 0) return index;
			return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal) &&
				ordinal >= 0 && ordinal < setting.Choices.Count ? ordinal : null;
		}

		private static string Key(string text) => new string(text.ToUpperInvariant()
			.Replace("DOTTED", "DOT", StringComparison.Ordinal).Replace("TRIPLET", "TRIP", StringComparison.Ordinal)
			.Where(char.IsLetterOrDigit).ToArray());

		// "375 ms" or "25%" carry their unit; the number is what counts.
		private static string Numeric(string value) =>
			new string(value.Trim().TakeWhile(character => char.IsDigit(character) || character is '.' or '-' or '+' or 'e' or 'E').ToArray());

		// The rig step's view of the effects: each block and its types, so the model can choose which to add. Every other
		// setting comes in the second step (DescribeSettings), with the chosen type's FM9 values and meanings in view.
		private static string Describe()
		{
			StringBuilder text = new("Effect blocks you may add, with their types (put the type in the effect's settings under the setting name shown; " +
				"every other setting of every block is set in a second step, with each chosen model's FM9 defaults and what each control does in view):");
			foreach (string blockType in ToneDesignEffects.BlockTypes)
			{
				if (blockType == "Drive")
				{
					text.Append(Environment.NewLine).Append("- Drive (a second drive, e.g. a boost in front of Drive 1): Drive Model from the same named choices as Drive 1.");
					continue;
				}
				if (!Fm9PresetBodyBlockInserter.CanInsert(blockType, 1)) continue;
				string? typeName = Fm9ModelDefaults.TypeSetting(blockType)
					?? Fm9BlockSettings.For(blockType).FirstOrDefault(setting => setting.IsChoice && setting.Name is "Type" or "Model" or "Algorithm" or "Basetype" or "Mode")?.Name;
				Fm9BlockSetting? type = typeName is null ? null : Fm9BlockSettings.Find(blockType, typeName);
				text.Append(Environment.NewLine).Append("- ").Append(blockType)
					.Append(type is null ? " (no types)." : $": {Format(type)}.");
			}
			return text.Append(Environment.NewLine).Append(Fm9ModelNotes.DescribeForPrompt()).ToString();
		}

		// A setting's name and range or options, then what it does to the sound when the guide documents it.
		private static string Format(string blockType, Fm9BlockSetting setting) =>
			Fm9SettingGuide.Describe(blockType, setting.Name) is string meaning ? $"{Format(setting)} — {meaning}" : Format(setting);

		private static string Format(Fm9BlockSetting setting)
		{
			if (setting.IsChoice)
			{
				IReadOnlyList<string> choices = setting.Choices;
				return choices.Count <= MaximumListedChoices
					? $"{setting.Name} ({string.Join(" | ", choices)})"
					: $"{setting.Name} (one of {choices.Count} names, e.g. {string.Join(", ", choices.Take(10))})";
			}
			string unit = ((setting.Definition.TypeCode >> 8) & 0xF) switch
			{
				1 => " dB", 2 => " Hz", 3 => " s", 4 => " ms", 5 => " %", _ => string.Empty
			};
			return $"{setting.Name} {setting.Minimum?.ToString("G6", CultureInfo.InvariantCulture)}..{setting.Maximum?.ToString("G6", CultureInfo.InvariantCulture)}{unit}";
		}
	}
}
