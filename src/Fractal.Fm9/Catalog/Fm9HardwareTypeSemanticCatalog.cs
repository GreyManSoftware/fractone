using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fractal.Companion.Auditioning;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareTypeSemanticResult(
		[property: JsonPropertyName("ordinal")] int Ordinal,
		[property: JsonPropertyName("renderedName")] string RenderedName,
		[property: JsonPropertyName("classification")] string Classification,
		[property: JsonPropertyName("changedParameterIds")] IReadOnlyList<int> ChangedParameterIds,
		[property: JsonPropertyName("bulkSha256")] string BulkSha256,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareTypeSemanticSelector(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("parameterName")] string ParameterName,
		[property: JsonPropertyName("parameterId")] int ParameterId,
		[property: JsonPropertyName("enumerationCount")] int EnumerationCount,
		[property: JsonPropertyName("baselineWireValue")] int BaselineWireValue,
		[property: JsonPropertyName("context")] IReadOnlyList<Fm9TypeSemanticAuditContext>? Context,
		[property: JsonPropertyName("editorPage")] string? EditorPage,
		[property: JsonPropertyName("rawEvidenceSha256")] string RawEvidenceSha256,
		[property: JsonPropertyName("results")] IReadOnlyList<Fm9HardwareTypeSemanticResult> Results);

	public sealed record Fm9HardwareTypeSemanticDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("hardwareConfirmedCount")] int HardwareConfirmedCount,
		[property: JsonPropertyName("conflictCount")] int ConflictCount,
		[property: JsonPropertyName("selectors")] IReadOnlyList<Fm9HardwareTypeSemanticSelector> Selectors);

	public sealed class Fm9HardwareTypeSemanticCatalog
	{
		private Fm9HardwareTypeSemanticCatalog(
			Fm9HardwareTypeSemanticDocument document)
		{
			Document = document;
			Validate(document);
		}

		public Fm9HardwareTypeSemanticDocument Document { get; }
		public IReadOnlyList<Fm9HardwareTypeSemanticSelector> Selectors =>
			Document.Selectors;

		public string ResolveRenderedName(
			string blockType,
			string parameterName,
			int ordinal)
		{
			Fm9HardwareTypeSemanticSelector selector = Selectors.SingleOrDefault(candidate =>
				string.Equals(
					candidate.BlockType,
					blockType,
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(
					candidate.ParameterName,
					parameterName,
					StringComparison.OrdinalIgnoreCase))
				?? throw new KeyNotFoundException(
					$"No hardware-confirmed FM9 type semantics exist for " +
					$"'{blockType}.{parameterName}'.");
			return selector.Results.SingleOrDefault(result => result.Ordinal == ordinal)
				?.RenderedName
				?? throw new KeyNotFoundException(
					$"No hardware-confirmed FM9 type semantic exists for " +
					$"'{blockType}.{parameterName}' ordinal {ordinal}.");
		}

		public static async Task<Fm9HardwareTypeSemanticCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareTypeSemanticCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-type-semantics.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 type-semantic catalog is unavailable.");
			Fm9HardwareTypeSemanticDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareTypeSemanticDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 type-semantic catalog is empty.");
			return new Fm9HardwareTypeSemanticCatalog(document);
		}

		private static void Validate(Fm9HardwareTypeSemanticDocument document)
		{
			int resultCount = document.Selectors.Sum(selector => selector.Results.Count);

			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != "12.00" ||
				document.Selectors.Count == 0 ||
				document.Selectors.Select(selector => (
					selector.BlockType.ToUpperInvariant(),
					selector.ParameterName.ToUpperInvariant())).Distinct().Count() !=
					document.Selectors.Count ||
				document.HardwareConfirmedCount != resultCount ||
				document.ConflictCount != 0 ||
				document.Selectors.Any(selector =>
					string.IsNullOrWhiteSpace(selector.BlockType) ||
					selector.Instance != 1 ||
					selector.EffectId <= 0 ||
					string.IsNullOrWhiteSpace(selector.ParameterName) ||
					selector.ParameterId < 0 ||
					selector.EnumerationCount != selector.Results.Count ||
					selector.BaselineWireValue < 0 ||
					selector.RawEvidenceSha256.Length != 64 ||
					selector.Results.Select(result => result.Ordinal).Distinct().Count() !=
						selector.Results.Count ||
					!selector.Results.Select(result => result.Ordinal)
						.Order()
						.SequenceEqual(Enumerable.Range(
							selector.Results.Min(result => result.Ordinal),
							selector.EnumerationCount)) ||
					selector.Results.Any(result =>
						result.Ordinal < 0 ||
						string.IsNullOrWhiteSpace(result.RenderedName) ||
						result.Classification != "HardwareConfirmed" ||
						result.ChangedParameterIds.Any(parameterId => parameterId < 0) ||
						result.BulkSha256.Length != 64 ||
						!result.BaselineRestored)))
			{
				throw new InvalidDataException(
					"The FM9 type-semantic catalog is invalid.");
			}
		}
	}
}
