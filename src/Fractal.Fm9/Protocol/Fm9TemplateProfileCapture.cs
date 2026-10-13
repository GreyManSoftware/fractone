using Fractal.Companion.Catalog;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fractal.Companion.Protocol
{
	public sealed record Fm9TemplateCaptureCandidate(
		int SchemaVersion,
		string FirmwareVersion,
		string TemplateSha256,
		IReadOnlyList<Fm9TemplateCaptureGridBlock> GridBlocks,
		IReadOnlyList<Fm9TemplateCaptureRecord> Records);

	public sealed record Fm9TemplateCaptureGridBlock(
		string BlockType,
		int Instance,
		ushort EffectId,
		int Row,
		int Column);

	public sealed record Fm9TemplateCaptureRecord(
		int Offset,
		ushort ParameterColumns,
		ushort ParameterRows,
		int Length);

	public sealed record Fm9TemplateProfileImport(
		int SchemaVersion,
		string FirmwareVersion,
		string TemplateSha256,
		IReadOnlyList<Fm9TemplateBlockDefinition> Blocks);

	public sealed record Fm9TemplateProfileScaffold(
		int SchemaVersion,
		string FirmwareVersion,
		string TemplateSha256,
		string Instructions,
		IReadOnlyList<Fm9TemplateProfileScaffoldBlock> Blocks,
		IReadOnlyList<Fm9TemplateCaptureRecord> RecordCandidates);

	public sealed record Fm9TemplateProfileScaffoldBlock(
		string BlockType,
		int Instance,
		ushort EffectId,
		ushort? ParameterColumns,
		ushort? ParameterRows,
		IReadOnlyList<Fm9TemplateParameterDefinition> Parameters,
		int? RecordOffset);

	public static class Fm9TemplateProfileCapture
	{
		public const int CurrentSchemaVersion = 1;

		public static Fm9TemplateCaptureCandidate Capture(
			ReadOnlySpan<byte> templateBytes,
			Fm9Firmware12Catalog catalog)
		{
			ArgumentNullException.ThrowIfNull(catalog);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(Fm9PresetDump.Parse(templateBytes));
			Dictionary<ushort, Fm9TemplateCaptureGridBlock> knownBlocks = [];

			foreach (Fm9BlockDescriptor descriptor in catalog.BlockDescriptors)
			{
				for (int instance = 1; instance <= descriptor.Instances; instance++)
				{
					knownBlocks.Add((ushort)catalog.ResolveEffectId(descriptor.Name, instance),
						new Fm9TemplateCaptureGridBlock(
							descriptor.Name, instance,
							(ushort)catalog.ResolveEffectId(descriptor.Name, instance), 0, 0));
				}
			}

			return new Fm9TemplateCaptureCandidate(
				CurrentSchemaVersion,
				Fm9Firmware12Catalog.FirmwareVersion,
				Convert.ToHexString(SHA256.HashData(templateBytes)),
				body.GridCells
					.Where(cell => cell.EffectId is not null && knownBlocks.ContainsKey((ushort)cell.EffectId.Value))
					.Select(cell => knownBlocks[(ushort)cell.EffectId!.Value] with
					{
						Row = cell.Row,
						Column = cell.Column
					})
					.OrderBy(block => block.Column).ThenBy(block => block.Row).ToArray(),
				body.BlockRecords.Select(record => new Fm9TemplateCaptureRecord(
					record.Offset, record.ParameterColumns, record.ParameterRows, record.Length)).ToArray());
		}

		public static Fm9TemplateProfileScaffold CreateScaffold(
			ReadOnlySpan<byte> templateBytes,
			Fm9Firmware12Catalog catalog)
		{
			ArgumentNullException.ThrowIfNull(catalog);
			Fm9TemplateCaptureCandidate capture = Capture(templateBytes, catalog);

			return new Fm9TemplateProfileScaffold(
				CurrentSchemaVersion,
				Fm9Firmware12Catalog.FirmwareVersion,
				capture.TemplateSha256,
				"Fill ParameterColumns, ParameterRows, and RecordOffset for every block only after " +
				"independently proving its binding. Select RecordOffset from RecordCandidates; no mapping " +
				"is inferred by this scaffold. Save the completed object without RecordCandidates as an " +
				"importable binding profile.",
				capture.GridBlocks.Select(block => new Fm9TemplateProfileScaffoldBlock(
					block.BlockType,
					block.Instance,
					block.EffectId,
					null,
					null,
					catalog.GetParametersForFamily(
						Fm9CatalogCapabilityInventory.GetParameterFamily(block.BlockType))
						.Where(parameter =>
							parameter.DisplayMinimum is not null && parameter.DisplayMaximum is not null)
						.Select(parameter => new Fm9TemplateParameterDefinition(
							parameter.Name,
							parameter.ParameterId,
							parameter.DisplayMinimum!.Value,
							parameter.DisplayMaximum!.Value,
							parameter.UsesLogarithmicScaling))
						.ToArray(),
					null)).ToArray(),
				capture.Records);
		}

		public static Fm9BlockRecordTemplateProfile Import(
			ReadOnlySpan<byte> templateBytes,
			Fm9TemplateProfileImport profile,
			Fm9Firmware12Catalog catalog)
		{
			ArgumentNullException.ThrowIfNull(profile);
			ArgumentNullException.ThrowIfNull(catalog);
			ValidateImportBindings(profile);

			if (profile.SchemaVersion != CurrentSchemaVersion ||
				!string.Equals(profile.FirmwareVersion, Fm9Firmware12Catalog.FirmwareVersion, StringComparison.Ordinal) ||
				!string.Equals(profile.TemplateSha256, Convert.ToHexString(SHA256.HashData(templateBytes)), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException(
					"The FM9 template profile does not match this exact firmware-12 template capture.");
			}

			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(Fm9PresetDump.Parse(templateBytes));

			foreach (Fm9TemplateBlockDefinition block in profile.Blocks)
			{
				int expectedEffectId = catalog.ResolveEffectId(block.BlockType, block.Instance);

				if (block.EffectId != expectedEffectId || block.RecordOffset is null)
				{
					throw new InvalidDataException(
						$"FM9 profile block '{block.BlockType} {block.Instance}' must declare its catalog effect ID and captured record offset.");
				}

				string family = Fm9CatalogCapabilityInventory.GetParameterFamily(block.BlockType);

				foreach (Fm9TemplateParameterDefinition parameter in block.Parameters)
				{
					Fm9ParameterDescriptor catalogParameter = catalog.GetParameterById(family, parameter.ParameterId);

					if (!string.Equals(parameter.Name, catalogParameter.Name, StringComparison.Ordinal) ||
						parameter.Minimum != catalogParameter.DisplayMinimum ||
						parameter.Maximum != catalogParameter.DisplayMaximum ||
						parameter.UsesLogarithmicScaling != catalogParameter.UsesLogarithmicScaling)
					{
						throw new InvalidDataException(
							$"FM9 profile parameter '{block.BlockType} {parameter.Name}' does not match the firmware catalog metadata.");
					}

					if (!Fm9CatalogCapabilityInventory.IsEligibleForProfilePromotion(
						block.BlockType,
						catalogParameter))
					{
						throw new InvalidDataException(
							$"FM9 profile parameter '{block.BlockType} {parameter.Name}' is not a verified continuous serialized control and cannot be promoted.");
					}
				}
			}

			return Fm9BlockRecordTemplateProfile.Create(body, profile.Blocks);
		}

		public static Fm9TemplateProfileImport LoadImport(string profilePath)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
			return ParseImport(File.ReadAllText(profilePath));
		}

		public static Fm9TemplateProfileImport ParseImport(string profileJson)
		{
			if (string.IsNullOrWhiteSpace(profileJson))
			{
				throw new InvalidDataException("The FM9 template profile is empty.");
			}

			using JsonDocument document = JsonDocument.Parse(profileJson);
			JsonElement root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException("The FM9 template profile must be a JSON object with a populated Blocks mapping.");
			}

			if (!TryGetPropertyIgnoreCase(root, "blocks", out JsonElement blocks))
			{
				if (TryGetPropertyIgnoreCase(root, "gridBlocks", out _) ||
					TryGetPropertyIgnoreCase(root, "records", out _))
				{
					throw new InvalidDataException(
						"This file is a --capture-fm9-template-profile capture artifact, not an importable binding profile. " +
						"Capture artifacts list gridBlocks and records but contain no proven Blocks mappings. Run " +
						"--scaffold-fm9-template-profile <template.syx>, then complete and independently validate " +
						"each block binding before using --validate-fm9-template-profile or --inspect.");
				}

				throw new InvalidDataException(
					"The FM9 template profile must contain a populated Blocks mapping. A capture artifact cannot be imported.");
			}

			if (blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
			{
				throw new InvalidDataException(
					"The FM9 template profile Blocks mapping must be a non-empty array of proven block bindings.");
			}

			if (ContainsUnboundScaffoldPlaceholder(blocks))
			{
				throw new InvalidDataException(
					"The FM9 template profile contains an unbound scaffold placeholder. Every block must declare " +
					"independently verified ParameterColumns, ParameterRows, and RecordOffset values.");
			}

			Fm9TemplateProfileImport profile = JsonSerializer.Deserialize<Fm9TemplateProfileImport>(
				profileJson,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
				?? throw new InvalidDataException("The FM9 template profile is empty.");
			ValidateImportBindings(profile);
			return profile;
		}

		private static bool ContainsUnboundScaffoldPlaceholder(JsonElement blocks)
		{
			foreach (JsonElement block in blocks.EnumerateArray())
			{
				if (block.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				foreach (string bindingProperty in new[] { "parameterColumns", "parameterRows", "recordOffset" })
				{
					if (TryGetPropertyIgnoreCase(block, bindingProperty, out JsonElement value) &&
						value.ValueKind == JsonValueKind.Null)
					{
						return true;
					}
				}
			}

			return false;
		}

		private static bool TryGetPropertyIgnoreCase(
			JsonElement objectElement,
			string name,
			out JsonElement value)
		{
			foreach (JsonProperty property in objectElement.EnumerateObject())
			{
				if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					value = property.Value;
					return true;
				}
			}

			value = default;
			return false;
		}

		private static void ValidateImportBindings(Fm9TemplateProfileImport profile)
		{
			if (profile.Blocks is null || profile.Blocks.Count == 0)
			{
				throw new InvalidDataException(
					"The FM9 template profile must contain a populated Blocks mapping of proven block bindings.");
			}

			foreach (Fm9TemplateBlockDefinition? block in profile.Blocks)
			{
				if (block is null)
				{
					throw new InvalidDataException("The FM9 template profile Blocks mapping cannot contain null entries.");
				}

				if (block.ParameterColumns == 0 || block.ParameterRows == 0 || block.RecordOffset is null)
				{
					throw new InvalidDataException(
						$"FM9 profile block '{block.BlockType} {block.Instance}' is an unbound scaffold placeholder. " +
						"It must declare independently verified ParameterColumns, ParameterRows, and RecordOffset values.");
				}

				if (block.Parameters is null)
				{
					throw new InvalidDataException(
						$"FM9 profile block '{block.BlockType} {block.Instance}' must declare a parameter mapping array.");
				}
			}
		}
	}
}
