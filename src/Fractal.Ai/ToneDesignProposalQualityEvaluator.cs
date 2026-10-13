using System.Globalization;
using Fractal.Application;

namespace Fractal.Ai
{
	public sealed record ToneDesignProposalQualityEvaluation(
		bool RequiresCorrection,
		IReadOnlyList<string> Corrections);

	public static class ToneDesignProposalQualityEvaluator
	{
		private sealed record SupportedBlock(
			int Column,
			IReadOnlyDictionary<string, ParameterConstraint> Parameters);

		private sealed record ParameterConstraint(
			double Minimum,
			double Maximum,
			bool Integer,
			IReadOnlySet<string>? AllowedValues = null);

		private static readonly IReadOnlyDictionary<string, SupportedBlock> SupportedBlocks =
			new Dictionary<string, SupportedBlock>(StringComparer.OrdinalIgnoreCase)
			{
				["Input"] = new(1, EmptyParameters()),
				["Gate/Expander"] = new(2, Parameters(
					("Threshold", -100, 0, false), ("Attack", 0.1, 1_000, false),
					("Hold", 1, 1_000, false), ("Release", 1, 1_000, false),
					("Ratio", 1, 20, false), ("Low Cut", 10, 1_000, false),
					("High Cut", 200, 20_000, false), ("Level", -80, 20, false))),
				["Drive"] = new(3, Parameters(
					("Drive Model", 0, 86, true),
					("Drive", 0, 10, false), ("Tone", 0, 10, false),
					("Level", 0, 10, false), ("Mix", 0, 100, false))),
				["Amp"] = new(4, CreateAmpParameters()),
				["Cab"] = new(5, Parameters(
					("Bank", 0, 4, true), ("Cab Type", 0, 1023, true),
					("Low Cut", 20, 200, false), ("High Cut", 2_000, 20_000, false))),
				["Parametric EQ"] = new(6, CreatePeqParameters()),
				["Reverb"] = new(7, Parameters(
					("Mix", 0, 100, false), ("Level", -80, 20, false),
					("Balance", -100, 100, false),
					("Time", 0.1, 100, false),
					("High Cut", 200, 20_000, false), ("Low Cut", 20, 2_000, false),
					("Pre-Delay", 0, 1_000, false))),
				["Output"] = new(8, EmptyParameters())
			};

		// A claim citing something that was never retrieved cannot be verified, so it is kept as a low-confidence
		// inference rather than rejected; nothing unverified is ever shown as documented.
		public static ToneDesignProposal DowngradeUnverifiableEvidence(
			ToneDesignProposal proposal,
			IReadOnlyList<ToneDesignEvidenceCitationBinding>? bindings)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			HashSet<string> bound = (bindings ?? []).Select(binding => binding.SourceUrl).ToHashSet(StringComparer.Ordinal);
			bool Unverifiable(string sourceUrl) => !bound.Contains(sourceUrl);
			return proposal with
			{
				Evidence = proposal.Evidence.Select(conclusion =>
					// Any documented or inferred claim without a bound source becomes the one uncited form the contract allows.
					conclusion.Classification is "documented" or "inferred" && Unverifiable(conclusion.SourceUrl) &&
					(conclusion.Classification, conclusion.Confidence, conclusion.SourceUrl, conclusion.SourceDate) !=
						("inferred", "low", "", "")
						? conclusion with { Classification = "inferred", Confidence = "low", SourceUrl = "", SourceDate = "" }
						: conclusion).ToArray(),
				Blocks = proposal.Blocks.Select(block => block.MapChannels(channel =>
					channel.ModelSelection is ToneDesignModelSelection selection &&
					selection.EvidenceBasis is "recording" or "live" && Unverifiable(selection.SourceUrl)
						? channel with { ModelSelection = selection with { EvidenceBasis = "inferred", SourceUrl = "" } }
						: channel)).ToArray()
			};
		}

		// A conclusion classified as substituted already is one; label it rather than spend a correction on wording.
		public static ToneDesignProposal LabelSubstitutions(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			return proposal with
			{
				Evidence = proposal.Evidence.Select(conclusion =>
					conclusion.Classification == "substituted" && !ClearlyIdentifiesFm9Substitution(conclusion)
						? conclusion with { Rationale = $"FM9 substitution: {conclusion.Rationale}" }
						: conclusion).ToArray()
			};
		}

		// The Ride layout's only valid routes are the seven row-3 links between adjacent columns, so they are not
		// a model decision; setting them avoids spending a correction round on an omitted cable.
		public static ToneDesignProposal WithRideRoutes(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			return proposal with
			{
				// With effects laid out along row 3 the chain is longer, but still one cable per adjacent pair.
				Routes = proposal.Blocks.Any(ToneDesignEffects.IsEffect)
					? ToneDesignEffects.SerialRoutes(proposal.Blocks)
					: Enumerable.Range(1, 7).Select(column => new ToneDesignRoute(3, column, 3)).ToArray()
			};
		}

		// Effects checked against the device dictionary (unknown settings dropped, options mapped, numbers clamped); without
		// a catalogue they cannot be exported, so they are removed and the Ride chain closes up.
		public static ToneDesignProposal RepairEffects(ToneDesignProposal proposal, IToneDesignEffectCatalog? catalog, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			if (!proposal.Blocks.Any(ToneDesignEffects.IsEffect))
				return proposal;
			if (catalog is not null)
				return proposal with
				{
					Blocks = proposal.Blocks.Select(block => ToneDesignEffects.IsEffect(block) ? catalog.Repair(block, adjustments) : catalog.RepairSettings(block, adjustments)).ToArray()
				};
			ToneDesignBlockProposal[] effects = proposal.Blocks.Where(ToneDesignEffects.IsEffect).ToArray();
			adjustments.Add($"removed {string.Join(", ", effects.Select(effect => effect.BlockType))} (effects are unavailable here)");
			return proposal with
			{
				Blocks = ToneDesignEffects.Layout(proposal.Blocks.Where(block => !ToneDesignEffects.IsEffect(block)).ToArray(), []),
				Scenes = proposal.Scenes.Select(scene => scene with
				{
					BlockSettings = scene.BlockSettings.Where(setting => !effects.Any(effect =>
						string.Equals(effect.BlockType, setting.BlockType, StringComparison.OrdinalIgnoreCase) && effect.Instance == setting.Instance)).ToArray()
				}).ToArray()
			};
		}

		// Keeps the modifiers the device can attach: a known source, a block of this preset and one of its continuous
		// settings (names matched without case), with both ends in 0..100 % (a reversed range is a deliberate sweep).
		public static ToneDesignProposal RepairModifiers(ToneDesignProposal proposal, IToneDesignEffectCatalog? catalog, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			if (proposal.Modifiers is not { Count: > 0 } modifiers)
				return proposal;
			if (catalog is null)
			{
				adjustments.Add("removed the modifiers (modifiers are unavailable here)");
				return proposal with { Modifiers = [] };
			}
			List<ToneDesignModifier> kept = [];
			foreach (ToneDesignModifier modifier in modifiers)
			{
				string? source = ToneDesignEffects.ModifierSources.FirstOrDefault(name => string.Equals(name, modifier.Source, StringComparison.OrdinalIgnoreCase));
				ToneDesignBlockProposal? block = proposal.Blocks.FirstOrDefault(block =>
					string.Equals(block.BlockType, modifier.BlockType, StringComparison.OrdinalIgnoreCase) && block.Instance == modifier.Instance);
				string? parameter = block is null ? null : catalog.ModifierTargets(block.BlockType)
					.FirstOrDefault(name => string.Equals(name, modifier.Parameter, StringComparison.OrdinalIgnoreCase));
				if (source is null || block is null || parameter is null)
				{
					adjustments.Add($"dropped the {modifier.Source} modifier on {modifier.BlockType} {modifier.Parameter} (the FM9 cannot attach it)");
					continue;
				}
				kept.Add(modifier with
				{
					Source = source, BlockType = block.BlockType, Parameter = parameter,
					Minimum = Math.Clamp(modifier.Minimum, 0, 100), Maximum = Math.Clamp(modifier.Maximum, 0, 100)
				});
			}
			return proposal with { Modifiers = kept };
		}

		// Gate and Reverb Level are the blocks' whole output (dry included), not an amount: a Blackened preset set Reverb
		// Level -10 dB for "a subtle space cue" and came out 10 dB quiet. Loudness belongs to Amp Level, so a cut is undone.
		public static ToneDesignProposal KeepBlockLevels(ToneDesignProposal proposal, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block =>
				{
					// Drive Level is the pedal's 0..10 output knob (its FM9 default is 5): a model treating it as dB wrote 0,
					// which all but silences the chain whenever the drive is on.
					if (block.BlockType == "Drive")
					{
						foreach (string channel in block.GetAuthoredChannels())
						{
							IReadOnlyDictionary<string, string> drive = block.GetParameters(channel);
							if (drive.TryGetValue("Level", out string? knob) &&
								double.TryParse(knob, NumberStyles.Float, CultureInfo.InvariantCulture, out double setting) && setting < 2)
							{
								block = block.WithChannel(channel, new Dictionary<string, string>(drive) { ["Level"] = "5" }, block.GetModelSelection(channel));
								adjustments.Add($"set {ToneDesignChannels.Label(block.BlockType, channel)} Level from {knob} to 5 (its output knob, where 0 is silent)");
							}
						}
						return block;
					}
					if (block.BlockType is not ("Gate/Expander" or "Reverb") || block.Instance != 1)
						return block;
					foreach (string channel in block.GetAuthoredChannels())
					{
						IReadOnlyDictionary<string, string> parameters = block.GetParameters(channel);
						if (!parameters.TryGetValue("Level", out string? text) ||
							!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double level) || level >= -3)
							continue;
						block = block.WithChannel(channel, new Dictionary<string, string>(parameters) { ["Level"] = "0" }, block.GetModelSelection(channel));
						adjustments.Add($"set {ToneDesignChannels.Label(block.BlockType, channel)} Level from {text} to 0 dB (it is the block's whole output, not an amount)");
					}
					return block;
				}).ToArray()
			};
		}

		// Amp Level is the preset's loudness: how loud an amp runs depends mostly on its model, so each amp takes the level
		// that brings its model (channel A's, as the level is shared by all channels) to the top of the output meter,
		// rather than the model's guess (0 dB on a USA MK IIC+ came out 10 dB hot).
		public static ToneDesignProposal LevelAmps(ToneDesignProposal proposal, IToneDesignEffectCatalog? catalog, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			if (catalog is null)
				return proposal;
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block =>
				{
					if (block.BlockType != "Amp" ||
						!int.TryParse(block.Parameters.GetValueOrDefault("Amp Model"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int model) ||
						catalog.AmpLevel(model) is not double level)
						return block;
					string text = level.ToString("0.#", CultureInfo.InvariantCulture);
					string? before = block.Parameters.GetValueOrDefault("Level");
					if (before == text)
						return block;
					foreach (string channel in block.GetAuthoredChannels())
						block = block.WithChannel(channel, new Dictionary<string, string>(block.GetParameters(channel)) { ["Level"] = text }, block.GetModelSelection(channel));
					adjustments.Add($"set {block.BlockType} {block.Instance} Level to {text} dB, where its model usually reaches the top of the output meter");
					return block;
				}).ToArray()
			};
		}

		private static readonly int[] OutputEqBands = [80, 240, 750, 2200, 6600];

		// The Amp's Output EQ and Parametric EQ 1 are in series, so a Parametric EQ band that repeats an Output EQ cut or
		// boost (same direction within a third of an octave) doubles it: models mirror the five bands, which scooped one
		// preset hollow (-5 dB at 750 Hz) and fizzy (+4 dB at 2.2 kHz). The Output EQ is kept; the repeat is flattened.
		public static ToneDesignProposal RemoveRepeatedEq(ToneDesignProposal proposal, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			ToneDesignBlockProposal? amp = proposal.Blocks.FirstOrDefault(block => block.BlockType == "Amp" && block.Instance == 1);
			if (amp is null)
				return proposal;
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block =>
				{
					if (block.BlockType != "Parametric EQ" || block.Instance != 1)
						return block;
					foreach (string channel in block.GetAuthoredChannels())
					{
						IReadOnlyDictionary<string, string> ampParameters = amp.GetParameters(amp.HasChannel(channel) ? channel : ToneDesignChannels.A);
						if (ampParameters.GetValueOrDefault("Output EQ Enabled") != "1")
							continue;
						Dictionary<string, string> parameters = new(block.GetParameters(channel), StringComparer.Ordinal);
						bool changed = false;
						for (int band = 1; band <= 5; band++)
						{
							if (!TryNumber(parameters, $"Frequency {band}", out double frequency) || !TryNumber(parameters, $"Gain {band}", out double gain) || gain == 0)
								continue;
							int match = OutputEqBands.FirstOrDefault(fixedBand => Math.Abs(Math.Log2(frequency / fixedBand)) <= 1d / 3);
							if (match == 0 || !TryNumber(ampParameters, $"Output EQ {match} Hz", out double output) || Math.Sign(output) != Math.Sign(gain))
								continue;
							parameters[$"Gain {band}"] = "0";
							changed = true;
							adjustments.Add($"flattened Parametric EQ band {band} ({gain.ToString("0.#", CultureInfo.InvariantCulture)} dB at " +
								$"{frequency.ToString("0", CultureInfo.InvariantCulture)} Hz), which repeated the Amp Output EQ " +
								$"{output.ToString("0.#", CultureInfo.InvariantCulture)} dB at {match} Hz");
						}
						if (changed)
							block = block.WithChannel(channel, parameters, block.GetModelSelection(channel));
					}
					return block;
				}).ToArray()
			};

			static bool TryNumber(IReadOnlyDictionary<string, string> values, string name, out double number)
			{
				number = 0;
				return values.TryGetValue(name, out string? text) &&
					double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
			}
		}

		// Clamps continuous values into the device ranges above (selectors and enumerations are left for correction).
		public static ToneDesignProposal ClampToDeviceRanges(ToneDesignProposal proposal, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block =>
				{
					if (!SupportedBlocks.TryGetValue(block.BlockType, out SupportedBlock? supported))
						return block;
					foreach (string channel in block.GetAuthoredChannels())
					{
						Dictionary<string, string> parameters = new(block.GetParameters(channel), StringComparer.OrdinalIgnoreCase);
						foreach (KeyValuePair<string, string> parameter in block.GetParameters(channel))
						{
							if (!supported.Parameters.TryGetValue(parameter.Key, out ParameterConstraint? constraint) ||
								constraint.Integer || constraint.AllowedValues is not null ||
								!double.TryParse(parameter.Value, System.Globalization.NumberStyles.Float,
									System.Globalization.CultureInfo.InvariantCulture, out double value) ||
								!double.IsFinite(value) || (value >= constraint.Minimum && value <= constraint.Maximum))
								continue;
							string clamped = Math.Clamp(value, constraint.Minimum, constraint.Maximum)
								.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
							adjustments.Add($"{ToneDesignChannels.Label(block.BlockType, channel)} {parameter.Key} {parameter.Value} → {clamped}");
							parameters[parameter.Key] = clamped;
						}
						block = block.WithChannel(channel, parameters, block.GetModelSelection(channel));
					}
					return block;
				}).ToArray()
			};
		}

		public static ToneDesignProposalQualityEvaluation Evaluate(
			ToneDesignProposal proposal,
			IReadOnlyList<ToneDesignSectionRequest>? requestedSections = null,
			IReadOnlyList<ToneDesignEvidenceCitationBinding>? citationBindings = null,
			bool requireGroundedSelections = false,
			Func<string, string, bool>? isDeviceSetting = null)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			List<string> corrections = [];
			ToneDesignModelGrounding.Evaluate(proposal, citationBindings, requireGroundedSelections, corrections);

			if (string.IsNullOrWhiteSpace(proposal.Summary))
			{
				corrections.Add("Add a concise, reviewable summary.");
			}

			foreach (KeyValuePair<string, SupportedBlock> supported in SupportedBlocks)
			{
				int count = proposal.Blocks.Count(block =>
					string.Equals(block.BlockType, supported.Key, StringComparison.OrdinalIgnoreCase));

				if (count == 0)
				{
					corrections.Add(
						$"Include the existing {supported.Key} 1 block at row 3, column {supported.Value.Column}; do not omit requested-chain behavior.");
				}
			}

			bool hasEffects = proposal.Blocks.Any(ToneDesignEffects.IsEffect);
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				if (ToneDesignEffects.IsEffect(block))
				{
					// Settings were checked against the device dictionary; the block itself must be an effect on row 3.
					if (!ToneDesignEffects.BlockTypes.Contains(block.BlockType, StringComparer.OrdinalIgnoreCase) || block.Row != 3)
						corrections.Add($"Use {block.BlockType} only as an effect from the listed effect blocks.");
					if (string.IsNullOrWhiteSpace(block.Rationale))
						corrections.Add($"Add a rationale for the {block.BlockType} effect.");
					if (block.Channels.Count > 0)
						corrections.Add($"Remove {block.BlockType} effect channels; effects play channel A.");
					continue;
				}
				if (!SupportedBlocks.TryGetValue(block.BlockType, out SupportedBlock? supported))
				{
					corrections.Add($"Remove unsupported block {block.BlockType} {block.Instance}.");
					continue;
				}

				// With effects the Ride blocks shift right but keep their order along row 3.
				if (block.Instance != 1 || block.Row != 3 || (!hasEffects && block.Column != supported.Column))
				{
					corrections.Add(
						$"Place {block.BlockType} 1 at fixed Ride position row 3, column {supported.Column}.");
				}

				if (string.IsNullOrWhiteSpace(block.Rationale))
				{
					corrections.Add(
						$"Add evidence-based rationale for {block.BlockType} {block.Instance}.");
				}
				if (string.Equals(block.BlockType, "Reverb", StringComparison.OrdinalIgnoreCase) &&
					!block.Parameters.ContainsKey("Time"))
				{
					corrections.Add(
						"Explicitly author global Reverb Time within 0.1..100 seconds; do not inherit template decay.");
				}

				if (block.Channels.Count > 0 &&
					!ToneDesignProposalJsonContract.ChannelBlocks.Contains(block.BlockType, StringComparer.OrdinalIgnoreCase))
				{
					corrections.Add(
						$"Remove {block.BlockType} channels; only {string.Join(", ", ToneDesignProposalJsonContract.ChannelBlocks)} may author channels B-D.");
				}

				foreach (string channel in block.GetAuthoredChannels())
				{
					EvaluateParameters(block, channel, supported, requireGroundedSelections, corrections, isDeviceSetting);
					if (requireGroundedSelections && channel != ToneDesignChannels.A && proposal.Scenes.All(scene =>
						ToneDesignSceneChannels.Find(scene, block.BlockType, block.Instance)?.Channel != channel))
					{
						corrections.Add(
							$"Play {block.BlockType} channel {channel} in at least one scene or remove that channel.");
					}
				}
			}

			foreach (IGrouping<string, ToneDesignBlockProposal> duplicate in proposal.Blocks
				.GroupBy(block => CreateBlockKey(block.BlockType, block.Instance), StringComparer.OrdinalIgnoreCase)
				.Where(group => group.Count() > 1))
			{
				corrections.Add($"Emit {duplicate.First().BlockType} {duplicate.First().Instance} at most once.");
			}

			// The chain is row 3, columns 1..N in Ride order with effects between; every adjacent pair is cabled.
			int lastColumn = hasEffects ? proposal.Blocks.Where(block => block.Row == 3).Select(block => block.Column).DefaultIfEmpty(8).Max() : 8;
			if (hasEffects)
			{
				string[] rideOrder = proposal.Blocks.Where(block => !ToneDesignEffects.IsEffect(block)).OrderBy(block => block.Column)
					.Select(block => block.BlockType).ToArray();
				if (!rideOrder.SequenceEqual(ToneDesignEffects.RideBlockTypes.Where(type => rideOrder.Contains(type, StringComparer.OrdinalIgnoreCase)),
					StringComparer.OrdinalIgnoreCase) ||
					proposal.Blocks.Select(block => block.Column).Order().SequenceEqual(Enumerable.Range(1, proposal.Blocks.Count)) is false)
					corrections.Add("Keep the Ride blocks in order along row 3 with effects between them.");
			}

			foreach (ToneDesignRoute route in proposal.Routes)
			{
				if (route.SourceRow != 3 ||
					route.DestinationRow != 3 ||
					route.SourceColumn < 1 || route.SourceColumn >= lastColumn)
				{
					corrections.Add(
						$"Use only row-3 adjacent routes from source columns 1 through {lastColumn - 1}.");
				}
			}

			for (int sourceColumn = 1; sourceColumn < lastColumn; sourceColumn++)
			{
				if (!proposal.Routes.Any(route =>
					route.SourceRow == 3 &&
					route.SourceColumn == sourceColumn &&
					route.DestinationRow == 3))
				{
					corrections.Add(
						$"Include the existing adjacent route from row 3, column {sourceColumn} to row 3, column {sourceColumn + 1}.");
				}
			}

			foreach (ToneDesignSceneProposal scene in proposal.Scenes)
			{
				if (string.IsNullOrWhiteSpace(scene.Name) ||
					string.IsNullOrWhiteSpace(scene.Purpose))
				{
					corrections.Add("Give every scene a non-empty name and purpose.");
				}

				if (!ToneDesignSectionRoles.IsSupported(scene.Role))
				{
					corrections.Add(
						$"Set scene '{scene.Name}' role to one of: {string.Join(", ", ToneDesignSectionRoles.All)}.");
				}

				if (scene.Sections.Count == 0 ||
					scene.Sections.Any(string.IsNullOrWhiteSpace) ||
					scene.Sections.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
						scene.Sections.Count)
				{
					corrections.Add(
						$"Give scene '{scene.Name}' one or more non-empty, unique section references.");
				}

				HashSet<string> sceneBlocks = new(StringComparer.OrdinalIgnoreCase);
				foreach (ToneDesignSceneBlockSetting setting in scene.BlockSettings)
				{
					if (proposal.Blocks.FirstOrDefault(block => ToneDesignEffects.IsEffect(block) && block.Instance == setting.Instance &&
						string.Equals(block.BlockType, setting.BlockType, StringComparison.OrdinalIgnoreCase)) is not null)
					{
						if (setting.Channel != ToneDesignChannels.A)
							corrections.Add($"Play the {setting.BlockType} effect on channel A in scene '{scene.Name}'.");
						if (!sceneBlocks.Add(CreateBlockKey(setting.BlockType, setting.Instance)))
							corrections.Add($"Emit {setting.BlockType} {setting.Instance} at most once in scene '{scene.Name}'.");
						continue;
					}
					if (!SupportedBlocks.ContainsKey(setting.BlockType) ||
						setting.Instance != 1 ||
						setting.Channel is not ("A" or "B" or "C" or "D"))
					{
						corrections.Add(
							$"Use only existing instance-1 Ride blocks and channels A-D in scene '{scene.Name}'.");
					}
					else if (requireGroundedSelections && proposal.Blocks.FirstOrDefault(block =>
						string.Equals(block.BlockType, setting.BlockType, StringComparison.OrdinalIgnoreCase)) is { } sceneBlock &&
						!sceneBlock.HasChannel(setting.Channel))
					{
						// A generated scene must play what the proposal describes, not a template channel.
						corrections.Add(
							$"Scene '{scene.Name}' plays {setting.BlockType} channel {setting.Channel}, which is not authored; " +
							$"author {setting.BlockType} channel {setting.Channel} in channels with that scene's own settings (what differs from channel A for it); " +
							$"play channel A only when that scene really sounds the same.");
					}

					if (!sceneBlocks.Add(CreateBlockKey(setting.BlockType, setting.Instance)))
					{
						corrections.Add(
							$"Emit {setting.BlockType} {setting.Instance} at most once in scene '{scene.Name}'.");
					}
				}

				IReadOnlySet<string> requiredSceneBlocks = SupportedBlocks.Keys
					.Select(blockType => CreateBlockKey(blockType, 1))
					.Concat(proposal.Blocks.Where(ToneDesignEffects.IsEffect).Select(block => CreateBlockKey(block.BlockType, block.Instance)))
					.ToHashSet(StringComparer.OrdinalIgnoreCase);
				if (!sceneBlocks.SetEquals(requiredSceneBlocks))
				{
					corrections.Add(
						$"Give scene '{scene.Name}' one explicit channel and bypass state for every existing Ride block; do not inherit omitted state.");
				}
			}

			AddSectionContractCorrections(
				proposal.Scenes,
				requestedSections ?? [],
				corrections);

			if (proposal.Cautions.Count == 0)
			{
				corrections.Add(
					"Add a caution that the proposal requires audition-based adjustment.");
			}

			if (proposal.Evidence.Count == 0)
			{
				corrections.Add(
					"Add structured evidence conclusions that distinguish documented source facts from requested adaptations and FM9 substitutions.");
			}

			foreach (ToneDesignEvidenceConclusion conclusion in proposal.Evidence)
			{
				if (string.IsNullOrWhiteSpace(conclusion.Claim))
				{
					corrections.Add("Give every evidence conclusion a non-empty claim.");
				}

				if (string.IsNullOrWhiteSpace(conclusion.Rationale))
				{
					corrections.Add("Give every evidence conclusion a non-empty rationale.");
				}

				if (conclusion.Classification is not ("documented" or "inferred" or "substituted"))
				{
					corrections.Add(
						"Classify every evidence conclusion as documented, inferred, or substituted.");
				}

				if (conclusion.Confidence is not ("high" or "medium" or "low"))
				{
					corrections.Add(
						"Set every evidence conclusion confidence to high, medium, or low.");
				}

				bool uncitedInference = conclusion.Classification == "inferred" &&
					string.IsNullOrWhiteSpace(conclusion.SourceUrl) && conclusion.Confidence == "low" &&
					string.IsNullOrWhiteSpace(conclusion.SourceDate);
				bool requiresCitation =
					conclusion.Classification == "documented" ||
					(conclusion.Classification == "inferred" && !uncitedInference);
				bool retrievalContractHasNoSources =
					citationBindings is { Count: 0 };
				if (requiresCitation && string.IsNullOrWhiteSpace(conclusion.SourceUrl))
					corrections.Add("Documented and cited inferred evidence require a bound sourceUrl.");
				if (citationBindings is null &&
					(requiresCitation || !string.IsNullOrWhiteSpace(conclusion.SourceUrl)) &&
					!IsAbsoluteHttpUrl(conclusion.SourceUrl))
				{
					corrections.Add(
						"Use an absolute http/https sourceUrl for documented or inferred evidence and for any cited substitution source.");
				}

				if (!string.IsNullOrWhiteSpace(conclusion.SourceUrl) &&
					citationBindings is not null)
				{
					if (retrievalContractHasNoSources)
					{
						corrections.Add(
							"No retrieved evidence sources are available. Remove the sourceUrl; fabricated or unbound citations are not accepted.");
					}
					else if (!citationBindings.Any(binding => string.Equals(
						binding.SourceUrl,
						conclusion.SourceUrl,
						StringComparison.Ordinal)))
					{
						corrections.Add(
							"Use only a source-reference supplied in the retrieved evidence context; unknown or unbound citations are not accepted.");
					}
				}

				if (requiresCitation && retrievalContractHasNoSources)
				{
					corrections.Add(
						"No retrieved evidence sources are available, so documented and cited inferred conclusions are not permitted. Use low-confidence uncited inference or rationalized FM9 substitutions.");
				}

				if (requiresCitation && string.IsNullOrWhiteSpace(conclusion.SourceDate))
				{
					corrections.Add(
						"Add a non-empty sourceDate to every documented or inferred evidence conclusion.");
				}

				if (conclusion.Classification == "substituted" &&
					!ClearlyIdentifiesFm9Substitution(conclusion))
				{
					corrections.Add(
						"Make every substituted conclusion explicitly identify the FM9 substitution.");
				}
			}

			corrections.AddRange(Fm9ProductionSafetyPolicy.Evaluate(proposal));

			return new ToneDesignProposalQualityEvaluation(
				corrections.Count > 0,
				corrections);
		}

		private static void EvaluateParameters(
			ToneDesignBlockProposal block,
			string channel,
			SupportedBlock supported,
			bool requireGroundedSelections,
			List<string> corrections,
			Func<string, string, bool>? isDeviceSetting = null)
		{
			string label = ToneDesignChannels.Label(block.BlockType, channel);
			IReadOnlyDictionary<string, string> parameters = block.GetParameters(channel);
			if (requireGroundedSelections)
			{
				// Amp Level is the block's shared output volume: repair sets an unauthored one to unity, so its absence
				// should not cost a correction round.
				foreach (string requiredParameter in supported.Parameters.Keys.Where(name =>
					!(block.BlockType.Equals("Amp", StringComparison.OrdinalIgnoreCase) && name == "Level")))
				{
					if (!parameters.ContainsKey(requiredParameter))
						corrections.Add($"Explicitly author {label} {requiredParameter}; generated proposals cannot inherit template values.");
				}
			}

			foreach (KeyValuePair<string, string> parameter in parameters)
			{
				if (!supported.Parameters.TryGetValue(
					parameter.Key,
					out ParameterConstraint? constraint))
				{
					// Any other device setting was checked against the FM9's dictionary (RepairEffects); it is not unsupported.
					if (isDeviceSetting?.Invoke(block.BlockType, parameter.Key) == true)
						continue;
					corrections.Add(
						$"Remove unsupported parameter {label} {parameter.Key}.");
					continue;
				}

				if (constraint.AllowedValues is not null)
				{
					if (!constraint.AllowedValues.Contains(parameter.Value))
					{
						corrections.Add(
							$"Set {label} {parameter.Key} to {string.Join(" or ", constraint.AllowedValues)}.");
					}

					continue;
				}

				bool valid = constraint.Integer
					? int.TryParse(
						parameter.Value,
						System.Globalization.NumberStyles.Integer,
						System.Globalization.CultureInfo.InvariantCulture,
						out int integerValue) &&
						integerValue >= constraint.Minimum &&
						integerValue <= constraint.Maximum
					: double.TryParse(
						parameter.Value,
						System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture,
						out double floatingPointValue) &&
						double.IsFinite(floatingPointValue) &&
						floatingPointValue >= constraint.Minimum &&
						floatingPointValue <= constraint.Maximum;

				if (!valid)
				{
					corrections.Add(
						$"Set {label} {parameter.Key} within {constraint.Minimum}..{constraint.Maximum}" +
						(constraint.Integer ? " using an integer." : "."));
				}
			}
		}

		private static void AddSectionContractCorrections(
			IReadOnlyList<ToneDesignSceneProposal> scenes,
			IReadOnlyList<ToneDesignSectionRequest> requestedSections,
			List<string> corrections)
		{
			foreach (ToneDesignSectionRequest requestedSection in requestedSections)
			{
				List<ToneDesignSceneProposal> coveringScenes = scenes
					.Where(scene => scene.Sections.Contains(
						requestedSection.Name,
						StringComparer.OrdinalIgnoreCase))
					.ToList();

				if (coveringScenes.Count == 0)
				{
					corrections.Add(
						$"Cover requested section '{requestedSection.Name}' with at least one scene.");
					continue;
				}

				foreach (ToneDesignSceneProposal scene in coveringScenes)
				{
					if (!string.Equals(
						scene.Role,
						requestedSection.Role,
						StringComparison.Ordinal))
					{
						corrections.Add(
							$"Set scene '{scene.Name}' role to '{requestedSection.Role}' because it covers requested section '{requestedSection.Name}'.");
					}
				}
			}
		}


		public static ToneDesignProposal ApplyCorrectionLoop(
			ToneDesignProposal proposal,
			ToneDesignProposalQualityEvaluation evaluation)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(evaluation);

			if (!evaluation.RequiresCorrection)
			{
				return proposal;
			}

			List<string> cautions = proposal.Cautions
				.Concat(evaluation.Corrections)
				.Append("Audition and adjust this proposal on your own equipment before use.")
				.Distinct(StringComparer.Ordinal)
				.ToList();

			return proposal with { Cautions = cautions };
		}

		public static string CreateCorrectionPrompt(ToneDesignProposalQualityEvaluation evaluation)
		{
			ArgumentNullException.ThrowIfNull(evaluation);
			return $"""
				Revise the previous JSON proposal to satisfy the FM9 Ride contract.
				Corrections:
				- {string.Join(Environment.NewLine + "- ", evaluation.Corrections)}
				Apply each correction to the exact JSON field it names; do not merely discuss it in rationale or cautions.
				Recheck complete Gate/Expander, Drive, Amp, Cab, Parametric EQ, and Reverb parameters, including Amp Low Cut and High Cut.
				Amp High Cut is a distinct field and must be 5000..20000; never copy Cab High Cut or Reverb High Cut into it.
				Drive Level cannot be negative.
				Every substituted evidence claim itself must contain the exact phrase "FM9 substitution".
				Keep documented source-rig facts separate from requested instrument, pickup, performance-context, and FM9 adaptations.
				Label unsupported facts as inferred or substituted with appropriate confidence instead of inventing them.
				Return the complete corrected JSON object only.
				""";
		}

		private static bool IsAbsoluteHttpUrl(string value)
		{
			return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
				(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
		}

		private static bool ClearlyIdentifiesFm9Substitution(
			ToneDesignEvidenceConclusion conclusion)
		{
			string description = $"{conclusion.Claim} {conclusion.Rationale}";
			return description.Contains("FM9", StringComparison.OrdinalIgnoreCase) &&
				(description.Contains("substitut", StringComparison.OrdinalIgnoreCase) ||
					description.Contains("adapt", StringComparison.OrdinalIgnoreCase));
		}

		private static string CreateBlockKey(string blockType, int instance) =>
			$"{blockType}\u001f{instance}";

		private static IReadOnlyDictionary<string, ParameterConstraint>
			EmptyParameters()
		{
			return new Dictionary<string, ParameterConstraint>(
				StringComparer.OrdinalIgnoreCase);
		}

		private static Dictionary<string, ParameterConstraint>
			Parameters(params (string Name, double Minimum, double Maximum, bool Integer)[] definitions)
		{
			return definitions.ToDictionary(
				definition => definition.Name,
				definition => new ParameterConstraint(
					definition.Minimum,
					definition.Maximum,
					definition.Integer),
				StringComparer.OrdinalIgnoreCase);
		}

		private static IReadOnlyDictionary<string, ParameterConstraint> CreateAmpParameters()
		{
			Dictionary<string, ParameterConstraint> parameters = Parameters(
				("Amp Model", 0, Fm9QualifiedModelCatalog.MaximumAmpModel, true), ("Level", -80, 20, false),
				("Gain", 0, 10, false), ("Bass", 0, 10, false),
				("Mid", 0, 10, false), ("Treble", 0, 10, false),
				("Master", 0, 10, false), ("Low Cut", 10, 1_000, false),
				("High Cut", 400, 40_000, false), ("Presence", 0, 10, false),
				("Output EQ Enabled", 0, 1, true),
				("Output EQ 80 Hz", -12, 12, false),
				("Output EQ 240 Hz", -12, 12, false),
				("Output EQ 750 Hz", -12, 12, false),
				("Output EQ 2200 Hz", -12, 12, false),
				("Output EQ 6600 Hz", -12, 12, false));
			parameters["Output EQ Type"] = EnumConstraint("Mark5Band");
			parameters["Output EQ Location"] = EnumConstraint("Output", "PrePowerAmp");

			return parameters;
		}

		private static ParameterConstraint EnumConstraint(params string[] allowedValues)
		{
			return new ParameterConstraint(
				0,
				0,
				false,
				new HashSet<string>(allowedValues, StringComparer.OrdinalIgnoreCase));
		}

		private static IReadOnlyDictionary<string, ParameterConstraint>
			CreatePeqParameters()
		{
			List<(string Name, double Minimum, double Maximum, bool Integer)> definitions = [];

			for (int band = 1; band <= 5; band++)
			{
				definitions.Add((
					$"Frequency {band}",
					band == 1 ? 20 : band == 5 ? 200 : 100,
					band == 1 ? 2_000 : band == 5 ? 20_000 : 10_000,
					false));
				definitions.Add(($"Q {band}", 0.1, 10, false));
				definitions.Add(($"Gain {band}", -20, 20, false));
			}

			return Parameters([.. definitions]);
		}
	}
}
