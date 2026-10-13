using Fractal.Ai;
using Fractal.Application;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;
using Fractal.Fm9;

namespace Fractone.Mcp
{
	public sealed record Fm9BuildResult(
		bool Passed,
		IReadOnlyList<string> Corrections,
		IReadOnlyList<string> Adjustments,
		string? Output,
		string? PresetName,
		Fm9PresetSummary? Summary,
		IReadOnlyList<string> Cautions);

	// The tone designer without a model: the agent writes the proposal from the guide, this runs the site's repairs and
	// checks and exports the FM9 firmware-12 Ride baseline preset exactly as the site's download does.
	public static class Fm9ToneDesigner
	{
		private static readonly Lazy<Fm9PresetDump> Template = new(() =>
		{
			using Stream stream = typeof(Fm9ToneDesigner).Assembly.GetManifestResourceStream("Fractone.Mcp.ride-baseline.syx")
				?? throw new InvalidOperationException("The Ride baseline template is missing from this build.");
			using MemoryStream copy = new();
			stream.CopyTo(copy);
			return Fm9PresetDump.Parse(copy.ToArray());
		});

		private static readonly IToneDesignEffectCatalog Effects = new Fm9ToneDesignEffectCatalog();

		// The contract, the FM9 model choices, the effect blocks and the model notes: what the site's model is told.
		public static string Guide() => ToneDesignOfflineBuild.Guide(Effects) + Environment.NewLine + OfflineNote;

		// The site runs a second model step that sets every other control of the chosen models; the offline build does not.
		private const string OfflineNote =
			"Offline build: the site's second step, which sets every other control of the chosen models from their FM9 values and " +
			"notes, does not run here, so the settings lists need not stay empty. Controls the contract lists go in parameters; any " +
			"other control that matters for the tone goes in that block's settings list as {name, value}, named exactly as " +
			"list_block_settings gives it (choices by name). Unknown names come back as corrections. Controls you leave out keep " +
			"the model's own FM9 voicing.";

		// The proposal JSON (text, or a path to a .json file) through the checks; when it passes, exported to output.
		public static Fm9BuildResult Build(string proposalJson, string output, string? name)
		{
			ToneDesignBuildResult result = ToneDesignOfflineBuild.Run(proposalJson, Effects);
			if (!result.Passed)
				return new(false, result.Corrections, result.Adjustments, null, null, null, result.Proposal.Cautions);
			ToneDesignProposal proposal = result.Proposal;
			string presetName = Fm9PresetName.Normalize(name) ?? Fm9PresetName.Normalize(proposal.PresetName) ?? "FM9 Tone Design";
			Fm9PresetDump exported = Fm9ProposalPresetExporter.Apply(Template.Value, proposal, presetName);
			byte[] bytes = exported.ToArray();
			Fm9Files.WriteAtomically(output, bytes);
			return new(true, [], result.Adjustments, Path.GetFullPath(output), exported.PresetName,
				Fm9Presets.Describe(Fm9UploadedPreset.Read(exported)), proposal.Cautions);
		}
	}
}
