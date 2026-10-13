using System.Text.Json;
using System.Text.Json.Nodes;
using Fractal.Application;

namespace Fractal.Ai
{
	internal static class ToneDesignProposalJsonContract
	{
		public const string SchemaName = "fm9_ride_tone_design_proposal";

		// Blocks whose generated proposals may author channels B-D. Every setting is stored per channel (Level included, so
		// a clean and a heavy amp channel each set their own volume); other blocks stay on channel A until their channels
		// have been auditioned with the models.
		public static IReadOnlyList<string> ChannelBlocks { get; } = ["Amp"];

		public static JsonElement Schema { get; } = ToneDesignTextLimits.AddToSchema(AddModifiers(AddEffects(AddRideSettings(AddGrounding(JsonDocument.Parse(
			"""
			{
			  "type": "object",
			  "additionalProperties": false,
			  "required": ["presetName", "summary", "blocks", "routes", "scenes", "cautions", "evidence"],
			  "properties": {
			    "presetName": { "type": "string", "minLength": 1, "maxLength": 31 },
			    "summary": { "type": "string", "minLength": 1 },
			    "blocks": {
			      "type": "array",
			      "minItems": 8,
			      "maxItems": 8,
			      "items": {
			        "oneOf": [
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Input" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 1 },
			              "parameters": { "type": "object", "additionalProperties": false, "properties": {} },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Gate/Expander" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 2 },
			              "parameters": {
			                "type": "object", "additionalProperties": false,
			                "required": ["Threshold", "Attack", "Hold", "Release", "Ratio", "Low Cut", "High Cut", "Level"],
			                "properties": {
			                  "Threshold": { "type": "number", "minimum": -100, "maximum": 0 },
			                  "Attack": { "type": "number", "minimum": 0.1, "maximum": 1000 },
			                  "Hold": { "type": "number", "minimum": 1, "maximum": 1000 },
			                  "Release": { "type": "number", "minimum": 1, "maximum": 1000 },
			                  "Ratio": { "type": "number", "minimum": 1, "maximum": 20 },
			                  "Low Cut": { "type": "number", "minimum": 10, "maximum": 1000 },
			                  "High Cut": { "type": "number", "minimum": 200, "maximum": 20000 },
			                  "Level": { "type": "number", "minimum": -80, "maximum": 12 }
			                }
			              },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Drive" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 3 },
			              "parameters": {
			                "type": "object", "additionalProperties": false,
			                "required": ["Drive", "Tone", "Level", "Mix"],
			                "properties": {
			                  "Drive": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Tone": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Level": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Mix": { "type": "number", "minimum": 0, "maximum": 100 }
			                }
			              },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Amp" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 4 },
			              "parameters": {
			                "type": "object", "additionalProperties": false,
			                "required": ["Amp Model", "Level", "Gain", "Bass", "Mid", "Treble", "Master", "Low Cut", "High Cut", "Presence", "Output EQ Type", "Output EQ Location", "Output EQ Enabled", "Output EQ 80 Hz", "Output EQ 240 Hz", "Output EQ 750 Hz", "Output EQ 2200 Hz", "Output EQ 6600 Hz"],
			                "properties": {
			                  "Amp Model": { "type": "integer", "minimum": 0, "maximum": 335 },
			                  "Level": { "type": "number", "minimum": -80, "maximum": 12 },
			                  "Gain": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Bass": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Mid": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Treble": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Master": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Low Cut": { "type": "number", "minimum": 10, "maximum": 1000 },
			                  "High Cut": { "type": "number", "minimum": 5000, "maximum": 20000 },
			                  "Presence": { "type": "number", "minimum": 0, "maximum": 10 },
			                  "Output EQ Type": { "type": "string", "const": "Mark5Band" },
			                  "Output EQ Location": { "type": "string", "enum": ["Output", "PrePowerAmp"] },
			                  "Output EQ Enabled": { "type": "integer", "minimum": 0, "maximum": 1 },
			                  "Output EQ 80 Hz": { "type": "number", "minimum": -12, "maximum": 12 },
			                  "Output EQ 240 Hz": { "type": "number", "minimum": -12, "maximum": 12 },
			                  "Output EQ 750 Hz": { "type": "number", "minimum": -12, "maximum": 12 },
			                  "Output EQ 2200 Hz": { "type": "number", "minimum": -12, "maximum": 12 },
			                  "Output EQ 6600 Hz": { "type": "number", "minimum": -12, "maximum": 12 }
			                }
			              },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Cab" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 5 },
			              "parameters": {
			                "type": "object", "additionalProperties": false,
			                "required": ["Bank", "Cab Type", "Low Cut", "High Cut"],
			                "properties": {
			                  "Bank": { "type": "integer", "minimum": 0, "maximum": 4 },
			                  "Cab Type": { "type": "integer", "minimum": 0, "maximum": 1023 },
			                  "Low Cut": { "type": "number", "minimum": 20, "maximum": 200 },
			                  "High Cut": { "type": "number", "minimum": 2000, "maximum": 20000 }
			                }
			              },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Parametric EQ" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 6 },
			              "parameters": {
			                "type": "object", "additionalProperties": false,
			                "required": ["Frequency 1", "Frequency 2", "Frequency 3", "Frequency 4", "Frequency 5", "Q 1", "Q 2", "Q 3", "Q 4", "Q 5", "Gain 1", "Gain 2", "Gain 3", "Gain 4", "Gain 5"],
			                "properties": {
			                  "Frequency 1": { "type": "number", "minimum": 20, "maximum": 2000 },
			                  "Frequency 2": { "type": "number", "minimum": 100, "maximum": 10000 },
			                  "Frequency 3": { "type": "number", "minimum": 100, "maximum": 10000 },
			                  "Frequency 4": { "type": "number", "minimum": 100, "maximum": 10000 },
			                  "Frequency 5": { "type": "number", "minimum": 200, "maximum": 20000 },
			                  "Q 1": { "type": "number", "minimum": 0.1, "maximum": 10 },
			                  "Q 2": { "type": "number", "minimum": 0.1, "maximum": 10 },
			                  "Q 3": { "type": "number", "minimum": 0.1, "maximum": 10 },
			                  "Q 4": { "type": "number", "minimum": 0.1, "maximum": 10 },
			                  "Q 5": { "type": "number", "minimum": 0.1, "maximum": 10 },
			                  "Gain 1": { "type": "number", "minimum": -20, "maximum": 20 },
			                  "Gain 2": { "type": "number", "minimum": -20, "maximum": 20 },
			                  "Gain 3": { "type": "number", "minimum": -20, "maximum": 20 },
			                  "Gain 4": { "type": "number", "minimum": -20, "maximum": 20 },
			                  "Gain 5": { "type": "number", "minimum": -20, "maximum": 20 }
			                }
			              },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Reverb" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 7 },
			              "parameters": {
			                "type": "object", "additionalProperties": false,
			                "required": ["Mix", "Level", "Balance", "Time", "High Cut", "Low Cut", "Pre-Delay"],
			                "properties": {
			                  "Mix": { "type": "number", "minimum": 0, "maximum": 100 },
			                  "Level": { "type": "number", "minimum": -80, "maximum": 12 },
			                  "Balance": { "type": "number", "minimum": -100, "maximum": 100 },
			                  "Time": { "type": "number", "minimum": 0.1, "maximum": 100 },
			                  "High Cut": { "type": "number", "minimum": 200, "maximum": 20000 },
			                  "Low Cut": { "type": "number", "minimum": 20, "maximum": 2000 },
			                  "Pre-Delay": { "type": "number", "minimum": 0, "maximum": 1000 }
			                }
			              },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          },
			          {
			            "type": "object", "additionalProperties": false,
			            "required": ["blockType", "instance", "row", "column", "parameters", "rationale"],
			            "properties": {
			              "blockType": { "type": "string", "const": "Output" },
			              "instance": { "type": "integer", "const": 1 },
			              "row": { "type": "integer", "const": 3 },
			              "column": { "type": "integer", "const": 8 },
			              "parameters": { "type": "object", "additionalProperties": false, "properties": {} },
			              "rationale": { "type": "string", "minLength": 1 }
			            }
			          }
			        ]
			      }
			    },
			    "routes": {
			      "type": "array",
			      "maxItems": 7,
			      "items": {
			        "type": "object",
			        "additionalProperties": false,
			        "required": ["sourceRow", "sourceColumn", "destinationRow"],
			        "properties": {
			          "sourceRow": { "type": "integer", "const": 3 },
			          "sourceColumn": { "type": "integer", "minimum": 1, "maximum": 7 },
			          "destinationRow": { "type": "integer", "const": 3 }
			        }
			      }
			    },
			    "scenes": {
			      "type": "array",
			      "maxItems": 8,
			      "items": {
			        "type": "object",
			        "additionalProperties": false,
			        "required": ["name", "purpose", "role", "sections", "blockSettings"],
			        "properties": {
			          "name": { "type": "string", "minLength": 1 },
			          "purpose": { "type": "string", "minLength": 1 },
			          "role": {
			            "type": "string",
			            "enum": ["rhythm", "clean", "lead", "harmony", "intro", "transition"]
			          },
			          "sections": {
			            "type": "array",
			            "minItems": 1,
			            "maxItems": 16,
			            "uniqueItems": true,
			            "items": { "type": "string", "minLength": 1 }
			          },
			          "blockSettings": {
			            "type": "array",
			            "minItems": 8,
			            "maxItems": 8,
			            "items": {
			              "type": "object",
			              "additionalProperties": false,
			              "required": ["blockType", "instance", "channel", "isBypassed"],
			              "properties": {
			                "blockType": {
			                  "type": "string",
			                  "enum": ["Input", "Gate/Expander", "Drive", "Amp", "Cab", "Parametric EQ", "Reverb", "Output"]
			                },
			                "instance": { "type": "integer", "const": 1 },
			                "channel": { "type": "string", "enum": ["A", "B", "C", "D"] },
			                "isBypassed": { "type": "boolean" }
			              }
			            }
			          }
			        }
			      }
			    },
			    "cautions": {
			      "type": "array",
			      "maxItems": 16,
			      "items": { "type": "string", "minLength": 1 }
			    },
			    "evidence": {
			      "type": "array",
			      "maxItems": 32,
			      "description": "Structured conclusions supporting the proposal. Keep documented historical source-rig facts separate from FM9 adaptations and label unsupported conclusions rather than inventing facts.",
			      "items": {
			        "type": "object",
			        "additionalProperties": false,
			        "required": ["claim", "classification", "confidence", "sourceUrl", "sourceDate", "rationale"],
			        "properties": {
			          "claim": {
			            "type": "string",
			            "minLength": 1,
			            "description": "A single evidence conclusion. A substituted conclusion must explicitly identify the FM9 substitution."
			          },
			          "classification": {
			            "type": "string",
			            "enum": ["documented", "inferred", "substituted"],
			            "description": "Whether the conclusion is directly documented, inferred from cited material, or an FM9 substitution."
			          },
			          "confidence": {
			            "type": "string",
			            "enum": ["high", "medium", "low"],
			            "description": "Confidence in this conclusion."
			          },
			          "sourceUrl": {
			            "type": "string",
			            "description": "Opaque source-reference from retrieved evidence when one is supplied; otherwise an absolute http/https citation URL for documented or inferred conclusions. Optional for substitutions unless citing the underlying historical source."
			          },
			          "sourceDate": {
			            "type": "string",
			            "description": "Non-empty source publication or access date for documented or inferred conclusions; optional for substitutions."
			          },
			          "rationale": {
			            "type": "string",
			            "minLength": 1,
			            "description": "Why the evidence supports the proposal while distinguishing historical source facts from FM9 adaptation."
			          }
			        }
			      }
			    }
			  }
			}
			""").RootElement.Clone())))));

		// Every Ride block also takes any other FM9 setting by its device name (an Amp's Overdrive, Depth, Saturation...), as
		// name/value pairs: the listed parameters are the common controls, not the limit of what the model may set.
		private static JsonElement AddRideSettings(JsonElement schema)
		{
			JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
			foreach (JsonNode? block in root["properties"]!["blocks"]!["items"]!["oneOf"]!.AsArray())
			{
				block!["required"]!.AsArray().Add("settings");
				block["properties"]!["settings"] = JsonNode.Parse(
					"""
					{"type":"array","maxItems":40,"items":{"type":"object","additionalProperties":false,
					 "required":["name","value"],
					 "properties":{"name":{"type":"string","minLength":1},"value":{"type":"string","minLength":1}}}}
					""");
			}
			return JsonSerializer.SerializeToElement(root);
		}

		// Modifiers: a controller (LFO, envelope, pedal, scene controller) driving one knob of a block between two points of
		// its range, in percent. The server checks the block and setting against the FM9's dictionary.
		private static JsonElement AddModifiers(JsonElement schema)
		{
			JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
			root["required"]!.AsArray().Add("modifiers");
			root["properties"]!["modifiers"] = new JsonObject
			{
				["type"] = "array",
				["maxItems"] = ToneDesignEffects.MaximumModifiers,
				["items"] = new JsonObject
				{
					["type"] = "object",
					["additionalProperties"] = false,
					["required"] = new JsonArray("source", "blockType", "instance", "parameter", "minimum", "maximum", "rationale"),
					["properties"] = new JsonObject
					{
						["source"] = new JsonObject
						{
							["type"] = "string",
							["enum"] = new JsonArray(ToneDesignEffects.ModifierSources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray())
						},
						["blockType"] = new JsonObject
						{
							["type"] = "string",
							["enum"] = new JsonArray(ToneDesignEffects.RideBlockTypes.Concat(ToneDesignEffects.BlockTypes)
								.Distinct(StringComparer.Ordinal).Select(type => (JsonNode?)JsonValue.Create(type)).ToArray())
						},
						["instance"] = JsonNode.Parse("""{"type":"integer","minimum":1,"maximum":2}"""),
						["parameter"] = JsonNode.Parse("""{"type":"string","minLength":1}"""),
						["minimum"] = JsonNode.Parse("""{"type":"number","minimum":0,"maximum":100}"""),
						["maximum"] = JsonNode.Parse("""{"type":"number","minimum":0,"maximum":100}"""),
						["rationale"] = JsonNode.Parse("""{"type":"string","minLength":1}""")
					}
				}
			};
			return JsonSerializer.SerializeToElement(root);
		}

		// Effect blocks beyond the Ride chain: each names the block it follows and its settings as name/value pairs (strict
		// structured output cannot hold an open map); the server checks both against the FM9's parameter dictionary and
		// lays out row 3 and its cables itself. Each scene gives every effect an on/bypassed state.
		private static JsonElement AddEffects(JsonElement schema)
		{
			JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
			JsonNode EffectTypes() => new JsonArray(ToneDesignEffects.BlockTypes.Select(type => (JsonNode?)JsonValue.Create(type)).ToArray());
			JsonArray positions = new(ToneDesignEffects.Anchors.Select(anchor => (JsonNode?)JsonValue.Create($"after {anchor}")).ToArray());
			root["required"]!.AsArray().Add("effects");
			root["properties"]!["effects"] = new JsonObject
			{
				["type"] = "array",
				["maxItems"] = ToneDesignEffects.MaximumEffects,
				["items"] = new JsonObject
				{
					["type"] = "object",
					["additionalProperties"] = false,
					["required"] = new JsonArray("blockType", "position", "settings", "rationale"),
					["properties"] = new JsonObject
					{
						["blockType"] = new JsonObject { ["type"] = "string", ["enum"] = EffectTypes() },
						["position"] = new JsonObject { ["type"] = "string", ["enum"] = positions },
						["settings"] = JsonNode.Parse(
							"""
							{"type":"array","maxItems":16,"items":{"type":"object","additionalProperties":false,
							 "required":["name","value"],
							 "properties":{"name":{"type":"string","minLength":1},"value":{"type":"string","minLength":1}}}}
							"""),
						["rationale"] = JsonNode.Parse("""{"type":"string","minLength":1}""")
					}
				}
			};
			JsonObject scene = root["properties"]!["scenes"]!["items"]!.AsObject();
			scene["required"]!.AsArray().Add("effectStates");
			scene["properties"]!["effectStates"] = new JsonObject
			{
				["type"] = "array",
				["maxItems"] = ToneDesignEffects.MaximumEffects,
				["items"] = new JsonObject
				{
					["type"] = "object",
					["additionalProperties"] = false,
					["required"] = new JsonArray("blockType", "isBypassed"),
					["properties"] = new JsonObject
					{
						["blockType"] = new JsonObject { ["type"] = "string", ["enum"] = EffectTypes() },
						["isBypassed"] = JsonNode.Parse("""{"type":"boolean"}""")
					}
				}
			};
			return JsonSerializer.SerializeToElement(root);
		}

		private static JsonElement AddGrounding(JsonElement schema)
		{
			JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
			JsonArray blocks = root["properties"]!["blocks"]!["items"]!["oneOf"]!.AsArray();
			foreach (JsonNode? block in blocks)
			{
				string type = block!["properties"]!["blockType"]!["const"]!.GetValue<string>();
				if (type is not ("Amp" or "Drive" or "Cab")) continue;
				block["required"]!.AsArray().Add("modelSelection");
				block["properties"]!["modelSelection"] = CreateModelSelectionSchema();
				if (type == "Drive")
				{
					block["properties"]!["parameters"]!["required"]!.AsArray().Add("Drive Model");
					block["properties"]!["parameters"]!["properties"]!["Drive Model"] =
						JsonNode.Parse("""{"type":"integer","minimum":0,"maximum":86}""");
					// The Ride chain always holds a Drive block; "inUse": false says the recording had no pedal, and the
					// server bypasses it in every scene rather than leaving a pedal the artist never used switched on.
					block["required"]!.AsArray().Add("inUse");
					block["properties"]!["inUse"] = JsonNode.Parse("""{"type":"boolean"}""");
				}
				if (ChannelBlocks.Contains(type))
				{
					// Strict structured output requires every property, so a single-channel block sends "channels":[].
					block["required"]!.AsArray().Add("channels");
					block["properties"]!["channels"] = new JsonObject
					{
						["type"] = "array",
						["maxItems"] = 3,
						["items"] = new JsonObject
						{
							["type"] = "object",
							["additionalProperties"] = false,
							["required"] = new JsonArray("channel", "parameters", "modelSelection"),
							["properties"] = new JsonObject
							{
								["channel"] = JsonNode.Parse("""{"type":"string","enum":["B","C","D"]}"""),
								["parameters"] = block["properties"]!["parameters"]!.DeepClone(),
								["modelSelection"] = CreateModelSelectionSchema()
							}
						}
					};
				}
			}
			return JsonSerializer.SerializeToElement(root);
		}

		private static JsonNode CreateModelSelectionSchema() => JsonNode.Parse("""
			{"type":"object","additionalProperties":false,
			 "required":["name","equipment","evidenceBasis","sourceUrl","adaptation"],
			 "properties":{
			   "name":{"type":"string","minLength":1},
			   "equipment":{"type":"string","minLength":1},
			   "evidenceBasis":{"type":"string","enum":["recording","live","inferred"]},
			   "sourceUrl":{"type":"string"},
			   "adaptation":{"type":"string","minLength":1}}}
			""")!;
	}
}
