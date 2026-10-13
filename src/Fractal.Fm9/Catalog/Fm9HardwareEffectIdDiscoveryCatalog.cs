using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareEffectIdSweepRange(
		[property: JsonPropertyName("firstEffectId")] int FirstEffectId,
		[property: JsonPropertyName("lastEffectId")] int LastEffectId,
		[property: JsonPropertyName("cataloguedIdsSkipped")] int CataloguedIdsSkipped,
		[property: JsonPropertyName("untestedIdsAttempted")] int UntestedIdsAttempted,
		[property: JsonPropertyName("acceptedIds")] int AcceptedIds);

	public sealed record Fm9HardwareEffectIdDiscovery(
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("renderedGridName")] string RenderedGridName,
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("parameterFamily")] string ParameterFamily,
		[property: JsonPropertyName("bulkValueCount")] int BulkValueCount,
		[property: JsonPropertyName("parameterColumns")] int ParameterColumns,
		[property: JsonPropertyName("parameterRows")] int ParameterRows,
		[property: JsonPropertyName("recordLength")] int RecordLength,
		[property: JsonPropertyName("serializedRecordSha256")] string SerializedRecordSha256,
		[property: JsonPropertyName("dumpSha256")] string DumpSha256,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareDiscoveredParameterQualification(
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("continuousPlanned")] int ContinuousPlanned,
		[property: JsonPropertyName("continuousAccepted")] int ContinuousAccepted,
		[property: JsonPropertyName("continuousRejected")] int ContinuousRejected,
		[property: JsonPropertyName("continuousRejectedParameters")] IReadOnlyList<string> ContinuousRejectedParameters,
		[property: JsonPropertyName("discretePlanned")] int DiscretePlanned,
		[property: JsonPropertyName("discreteAccepted")] int DiscreteAccepted,
		[property: JsonPropertyName("discreteRejected")] int DiscreteRejected,
		[property: JsonPropertyName("restoredMappings")] int RestoredMappings,
		[property: JsonPropertyName("continuousEvidenceSha256")] string ContinuousEvidenceSha256,
		[property: JsonPropertyName("discreteEvidenceSha256")] string DiscreteEvidenceSha256);

	public sealed record Fm9HardwareEffectIdDiscoveryDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("testedRanges")] IReadOnlyList<Fm9HardwareEffectIdSweepRange> TestedRanges,
		[property: JsonPropertyName("discoveries")] IReadOnlyList<Fm9HardwareEffectIdDiscovery> Discoveries,
		[property: JsonPropertyName("parameterQualifications")]
			IReadOnlyList<Fm9HardwareDiscoveredParameterQualification> ParameterQualifications);

	public sealed class Fm9HardwareEffectIdDiscoveryCatalog
	{
		private Fm9HardwareEffectIdDiscoveryCatalog(
			Fm9HardwareEffectIdDiscoveryDocument document)
		{
			Document = document;
			Validate(document);
		}

		public Fm9HardwareEffectIdDiscoveryDocument Document { get; }
		public IReadOnlyList<Fm9HardwareEffectIdDiscovery> Discoveries => Document.Discoveries;

		public static async Task<Fm9HardwareEffectIdDiscoveryCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareEffectIdDiscoveryCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-effect-id-discovery.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 effect-ID discovery catalog is unavailable.");
			Fm9HardwareEffectIdDiscoveryDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareEffectIdDiscoveryDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 effect-ID discovery catalog is empty.");
			return new Fm9HardwareEffectIdDiscoveryCatalog(document);
		}

		private static void Validate(Fm9HardwareEffectIdDiscoveryDocument document)
		{
			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != "12.00" ||
				document.TestedRanges.Count != 2 ||
				document.TestedRanges.Sum(range => range.UntestedIdsAttempted) != 242 ||
				document.TestedRanges.Sum(range => range.AcceptedIds) != 3 ||
				document.Discoveries.Count != 3 ||
				document.Discoveries.Select(discovery => discovery.EffectId).Distinct().Count() != 3 ||
				document.Discoveries.Any(discovery =>
					discovery.EffectId <= 0 ||
					string.IsNullOrWhiteSpace(discovery.RenderedGridName) ||
					string.IsNullOrWhiteSpace(discovery.BlockType) ||
					discovery.Instance < 1 ||
					string.IsNullOrWhiteSpace(discovery.ParameterFamily) ||
					discovery.BulkValueCount <= 0 ||
					discovery.ParameterColumns * discovery.ParameterRows !=
						discovery.BulkValueCount ||
					discovery.RecordLength != (23 + discovery.BulkValueCount) * sizeof(ushort) ||
					discovery.SerializedRecordSha256.Length != 64 ||
					discovery.DumpSha256.Length != 64 ||
					!discovery.BaselineRestored) ||
				document.ParameterQualifications.Count != 1 ||
				document.ParameterQualifications.Any(qualification =>
					qualification.EffectId != 138 ||
					qualification.BlockType != "Megatap Delay" ||
					qualification.Instance != 1 ||
					qualification.ContinuousPlanned !=
						qualification.ContinuousAccepted + qualification.ContinuousRejected ||
					qualification.DiscretePlanned !=
						qualification.DiscreteAccepted + qualification.DiscreteRejected ||
					qualification.RestoredMappings !=
						qualification.ContinuousPlanned + qualification.DiscretePlanned ||
					qualification.ContinuousRejectedParameters.Count !=
						qualification.ContinuousRejected ||
					qualification.ContinuousEvidenceSha256.Length != 64 ||
					qualification.DiscreteEvidenceSha256.Length != 64))
			{
				throw new InvalidDataException(
					"The FM9 effect-ID discovery catalog is invalid.");
			}
		}
	}
}
