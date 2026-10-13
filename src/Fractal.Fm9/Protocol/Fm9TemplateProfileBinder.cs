using Fractal.Companion.Catalog;
using System.Security.Cryptography;

namespace Fractal.Companion.Protocol
{
	public sealed record Fm9TemplateBindingEvidence(
		string Confidence,
		IReadOnlyList<string> Evidence);

	public sealed record Fm9TemplateBindingAssessment(
		string BlockType,
		int Instance,
		ushort EffectId,
		IReadOnlyList<int> CandidateOffsets,
		Fm9TemplateBindingEvidence Evidence,
		Fm9TemplateBlockDefinition? PromotedBinding);

	public sealed record Fm9TemplateBindingResult(
		int SchemaVersion,
		string FirmwareVersion,
		string TemplateSha256,
		IReadOnlyList<Fm9TemplateBindingAssessment> Bindings)
	{
		public Fm9TemplateProfileImport ToImport()
		{
			return new Fm9TemplateProfileImport(
				SchemaVersion,
				FirmwareVersion,
				TemplateSha256,
				Bindings
					.Where(binding => binding.PromotedBinding is not null)
					.Select(binding => binding.PromotedBinding!)
					.ToArray());
		}
	}

	public static class Fm9TemplateProfileBinder
	{
		private sealed record DocumentedLayout(
			string BlockType,
			ushort ParameterColumns,
			ushort ParameterRows,
			IReadOnlyList<(int ParameterId, ushort ExpectedValue)> CurrentValueSignatures);

		private static readonly IReadOnlyList<DocumentedLayout> RideBaselineLayouts =
		[
			new("Input", 10, 4, []),
			new("Gate/Expander", 19, 4, []),
			new("Drive", 44, 4, []),
			new("Amp", 147, 4, [(10, 314)]),
			new("Cab", 106, 4, [(0, 1), (4, 1018), (62, 39455), (66, 42808)]),
			new("Parametric EQ", 33, 4, []),
			new("Reverb", 73, 4, []),
			new("Output", 26, 4, [])
		];

		public static Fm9TemplateBindingResult Bind(
			ReadOnlySpan<byte> templateBytes,
			Fm9Firmware12Catalog catalog)
		{
			ArgumentNullException.ThrowIfNull(catalog);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(Fm9PresetDump.Parse(templateBytes));
			return Bind(
				body,
				catalog,
				Convert.ToHexString(SHA256.HashData(templateBytes)));
		}

		public static Fm9TemplateBindingResult Bind(
			Fm9PresetBody body,
			Fm9Firmware12Catalog catalog,
			string templateSha256)
		{
			ArgumentNullException.ThrowIfNull(body);
			ArgumentNullException.ThrowIfNull(catalog);
			ArgumentException.ThrowIfNullOrWhiteSpace(templateSha256);
			List<Fm9TemplateBindingAssessment> bindings = [];

			foreach (DocumentedLayout layout in RideBaselineLayouts)
			{
				int effectId = catalog.ResolveEffectId(layout.BlockType);
				List<int> candidateOffsets = body.BlockRecords
					.Where(record => record.ParameterColumns == layout.ParameterColumns &&
						record.ParameterRows == layout.ParameterRows)
					.Select(record => record.Offset)
					.ToList();
				List<string> evidence =
				[
					$"Catalog effect ID {effectId} is present exactly {body.GridCells.Count(cell => cell.EffectId == effectId)} time(s) on the template grid.",
					$"Documented serialized layout is {layout.ParameterColumns} columns × {layout.ParameterRows} channels; it has {candidateOffsets.Count} record candidate(s)."
				];
				Fm9PresetBlockRecord? record = candidateOffsets.Count == 1
					? body.BlockRecords.Single(candidate => candidate.Offset == candidateOffsets[0])
					: null;

				if (record is not null && HasCurrentValueSignatures(body, record, layout, evidence))
				{
					bool gridIsUnique = body.GridCells.Count(cell => cell.EffectId == effectId) == 1;
					Fm9TemplateBlockDefinition? promoted = gridIsUnique
						? new Fm9TemplateBlockDefinition(
							layout.BlockType,
							1,
							(ushort)effectId,
							layout.ParameterColumns,
							layout.ParameterRows,
							GetSerializableParameters(catalog, layout.BlockType, layout.ParameterColumns),
							record.Offset,
							new Fm9TemplateBindingEvidence("Unambiguous", evidence))
						: null;
					bindings.Add(new Fm9TemplateBindingAssessment(
						layout.BlockType,
						1,
						(ushort)effectId,
						candidateOffsets,
						new Fm9TemplateBindingEvidence(
							promoted is null ? "Unresolved" : "Unambiguous",
							evidence),
						promoted));
					continue;
				}

				if (record is not null)
				{
					evidence.Add("The current-value signature does not match this documented Ride baseline.");
				}

				bindings.Add(new Fm9TemplateBindingAssessment(
					layout.BlockType,
					1,
					(ushort)effectId,
					candidateOffsets,
					new Fm9TemplateBindingEvidence("Unresolved", evidence),
					null));
			}

			return new Fm9TemplateBindingResult(
				Fm9TemplateProfileCapture.CurrentSchemaVersion,
				Fm9Firmware12Catalog.FirmwareVersion,
				templateSha256,
				bindings);
		}

		private static bool HasCurrentValueSignatures(
			Fm9PresetBody body,
			Fm9PresetBlockRecord record,
			DocumentedLayout layout,
			List<string> evidence)
		{
			foreach ((int parameterId, ushort expectedValue) in layout.CurrentValueSignatures)
			{
				ushort actualValue = ReadParameter(body.Bytes.Span, record, parameterId);

				if (actualValue != expectedValue)
				{
					evidence.Add(
						$"Current parameter signature {parameterId} is {actualValue}, expected {expectedValue}.");
					return false;
				}

				evidence.Add(
					$"Current parameter signature {parameterId} is {actualValue}, matching the captured Ride baseline.");
			}

			return true;
		}

		private static IReadOnlyList<Fm9TemplateParameterDefinition> GetSerializableParameters(
			Fm9Firmware12Catalog catalog,
			string blockType,
			ushort parameterColumns)
		{
			string family = Fm9CatalogCapabilityInventory.GetParameterFamily(blockType);

			return catalog.GetParametersForFamily(family)
				.Where(parameter => parameter.ParameterId >= 0 &&
					parameter.ParameterId < parameterColumns &&
					Fm9CatalogCapabilityInventory.IsEligibleForProfilePromotion(blockType, parameter))
				.Select(parameter => new Fm9TemplateParameterDefinition(
					parameter.Name,
					parameter.ParameterId,
					parameter.DisplayMinimum!.Value,
					parameter.DisplayMaximum!.Value,
					parameter.UsesLogarithmicScaling))
				.ToArray();
		}

		private static ushort ReadParameter(
			ReadOnlySpan<byte> bytes,
			Fm9PresetBlockRecord record,
			int parameterId)
		{
			int wordOffset = parameterId < 6
				? 17 + parameterId
				: 23 + parameterId - 6;
			int offset = record.Offset + (wordOffset * sizeof(ushort));
			return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
		}
	}
}
