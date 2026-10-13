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

		public static bool IsBlock(int effectId) => Ids.Any(entry => effectId >= entry.First && effectId < entry.First + entry.Instances);

		// (family rank, instance); unknown ids sort after every known family, by id.
		public static (int Rank, int Instance) Key(int effectId)
		{
			foreach ((string family, int first, int instances) in Ids)
				if (effectId >= first && effectId < first + instances)
					return (Array.IndexOf(Families, family), effectId - first + 1);
			return (Families.Length, effectId);
		}
	}
}
