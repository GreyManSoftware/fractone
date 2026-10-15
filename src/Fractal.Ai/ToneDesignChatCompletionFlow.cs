using System.Text.Json;
using System.Text.Json.Serialization;
using Fractal.Application;

namespace Fractal.Ai
{
	internal sealed record ToneDesignChatMessage(
		[property: JsonPropertyName("role")] string Role,
		[property: JsonPropertyName("content")] string Content);

	internal static class ToneDesignChatCompletionFlow
	{
		public static Task<ToneDesignChatResponse> ExecuteAsync(
			string provider,
			ToneDesignChatRequest request,
			ToneDesignEvidenceRetriever evidenceRetriever,
			ToneDesignProviderResilienceOptions resilience,
			Func<IReadOnlyList<ToneDesignChatMessage>, ToneDesignResponseSchema?, CancellationToken, Task<string>> sendAsync,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(request);

			if (string.IsNullOrWhiteSpace(request.UserPrompt))
			{
				throw new ArgumentException("A tone description is required.", nameof(request));
			}

			ValidateExportBoundary(request);
			ToneDesignDeviceConstraints device = ToneDesignDeviceConstraints.For(request.DeviceProfile?.Id);
			ToneDesignResponseSchema? schema = ToneDesignProposalJsonContract.ForRequest(device, request.AllowDynaCab);

			return ToneDesignProviderExecution.ExecuteAsync(
				provider,
				resilience,
				cancellationToken,
				async operationToken =>
				{
					request.Progress?.Report(new ToneDesignProgressUpdate(
						ToneDesignProgressStage.EvidenceRetrieval,
						"Retrieving and binding permitted evidence."));
					ToneDesignEvidencePromptContext evidenceContext =
						await evidenceRetriever.EnrichAsync(request, operationToken);
					request.ValidateFinalUserPrompt(evidenceContext.Prompt);
					List<ToneDesignChatMessage> messages =
					[
						new("system", ToneDesignSafetyPrompt.System + Environment.NewLine + ToneDesignModelGrounding.CreatePrompt() +
							Environment.NewLine + (request.Effects?.DescribeForPrompt() ?? NoEffects) + DeviceSection(device) +
							DynaCabSection(request, device)),
						new("user", evidenceContext.Prompt)
					];

					request.Progress?.Report(new ToneDesignProgressUpdate(
						ToneDesignProgressStage.InitialGeneration,
						$"Requesting the initial structured proposal from {provider}."));
					string proposal = await sendAsync(messages, schema, operationToken);

					for (int correctionAttempt = 0; ;)
					{
						if (TryParseProposal(
							proposal,
							evidenceContext,
							out ToneDesignProposal? parsed,
							out Exception? parseException))
						{
							parsed = ToneDesignTextLimits.Apply(parsed!);
							if (request.Refinement is ToneDesignRefinementContext refinement)
								parsed = ToneDesignProposalRefinementPolicy.RestoreProvenance(refinement.OriginalProposal, parsed!);
							List<string> adjustments = [];
							parsed = ToneDesignOfflineBuild.Repair(parsed!, request.Effects, evidenceContext.CitationBindings, adjustments);
							parsed = KeepDynaCabAllowed(parsed, request.AllowDynaCab, adjustments);
							ToneDesignProposalQualityEvaluation evaluation =
								ToneDesignProposalQualityEvaluator.Evaluate(
									parsed!,
									request.Sections,
									evidenceContext.CitationBindings,
									requireGroundedSelections: true,
									isDeviceSetting: request.Effects is { } catalog ? catalog.IsSetting : null,
									device: device);
							if (!evaluation.RequiresCorrection)
							{
								request.Progress?.Report(new ToneDesignProgressUpdate(
									ToneDesignProgressStage.ValidationExport,
									"Validating the proposal and preparing bounded export."));
								parsed = await WithDetailedSettingsAsync(parsed!, request, evidenceContext, adjustments, sendAsync, operationToken);
								parsed = ToneDesignProposalQualityEvaluator.LevelAmps(parsed, request.Effects, adjustments);
								// The detailed-settings step sees every Cab setting, Mode included; it must not switch the cab mode.
								parsed = KeepDynaCabAllowed(parsed, request.AllowDynaCab, adjustments);
								// Keep within the parser's 16-caution limit so the stored proposal still re-parses.
								IEnumerable<string> serverCautions = adjustments.Count == 0
									? [ToneDesignModelGrounding.DefaultsCaution]
									: [ToneDesignModelGrounding.DefaultsCaution, DescribeAdjustments(adjustments)];
								return new ToneDesignChatResponse(parsed! with
								{
									Cautions = parsed!.Cautions.Take(14).Concat(serverCautions)
										.Distinct(StringComparer.Ordinal).ToArray()
								});
							}

							if (correctionAttempt >= resilience.MaximumCorrectionAttempts)
							{
								throw new ToneDesignProviderException(
									ToneDesignProviderErrorKind.InvalidResponse,
									provider,
									$"The corrected {provider} tone-design proposal still fails quality evaluation: " +
									string.Join(" ", evaluation.Corrections));
							}

							proposal = await RequestCorrectionAsync(
								messages,
								proposal,
								ToneDesignProposalCorrectionPrompt.ForQuality(evaluation),
								++correctionAttempt,
								resilience.MaximumCorrectionAttempts,
								request,
								schema,
								sendAsync,
								operationToken);
							continue;
						}

						if (correctionAttempt >= resilience.MaximumCorrectionAttempts)
						{
							throw ToneDesignProviderExecution.InvalidResponse(
								provider,
								parseException!);
						}

						proposal = await RequestCorrectionAsync(
							messages,
							proposal,
							ToneDesignProposalCorrectionPrompt.ForParseFailure(parseException!),
							++correctionAttempt,
							resilience.MaximumCorrectionAttempts,
							request,
							schema,
							sendAsync,
							operationToken);
					}
				});
		}

		// The second step: every setting of the chosen blocks with the chosen models' FM9 values in view. A failed or
		// unusable reply keeps the rig step's proposal, which is already complete and valid.
		private static async Task<ToneDesignProposal> WithDetailedSettingsAsync(
			ToneDesignProposal proposal,
			ToneDesignChatRequest request,
			ToneDesignEvidencePromptContext evidenceContext,
			List<string> adjustments,
			Func<IReadOnlyList<ToneDesignChatMessage>, ToneDesignResponseSchema?, CancellationToken, Task<string>> sendAsync,
			CancellationToken cancellationToken)
		{
			if (request.Effects is not { } catalog || catalog.DescribeSettings(proposal) is not { Length: > 0 } settings)
				return proposal;
			request.Progress?.Report(new ToneDesignProgressUpdate(
				ToneDesignProgressStage.InitialGeneration, "Setting every control of the chosen models."));
			try
			{
				string reply = await sendAsync(
					[
						new("system", ToneDesignDetailedSettings.System),
						new("user", ToneDesignDetailedSettings.CreateUserPrompt(evidenceContext.Prompt, proposal, settings))
					],
					ToneDesignDetailedSettings.Schema,
					cancellationToken);
				IReadOnlyList<ToneDesignSettingChange> changes = ToneDesignDetailedSettings.Parse(reply);
				if (changes.Count == 0)
					return proposal;
				(ToneDesignProposal Proposal, List<string> Adjustments)? Detail(IReadOnlyList<ToneDesignSettingChange> applied)
				{
					List<string> detailAdjustments = [];
					ToneDesignProposal detailed = Fm9ProductionSafetyPolicy.Repair(
						ToneDesignProposalQualityEvaluator.KeepBlockLevels(ToneDesignProposalQualityEvaluator.RemoveRepeatedEq(
							ToneDesignProposalQualityEvaluator.ClampToDeviceRanges(
								ToneDesignDetailedSettings.Apply(proposal, applied, catalog, detailAdjustments), detailAdjustments),
							detailAdjustments), detailAdjustments),
						detailAdjustments);
					return ToneDesignProposalQualityEvaluator.Evaluate(
						detailed, request.Sections, evidenceContext.CitationBindings, requireGroundedSelections: true,
						isDeviceSetting: catalog.IsSetting, device: ToneDesignDeviceConstraints.For(request.DeviceProfile?.Id)).RequiresCorrection ? null : (detailed, detailAdjustments);
				}

				// One bad change must not cost the others: when the whole set fails, keep each change that passes
				// together with those kept before it.
				(ToneDesignProposal Proposal, List<string> Adjustments)? result = Detail(changes);
				if (result is null)
				{
					List<ToneDesignSettingChange> kept = [];
					foreach (ToneDesignSettingChange change in changes)
						if (Detail([.. kept, change]) is not null)
							kept.Add(change);
					adjustments.Add($"left out {changes.Count - kept.Count} detailed setting(s) that failed the checks");
					if (kept.Count == 0)
						return proposal;
					changes = kept;
					result = Detail(changes);
					if (result is null)
						return proposal;
				}
				(ToneDesignProposal detailed, List<string> detailAdjustments) = result.Value;
				adjustments.AddRange(detailAdjustments);
				return detailed with
				{
					Cautions = [.. detailed.Cautions.Take(13), ToneDesignTextLimits.Apply(detailed with { Cautions = [ToneDesignDetailedSettings.Describe(changes)] }).Cautions[0]]
				};
			}
			catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ToneDesignProviderException)
			{
				adjustments.Add("kept the rig step's settings because the detailed-settings step failed");
				return proposal;
			}
		}

		private const string NoEffects =
			"Effects and modifiers are not available for this request: emit empty effects and modifiers arrays and an empty effectStates array in every scene.";

		private static string DescribeAdjustments(IReadOnlyList<string> adjustments)
		{
			const int Listed = 5;
			string listed = string.Join("; ", adjustments.Take(Listed));
			return $"Adjusted by the production checks: {listed}" +
				(adjustments.Count > Listed ? $"; and {adjustments.Count - Listed} more." : ".");
		}

		private static async Task<string> RequestCorrectionAsync(
			List<ToneDesignChatMessage> messages,
			string proposal,
			string correctionPrompt,
			int attempt,
			int maximumAttempts,
			ToneDesignChatRequest request,
			ToneDesignResponseSchema? schema,
			Func<IReadOnlyList<ToneDesignChatMessage>, ToneDesignResponseSchema?, CancellationToken, Task<string>> sendAsync,
			CancellationToken cancellationToken)
		{
			request.Progress?.Report(new ToneDesignProgressUpdate(
				ToneDesignProgressStage.CorrectionAttempt,
				$"Requesting bounded contract-correction attempt {attempt} of {maximumAttempts}."));
			messages.Add(new ToneDesignChatMessage("assistant", proposal));
			messages.Add(new ToneDesignChatMessage("user", correctionPrompt));
			return await sendAsync(messages, schema, cancellationToken);
		}

		private static bool TryParseProposal(
			string proposal,
			ToneDesignEvidencePromptContext evidenceContext,
			out ToneDesignProposal? parsed,
			out Exception? exception)
		{
			try
			{
				parsed = evidenceContext.ResolveCitations(
					ToneDesignProposalParser.Parse(proposal));
				exception = null;
				return true;
			}
			catch (Exception caught) when (
				caught is JsonException or InvalidDataException or FormatException)
			{
				parsed = null;
				exception = caught;
				return false;
			}
		}

		// Only when the player ticked "Allow DynaCab": otherwise the system prompt is exactly what it was before DynaCab.
		private static string DynaCabSection(ToneDesignChatRequest request, ToneDesignDeviceConstraints device) =>
			request.AllowDynaCab ? Environment.NewLine + ToneDesignDynaCab.DescribeForPrompt(device.Device) : string.Empty;

		// Without the player's permission a Cab never plays DynaCab, whichever step asked for it; the factory cab stays.
		internal static ToneDesignProposal KeepDynaCabAllowed(ToneDesignProposal proposal, bool allowDynaCab, List<string> adjustments)
		{
			// Legacy-mode settings that share these names (Mute2 mutes a legacy IR slot too) are left alone.
			if (allowDynaCab || !proposal.Blocks.Any(block => ToneDesignDynaCab.IsCab(block) &&
				block.GetAuthoredChannels().Any(channel => ToneDesignDynaCab.IsDynaCab(block.GetParameters(channel)))))
				return proposal;
			adjustments.Add("kept the factory cab (DynaCab is not allowed for this preset)");
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block => !ToneDesignDynaCab.IsCab(block) ? block : block.MapChannels(channel =>
					ToneDesignDynaCab.IsDynaCab(channel.Parameters)
						? channel with { Parameters = ToneDesignDynaCab.WithoutDynaCab(channel.Parameters) }
						: channel)).ToArray()
			};
		}

		// Empty for the FM9, so its system prompt is exactly what it was before other devices.
		private static string DeviceSection(ToneDesignDeviceConstraints device) =>
			device.DescribeForPrompt() is { Length: > 0 } section ? Environment.NewLine + section : string.Empty;

		// The FM9's verified Ride boundary, or a gen-3 unit offered as a labelled beta (written in the same contract and
		// translated by its exporter); nothing else reaches a paid call.
		private static void ValidateExportBoundary(ToneDesignChatRequest request)
		{
			if (request.DeviceProfile is not null && !request.DeviceProfile.IsBeta &&
				(request.DeviceProfile.Id != FractalDeviceId.Fm9 ||
					!request.DeviceProfile.IsOutputEligible ||
					!request.DeviceProfile.VerifiedFirmwareProfiles.Contains(
						FractalDeviceProfileCatalog.Fm9Firmware12RideBaselineProfile,
						StringComparer.Ordinal)))
			{
				throw new ToneDesignChatRequestValidationException(
					"Tone-design generation is restricted to the verified FM9 firmware-12 Ride export boundary.");
			}
		}


	}

	internal static class ToneDesignProposalCorrectionPrompt
	{
		public static string ForQuality(ToneDesignProposalQualityEvaluation evaluation) =>
			ToneDesignProposalQualityEvaluator.CreateCorrectionPrompt(evaluation);

		private const int MaximumReportedErrorCharacters = 2_000;

		// The contract's own validation (including qualified-model checks) runs inside the parser, so the
		// specific error is what lets the model correct a well-formed but rejected proposal.
		public static string ForParseFailure(Exception error)
		{
			ArgumentNullException.ThrowIfNull(error);
			string detail = error.Message.Length <= MaximumReportedErrorCharacters
				? error.Message
				: error.Message[..MaximumReportedErrorCharacters];
			return
				$"""
				The previous response was rejected by the tone-design contract.
				Validation error: {detail}
				Fix exactly what the validation error describes. Return one corrected JSON object only: no Markdown, prose, code fences, comments, or trailing text.
				Use the exact six top-level properties summary, blocks, routes, scenes, cautions, and evidence.
				Preserve the fixed FM9 firmware-12 Ride block roster, positions, complete parameter fields, bounded values, scene state, evidence rules, and safety guidance from the system message.
				""";
		}
	}
}
