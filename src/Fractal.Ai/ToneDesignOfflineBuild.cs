using System.Text;
using Fractal.Application;

namespace Fractal.Ai
{
	public sealed record ToneDesignBuildResult(
		ToneDesignProposal Proposal,
		IReadOnlyList<string> Adjustments,
		IReadOnlyList<string> Corrections)
	{
		public bool Passed => Corrections.Count == 0;
	}

	// The site's generation without the model call: an agent writes the proposal JSON itself, and this runs exactly the
	// deterministic repairs and quality checks the site runs on a model's reply. Corrections are what the site would send
	// back to the model; an agent fixes them and builds again. No API key or evidence retrieval is involved, so gear choices
	// must be labelled inferred or substituted.
	public static class ToneDesignOfflineBuild
	{
		// What the site's model is told: the JSON contract and ranges, the FM9 model choices and the effect blocks.
		public static string Guide(IToneDesignEffectCatalog? effects)
		{
			StringBuilder guide = new();
			guide.AppendLine(ToneDesignSafetyPrompt.System);
			guide.AppendLine(ToneDesignModelGrounding.CreatePrompt());
			guide.AppendLine(effects?.DescribeForPrompt() ?? "Effects and modifiers are not available: emit empty effects and modifiers arrays.");
			return guide.ToString();
		}

		public static ToneDesignBuildResult Run(string json, IToneDesignEffectCatalog? effects)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(json);
			ToneDesignProposal parsed = ToneDesignTextLimits.Apply(ToneDesignProposalParser.Parse(json));
			List<string> adjustments = [];
			parsed = Repair(parsed, effects, [], adjustments);
			ToneDesignProposalQualityEvaluation evaluation = ToneDesignProposalQualityEvaluator.Evaluate(
				parsed, [], [], requireGroundedSelections: true, isDeviceSetting: effects is null ? null : effects.IsSetting);
			if (evaluation.RequiresCorrection)
				return new(parsed, adjustments, evaluation.Corrections);
			parsed = ToneDesignProposalQualityEvaluator.LevelAmps(parsed, effects, adjustments);
			return new(parsed, adjustments, []);
		}

		// The deterministic repairs, in the order the site applies them to every reply: effects and modifiers the FM9 can
		// hold, Ride routes, unverifiable evidence downgraded, label substitutions, device ranges, repeated EQ, block levels and
		// the production safety policy.
		public static ToneDesignProposal Repair(ToneDesignProposal proposal, IToneDesignEffectCatalog? effects,
			IReadOnlyList<ToneDesignEvidenceCitationBinding> citations, List<string> adjustments)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ArgumentNullException.ThrowIfNull(adjustments);
			ToneDesignProposal repaired = ToneDesignProposalQualityEvaluator.RepairEffects(proposal, effects, adjustments);
			repaired = ToneDesignProposalQualityEvaluator.RepairModifiers(repaired, effects, adjustments);
			repaired = ToneDesignProposalQualityEvaluator.WithRideRoutes(repaired);
			repaired = ToneDesignProposalQualityEvaluator.DowngradeUnverifiableEvidence(repaired, citations);
			repaired = ToneDesignProposalQualityEvaluator.LabelSubstitutions(repaired);
			repaired = ToneDesignProposalQualityEvaluator.ClampToDeviceRanges(repaired, adjustments);
			repaired = ToneDesignProposalQualityEvaluator.RemoveRepeatedEq(repaired, adjustments);
			repaired = ToneDesignProposalQualityEvaluator.KeepBlockLevels(repaired, adjustments);
			return Fm9ProductionSafetyPolicy.Repair(repaired, adjustments);
		}
	}
}
