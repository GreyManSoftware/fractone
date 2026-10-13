using Fractal.Application;
using Fractal.Companion.Catalog;

namespace Fractal.Companion.Protocol
{
	// A preset's modifiers. The FM9 keeps 32 modifier slots (the 25x1 records with effect ids 3-34 before the block
	// records); a slot whose source is NONE is unused. Each slot names its source, the target block by effect id and the
	// target parameter by id, with its range and curve as raw words. Writing a slot alone attaches the modifier: on preset
	// 510's edit buffer FM9-Edit then showed "Mix: ..., Modifier Enabled" for the targeted Delay Mix (2026-10-04).
	public static class Fm9PresetModifiers
	{
		public const int SlotCount = 32;
		private const int FirstSlotId = 3;
		private const int SlotColumns = 25;
		private const int Source = 0, Minimum = 1, Maximum = 2, Start = 3, Mid = 4, End = 5, EffectId = 8, Parameter = 9, Channel = 17;
		private const double FullScale = 65534;

		public static IReadOnlyList<string> Sources => Definitions.Find(Source)!.Labels;

		private static Fm9EffectSection Definitions => Fm9EffectDefinitions.Bundled.ForFamily("MOD")
			?? throw new InvalidOperationException("The FM9 parameter dictionary has no modifier definitions.");

		// The modifiers in use, by slot, whose target is a block setting FracAI can name.
		public static IReadOnlyList<(int Slot, ToneDesignModifier Modifier)> Read(Fm9PresetBody body)
		{
			ArgumentNullException.ThrowIfNull(body);
			List<(int, ToneDesignModifier)> modifiers = [];
			foreach ((int slot, Fm9PresetBlockRecord record) in Slots(body))
			{
				int Word(int id) => BitConverter.ToUInt16(body.Bytes.Span[(record.Offset + (17 + id) * 2)..]);
				int source = Word(Source);
				if (source == 0 || source >= Sources.Count) continue;
				if (Fm9BlockSettings.Identify(Word(EffectId)) is not { } target) continue;
				string? parameter = ParameterName(target.BlockType, Word(Parameter));
				if (parameter is null) continue;
				modifiers.Add((slot, new ToneDesignModifier(Sources[source], target.BlockType, target.Instance, parameter)
				{
					Minimum = Percent(Word(Minimum)),
					Maximum = Percent(Word(Maximum)),
					Start = Percent(Word(Start)),
					Mid = Percent(Word(Mid)),
					End = Percent(Word(End)),
					Channel = Word(Channel) is >= 1 and <= 4 and int channel ? ((char)('A' + channel - 1)).ToString() : "All"
				}));
			}
			return modifiers;
		}

		// Writes the modifiers: one already in a slot (same source and target) keeps it, new ones take free slots, and slots
		// whose modifier is gone are freed (source NONE). Untouched slots keep every word.
		public static byte[] Write(Fm9PresetBody body, IReadOnlyList<ToneDesignModifier> modifiers)
		{
			ArgumentNullException.ThrowIfNull(body);
			ArgumentNullException.ThrowIfNull(modifiers);
			if (modifiers.Count > SlotCount)
				throw new InvalidDataException($"An FM9 preset holds at most {SlotCount} modifiers.");
			byte[] bytes = body.Bytes.ToArray();
			Dictionary<int, Fm9PresetBlockRecord> slots = Slots(body).ToDictionary(slot => slot.Slot, slot => slot.Record);
			List<(int Slot, ToneDesignModifier Modifier)> existing = Read(body).ToList();
			HashSet<int> used = [];
			List<ToneDesignModifier> pending = [];
			foreach (ToneDesignModifier modifier in modifiers)
			{
				int match = existing.FindIndex(entry => !used.Contains(entry.Slot) && SameAssignment(entry.Modifier, modifier));
				if (match >= 0)
				{
					used.Add(existing[match].Slot);
					if (existing[match].Modifier != modifier) WriteSlot(bytes, slots[existing[match].Slot], modifier);
				}
				else pending.Add(modifier);
			}
			foreach ((int slot, _) in existing.Where(entry => !used.Contains(entry.Slot)))
				Set(bytes, slots[slot], Source, 0);
			HashSet<int> occupied = existing.Where(entry => used.Contains(entry.Slot)).Select(entry => entry.Slot).ToHashSet();
			foreach (ToneDesignModifier modifier in pending)
			{
				int free = slots.Keys.Order().FirstOrDefault(slot => !occupied.Contains(slot) &&
					BitConverter.ToUInt16(bytes.AsSpan(slots[slot].Offset + (17 + Source) * 2)) == 0);
				if (free == 0) throw new InvalidDataException($"An FM9 preset holds at most {SlotCount} modifiers.");
				occupied.Add(free);
				WriteSlot(bytes, slots[free], modifier);
			}
			return bytes;
		}

		private static void WriteSlot(byte[] bytes, Fm9PresetBlockRecord record, ToneDesignModifier modifier)
		{
			int source = Sources.ToList().FindIndex(label => string.Equals(label, modifier.Source, StringComparison.OrdinalIgnoreCase));
			if (source <= 0) throw new InvalidDataException($"'{modifier.Source}' is not an FM9 modifier source.");
			int effect = Fm9BlockSettings.EffectIdOf(modifier.BlockType, modifier.Instance)
				?? throw new InvalidDataException($"{modifier.BlockType} {modifier.Instance} is not an FM9 block.");
			int parameter = ParameterId(modifier.BlockType, modifier.Parameter)
				?? throw new InvalidDataException($"{modifier.BlockType} has no setting '{modifier.Parameter}' a modifier can drive.");
			int channel = modifier.Channel is "A" or "B" or "C" or "D" ? modifier.Channel[0] - 'A' + 1
				: modifier.Channel == "All" ? 0 : throw new InvalidDataException("A modifier applies to all channels or one of A-D.");
			Set(bytes, record, Source, source);
			Set(bytes, record, EffectId, effect);
			Set(bytes, record, Parameter, parameter);
			Set(bytes, record, Minimum, Word(modifier.Minimum));
			Set(bytes, record, Maximum, Word(modifier.Maximum));
			Set(bytes, record, Start, Word(modifier.Start));
			Set(bytes, record, Mid, Word(modifier.Mid));
			Set(bytes, record, End, Word(modifier.End));
			Set(bytes, record, Channel, channel);
		}

		private static bool SameAssignment(ToneDesignModifier first, ToneDesignModifier second) =>
			string.Equals(first.Source, second.Source, StringComparison.OrdinalIgnoreCase) && first.Instance == second.Instance &&
			string.Equals(first.BlockType, second.BlockType, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(first.Parameter, second.Parameter, StringComparison.OrdinalIgnoreCase);

		// A modifier can drive any dictionary setting, and the curated ones the Ride template names (Amp Gain, Delay Mix...).
		private static string? ParameterName(string blockType, int id) =>
			Fm9BlockSettings.For(blockType).FirstOrDefault(setting => setting.Id == id)?.Name ??
			Fm9BlockRecordTemplateProfile.FindFamilyDefinition(blockType)?.Parameters.FirstOrDefault(parameter => parameter.ParameterId == id)?.Name;

		// The settings of a block a modifier can drive: its continuous knobs, curated names first, as ParameterId resolves them.
		public static IReadOnlyList<string> Targets(string blockType) =>
			(Fm9BlockRecordTemplateProfile.FindFamilyDefinition(blockType)?.Parameters ?? [])
				.Where(parameter => !parameter.IsDiscrete && parameter.Maximum > parameter.Minimum).Select(parameter => parameter.Name)
				.Concat(Fm9BlockSettings.For(blockType).Where(setting => setting.IsContinuous).Select(setting => setting.Name))
				.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

		private static int? ParameterId(string blockType, string name) =>
			Fm9BlockSettings.Find(blockType, name)?.Id ??
			Fm9BlockRecordTemplateProfile.FindFamilyDefinition(blockType)?.Parameters
				.FirstOrDefault(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))?.ParameterId;

		private static IEnumerable<(int Slot, Fm9PresetBlockRecord Record)> Slots(Fm9PresetBody body) =>
			body.BlockRecords.Where(record => record.ParameterColumns == SlotColumns && record.ParameterRows == 1 && record.Offset >= 12)
				.Select(record => (Slot: (int)BitConverter.ToUInt16(body.Bytes.Span[(record.Offset - 12)..]), Record: record))
				.Where(slot => slot.Slot is >= FirstSlotId and < FirstSlotId + SlotCount);

		private static void Set(byte[] bytes, Fm9PresetBlockRecord record, int id, int value) =>
			BitConverter.TryWriteBytes(bytes.AsSpan(record.Offset + (17 + id) * 2), checked((ushort)value));

		private static double Percent(int word) => Math.Round(word / FullScale * 100, 1);

		private static int Word(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * FullScale, MidpointRounding.AwayFromZero);
	}
}
