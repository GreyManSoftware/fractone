namespace Fractal.Application
{
	// What a generated preset must respect on each gen-3 unit beyond the FM9 contract it is written in. Proposals stay in
	// the FM9's vocabulary (the exporter translates them); these limits are where the FM3 and Axe-Fx III differ in ways a
	// translation can't hide. From FracCompat's device brief (2026-10-14); the FM3 and III values are unverified on hardware.
	public sealed record ToneDesignDeviceConstraints(
		FractalDeviceId Device,
		string DisplayName,
		int GridRows,
		int GridColumns,
		// Ride blocks with no second instance on the device (the FM9 and III have two Amps, Cabs and Reverbs).
		IReadOnlyList<string> SingleInstanceBlocks,
		// The highest Input block: the FM9 and III's Input 4 is USB, which the FM3 doesn't have as a block.
		int MaximumInputBlock,
		// Delay models of the FM9 the device doesn't have, by their FM9 names.
		IReadOnlyList<string> UnavailableDelayModels,
		// Fewer effects than the grid holds where the DSP is smaller, so a full chain still fits the CPU.
		int MaximumEffects)
	{
		public static ToneDesignDeviceConstraints Fm9 { get; } = new(
			FractalDeviceId.Fm9, "FM9", 6, 14, [], 4, [], ToneDesignEffects.MaximumEffects);

		// Twelve columns leave room for four effects beside the eight Ride blocks; the FM3's single DSP is the tighter
		// limit anyway, so the prompt also asks for modest chains.
		public static ToneDesignDeviceConstraints Fm3 { get; } = new(
			FractalDeviceId.Fm3, "FM3", 4, 12, ["Amp", "Cab", "Reverb"], 3, ["Diffused Delay", "Zephyr"], 4);

		public static ToneDesignDeviceConstraints AxeFx3 { get; } = new(
			FractalDeviceId.AxeFxIii, "Axe-Fx III", 6, 14, [], 4, [], ToneDesignEffects.MaximumEffects);

		public static ToneDesignDeviceConstraints For(FractalDeviceId? device) => device switch
		{
			FractalDeviceId.Fm3 => Fm3,
			FractalDeviceId.AxeFxIii => AxeFx3,
			_ => Fm9
		};

		// The system-prompt section for a non-FM9 target; the FM9's prompt is unchanged, so this is empty for it.
		public string DescribeForPrompt()
		{
			if (Device == FractalDeviceId.Fm9)
				return string.Empty;
			List<string> lines =
			[
				$"Target device: {DisplayName} (beta, unverified on hardware). This request's device profile declares a beta exporter that translates the FM9 firmware-12 Ride contract below to the {DisplayName}, so write the proposal in exactly that FM9 contract and these {DisplayName} limits; name models as the FM9 catalogue does.",
				$"{DisplayName} grid: {GridRows} rows by {GridColumns} columns, so add at most {MaximumEffects} effects."
			];
			if (SingleInstanceBlocks.Count > 0)
				lines.Add($"The {DisplayName} has no second {string.Join(", ", SingleInstanceBlocks)} block: never add a second instance of them.");
			if (MaximumInputBlock < 4)
				lines.Add($"The {DisplayName} has no USB Input block: use Input 1 to {MaximumInputBlock} only.");
			if (UnavailableDelayModels.Count > 0)
				lines.Add($"The {DisplayName}'s Delay roster lacks {string.Join(" and ", UnavailableDelayModels)}: choose another Delay model.");
			if (Device == FractalDeviceId.Fm3)
				lines.Add("The FM3's CPU budget is lower than the FM9's: keep chains modest, preferring one delay and one modulation over stacked delays, pitch or synth blocks.");
			return string.Join(Environment.NewLine, lines);
		}

		// What the proposal asks of the device that it can't do, as corrections the model (or the player) can act on.
		public IReadOnlyList<string> Problems(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			List<string> problems = [];
			if (Device == FractalDeviceId.Fm9)
				return problems;
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block =>
				block.Instance > 1 && SingleInstanceBlocks.Contains(block.BlockType, StringComparer.OrdinalIgnoreCase)))
				problems.Add($"Remove {block.BlockType} {block.Instance}: the {DisplayName} has only one {block.BlockType} block.");
			int effects = proposal.Blocks.Count(ToneDesignEffects.IsEffect);
			if (effects > MaximumEffects)
				problems.Add($"Use at most {MaximumEffects} effects on the {DisplayName} (this preset has {effects}).");
			if (proposal.Blocks.Any(block => block.Row > GridRows || block.Column > GridColumns))
				problems.Add($"Keep every block within the {DisplayName}'s {GridRows} x {GridColumns} grid.");
			if (proposal.InputBlockInstance > MaximumInputBlock)
				problems.Add($"Use Input 1 to {MaximumInputBlock}: the {DisplayName} has no USB Input block.");
			foreach (ToneDesignBlockProposal delay in proposal.Blocks.Where(block =>
				string.Equals(block.BlockType, "Delay", StringComparison.OrdinalIgnoreCase)))
				foreach (string channel in delay.GetAuthoredChannels())
					foreach (string model in delay.GetParameters(channel).Values.Where(value =>
						UnavailableDelayModels.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
						problems.Add($"Choose another Delay model than {model} for Delay {delay.Instance}: the {DisplayName} doesn't have it.");
			return problems;
		}
	}
}
