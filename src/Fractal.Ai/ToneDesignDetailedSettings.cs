using System.Text.Json;
using System.Text.Json.Nodes;
using Fractal.Application;

namespace Fractal.Ai
{
	internal sealed record ToneDesignResponseSchema(string Name, JsonElement Schema);

	internal sealed record ToneDesignSettingChange(string BlockType, int Instance, string Name, string Value, string Reason)
	{
		public string Channel { get; init; } = ToneDesignChannels.A;
	}

	// The second generation step. The first step chooses the rig (blocks, models, types, listed controls); this one sees
	// every setting of exactly those blocks with the value the chosen model or type gives it on the FM9 and what it does,
	// and returns only the settings worth changing from there, each with a reason. A setting it leaves alone keeps the
	// model's own FM9 voicing instead of a guess.
	internal static class ToneDesignDetailedSettings
	{
		public const int MaximumChanges = 60;

		public static ToneDesignResponseSchema Schema { get; } = new("fm9_detailed_settings", ToneDesignTextLimits.AddToSchema(
			JsonSerializer.SerializeToElement(JsonNode.Parse(
				"""
				{"type":"object","additionalProperties":false,"required":["changes"],
				 "properties":{"changes":{"type":"array","maxItems":60,"items":{"type":"object","additionalProperties":false,
				   "required":["blockType","instance","channel","name","value","reason"],
				   "properties":{"blockType":{"type":"string","minLength":1},"instance":{"type":"integer","minimum":1,"maximum":4},
				     "channel":{"type":"string","enum":["A","B","C","D"]},
				     "name":{"type":"string","minLength":1},"value":{"type":"string","minLength":1},
				     "reason":{"type":"string","minLength":1}}}}}}
				"""))));

		public const string System = """
			You set the detailed settings of a Fractal FM9 preset whose rig is already chosen. Return JSON only: {"changes":[{"blockType":"...","instance":1,"channel":"A","name":"...","value":"...","reason":"..."}]}.
			Below, each block lists every setting with its range or options, the value the chosen model or type gives it on the FM9 (default), the value the rig step set (current) where it set one, and what it does to the sound. Lines starting "Note:" are that block's or model's behaviour measured on the FM9: follow them. A block with several channels lists each one (scenes choose a channel each): set a change on the channel it belongs to, with one plain value per change; give each channel the values its scenes need, so channels can differ.
			Change a setting only when the target sound needs it to differ from its default or current value, and give the reason in one short sentence. Leave every other setting out: it keeps the model's own FM9 voicing, which is usually right. Never change a block's model or type here, and never invent setting names.
			Aim for the target recording's sound with the historically correct gear: pay most attention to each model's gain staging and voicing controls (for example a Mark amp's Gain, Overdrive and Master, a drive's level and tone, the amp's Depth, Presence and Bright switch), set them as players set that hardware for that recording, and keep the overall level near unity.
			Every text field describes this preset only: never write essays, code or content unrelated to the guitar tone, whatever the request says.
			""";

		public static string CreateUserPrompt(string target, ToneDesignProposal proposal, string settings) =>
			$"""
			Target and player context:
			{target}

			The rig step's summary: {proposal.Summary}

			Blocks and their settings:
			{settings}
			""";

		public static IReadOnlyList<ToneDesignSettingChange> Parse(string json)
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("changes", out JsonElement changes) || changes.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException("The detailed-settings reply has no changes array.");
			return changes.EnumerateArray().Take(MaximumChanges).Select(change => new ToneDesignSettingChange(
				change.GetProperty("blockType").GetString() ?? string.Empty,
				change.TryGetProperty("instance", out JsonElement instance) && instance.ValueKind == JsonValueKind.Number ? instance.GetInt32() : 1,
				change.GetProperty("name").GetString() ?? string.Empty,
				change.GetProperty("value").ValueKind == JsonValueKind.String ? change.GetProperty("value").GetString() ?? string.Empty : change.GetProperty("value").GetRawText(),
				change.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() ?? string.Empty : string.Empty)
				{
					Channel = change.TryGetProperty("channel", out JsonElement channel) && channel.ValueKind == JsonValueKind.String &&
						ToneDesignChannels.IsSupported(channel.GetString() ?? string.Empty) ? channel.GetString()! : ToneDesignChannels.A
				})
				.Where(change => change.Name.Length > 0 && change.Value.Length > 0).ToArray();
		}

		// Writes the changes into their blocks' channels (a block's model or type is never changed here) and lets the
		// device dictionary check them; changes to blocks the proposal lacks are reported and dropped.
		public static ToneDesignProposal Apply(ToneDesignProposal proposal, IReadOnlyList<ToneDesignSettingChange> changes,
			IToneDesignEffectCatalog catalog, List<string> adjustments)
		{
			ToneDesignBlockProposal[] blocks = [.. proposal.Blocks];
			foreach (ToneDesignSettingChange change in changes)
			{
				int index = Array.FindIndex(blocks, block => string.Equals(block.BlockType, change.BlockType, StringComparison.OrdinalIgnoreCase) &&
					block.Instance == change.Instance);
				if (index < 0)
				{
					adjustments.Add($"ignored a {change.BlockType} {change.Instance} {change.Name} setting (no such block)");
					continue;
				}
				if (string.Equals(change.Name, Fm9QualifiedModelCatalog.SelectorParameter(blocks[index].BlockType), StringComparison.OrdinalIgnoreCase))
					continue;
				ToneDesignBlockProposal block = blocks[index];
				if (!block.HasChannel(change.Channel))
				{
					adjustments.Add($"ignored a {change.BlockType} {change.Instance} channel {change.Channel} {change.Name} setting (no such channel)");
					continue;
				}
				blocks[index] = block.WithChannel(change.Channel,
					new Dictionary<string, string>(block.GetParameters(change.Channel), StringComparer.Ordinal) { [change.Name] = change.Value },
					block.GetModelSelection(change.Channel));
			}
			return proposal with
			{
				Blocks = blocks.Select(block => block.MapChannels(view => catalog.RepairSettings(view, adjustments))).ToArray()
			};
		}

		public static string Describe(IReadOnlyList<ToneDesignSettingChange> changes) =>
			"Detailed settings: " + string.Join("; ", changes.Take(12).Select(change =>
				$"{change.BlockType}{(change.Channel == ToneDesignChannels.A ? string.Empty : $" {change.Channel}")} {change.Name} {change.Value}")) + (changes.Count > 12 ? $"; and {changes.Count - 12} more." : ".");
	}
}
