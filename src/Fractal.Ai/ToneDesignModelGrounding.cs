using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Fractal.Application;

namespace Fractal.Ai
{
	internal static class ToneDesignModelGrounding
	{
		public const string DefaultsCaution =
			"Each model loads its own FM9 voicing, as selecting it on the FM9 does. Audition with your rig.";

		public static string CreatePrompt()
		{
			StringBuilder prompt = new();
			prompt.AppendLine("Firmware-qualified FM9 12.00 named selector choices (identity only, not recording evidence or model-default qualification):");
			// The full Cab roster (over 2,000 factory names) is too large to list; a menu of one per family is listed below.
			foreach (IGrouping<string, Fm9QualifiedModelChoice> group in Fm9QualifiedModelCatalog.Choices
				.Where(choice => choice.BlockType != "Cab").GroupBy(choice => choice.BlockType))
			{
				prompt.AppendLine($"{group.Key}:");
				foreach (Fm9QualifiedModelChoice choice in group)
					prompt.AppendLine(choice.Bank is int bank
						? $"bank {bank}, ordinal {choice.Ordinal} = {choice.Name}"
						: $"{choice.Ordinal} = {choice.Name}");
			}
			prompt.AppendLine("For Amp, Drive and Cab emit modelSelection {name,equipment,evidenceBasis,sourceUrl,adaptation}. Name must exactly identify the selected ordinal, and Cab bank. EvidenceBasis is recording, live or inferred.");
			prompt.AppendLine("Start with actual equipment supported by the supplied excerpts, distinguishing recording from live. Then choose a representative named FM9 model; when no evidence exists, artist/track alone is sufficient: use an explicitly uncertain inferred tonal assumption, empty sourceUrl, and low-confidence evidence. Do not ask for extra gear details merely because research is unavailable.");
			prompt.AppendLine("Equipment is the verbatim equipment name in the bound excerpt for recording/live bases; do not claim the FM9 substitution itself was used historically. All FM9 selections and compensation are adaptations, never documented rig facts.");
			prompt.AppendLine("Adaptation must explain the representative model choice and compensation for modelling, requested instrument/pickups and listening setup; unspecified listening setups remain assumptions for audition, not facts.");
			prompt.AppendLine("Cab: choose one name exactly as written from this factory menu (one representative per cabinet family; the name alone sets Bank and Cab Type, so just copy it), or a factory name given in the model notes. Pick the family closest to the source cabinet and speakers, e.g. a Mesa-style 4x12 for a Mesa 4x12 rig and a Marshall-style 4x12 for a Marshall one. Do not invent, number or describe names, and do not infer IR response or microphone placement from a name.");
			prompt.AppendLine(string.Join("; ", Fm9QualifiedModelCatalog.CabMenu.Select(choice => choice.Name)));
			prompt.AppendLine("Channels: a block's own parameters and modelSelection are channel A. Amp may add channels B-D, each a complete separate amp: \"channels\":[{\"channel\":\"B\",\"parameters\":{all Amp parameters},\"modelSelection\":{...}}]; otherwise \"channels\":[]. Scenes choose each block's channel and bypass, e.g. a clean verse and heavy chorus: Amp A Jazz 120 clean, Amp B USA MK IIC+; the clean scene plays Amp A with Drive bypassed, the rhythm scene Amp B. Add a channel only when a scene plays it; every other block uses channel A in every scene. When scenes stand for different songs or song sections, give each its own Amp channel with the differences that part's recorded tone has (gain, mids, presence, EQ), however subtle; share a channel only when you believe the parts really sound the same, and say so in the summary.");
			prompt.AppendLine(DefaultsCaution);
			prompt.AppendLine("Export loads each selected model's own FM9 voicing (tone stack, bias, clipping...), exactly as selecting it on the FM9 does, so choose the model of the hardware and set only the listed controls. Describe this to the user only as: " + DefaultsCaution);
			prompt.AppendLine("Do not request or claim unsupported model-specific default restoration. All continuous fields are explicit overrides, not the selected model's original defaults. Do not describe a Clean-named amp as a high-gain preamp.");
			prompt.AppendLine("A clean amp cannot itself be described as a high-gain amp/preamp. For a high-gain overall tone through a clean amp, explicitly attribute that high-gain tone to Drive in the same sentence, author positive Drive and Mix, and explicitly enable Drive in every scene playing that amp channel; mentioning a pedal or an unrelated negation is not attribution.");
			return prompt.ToString();
		}

		public static void Evaluate(
			ToneDesignProposal proposal,
			IReadOnlyList<ToneDesignEvidenceCitationBinding>? bindings,
			bool required,
			List<string> corrections)
		{
			// Effects are inferred additions checked against the device dictionary; grounding covers the Ride chain's models.
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block => !ToneDesignEffects.IsEffect(block)))
			{
				if (Fm9QualifiedModelCatalog.SelectorParameter(block.BlockType) is null)
					continue;
				foreach (string channel in block.GetAuthoredChannels())
					EvaluateChannel(block.ForChannel(channel), ToneDesignChannels.Label(block.BlockType, channel), bindings, required, corrections);
			}
			if (required)
			{
				ToneDesignModelSelection[] selections = proposal.Blocks
					.SelectMany(block => block.GetAuthoredChannels().Select(block.GetModelSelection))
					.OfType<ToneDesignModelSelection>()
					.ToArray();
				foreach (ToneDesignEvidenceConclusion conclusion in proposal.Evidence.Where(item => item.Classification == "documented"))
				{
					if (!selections.Any(selection =>
						selection.EvidenceBasis is "recording" or "live" &&
						selection.SourceUrl == conclusion.SourceUrl &&
						conclusion.Claim.Contains(selection.Equipment, StringComparison.OrdinalIgnoreCase)))
						corrections.Add("Documented rig conclusions must identify equipment supported by a bound recording/live selection; label unsupported conclusions inferred or substituted.");
					if (Regex.IsMatch(conclusion.Claim, @"\b(record(?:ed|ing)?|studio|album|session)\b", RegexOptions.IgnoreCase) &&
						!selections.Any(selection =>
							selection.EvidenceBasis == "recording" && selection.SourceUrl == conclusion.SourceUrl))
						corrections.Add("Live-rig evidence cannot document recording-equipment claims.");
				}
			}
		}

		private static void EvaluateChannel(
			ToneDesignBlockProposal block,
			string label,
			IReadOnlyList<ToneDesignEvidenceCitationBinding>? bindings,
			bool required,
			List<string> corrections)
		{
			ToneDesignModelSelection? selection = block.ModelSelection;
			if (selection is null)
			{
				if (required)
					corrections.Add($"Add a qualified named modelSelection and explicit selector for {label}.");
				return;
			}
			string selector = Fm9QualifiedModelCatalog.SelectorParameter(block.BlockType)!;
			if (required && !block.Parameters.ContainsKey(selector))
				corrections.Add($"Explicitly author {label} {selector}; never inherit a model from the template.");
			if (string.IsNullOrWhiteSpace(selection.Name) || string.IsNullOrWhiteSpace(selection.Equipment) ||
				string.IsNullOrWhiteSpace(selection.Adaptation))
				corrections.Add($"Explain {label} equipment uncertainty and modelling/listening compensation.");
			if (selection.EvidenceBasis is not ("recording" or "live" or "inferred"))
				corrections.Add($"Use recording, live or inferred evidenceBasis for {label}.");
			if (selection.EvidenceBasis == "inferred")
			{
				if (!string.IsNullOrWhiteSpace(selection.SourceUrl))
					corrections.Add($"Keep unsupported {label} equipment inference uncited; use recording/live only with bound evidence.");
				if (Regex.IsMatch(block.Rationale,
					@"\b(?:recorded (?:with|through|using)|(?:recording|studio|album) (?:rig|equipment) (?:was|is|used)|documented (?:rig|equipment|use))\b",
					RegexOptions.IgnoreCase))
					corrections.Add($"Do not state actual {label} recording equipment in an inferred selection.");
				return;
			}
			if (!required)
				return;
			ToneDesignEvidenceCitationBinding? binding = bindings?.FirstOrDefault(candidate =>
				candidate.SourceUrl.Equals(selection.SourceUrl, StringComparison.Ordinal));
			// The model saw the excerpt entity-encoded with line breaks as spaces, so compare in that same form.
			string equipment = WebUtility.HtmlDecode(selection.Equipment ?? string.Empty);
			if (binding is null || string.IsNullOrWhiteSpace(equipment) ||
				!binding.Excerpt.ReplaceLineEndings(" ").Contains(equipment, StringComparison.OrdinalIgnoreCase))
			{
				corrections.Add($"The {label} equipment claim is not supported by its bound excerpt; use inferred with empty sourceUrl.");
				return;
			}
			if (selection.EvidenceBasis == "recording" &&
				!Regex.IsMatch(binding.Excerpt, @"\b(record(?:ed|ing)?|studio|album|session)\b", RegexOptions.IgnoreCase))
				corrections.Add($"Do not promote {label} live-rig evidence to recording equipment; use live or inferred.");
			IReadOnlySet<string> equipmentRoles = GetExplicitEquipmentRoles(binding.Excerpt, equipment);
			if (equipmentRoles.Count > 0 && !equipmentRoles.Contains(block.BlockType))
				corrections.Add($"The excerpt identifies '{equipment}' as {string.Join("/", equipmentRoles)}, not {block.BlockType} equipment; use appropriate source equipment or an inferred substitution.");
		}

		private static IReadOnlySet<string> GetExplicitEquipmentRoles(string excerpt, string equipment)
		{
			HashSet<string> roles = new(StringComparer.OrdinalIgnoreCase);
			string rolePattern = @"(?:amplifier|amp|preamp|head|pedal|cabinet|cab)";
			string name = Regex.Escape(equipment);
			foreach (Match match in Regex.Matches(excerpt,
				$@"\b(?<role>{rolePattern})\s*[:=]\s*[""']?{name}(?!\w)|(?<!\w){name}[""']?\s+(?<role>{rolePattern})\b",
				RegexOptions.IgnoreCase))
			{
				string role = match.Groups["role"].Value.ToUpperInvariant() switch
				{
					"AMPLIFIER" or "AMP" or "PREAMP" or "HEAD" => "Amp",
					"PEDAL" => "Drive",
					"CABINET" or "CAB" => "Cab",
					_ => throw new InvalidOperationException("Unrecognized explicit equipment role.")
				};
				roles.Add(role);
			}
			return roles;
		}
	}
}
