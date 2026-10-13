namespace Fractal.Application
{
	// Describes effect blocks for the prompt and checks what a model wrote for them against the device's own parameter
	// dictionary (implemented where that dictionary lives; see Fm9ToneDesignEffectCatalog).
	public interface IToneDesignEffectCatalog
	{
		// A compact list of the effect blocks and their key settings, for the system prompt.
		string DescribeForPrompt();

		// The effect with unknown settings dropped, option names mapped to the device's own and numbers clamped into range;
		// every change is described in adjustments.
		ToneDesignBlockProposal Repair(ToneDesignBlockProposal effect, List<string> adjustments);

		// The settings of a block a modifier can drive, as the device names them.
		IReadOnlyList<string> ModifierTargets(string blockType);

		// Whether a name is one of the device's settings for that block (beyond the contract's listed parameters).
		bool IsSetting(string blockType, string name);

		// Any block with its device settings checked: unknown values dropped, numbers clamped, options by their names.
		ToneDesignBlockProposal RepairSettings(ToneDesignBlockProposal block, List<string> adjustments);

		// For the detailed-settings step: every setting of each block in the proposal, with the value the chosen model or
		// type gives it on the device, the proposal's current value where it sets one, and what the setting does. Empty
		// when the host cannot describe them (the step is then skipped).
		string DescribeSettings(ToneDesignProposal proposal);

		// The Amp Level (dB) that brings this amp model to the top of the device's output meter, or null when unknown.
		double? AmpLevel(int model);
	}

	// Effect blocks a generated preset may add to the Ride chain. The Ride template's eight blocks keep their fixed order on
	// row 3; each effect goes after a named block (its anchor) and the row is renumbered, so the model never handles grid
	// positions or cables. A second Drive (instance 2) is an effect, for boost-into-drive stacks.
	public static class ToneDesignEffects
	{
		public const int MaximumEffects = 6;
		// Fourteen grid columns: the eight Ride blocks plus six effects.
		public const int MaximumColumns = 14;
		private const int Row = 3;

		public static IReadOnlyList<string> RideBlockTypes { get; } =
			["Input", "Gate/Expander", "Drive", "Amp", "Cab", "Parametric EQ", "Reverb", "Output"];

		public static IReadOnlyList<string> BlockTypes { get; } =
		[
			"Compressor", "Graphic EQ", "Drive", "Delay", "Multitap Delay", "Ten-Tap Delay", "Plex Delay", "Chorus", "Flanger",
			"Phaser", "Rotary", "Wah", "Formant", "Volume/Pan", "Pan/Tremolo", "Pitch", "Filter", "Enhancer",
			"Multiband Compressor", "Synth", "Ring Modulator", "Resonator"
		];

		public const int MaximumModifiers = 4;

		// Modifier sources a generated preset may use: the FM9's own controllers, the onboard pedal jacks and the first
		// external controller (the usual expression pedal), plus the scene controllers; footswitch-specific sources are left to the player.
		public static IReadOnlyList<string> ModifierSources { get; } =
		[
			"LFO 1A", "LFO 1B", "LFO 2A", "LFO 2B", "ADSR 1", "ADSR 2", "Sequencer", "Envelope Follower", "Pedal 1", "Pedal 2",
			"External 1", "Scene Controller 1", "Scene Controller 2", "Scene Controller 3", "Scene Controller 4"
		];

		// An effect sits after one of these (after Reverb is the end of the chain, before Output).
		public static IReadOnlyList<string> Anchors { get; } =
			["Input", "Gate/Expander", "Drive", "Amp", "Cab", "Parametric EQ", "Reverb"];

		public static bool IsEffect(ToneDesignBlockProposal block)
		{
			ArgumentNullException.ThrowIfNull(block);
			return block.Instance != 1 || !RideBlockTypes.Contains(block.BlockType, StringComparer.OrdinalIgnoreCase);
		}

		// Ride blocks in their fixed order with each effect after its anchor (effects sharing an anchor keep their order),
		// all on row 3 and numbered from column 1.
		public static IReadOnlyList<ToneDesignBlockProposal> Layout(
			IReadOnlyList<ToneDesignBlockProposal> rideBlocks,
			IReadOnlyList<(ToneDesignBlockProposal Effect, string Anchor)> effects)
		{
			ArgumentNullException.ThrowIfNull(rideBlocks);
			ArgumentNullException.ThrowIfNull(effects);
			if (effects.Count > MaximumEffects)
				throw new InvalidDataException($"A preset can add at most {MaximumEffects} effects to the Ride chain.");
			List<ToneDesignBlockProposal> ordered = [];
			foreach (ToneDesignBlockProposal ride in rideBlocks.OrderBy(block => RideIndex(block.BlockType)))
			{
				ordered.Add(ride);
				ordered.AddRange(effects.Where(effect => string.Equals(effect.Anchor, ride.BlockType, StringComparison.OrdinalIgnoreCase))
					.Select(effect => effect.Effect));
			}
			string[] unanchored = effects.Where(effect => !rideBlocks.Any(ride =>
				string.Equals(ride.BlockType, effect.Anchor, StringComparison.OrdinalIgnoreCase))).Select(effect => effect.Anchor).ToArray();
			if (unanchored.Length > 0)
				throw new InvalidDataException($"Place each effect after one of: {string.Join(", ", Anchors)} (not {string.Join(", ", unanchored)}).");
			if (ordered.Count > MaximumColumns)
				throw new InvalidDataException($"The chain needs {ordered.Count} grid columns; the FM9 has {MaximumColumns}.");
			return ordered.Select((block, index) => block with { Row = Row, Column = index + 1 }).ToArray();
		}

		// One cable from each row-3 block to the next: the only routing a serial chain needs.
		public static IReadOnlyList<ToneDesignRoute> SerialRoutes(IEnumerable<ToneDesignBlockProposal> blocks)
		{
			ArgumentNullException.ThrowIfNull(blocks);
			int[] columns = blocks.Where(block => block.Row == Row).Select(block => block.Column).Order().ToArray();
			return columns.Zip(columns.Skip(1)).Where(pair => pair.Second == pair.First + 1)
				.Select(pair => new ToneDesignRoute(Row, pair.First, Row)).ToArray();
		}

		private static int RideIndex(string blockType) =>
			RideBlockTypes.ToList().FindIndex(type => string.Equals(type, blockType, StringComparison.OrdinalIgnoreCase));
	}
}
