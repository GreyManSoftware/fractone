using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareBlockSemanticResult(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("renderedBlockIdentity")] string RenderedBlockIdentity,
		[property: JsonPropertyName("classification")] string Classification,
		[property: JsonPropertyName("dumpSha256")] string DumpSha256,
		[property: JsonPropertyName("serializedRecordSha256")] string SerializedRecordSha256,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareBlockSemanticDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("exactCount")] int ExactCount,
		[property: JsonPropertyName("catalogAliasCount")] int CatalogAliasCount,
		[property: JsonPropertyName("conflictCount")] int ConflictCount,
		[property: JsonPropertyName("results")] IReadOnlyList<Fm9HardwareBlockSemanticResult> Results);

	public sealed class Fm9HardwareBlockSemanticCatalog
	{
		private Fm9HardwareBlockSemanticCatalog(
			Fm9HardwareBlockSemanticDocument document)
		{
			Document = document;
			Validate(document);
		}

		public Fm9HardwareBlockSemanticDocument Document { get; }
		public IReadOnlyList<Fm9HardwareBlockSemanticResult> Results => Document.Results;

		public static async Task<Fm9HardwareBlockSemanticCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareBlockSemanticCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-block-semantics.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 block-semantic catalog is unavailable.");
			Fm9HardwareBlockSemanticDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareBlockSemanticDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 block-semantic catalog is empty.");
			return new Fm9HardwareBlockSemanticCatalog(document);
		}

		private static void Validate(Fm9HardwareBlockSemanticDocument document)
		{
			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != "12.00" ||
				document.Results.Count != 37 ||
				document.ExactCount != 32 ||
				document.CatalogAliasCount != 5 ||
				document.ConflictCount != 0 ||
				document.Results.Select(result => result.EffectId).Distinct().Count() != 37 ||
				document.Results.Count(result => result.Classification == "Exact") !=
					document.ExactCount ||
				document.Results.Count(result => result.Classification == "CatalogAlias") !=
					document.CatalogAliasCount ||
				document.Results.Any(result =>
					string.IsNullOrWhiteSpace(result.BlockType) ||
					result.Instance != 1 ||
					result.EffectId <= 0 ||
					string.IsNullOrWhiteSpace(result.RenderedBlockIdentity) ||
					result.Classification is not ("Exact" or "CatalogAlias") ||
					result.DumpSha256.Length != 64 ||
					result.SerializedRecordSha256.Length != 64 ||
					!result.BaselineRestored))
			{
				throw new InvalidDataException(
					"The FM9 block-semantic catalog is invalid.");
			}
		}
	}
}
