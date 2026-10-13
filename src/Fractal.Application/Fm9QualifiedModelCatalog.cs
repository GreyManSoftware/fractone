using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fractal.Application
{
	public sealed record Fm9QualifiedModelChoice(
		string BlockType,
		string ParameterName,
		int Ordinal,
		string Name,
		int? Bank,
		IReadOnlyList<int> ChangedParameterIds);

	public static class Fm9QualifiedModelCatalog
	{
		// FM9 firmware 12.00 has 336 Amp models (0-335), every one hardware-confirmed in the type sweep.
		public const int MaximumAmpModel = 335;

		private static readonly Lazy<IReadOnlyList<Fm9QualifiedModelChoice>> QualifiedChoices = new(Load);
		public static IReadOnlyList<Fm9QualifiedModelChoice> Choices => QualifiedChoices.Value;

		public static string? SelectorParameter(string blockType) => blockType.ToUpperInvariant() switch
		{
			"AMP" => "Amp Model",
			"DRIVE" => "Drive Model",
			"CAB" => "Cab Type",
			_ => null
		};

		public static Fm9QualifiedModelChoice Resolve(string blockType, string name, int? bank = null)
		{
			return Choices.SingleOrDefault(choice =>
				choice.BlockType.Equals(blockType, StringComparison.OrdinalIgnoreCase) &&
				choice.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
				choice.Bank == bank)
				?? throw new InvalidDataException($"Unsupported qualified {blockType} selection '{name}'.");
		}

		public static Fm9QualifiedModelChoice? Find(string blockType, int ordinal, int? bank = null) =>
			Choices.SingleOrDefault(choice =>
				choice.BlockType.Equals(blockType, StringComparison.OrdinalIgnoreCase) &&
				choice.Ordinal == ordinal && choice.Bank == bank);

		public static ToneDesignBlockProposal Normalize(ToneDesignBlockProposal block)
		{
			ArgumentNullException.ThrowIfNull(block);
			return block.MapChannels(NormalizeChannel);
		}

		private static ToneDesignBlockProposal NormalizeChannel(ToneDesignBlockProposal block)
		{
			string? parameter = SelectorParameter(block.BlockType);
			if (parameter is null || !block.Parameters.TryGetValue(parameter, out string? value))
				return block;
			if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
				return block;
			int? bank = null;
			if (block.BlockType.Equals("Cab", StringComparison.OrdinalIgnoreCase))
			{
				if (!block.Parameters.TryGetValue("Bank", out string? bankText) ||
					!int.TryParse(bankText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBank))
					throw new InvalidDataException("Cab selection requires an explicit integer Bank.");
				bank = parsedBank;
			}
			Fm9QualifiedModelChoice choice = Resolve(block.BlockType, value, bank);
			Dictionary<string, string> parameters = new(block.Parameters, StringComparer.OrdinalIgnoreCase)
			{
				[parameter] = choice.Ordinal.ToString(CultureInfo.InvariantCulture)
			};
			return block with { Parameters = parameters };
		}

		// A model names gear far more reliably than it recalls catalogue ordinals (the original Amp 205 defect paired the
		// right name with the wrong number), so an exact qualified name decides the selector and, for a Cab, its bank.
		public static ToneDesignBlockProposal AlignSelectorToName(ToneDesignBlockProposal block)
		{
			ArgumentNullException.ThrowIfNull(block);
			return block.MapChannels(AlignChannelSelectorToName);
		}

		private static ToneDesignBlockProposal AlignChannelSelectorToName(ToneDesignBlockProposal block)
		{
			string? parameter = SelectorParameter(block.BlockType);
			if (parameter is null || block.ModelSelection is not ToneDesignModelSelection selection)
				return block;
			// Models sometimes copy the prompt's list format ("21 = USA MK IIC+", "bank 1, ordinal 587 = ..."); keep the name.
			if (Regex.Match(selection.Name, @"^\s*(?:bank\s*\d+\s*,\s*ordinal\s*)?\d+\s*=\s*(?<name>\S.*?)\s*$",
				RegexOptions.IgnoreCase) is { Success: true } listed)
			{
				selection = selection with { Name = listed.Groups["name"].Value };
				block = block with { ModelSelection = selection };
			}
			// Small models often put the selector in the name field ("28", or "1:452" as bank:type for a Cab); when it
			// equals the authored selector it identifies the same model.
			string numericName = selection.Name.Trim();
			if (block.BlockType.Equals("Cab", StringComparison.OrdinalIgnoreCase) &&
				Regex.Match(numericName, @"^(\d+)\s*[:/]\s*(\d+)$") is { Success: true } bankAndType)
			{
				if (!block.Parameters.TryGetValue("Bank", out string? authoredBank) ||
					authoredBank.Trim() != bankAndType.Groups[1].Value)
					return block;
				numericName = bankAndType.Groups[2].Value;
			}
			if (int.TryParse(numericName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int namedOrdinal) &&
				block.Parameters.TryGetValue(parameter, out string? selectorText) &&
				int.TryParse(selectorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selectorOrdinal) &&
				namedOrdinal == selectorOrdinal)
			{
				int? selectorBank = block.Parameters.TryGetValue("Bank", out string? bankValue) &&
					int.TryParse(bankValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBank)
					? parsedBank
					: null;
				Fm9QualifiedModelChoice? numbered = Find(block.BlockType, selectorOrdinal,
					block.BlockType.Equals("Cab", StringComparison.OrdinalIgnoreCase) ? selectorBank : null);
				return numbered is null ? block : block with { ModelSelection = selection with { Name = numbered.Name } };
			}
			Fm9QualifiedModelChoice[] named = Choices.Where(choice =>
				choice.BlockType.Equals(block.BlockType, StringComparison.OrdinalIgnoreCase) &&
				choice.Name.Equals(selection.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
			// A few Cab names repeat across banks; the model's own Bank breaks the tie.
			if (named.Length > 1 && block.Parameters.TryGetValue("Bank", out string? bankText) &&
				int.TryParse(bankText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requestedBank))
				named = named.Where(choice => choice.Bank == requestedBank).ToArray();
			if (named.Length != 1)
				return block;
			Dictionary<string, string> parameters = new(block.Parameters, StringComparer.OrdinalIgnoreCase)
			{
				[parameter] = named[0].Ordinal.ToString(CultureInfo.InvariantCulture)
			};
			if (named[0].Bank is int bank)
				parameters["Bank"] = bank.ToString(CultureInfo.InvariantCulture);
			return block with { Parameters = parameters, ModelSelection = selection with { Name = named[0].Name } };
		}

		public static IReadOnlyList<string> Evaluate(ToneDesignProposal proposal)
		{
			List<string> corrections = [];
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				if (SelectorParameter(block.BlockType) is null)
					continue;
				foreach (string channel in block.GetAuthoredChannels())
					EvaluateChannel(proposal, block, channel, corrections);
				// An unauthored channel plays the template's model, which no named selection describes.
				if (block.GetAuthoredChannels().Any(channel => block.GetModelSelection(channel) is not null))
				{
					foreach (ToneDesignSceneProposal scene in proposal.Scenes)
					{
						if (ToneDesignSceneChannels.Find(scene, block.BlockType, block.Instance) is { IsBypassed: false } setting &&
							!block.HasChannel(setting.Channel))
							corrections.Add($"Scene '{scene.Name}' enables {block.BlockType} on channel {setting.Channel}, which is not authored; " +
								$"author channel {setting.Channel} with that scene's own settings, or play an authored channel ({string.Join(", ", block.GetAuthoredChannels())}) only when that scene really sounds the same.");
					}
				}
			}
			return corrections;
		}

		private static void EvaluateChannel(
			ToneDesignProposal proposal,
			ToneDesignBlockProposal owner,
			string channel,
			List<string> corrections)
		{
			ToneDesignBlockProposal block = owner.ForChannel(channel);
			string label = ToneDesignChannels.Label(block.BlockType, channel);
			string selector = SelectorParameter(block.BlockType)!;
			if (!block.Parameters.TryGetValue(selector, out string? value))
			{
				if (block.ModelSelection is not null)
					corrections.Add($"Explicitly author {label} {selector}; a named model cannot inherit its selector.");
				return;
			}
			int? bank = null;
			if (block.BlockType.Equals("Cab", StringComparison.OrdinalIgnoreCase) &&
				block.Parameters.TryGetValue("Bank", out string? bankText) && int.TryParse(bankText, out int parsedBank))
				bank = parsedBank;
			if (block.ModelSelection is not null &&
				block.BlockType.Equals("Cab", StringComparison.OrdinalIgnoreCase) && bank is null)
				corrections.Add("A named Cab selection requires its explicit Bank.");
			if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal) ||
				ordinal < 0 || ordinal > (block.BlockType.Equals("Amp", StringComparison.OrdinalIgnoreCase) ? MaximumAmpModel :
					block.BlockType.Equals("Drive", StringComparison.OrdinalIgnoreCase) ? 86 : 1023))
			{
				corrections.Add($"Use a supported integer {label} {selector}.");
				return;
			}
			Fm9QualifiedModelChoice? choice = Find(block.BlockType, ordinal, bank);
			if (block.ModelSelection is ToneDesignModelSelection selection &&
				(choice is null || !choice.Name.Equals(selection.Name, StringComparison.OrdinalIgnoreCase)))
				corrections.Add(DescribeSelectionMismatch(label, block.BlockType, selector, selection.Name, ordinal, bank, choice));
			if (block.ModelSelection is null)
				return;
			string identity = choice?.Name ?? string.Empty;
			if (block.BlockType.Equals("Amp", StringComparison.OrdinalIgnoreCase) &&
				identity.Contains("Clean", StringComparison.OrdinalIgnoreCase) &&
				!HasNonCleanChannel(owner) &&
				HasCleanAmpContradiction(proposal, owner, channel))
				corrections.Add($"{label} {ordinal} is {identity}, not a high-gain amp; correct the model or rationale.");
			if (block.BlockType.Equals("Cab", StringComparison.OrdinalIgnoreCase))
			{
				identity = choice?.Name ?? IdentifyCommunityCab(bank, ordinal);
				if (identity.Contains("Deluxe Tweed", StringComparison.OrdinalIgnoreCase) &&
					block.Rationale.Contains("Marshall", StringComparison.OrdinalIgnoreCase))
					corrections.Add($"Cab bank {bank}/{ordinal} identifies {identity}, not a Marshall Cab.");
			}
		}

		// The block rationale describes every channel, so a high-gain description belongs to another channel's amp when one
		// exists (a clean channel A beside a high-gain channel B is the ordinary two-amp preset).
		private static bool HasNonCleanChannel(ToneDesignBlockProposal amp) =>
			amp.GetAuthoredChannels().Any(channel =>
				amp.GetParameters(channel).TryGetValue("Amp Model", out string? text) &&
				int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal) &&
				Find("Amp", ordinal) is { } choice &&
				!choice.Name.Contains("Clean", StringComparison.OrdinalIgnoreCase));

		// The correction must say what is valid; a bare "must match" left the model repeating the same invented name.
		private static string DescribeSelectionMismatch(
			string label,
			string blockType,
			string selector,
			string name,
			int ordinal,
			int? bank,
			Fm9QualifiedModelChoice? choice)
		{
			string requested = bank.HasValue ? $"Bank {bank} / {selector} {ordinal}" : $"{selector} {ordinal}";
			string actual = choice is null ? $"{requested} is not qualified" : $"{requested} is '{choice.Name}'";
			bool knownName = Choices.Any(candidate =>
				candidate.BlockType.Equals(blockType, StringComparison.OrdinalIgnoreCase) &&
				candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
			if (knownName)
				return $"The {label} name must match its qualified selector: {actual}, not '{name}'. " +
					$"Use one exact name with its own selector.";
			if (!blockType.Equals("Cab", StringComparison.OrdinalIgnoreCase))
				return $"The {label} name must match its qualified selector: '{name}' is not a listed {blockType} model " +
					$"({actual}). Use an exact {blockType} name from the system message list; its selector follows from the name.";
			string cabs = string.Join("; ", ClosestCabs(name, 12)
				.Select(candidate => $"'{candidate.Name}' (Bank {candidate.Bank}, Cab Type {candidate.Ordinal})"));
			return $"The Cab name must match its qualified selector and bank: '{name}' is not an FM9 factory Cab name " +
				$"({actual}). Use one exact roster name, for example the closest matches: {cabs}.";
		}

		private static bool HasCleanAmpContradiction(ToneDesignProposal proposal, ToneDesignBlockProposal amp, string channel)
		{
			foreach (string sentence in Regex.Split(amp.Rationale, @"[.!?;]"))
			{
				string assertion = Regex.Replace(sentence,
					@"\b(?:not (?:a |an )?|without |rather than (?:a |an )?)(?:high[- ]gain(?: (?:amp(?:lifier)?|preamp|head))?|saturated amp)\b",
					string.Empty, RegexOptions.IgnoreCase);
				if (!Regex.IsMatch(assertion, @"\b(?:high[- ]gain|saturated amp)\b", RegexOptions.IgnoreCase))
					continue;
				if (Regex.IsMatch(assertion, @"\b(?:high[- ]gain (?:amp(?:lifier)?|preamp|head)|saturated amp)\b", RegexOptions.IgnoreCase))
					return true;
				bool externalAttribution = Regex.IsMatch(assertion,
					@"\bhigh[- ]gain\b.{0,60}\b(?:from|provided by|supplied by|produced by|created by)\s+(?:the\s+)?(?:external\s+)?(?:Drive|pedal)\b|\b(?:Drive|pedal)\s+(?:block\s+)?(?:provides|supplies|produces|creates)\b.{0,60}\bhigh[- ]gain\b",
					RegexOptions.IgnoreCase);
				if (!externalAttribution || !HasExplicitlyEnabledDrive(proposal, amp, channel))
					return true;
			}
			return false;
		}

		// Every enabled scene that plays this Amp channel must also enable a Drive channel with positive Drive and Mix.
		private static bool HasExplicitlyEnabledDrive(ToneDesignProposal proposal, ToneDesignBlockProposal amp, string channel)
		{
			ToneDesignBlockProposal? drive = proposal.Blocks.FirstOrDefault(block =>
				block.BlockType.Equals("Drive", StringComparison.OrdinalIgnoreCase) && block.Instance == 1);
			if (drive is null)
				return false;
			ToneDesignSceneProposal[] enabledAmpScenes = proposal.Scenes.Where(scene =>
				ToneDesignSceneChannels.Find(scene, "Amp", amp.Instance) is { IsBypassed: false } &&
				ToneDesignSceneChannels.Effective(scene, amp) == channel).ToArray();
			return enabledAmpScenes.Length > 0 && enabledAmpScenes.All(scene =>
				ToneDesignSceneChannels.Find(scene, "Drive", 1) is { IsBypassed: false } &&
				IsDriving(drive.GetParameters(ToneDesignSceneChannels.Effective(scene, drive))));
		}

		private static bool IsDriving(IReadOnlyDictionary<string, string> drive) =>
			drive.TryGetValue("Drive", out string? driveText) &&
			drive.TryGetValue("Mix", out string? mixText) &&
			double.TryParse(driveText, NumberStyles.Float, CultureInfo.InvariantCulture, out double gain) &&
			double.TryParse(mixText, NumberStyles.Float, CultureInfo.InvariantCulture, out double mix) &&
			double.IsFinite(gain) && double.IsFinite(mix) && gain > 0 && mix > 0;

		private static string IdentifyCommunityCab(int? bank, int ordinal)
		{
			if (bank is null) return string.Empty;
			using JsonDocument document = Read("fm9-community.json");
			JsonElement root = document.RootElement;
			JsonElement enums = root.GetProperty("data");
			if (enums.TryGetProperty("FM9_CAB_ROSTERS_BY_BANK", out JsonElement rosters) &&
				rosters.TryGetProperty(bank.Value.ToString(CultureInfo.InvariantCulture), out JsonElement roster) &&
				roster.TryGetProperty(ordinal.ToString(CultureInfo.InvariantCulture), out JsonElement name))
				return name.GetString() ?? string.Empty;
			return string.Empty;
		}

		private static IReadOnlyList<Fm9QualifiedModelChoice> Load()
		{
			List<Fm9QualifiedModelChoice> choices = [];
			using JsonDocument types = Read("fm9-types.json");
			if (types.RootElement.GetProperty("firmwareVersion").GetString() != "12.00")
				throw new InvalidDataException("Unsupported model catalogue firmware.");
			foreach (JsonElement selector in types.RootElement.GetProperty("selectors").EnumerateArray())
			{
				string block = selector.GetProperty("blockType").GetString()!;
				string parameter = selector.GetProperty("parameterName").GetString()!;
				if (parameter is not ("DISTORT_TYPE" or "FUZZ_TYPE")) continue;
				foreach (JsonElement result in selector.GetProperty("results").EnumerateArray())
				{
					int ordinal = result.GetProperty("ordinal").GetInt32();
					if (block == "Amp" && ordinal > MaximumAmpModel) continue;
					if (result.GetProperty("classification").GetString() != "HardwareConfirmed" ||
						!result.GetProperty("baselineRestored").GetBoolean())
						throw new InvalidDataException("Unqualified model catalogue entry.");
					choices.Add(new(block, SelectorParameter(block)!, ordinal,
						result.GetProperty("renderedName").GetString()!, null,
						result.GetProperty("changedParameterIds").EnumerateArray().Select(value => value.GetInt32()).ToArray()));
				}
			}
			using JsonDocument cabs = Read("fm9-cabs.json");
			foreach (JsonElement anchor in cabs.RootElement.GetProperty("anchors").EnumerateArray())
			{
				if (!anchor.GetProperty("baselineRestored").GetBoolean() ||
					!anchor.GetProperty("fullPresetBaselineRestored").GetBoolean())
					throw new InvalidDataException("Unqualified Cab anchor.");
				choices.Add(new("Cab", "Cab Type", anchor.GetProperty("ordinal").GetInt32(),
					anchor.GetProperty("renderedName").GetString()!, anchor.GetProperty("bank").GetInt32(), []));
			}
			// The firmware Cab rosters (Factory 1, Factory 2, Legacy) come from FM9-Edit's cab manager, so every entry is a
			// real, portable choice; the nine anchors above are only the live-probed subset.
			using JsonDocument community = Read("fm9-community.json");
			JsonElement rosters = community.RootElement.GetProperty("data").GetProperty("FM9_CAB_ROSTERS_BY_BANK");
			foreach (int bank in FirmwareCabBanks)
			{
				foreach (JsonProperty entry in rosters.GetProperty(bank.ToString(CultureInfo.InvariantCulture)).EnumerateObject())
				{
					int ordinal = int.Parse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture);
					if (!choices.Any(choice => choice.BlockType == "Cab" && choice.Bank == bank && choice.Ordinal == ordinal))
						choices.Add(new("Cab", "Cab Type", ordinal, entry.Value.GetString()!, bank, []));
				}
			}
			return choices.AsReadOnly();
		}

		private static readonly int[] FirmwareCabBanks = [0, 1, 3];

		// One representative per cabinet family (its first 57-miked entry) from Factory 1 and 2: short enough for the prompt,
		// broad enough that the model picks a real cabinet instead of guessing names. Legacy duplicates these families.
		public static IReadOnlyList<Fm9QualifiedModelChoice> CabMenu { get; } = Choices
			.Where(choice => choice.BlockType == "Cab" && choice.Bank is 0 or 1)
			.OrderBy(choice => choice.Bank).ThenBy(choice => choice.Ordinal)
			.Select(choice => (choice, family: Regex.Match(choice.Name, "^(.*?) 57( |$)")))
			.Where(entry => entry.family.Success)
			.DistinctBy(entry => entry.family.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
			.Select(entry => entry.choice)
			.OrderBy(choice => choice.Name, StringComparer.Ordinal)
			.ToArray();

		// Character-trigram similarity tolerates the model's near misses ("Recto2 Orange" vs "4x12 Recto 57 A").
		public static IReadOnlyList<Fm9QualifiedModelChoice> ClosestCabs(string name, int count)
		{
			HashSet<string> wanted = Trigrams(name);
			return Choices
				.Where(choice => choice.BlockType == "Cab")
				.Select(choice =>
				{
					HashSet<string> candidate = Trigrams(choice.Name);
					int shared = candidate.Count(wanted.Contains);
					return (choice, score: shared == 0 ? 0 : (double)shared / (wanted.Count + candidate.Count - shared));
				})
				.OrderByDescending(scored => scored.score)
				.ThenBy(scored => scored.choice.Bank)
				.ThenBy(scored => scored.choice.Ordinal)
				.Take(count)
				.Select(scored => scored.choice)
				.ToArray();
		}

		private static HashSet<string> Trigrams(string text)
		{
			string normalized = $"  {Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim()}  ";
			HashSet<string> grams = [];
			for (int index = 0; index + 3 <= normalized.Length; index++)
				grams.Add(normalized.Substring(index, 3));
			return grams;
		}

		private static JsonDocument Read(string resource)
		{
			using Stream stream = typeof(Fm9QualifiedModelCatalog).Assembly.GetManifestResourceStream(
				$"Fractal.Application.{resource}") ?? throw new InvalidOperationException("Model catalogue unavailable.");
			return JsonDocument.Parse(stream);
		}
	}
}
