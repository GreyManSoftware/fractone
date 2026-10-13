using System.Text.Json;
using System.Text.Json.Nodes;
using Fractal.Application;

namespace Fractal.Ai
{
	// Caps on the reply's free text. The schema states them so the model writes within them, and Apply cuts anything
	// longer: a request cannot turn the summary or a rationale into a general-purpose essay, story or code listing.
	public static class ToneDesignTextLimits
	{
		public const int Summary = 1000;
		public const int Rationale = 400;
		public const int Caution = 300;
		public const int Short = 60;
		public const int Purpose = 200;

		private static readonly IReadOnlyDictionary<string, int> ByProperty = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			["summary"] = Summary,
			["rationale"] = Rationale,
			["claim"] = Rationale,
			["adaptation"] = Rationale,
			["cautions"] = Caution,
			["purpose"] = Purpose,
			["name"] = Short,
			["sections"] = Short,
			["value"] = Short,
			["parameter"] = Short,
			["equipment"] = 120,
			["sourceDate"] = 40,
			["sourceUrl"] = 500
		};

		public static ToneDesignProposal Apply(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			return proposal with
			{
				Summary = Cut(proposal.Summary, Summary),
				Cautions = proposal.Cautions.Select(caution => Cut(caution, Caution)).ToArray(),
				Blocks = proposal.Blocks.Select(block => block with
				{
					Rationale = Cut(block.Rationale, Rationale),
					ModelSelection = Limit(block.ModelSelection),
					Channels = block.Channels.Select(channel => channel with { ModelSelection = Limit(channel.ModelSelection) }).ToArray()
				}).ToArray(),
				Scenes = proposal.Scenes.Select(scene => scene with
				{
					Name = Cut(scene.Name, Short),
					Purpose = Cut(scene.Purpose, Purpose),
					Sections = scene.Sections.Select(section => Cut(section, Short)).ToArray()
				}).ToArray(),
				Evidence = proposal.Evidence.Select(item => item with
				{
					Claim = Cut(item.Claim, Rationale),
					Rationale = Cut(item.Rationale, Rationale)
				}).ToArray()
			};
		}

		// Every free-text string in the schema gets a maxLength: by property name, or the rationale length otherwise.
		public static JsonElement AddToSchema(JsonElement schema)
		{
			JsonNode root = JsonNode.Parse(schema.GetRawText())!;
			Visit(root, null);
			return JsonSerializer.SerializeToElement(root);
		}

		private static void Visit(JsonNode? node, string? property)
		{
			if (node is JsonArray array)
			{
				foreach (JsonNode? item in array)
					Visit(item, property);
				return;
			}
			if (node is not JsonObject value)
				return;
			if (value["type"] is JsonValue type && type.TryGetValue(out string? kind) && kind == "string" &&
				value["enum"] is null && value["const"] is null && value["maxLength"] is null)
				value["maxLength"] = property is not null && ByProperty.TryGetValue(property, out int limit) ? limit : Rationale;
			foreach ((string key, JsonNode? child) in value.ToArray())
			{
				if (key == "properties" && child is JsonObject properties)
				{
					foreach ((string name, JsonNode? definition) in properties.ToArray())
						Visit(definition, name);
				}
				else
				{
					// "items" and the oneOf/anyOf branches describe the same property as their parent.
					Visit(child, key is "items" or "oneOf" or "anyOf" or "allOf" ? property : null);
				}
			}
		}

		private static ToneDesignModelSelection? Limit(ToneDesignModelSelection? selection) =>
			selection is null ? null : selection with
			{
				Equipment = Cut(selection.Equipment, 120),
				Adaptation = Cut(selection.Adaptation, Rationale)
			};

		private static string Cut(string? text, int limit)
		{
			if (string.IsNullOrEmpty(text) || text.Length <= limit)
				return text ?? string.Empty;
			int space = text.LastIndexOf(' ', limit - 1);
			return text[..(space > limit / 2 ? space : limit - 1)].TrimEnd(' ', ',', ';', ':') + "…";
		}
	}
}
