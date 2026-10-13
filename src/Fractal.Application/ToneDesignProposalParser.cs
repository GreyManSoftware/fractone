using System.Text.Json;
using Fractal.Core;

namespace Fractal.Application
{
	public static class ToneDesignProposalParser
	{
		private const int MaximumBlocks = 16;
		private const int MaximumParametersPerBlock = 32;
		private const int MaximumCautions = 16;
		private const int MaximumRoutes = 24;
		private const int MaximumScenes = 8;
		private const int MaximumSceneBlockSettings = 8;
		private const int MaximumSceneSections = 16;
		private const int MaximumEvidenceConclusions = 32;
		private static readonly IReadOnlySet<string> CompleteRideBlockKeys =
			new string[]
			{
				"Input",
				"Gate/Expander",
				"Drive",
				"Amp",
				"Cab",
				"Parametric EQ",
				"Reverb",
				"Output"
			}
			.Select(blockType => CreateBlockKey(blockType, 1))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		private static readonly IReadOnlySet<string> FixedSceneBlockKeys =
			new[] { "Input", "Output" }
			.Select(blockType => CreateBlockKey(blockType, 1))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		public static ToneDesignProposal Parse(string response)
		{
			if (string.IsNullOrWhiteSpace(response))
			{
				throw new InvalidDataException("The tone-design response is empty.");
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(response);
				JsonElement root = document.RootElement;
				string summary = ReadRequiredString(root, "summary");
				JsonElement blocksElement = GetRequiredArray(root, "blocks");
				JsonElement cautionsElement = GetRequiredArray(root, "cautions");
				IReadOnlyList<ToneDesignRoute> routes = ReadRoutes(root);
				IReadOnlyList<ToneDesignEvidenceConclusion> evidence = ReadEvidence(root);

				if (blocksElement.GetArrayLength() > MaximumBlocks ||
					cautionsElement.GetArrayLength() > MaximumCautions ||
					routes.Count > MaximumRoutes ||
					evidence.Count > MaximumEvidenceConclusions)
				{
					throw new InvalidDataException("The tone-design response exceeds the proposal limits.");
				}

				List<ToneDesignBlockProposal> blocks = [];

				foreach (JsonElement blockElement in blocksElement.EnumerateArray())
				{
					string blockType = ReadRequiredString(blockElement, "blockType");
					int instance = ReadRequiredPositiveInt32(blockElement, "instance");
					int row = ReadRequiredPositiveInt32(blockElement, "row");
					int column = ReadRequiredPositiveInt32(blockElement, "column");

					Dictionary<string, string> parameters = ReadParameters(blockElement);
					// Any other device setting by name (validated later against the FM9's dictionary); listed parameters win.
					if (blockElement.TryGetProperty("settings", out JsonElement settingsElement) && settingsElement.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement setting in settingsElement.EnumerateArray())
						{
							string name = ReadRequiredString(setting, "name");
							if (!parameters.ContainsKey(name) && setting.TryGetProperty("value", out JsonElement valueElement))
								parameters[name] = valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() ?? string.Empty : valueElement.GetRawText();
						}
					}
					blocks.Add(Fm9QualifiedModelCatalog.Normalize(new ToneDesignBlockProposal(
						blockType,
						instance,
						row,
						column,
						parameters)
					{
						Rationale = ReadOptionalString(blockElement, "rationale"),
						ModelSelection = ReadModelSelection(blockElement),
						Channels = ReadChannels(blockElement, parameters)
					}));
				}

				// Effects go after their anchors along row 3; without any the Ride columns stay exactly as the model wrote them.
				IReadOnlyList<(ToneDesignBlockProposal Effect, string Anchor)> effects = ReadEffects(root);
				if (effects.Count > 0)
					blocks = ToneDesignEffects.Layout(blocks, effects).ToList();
				IReadOnlyList<ToneDesignSceneProposal> scenes = ReadScenes(root, effects.Select(effect => effect.Effect).ToArray());
				// "inUse": false on Drive 1 means the recording had no pedal: bypass it everywhere (a preset without scenes gets
				// one, since the FM9 stores bypass per scene).
				bool driveUnused = blocksElement.EnumerateArray().Any(element =>
					element.TryGetProperty("blockType", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Drive" &&
					(!element.TryGetProperty("instance", out JsonElement number) || number.ValueKind != JsonValueKind.Number || number.GetInt32() == 1) &&
					element.TryGetProperty("inUse", out JsonElement inUse) && inUse.ValueKind == JsonValueKind.False);
				if (driveUnused && blocks.Any(block => block.BlockType == "Drive" && block.Instance == 1))
				{
					scenes = scenes.Count > 0
						? scenes.Select(scene => scene with
						{
							BlockSettings = scene.BlockSettings.Select(setting => setting.BlockType == "Drive" && setting.Instance == 1
								? setting with { IsBypassed = true } : setting).ToArray()
						}).ToArray()
						: [new ToneDesignSceneProposal("Main", "The recording used no drive pedal, so Drive 1 is bypassed.",
							blocks.Select(block => new ToneDesignSceneBlockSetting(block.BlockType, block.Instance, "A",
								block.BlockType == "Drive" && block.Instance == 1)).ToArray())
						{
							Role = ToneDesignSectionRoles.Rhythm,
							Sections = ["main"]
						}];
				}
				if (scenes.Count > MaximumScenes)
				{
					throw new InvalidDataException("The tone-design response exceeds the proposal limits.");
				}

				List<string> cautions = [];

				foreach (JsonElement caution in cautionsElement.EnumerateArray())
				{
					if (caution.ValueKind != JsonValueKind.String ||
						string.IsNullOrWhiteSpace(caution.GetString()))
					{
						throw new InvalidDataException("Each caution must be a non-empty string.");
					}

					cautions.Add(caution.GetString()!);
				}

				ToneDesignProposal proposal = new(summary, blocks
					.Select(block => InheritUnchangedSelections(Fm9QualifiedModelCatalog.AlignSelectorToName(block)))
					.ToArray(), cautions)
				{
					Routes = routes,
					Scenes = scenes,
					Evidence = evidence,
					PresetName = Fm9PresetName.Normalize(root.TryGetProperty("presetName", out JsonElement presetName) &&
						presetName.ValueKind == JsonValueKind.String ? presetName.GetString() : null),
					Modifiers = ReadModifiers(root)
				};
				IReadOnlyList<string> selectorErrors = Fm9QualifiedModelCatalog.Evaluate(proposal);
				if (selectorErrors.Count > 0)
					throw new InvalidDataException(string.Join(" ", selectorErrors));
				return proposal;
			}
			catch (JsonException exception)
			{
				throw new InvalidDataException("The tone-design response is not valid JSON.", exception);
			}
		}

		private static Dictionary<string, string> ReadParameters(JsonElement parent)
		{
			if (!parent.TryGetProperty("parameters", out JsonElement parametersElement) ||
				parametersElement.ValueKind != JsonValueKind.Object ||
				parametersElement.EnumerateObject().Count() > MaximumParametersPerBlock)
			{
				throw new InvalidDataException("Each proposed block must contain a bounded parameters object.");
			}

			Dictionary<string, string> parameters = [];

			foreach (JsonProperty parameter in parametersElement.EnumerateObject())
			{
				if (string.IsNullOrWhiteSpace(parameter.Name) ||
					parameter.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
				{
					throw new InvalidDataException("Block parameters must use scalar values.");
				}

				parameters.Add(parameter.Name, parameter.Value.ToString());
			}

			return parameters;
		}

		private static ToneDesignModelSelection? ReadModelSelection(JsonElement parent)
		{
			if (!parent.TryGetProperty("modelSelection", out JsonElement selectionElement) ||
				selectionElement.ValueKind == JsonValueKind.Null)
			{
				return null;
			}

			string basis = ReadRequiredString(selectionElement, "evidenceBasis");
			// Only recording/live selections quote source equipment; an inferred one has none to name.
			string equipment = basis == "inferred" && string.IsNullOrWhiteSpace(ReadString(selectionElement, "equipment"))
				? "Not documented"
				: ReadRequiredString(selectionElement, "equipment");
			ToneDesignModelSelection selection = new(
				ReadRequiredString(selectionElement, "name"),
				equipment,
				basis,
				ReadString(selectionElement, "sourceUrl"),
				ReadRequiredString(selectionElement, "adaptation"));
			if (selection.EvidenceBasis is not ("recording" or "live" or "inferred"))
				throw new InvalidDataException("Model evidence basis must be recording, live, or inferred.");
			return selection;
		}

		// Channels B-D. Each starts from channel A's parameters, so a channel only has to state what differs; omitting
		// "channels" (every single-channel proposal) leaves the block on channel A alone.
		private static IReadOnlyList<ToneDesignBlockChannel> ReadChannels(
			JsonElement blockElement,
			IReadOnlyDictionary<string, string> channelA)
		{
			if (!blockElement.TryGetProperty("channels", out JsonElement channelsElement) ||
				channelsElement.ValueKind == JsonValueKind.Null)
			{
				return [];
			}

			if (channelsElement.ValueKind != JsonValueKind.Array ||
				channelsElement.GetArrayLength() > ToneDesignChannels.All.Count - 1)
			{
				throw new InvalidDataException("Block 'channels' must be an array of at most three channels B, C, or D.");
			}

			List<ToneDesignBlockChannel> channels = [];
			foreach (JsonElement channelElement in channelsElement.EnumerateArray())
			{
				string channel = ReadRequiredString(channelElement, "channel");
				if (channel is not ("B" or "C" or "D") || channels.Any(existing => existing.Channel == channel))
				{
					throw new InvalidDataException(
						"Each additional block channel must be a unique B, C, or D; channel A is the block's own parameters.");
				}

				Dictionary<string, string> parameters = new(channelA, StringComparer.OrdinalIgnoreCase);
				foreach (KeyValuePair<string, string> parameter in ReadParameters(channelElement))
					parameters[parameter.Key] = parameter.Value;
				channels.Add(new ToneDesignBlockChannel(channel, parameters)
				{
					ModelSelection = ReadModelSelection(channelElement)
				});
			}

			return channels.OrderBy(channel => channel.Channel, StringComparer.Ordinal).ToArray();
		}

		// A channel that keeps channel A's model (same selector and bank) keeps its documented selection too.
		private static ToneDesignBlockProposal InheritUnchangedSelections(ToneDesignBlockProposal block)
		{
			string? selector = Fm9QualifiedModelCatalog.SelectorParameter(block.BlockType);
			if (selector is null || block.ModelSelection is null || block.Channels.Count == 0)
				return block;
			return block with
			{
				Channels = block.Channels.Select(channel =>
					channel.ModelSelection is null &&
					SameValue(block.Parameters, channel.Parameters, selector) &&
					SameValue(block.Parameters, channel.Parameters, "Bank")
						? channel with { ModelSelection = block.ModelSelection }
						: channel).ToArray()
			};
		}

		private static bool SameValue(
			IReadOnlyDictionary<string, string> first,
			IReadOnlyDictionary<string, string> second,
			string key) =>
			first.TryGetValue(key, out string? left) == second.TryGetValue(key, out string? right) &&
			string.Equals(left, right, StringComparison.Ordinal);

		private static string ReadRequiredString(JsonElement parent, string propertyName)
		{
			if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
				property.ValueKind != JsonValueKind.String ||
				string.IsNullOrWhiteSpace(property.GetString()))
			{
				throw new InvalidDataException($"The tone-design response requires a non-empty '{propertyName}' string.");
			}

			return property.GetString()!;
		}

		private static string ReadOptionalString(JsonElement parent, string propertyName)
		{
			if (!parent.TryGetProperty(propertyName, out JsonElement property))
			{
				return string.Empty;
			}

			if (property.ValueKind != JsonValueKind.String ||
				string.IsNullOrWhiteSpace(property.GetString()))
			{
				throw new InvalidDataException(
					$"The tone-design response requires a non-empty '{propertyName}' string when supplied.");
			}

			return property.GetString()!;
		}

		private static string ReadString(JsonElement parent, string propertyName)
		{
			if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
				property.ValueKind != JsonValueKind.String)
			{
				throw new InvalidDataException(
					$"The tone-design response requires a '{propertyName}' string.");
			}

			return property.GetString()!;
		}

		private static JsonElement GetRequiredArray(JsonElement parent, string propertyName)
		{
			if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
				property.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException($"The tone-design response requires a '{propertyName}' array.");
			}

			return property;
		}

		private static int ReadRequiredPositiveInt32(JsonElement parent, string propertyName)
		{
			if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
				!property.TryGetInt32(out int value) ||
				value <= 0)
			{
				throw new InvalidDataException(
					$"The tone-design response requires a positive '{propertyName}' integer.");
			}

			return value;
		}

		private static IReadOnlyList<ToneDesignRoute> ReadRoutes(JsonElement root)
		{
			if (!root.TryGetProperty("routes", out JsonElement routesElement))
			{
				return [];
			}

			if (routesElement.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("The tone-design response requires a 'routes' array.");
			}

			List<ToneDesignRoute> routes = [];

			foreach (JsonElement routeElement in routesElement.EnumerateArray())
			{
				routes.Add(new ToneDesignRoute(
					ReadRequiredPositiveInt32(routeElement, "sourceRow"),
					ReadRequiredPositiveInt32(routeElement, "sourceColumn"),
					ReadRequiredPositiveInt32(routeElement, "destinationRow")));
			}

			return routes;
		}

		// Read as written; the server's repairs check each target against the device and drop what it cannot drive.
		private static IReadOnlyList<ToneDesignModifier>? ReadModifiers(JsonElement root)
		{
			if (!root.TryGetProperty("modifiers", out JsonElement modifiersElement) || modifiersElement.ValueKind == JsonValueKind.Null)
				return null;
			if (modifiersElement.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException("The tone-design response requires a 'modifiers' array.");
			if (modifiersElement.GetArrayLength() > ToneDesignEffects.MaximumModifiers)
				throw new InvalidDataException($"Add at most {ToneDesignEffects.MaximumModifiers} modifiers.");
			double Percent(JsonElement element, string name, double fallback) =>
				element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
			return modifiersElement.EnumerateArray().Select(element => new ToneDesignModifier(
				ReadRequiredString(element, "source"),
				ReadRequiredString(element, "blockType"),
				element.TryGetProperty("instance", out JsonElement instance) && instance.ValueKind == JsonValueKind.Number ? instance.GetInt32() : 1,
				ReadRequiredString(element, "parameter"))
			{
				Minimum = Percent(element, "minimum", 0),
				Maximum = Percent(element, "maximum", 100)
			}).ToArray();
		}

		private static IReadOnlyList<(ToneDesignBlockProposal Effect, string Anchor)> ReadEffects(JsonElement root)
		{
			if (!root.TryGetProperty("effects", out JsonElement effectsElement))
				return [];
			if (effectsElement.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException("The tone-design response requires an 'effects' array.");
			if (effectsElement.GetArrayLength() > ToneDesignEffects.MaximumEffects)
				throw new InvalidDataException($"Add at most {ToneDesignEffects.MaximumEffects} effects.");
			List<(ToneDesignBlockProposal, string)> effects = [];
			Dictionary<string, int> instances = new(StringComparer.OrdinalIgnoreCase);
			foreach (JsonElement effectElement in effectsElement.EnumerateArray())
			{
				string blockType = ReadRequiredString(effectElement, "blockType");
				if (!ToneDesignEffects.BlockTypes.Contains(blockType, StringComparer.OrdinalIgnoreCase))
					throw new InvalidDataException($"'{blockType}' is not an effect block; use one of: {string.Join(", ", ToneDesignEffects.BlockTypes)}.");
				blockType = ToneDesignEffects.BlockTypes.First(type => string.Equals(type, blockType, StringComparison.OrdinalIgnoreCase));
				string position = ReadRequiredString(effectElement, "position");
				string anchor = position.StartsWith("after ", StringComparison.OrdinalIgnoreCase) ? position[6..].Trim() : position.Trim();
				// A second Drive is instance 2 (the Ride chain's Drive is instance 1); repeated effects take the next instance.
				int instance = instances.GetValueOrDefault(blockType, blockType == "Drive" ? 1 : 0) + 1;
				instances[blockType] = instance;
				Dictionary<string, string> settings = new(StringComparer.Ordinal);
				foreach (JsonElement setting in GetRequiredArray(effectElement, "settings").EnumerateArray())
				{
					string name = ReadRequiredString(setting, "name");
					string value = setting.TryGetProperty("value", out JsonElement valueElement) ? valueElement.ValueKind switch
					{
						JsonValueKind.String => valueElement.GetString() ?? string.Empty,
						JsonValueKind.Number => valueElement.GetRawText(),
						JsonValueKind.True => "1",
						JsonValueKind.False => "0",
						_ => throw new InvalidDataException($"{blockType} {name} needs a value.")
					} : throw new InvalidDataException($"{blockType} {name} needs a value.");
					settings[name] = value;
				}
				effects.Add((Fm9QualifiedModelCatalog.Normalize(new ToneDesignBlockProposal(blockType, instance, 3, 0, settings)
				{
					Rationale = ReadOptionalString(effectElement, "rationale")
				}), anchor));
			}
			return effects;
		}

		private static IReadOnlyList<ToneDesignSceneProposal> ReadScenes(JsonElement root, IReadOnlyList<ToneDesignBlockProposal> effects)
		{
			if (!root.TryGetProperty("scenes", out JsonElement scenesElement))
			{
				return [];
			}

			if (scenesElement.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("The tone-design response requires a 'scenes' array.");
			}

			List<ToneDesignSceneProposal> scenes = [];

			foreach (JsonElement sceneElement in scenesElement.EnumerateArray())
			{
				string name = ReadRequiredString(sceneElement, "name");
				string purpose = ReadRequiredString(sceneElement, "purpose");
				string role = ReadRequiredString(sceneElement, "role");
				if (!ToneDesignSectionRoles.IsSupported(role))
				{
					throw new InvalidDataException(
						$"Scene '{name}' must use a supported role: {string.Join(", ", ToneDesignSectionRoles.All)}.");
				}

				JsonElement sectionsElement = GetRequiredArray(sceneElement, "sections");
				if (sectionsElement.GetArrayLength() is < 1 or > MaximumSceneSections)
				{
					throw new InvalidDataException(
						"Each scene must reference between one and sixteen musical sections.");
				}

				List<string> sections = [];
				HashSet<string> uniqueSections = new(StringComparer.OrdinalIgnoreCase);
				foreach (JsonElement sectionElement in sectionsElement.EnumerateArray())
				{
					if (sectionElement.ValueKind != JsonValueKind.String ||
						string.IsNullOrWhiteSpace(sectionElement.GetString()) ||
						!uniqueSections.Add(sectionElement.GetString()!))
					{
						throw new InvalidDataException(
							"Scene section references must be non-empty and unique.");
					}

					sections.Add(sectionElement.GetString()!);
				}

				JsonElement settingsElement = GetRequiredArray(sceneElement, "blockSettings");

				if (settingsElement.GetArrayLength() != MaximumSceneBlockSettings)
				{
					throw new InvalidDataException(
						"Each scene must provide exactly one explicit channel and bypass state for each of the eight supported Ride blocks.");
				}

				List<ToneDesignSceneBlockSetting> settings = [];
				HashSet<string> settingKeys = new(StringComparer.OrdinalIgnoreCase);
				List<string> duplicates = [];
				bool conflicting = false;

				foreach (JsonElement settingElement in settingsElement.EnumerateArray())
				{
					if (!settingElement.TryGetProperty("isBypassed", out JsonElement bypassElement) ||
						(bypassElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
					{
						throw new InvalidDataException("Each scene block setting requires a boolean 'isBypassed' value.");
					}

					string blockType = ReadRequiredString(settingElement, "blockType");
					int instance = ReadRequiredPositiveInt32(settingElement, "instance");
					string channel = ReadRequiredString(settingElement, "channel");
					if (channel is not ("A" or "B" or "C" or "D"))
					{
						throw new InvalidDataException(
							"Each scene block setting channel must be A, B, C, or D.");
					}

					if (!settingKeys.Add(CreateBlockKey(blockType, instance)))
					{
						duplicates.Add($"{blockType} {instance}");
						bool identical = settings.Any(existing =>
							string.Equals(existing.BlockType, blockType, StringComparison.OrdinalIgnoreCase) &&
							existing.Instance == instance && existing.Channel == channel &&
							existing.IsBypassed == bypassElement.GetBoolean());
						if (!identical)
							conflicting = true;
						continue;
					}

					settings.Add(new ToneDesignSceneBlockSetting(
						blockType,
						instance,
						channel,
						bypassElement.GetBoolean()));
				}

				// With exactly eight entries a repeated block always displaces another. Input and Output are always active
				// on channel A, so when only they were displaced the scene is complete without guessing; otherwise name
				// both sides of the mistake so the model can fix it.
				string[] missing = CompleteRideBlockKeys.Except(settingKeys, StringComparer.OrdinalIgnoreCase)
					.Order(StringComparer.Ordinal)
					.ToArray();
				if (!conflicting && missing.Length > 0 && missing.All(key => FixedSceneBlockKeys.Contains(key)))
				{
					foreach (string key in missing)
						settings.Add(new ToneDesignSceneBlockSetting(key.Split('\u001f')[0], 1, "A", false));
					missing = [];
				}
				if (missing.Length > 0)
				{
					string omitted = string.Join(", ", missing.Select(key => key.Replace('\u001f', ' ')));
					throw new InvalidDataException(duplicates.Count > 0
						? $"Scene '{name}' contains duplicate state for {string.Join(", ", duplicates)} and omits {omitted}; " +
							"give each of the eight Ride blocks exactly one channel and bypass state."
						: $"Scene '{name}' must provide one explicit channel and bypass state for every supported Ride block " +
							$"(missing {omitted}); omitted state cannot inherit from the template.");
				}

				// Each effect's state in this scene (on unless the scene bypasses it); effects play channel A.
				Dictionary<string, bool> effectBypass = new(StringComparer.OrdinalIgnoreCase);
				if (sceneElement.TryGetProperty("effectStates", out JsonElement statesElement) && statesElement.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement state in statesElement.EnumerateArray())
						effectBypass[ReadRequiredString(state, "blockType")] =
							state.TryGetProperty("isBypassed", out JsonElement bypassed) && bypassed.ValueKind == JsonValueKind.True;
				}
				foreach (ToneDesignBlockProposal effect in effects)
					settings.Add(new ToneDesignSceneBlockSetting(effect.BlockType, effect.Instance, ToneDesignChannels.A,
						effectBypass.GetValueOrDefault(effect.BlockType)));

				scenes.Add(new ToneDesignSceneProposal(name, purpose, settings)
				{
					Role = role,
					Sections = sections
				});
			}

			return scenes;
		}

		private static string CreateBlockKey(string blockType, int instance) =>
			$"{blockType}\u001f{instance}";

		private static IReadOnlyList<ToneDesignEvidenceConclusion> ReadEvidence(JsonElement root)
		{
			if (!root.TryGetProperty("evidence", out JsonElement evidenceElement))
			{
				return [];
			}

			if (evidenceElement.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("The tone-design response requires an 'evidence' array.");
			}

			List<ToneDesignEvidenceConclusion> evidence = [];

			foreach (JsonElement conclusionElement in evidenceElement.EnumerateArray())
			{
				evidence.Add(new ToneDesignEvidenceConclusion(
					ReadRequiredString(conclusionElement, "claim"),
					ReadRequiredString(conclusionElement, "classification"),
					ReadRequiredString(conclusionElement, "confidence"),
					ReadString(conclusionElement, "sourceUrl"),
					ReadString(conclusionElement, "sourceDate"),
					ReadRequiredString(conclusionElement, "rationale")));
			}

			return evidence;
		}
	}
}
