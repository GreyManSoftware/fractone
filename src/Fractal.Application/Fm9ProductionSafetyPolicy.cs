using System.Globalization;

namespace Fractal.Application
{
	public sealed record Fm9ProductionParameterRange(string BlockType, string ParameterName, double Minimum, double Maximum);

	public static class Fm9ProductionSafetyPolicy
	{
		// Cumulative positive Amp Output EQ and Parametric EQ boost at or above this frequency is capped.
		public const double HighFrequencyBoostThresholdHz = 2_000;
		// Advisory only (the EQ graph shows it): more boost above 2 kHz than this tends to sound harsh on FRFR speakers.
		public const double MaximumHighFrequencyBoostDecibels = 8;
		public const double MaximumLevelDecibels = 12;

		// The FM9's own parameter ranges. The only tighter limits are loudness caps on dB output levels (+12 dB), which
		// protect ears and speakers; tone choices such as gain, EQ, cuts and reverb amount are the target sound's call.
		public static IReadOnlyList<Fm9ProductionParameterRange> ParameterRanges { get; } = Array.AsReadOnly<Fm9ProductionParameterRange>(
		[
			new("Gate/Expander", "Threshold", -100, 0),
			new("Gate/Expander", "Attack", 0.1, 1_000),
			new("Gate/Expander", "Hold", 1, 1_000),
			new("Gate/Expander", "Release", 1, 1_000),
			new("Gate/Expander", "Ratio", 1, 20),
			new("Gate/Expander", "Low Cut", 10, 1_000),
			new("Gate/Expander", "High Cut", 200, 20_000),
			new("Gate/Expander", "Level", -80, MaximumLevelDecibels),
			new("Drive", "Drive", 0, 10),
			new("Drive", "Tone", 0, 10),
			new("Drive", "Level", 0, 10),
			new("Drive", "Mix", 0, 100),
			// The Ride template leaves Amp Level at -12 dB, about 12 dB short of a hot output; generated presets set it.
			new("Amp", "Level", -80, MaximumLevelDecibels),
			new("Amp", "Gain", 0, 10),
			new("Amp", "Bass", 0, 10),
			new("Amp", "Mid", 0, 10),
			new("Amp", "Treble", 0, 10),
			new("Amp", "Master", 0, 10),
			new("Amp", "Low Cut", 10, 1_000),
			// Correctness, not taste: below 5 kHz is almost always the Cab or Reverb cut copied into the wrong field.
			new("Amp", "High Cut", 5_000, 20_000),
			new("Amp", "Presence", 0, 10),
			new("Amp", "Output EQ 80 Hz", -12, 12),
			new("Amp", "Output EQ 240 Hz", -12, 12),
			new("Amp", "Output EQ 750 Hz", -12, 12),
			new("Amp", "Output EQ 2200 Hz", -12, 12),
			new("Amp", "Output EQ 6600 Hz", -12, 12),
			new("Cab", "Low Cut", 20, 200),
			new("Cab", "High Cut", 2_000, 20_000),
			new("Parametric EQ", "Frequency 1", 20, 2_000),
			new("Parametric EQ", "Frequency 2", 100, 10_000),
			new("Parametric EQ", "Frequency 3", 100, 10_000),
			new("Parametric EQ", "Frequency 4", 100, 10_000),
			new("Parametric EQ", "Frequency 5", 200, 20_000),
			.. Enumerable.Range(1, 5).SelectMany(band => new Fm9ProductionParameterRange[]
			{
				new("Parametric EQ", $"Q {band}", 0.1, 10),
				new("Parametric EQ", $"Gain {band}", -20, 20)
			}),
			new("Reverb", "Mix", 0, 100),
			new("Reverb", "Level", -80, MaximumLevelDecibels),
			new("Reverb", "Balance", -100, 100),
			new("Reverb", "Time", 0.1, 100),
			new("Reverb", "High Cut", 200, 20_000),
			new("Reverb", "Low Cut", 20, 2_000),
			new("Reverb", "Pre-Delay", 0, 1_000)
		]);

		public static IReadOnlyList<string> Evaluate(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			List<string> corrections = [];

			AddRangeCorrections(proposal, corrections);
			corrections.AddRange(Fm9QualifiedModelCatalog.Evaluate(proposal));

			return corrections.Distinct(StringComparer.Ordinal).ToArray();
		}

		public static void EnsureSafeForExport(ToneDesignProposal proposal)
		{
			IReadOnlyList<string> corrections = Evaluate(proposal);
			if (corrections.Count > 0)
			{
				throw new InvalidDataException(
					"The FM9 proposal exceeds the FM9's parameter ranges or loudness limits: " +
					string.Join(" ", corrections));
			}
		}

		private static void AddRangeCorrections(
			ToneDesignProposal proposal,
			List<string> corrections)
		{
			foreach (Fm9ProductionParameterRange range in ParameterRanges)
			{
				ToneDesignBlockProposal? block = FindBlock(proposal, range.BlockType);
				if (block is null)
					continue;
				foreach (string channel in block.GetAuthoredChannels())
				{
					if (!block.GetParameters(channel).TryGetValue(range.ParameterName, out string? text))
						continue;
					if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
						!double.IsFinite(value) || value < range.Minimum || value > range.Maximum)
					{
						corrections.Add(
							$"Set {ToneDesignChannels.Label(range.BlockType, channel)} {range.ParameterName} within the FM9 range {range.Minimum}..{range.Maximum}.");
					}
				}
			}
		}

		// Brings numeric values into the FM9's ranges and loudness limits; a model cannot do better than clamping by
		// retrying, so only structural problems should cost a correction round.
		// The template's Amp Level is -12 dB, about 12 dB short of a hot output, so a proposal that leaves it out gets
		// unity (0 dB) rather than inheriting the quiet template value or spending a correction round.
		private static ToneDesignProposal WithAmpLevel(ToneDesignProposal proposal, List<string> adjustments)
		{
			if (FindBlock(proposal, "Amp") is not ToneDesignBlockProposal amp ||
				amp.Parameters.Keys.Any(key => key.Equals("Level", StringComparison.OrdinalIgnoreCase)))
				return proposal;
			adjustments.Add("Amp Level unset → 0");
			Dictionary<string, string> parameters = new(amp.Parameters, StringComparer.OrdinalIgnoreCase) { ["Level"] = "0" };
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block => ReferenceEquals(block, amp) ? block with { Parameters = parameters } : block).ToArray()
			};
		}

		public static ToneDesignProposal Repair(ToneDesignProposal proposal, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			ToneDesignProposal repaired = WithAmpLevel(proposal, adjustments);

			foreach (Fm9ProductionParameterRange range in ParameterRanges)
			{
				foreach (string channel in FindBlock(repaired, range.BlockType)?.GetAuthoredChannels() ?? [])
				{
					double? value = GetValue(repaired, range.BlockType, channel, range.ParameterName);
					if (value is double current && (current < range.Minimum || current > range.Maximum))
						repaired = Set(repaired, range.BlockType, channel, range.ParameterName,
							Math.Clamp(current, range.Minimum, range.Maximum), adjustments);
				}
			}

			// Drive Mix is a wet/dry blend that models read as an intensity (they chose 12-24% for a boost); anything
			// below 100 bleeds clean signal past the pedal. Keep a lower mix only when the block says it is a blend.
			foreach (string channel in FindBlock(repaired, "Drive")?.GetAuthoredChannels() ?? [])
			{
				if (GetValue(repaired, "Drive", channel, "Mix") < 100 && !DescribesParallelBlend(repaired))
					repaired = Set(repaired, "Drive", channel, "Mix", 100, adjustments);
			}

			return repaired;
		}

		private static bool DescribesParallelBlend(ToneDesignProposal proposal)
		{
			ToneDesignBlockProposal? drive = FindBlock(proposal, "Drive");
			string text = $"{drive?.Rationale} {drive?.ModelSelection?.Adaptation}";
			return text.Contains("parallel", StringComparison.OrdinalIgnoreCase) ||
				text.Contains("blend", StringComparison.OrdinalIgnoreCase);
		}

		private static ToneDesignProposal Set(
			ToneDesignProposal proposal,
			string blockType,
			string channel,
			string parameterName,
			double value,
			List<string> adjustments)
		{
			ToneDesignBlockProposal? block = FindBlock(proposal, blockType);
			if (block is null || !block.HasChannel(channel) ||
				!block.GetParameters(channel).TryGetValue(parameterName, out string? previous))
				return proposal;
			string formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
			// A setting adjusted by several rules is reported once, from its original value to its final one.
			string label = $"{ToneDesignChannels.Label(blockType, channel)} {parameterName} ";
			int earlier = adjustments.FindIndex(entry => entry.StartsWith(label, StringComparison.Ordinal));
			if (earlier >= 0)
			{
				string original = adjustments[earlier][label.Length..].Split(" → ")[0];
				adjustments[earlier] = $"{label}{original} → {formatted}";
			}
			else
				adjustments.Add($"{label}{previous} → {formatted}");
			Dictionary<string, string> parameters = new(block.GetParameters(channel), StringComparer.OrdinalIgnoreCase)
			{
				[parameterName] = formatted
			};
			ToneDesignBlockProposal updated = block.WithChannel(channel, parameters, block.GetModelSelection(channel));
			return proposal with
			{
				Blocks = proposal.Blocks.Select(candidate => ReferenceEquals(candidate, block) ? updated : candidate).ToArray()
			};
		}

		private static ToneDesignBlockProposal? FindBlock(ToneDesignProposal proposal, string blockType) =>
			proposal.Blocks.FirstOrDefault(candidate =>
				string.Equals(candidate.BlockType, blockType, StringComparison.OrdinalIgnoreCase));

		private static double? GetValue(
			ToneDesignProposal proposal,
			string blockType,
			string channel,
			string parameterName)
		{
			ToneDesignBlockProposal? block = FindBlock(proposal, blockType);
			return block is not null &&
				block.GetParameters(channel).TryGetValue(parameterName, out string? text) &&
				double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
				double.IsFinite(value)
					? value
					: null;
		}
	}
}
