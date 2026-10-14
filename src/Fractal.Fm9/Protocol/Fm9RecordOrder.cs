namespace Fractal.Companion.Protocol
{
	// The order Gen-3 devices keep block records in: a fixed family rank (alphabetical by the firmware's internal block
	// names, e.g. AMP, CABINET, CHORUS, COMPRESSOR...), then instance. Measured by FracCompat over every current-format
	// preset the devices wrote (FM9, Axe-Fx III, FM3): no contradictions. Families never seen side by side (IR Player,
	// Megatap, Mixer, Multiplexer; Plex, Resonator, Reverb; Synth, Ten-Tap; Dynamic Distortion, Wah; RTA, Tone Match) keep
	// this order, ties broken by first effect id. Vocoder has never been seen.
	public static class Fm9RecordOrder
	{
		private static readonly string[] Families =
		[
			"Amp", "Cab", "Chorus", "Compressor", "Crossover", "Delay", "Enhancer", "Return", "Send", "Filter", "Flanger",
			"Formant", "Drive", "Gate", "GEQ", "Multitap", "Input", "IR Player", "Megatap", "Mixer", "Multiplexer",
			"Multiband Comp", "Output", "PEQ", "Pitch", "Phaser", "Plex", "Resonator", "Reverb", "Ring Mod", "Rotary", "Synth",
			"Ten-Tap", "Pan/Tremolo", "Volume/Pan", "Dynamic Distortion", "Wah", "RTA", "Tone Match", "Looper", "Vocoder"
		];

		// Each family's first effect id and instance count.
		private static readonly (string Family, int First, int Instances)[] Ids =
		[
			("Input", 37, 5), ("Output", 42, 4), ("Compressor", 46, 4), ("GEQ", 50, 4), ("PEQ", 54, 4), ("Amp", 58, 4),
			("Cab", 62, 4), ("Reverb", 66, 4), ("Delay", 70, 4), ("Multitap", 74, 4), ("Chorus", 78, 4), ("Flanger", 82, 4),
			("Rotary", 86, 4), ("Phaser", 90, 4), ("Wah", 94, 4), ("Formant", 98, 4), ("Volume/Pan", 102, 4),
			("Pan/Tremolo", 106, 4), ("Pitch", 110, 4), ("Filter", 114, 4), ("Drive", 118, 4), ("Enhancer", 122, 4),
			("Mixer", 126, 4), ("Synth", 130, 4), ("Vocoder", 134, 4), ("Megatap", 138, 4), ("Crossover", 142, 4),
			("Gate", 146, 4), ("Ring Mod", 150, 4), ("Multiband Comp", 154, 4), ("Ten-Tap", 158, 4), ("Resonator", 162, 4),
			("Looper", 166, 4), ("Tone Match", 170, 4), ("RTA", 174, 4), ("Plex", 178, 4), ("Send", 182, 4), ("Return", 186, 4),
			("Multiplexer", 191, 4), ("IR Player", 195, 4), ("Dynamic Distortion", 202, 1)
		];

		// Families whose records the Axe-Fx III writes with prefix word 1 = 0xFFFF, as it does every non-block record (Output
		// and Inputs 2-5 too). The FM3 and FM9 always write 0. Measured by FracCompat on formats 0x0145/0x0146
		// (synthesis\REPORT.md, 2026-10-13).
		private static readonly HashSet<string> AxeFx3MarkedFamilies =
		[
			"Synth", "Megatap", "Crossover", "Mixer", "Ring Mod", "Multiband Comp", "Ten-Tap", "Resonator", "Looper", "RTA", "Send",
			"Return", "Multiplexer", "IR Player", "Dynamic Distortion", "Output"
		];

		private const int ModifierFirst = 3;
		private const int ModifierLast = 34;
		private const int PrefixBytes = 12;
		private const int HeaderWords = 17;
		private const int GridOffset = 0x104;
		private const int GridCellSize = 4;

		public static bool IsBlock(int effectId) => Ids.Any(entry => effectId >= entry.First && effectId < entry.First + entry.Instances);

		// The non-block records before the modifiers and after the blocks: the FM9 writes record 200 (PRESET) first, the FM3
		// and Axe-Fx III after record 201 (PERFORM). A lone 0 word ends the chain on all three.
		public static IReadOnlyList<int> Leading(Gen3Device device) => device.Record200First ? [200, 2] : [2];

		public static IReadOnlyList<int> Trailing(Gen3Device device) => device.Record200First ? [201] : [201, 200];

		// Word 1 of a record's 12-byte prefix (effect id, this word, three zeros, scene 1's channel).
		public static ushort PrefixWord1(Gen3Device device, int effectId)
		{
			ArgumentNullException.ThrowIfNull(device);
			if (device != Gen3Device.AxeFx3) return 0;
			if (!IsBlock(effectId)) return 0xFFFF;
			(string family, int first, _) = Ids.First(entry => effectId >= entry.First && effectId < entry.First + entry.Instances);
			return family == "Input" ? (ushort)(effectId == first ? 0 : 0xFFFF) : AxeFx3MarkedFamilies.Contains(family) ? (ushort)0xFFFF : (ushort)0;
		}

		// Every unit of the record chain, which starts right after the device's grid. Unlike the body decoder's records,
		// these include records 200 and 2 and need no width limit.
		public static IReadOnlyList<Fm9RecordUnit> Units(Fm9PresetBody body)
		{
			ArgumentNullException.ThrowIfNull(body);
			ReadOnlySpan<byte> bytes = body.Bytes.Span;
			List<Fm9RecordUnit> units = [];
			int offset = GridOffset + (body.Device.GridRows * body.Device.GridColumns * GridCellSize);
			while (offset + PrefixBytes + (HeaderWords * 2) <= bytes.Length)
			{
				int columns = Word(bytes, offset + PrefixBytes + 30), rows = Word(bytes, offset + PrefixBytes + 32);
				int length = PrefixBytes + ((HeaderWords + (columns * rows)) * 2);
				if (columns == 0 || rows == 0 || offset + length > bytes.Length)
					throw new InvalidDataException($"The record chain breaks at body offset {offset}.");
				units.Add(new(Word(bytes, offset), (ushort)Word(bytes, offset + 2), offset, columns, rows, length));
				offset += length;
			}
			if (offset + 2 != bytes.Length || Word(bytes, offset) != 0)
				throw new InvalidDataException($"The record chain does not end with a lone 0 word (body offset {offset}).");
			return units;
		}

		// How a body's record chain departs from its device's order: leading records, modifiers 3-34 in slot order, blocks
		// by family and instance, trailing records, and each prefix's word 1. Empty when it follows it.
		public static IReadOnlyList<string> Check(Fm9PresetBody body)
		{
			IReadOnlyList<Fm9RecordUnit> units = Units(body);
			Gen3Device device = body.Device;
			List<string> problems = [];
			int[] ids = units.Select(unit => unit.EffectId).ToArray();
			IReadOnlyList<int> leading = Leading(device), trailing = Trailing(device);
			if (!ids.Take(leading.Count).SequenceEqual(leading))
				problems.Add($"The {device.Name} chain starts with {string.Join(", ", leading)}, not {string.Join(", ", ids.Take(leading.Count))}.");
			if (!ids.TakeLast(trailing.Count).SequenceEqual(trailing))
				problems.Add($"The {device.Name} chain ends with {string.Join(", ", trailing)}, not {string.Join(", ", ids.TakeLast(trailing.Count))}.");
			int[] middle = ids.Skip(leading.Count).SkipLast(trailing.Count).ToArray();
			int[] modifiers = middle.TakeWhile(id => id is >= ModifierFirst and <= ModifierLast).ToArray();
			if (!modifiers.SequenceEqual(Enumerable.Range(ModifierFirst, modifiers.Length)))
				problems.Add("The modifier slots are out of order.");
			int[] blocks = middle.Skip(modifiers.Length).ToArray();
			if (blocks.FirstOrDefault(id => !IsBlock(id)) is int stray and not 0)
				problems.Add($"Record {stray} sits among the blocks.");
			for (int index = 1; index < blocks.Length; index++)
				if (Key(blocks[index - 1]).CompareTo(Key(blocks[index])) > 0)
					problems.Add($"Block {blocks[index]} comes after block {blocks[index - 1]}.");
			foreach (Fm9RecordUnit unit in units.Where(unit => unit.PrefixWord1 != PrefixWord1(device, unit.EffectId)))
				problems.Add($"Record {unit.EffectId} has prefix word 1 = 0x{unit.PrefixWord1:X4}, not 0x{PrefixWord1(device, unit.EffectId):X4}.");
			return problems;
		}

		private static int Word(ReadOnlySpan<byte> bytes, int offset) => bytes[offset] | (bytes[offset + 1] << 8);

		// (family rank, instance); unknown ids sort after every known family, by id.
		public static (int Rank, int Instance) Key(int effectId)
		{
			foreach ((string family, int first, int instances) in Ids)
				if (effectId >= first && effectId < first + instances)
					return (Array.IndexOf(Families, family), effectId - first + 1);
			return (Families.Length, effectId);
		}
	}

	// One unit of a preset's record chain: Offset is where its 12-byte prefix starts, Length runs to the next unit's prefix.
	public sealed record Fm9RecordUnit(int EffectId, ushort PrefixWord1, int Offset, int Columns, int Rows, int Length);
}
