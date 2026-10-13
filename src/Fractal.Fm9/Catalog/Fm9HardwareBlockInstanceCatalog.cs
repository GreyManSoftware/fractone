using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareBlockInstance(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("gridBlockIdentifier")] int GridBlockIdentifier,
		[property: JsonPropertyName("parameterFamily")] string ParameterFamily,
		[property: JsonPropertyName("catalogParameterCount")] int CatalogParameterCount,
		[property: JsonPropertyName("accepted")] bool Accepted,
		[property: JsonPropertyName("bulkValueCount")] int? BulkValueCount,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored,
		[property: JsonPropertyName("failure")] string? Failure);

	public sealed record Fm9HardwareBlockInstanceCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("instances")] IReadOnlyList<Fm9HardwareBlockInstance> Instances);

	public sealed class Fm9HardwareBlockInstanceCatalog
	{
		public const int ExpectedInstanceCount = 141;
		public const int ExpectedAcceptedCount = 80;
		public const int ExpectedRejectedCount = 61;

		private Fm9HardwareBlockInstanceCatalog(
			Fm9HardwareBlockInstanceCatalogDocument document)
		{
			Document = document;
			Validate(document);
		}

		public Fm9HardwareBlockInstanceCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareBlockInstance> Instances => Document.Instances;
		public IReadOnlyList<Fm9HardwareBlockInstance> AcceptedInstances =>
			Instances.Where(instance => instance.Accepted).ToArray();

		private static readonly Lazy<Fm9HardwareBlockInstanceCatalog> BundledCatalog =
			new(() => LoadBundledAsync().GetAwaiter().GetResult());

		// The bundled catalog, loaded once, for code that names blocks by effect id.
		public static Fm9HardwareBlockInstanceCatalog Bundled => BundledCatalog.Value;

		public static async Task<Fm9HardwareBlockInstanceCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareBlockInstanceCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-block-instances.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 hardware block-instance catalog is unavailable.");
			Fm9HardwareBlockInstanceCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareBlockInstanceCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 hardware block-instance catalog is empty.");
			return new Fm9HardwareBlockInstanceCatalog(document);
		}

		private static void Validate(Fm9HardwareBlockInstanceCatalogDocument document)
		{
			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != Fm9Firmware12Catalog.FirmwareVersion ||
				document.Instances.Count != ExpectedInstanceCount ||
				document.Instances.Count(instance => instance.Accepted) != ExpectedAcceptedCount ||
				document.Instances.Count(instance => !instance.Accepted) != ExpectedRejectedCount ||
				document.Instances.Select(instance => instance.EffectId).Distinct().Count() !=
					ExpectedInstanceCount ||
				document.Instances.Any(instance =>
					string.IsNullOrWhiteSpace(instance.BlockType) ||
					instance.Instance < 1 ||
					instance.EffectId <= 0 ||
					instance.GridBlockIdentifier != (instance.EffectId & 0x7F) ||
					!instance.BaselineRestored ||
					instance.Accepted && instance.BulkValueCount is null or <= 0 ||
					!instance.Accepted && instance.BulkValueCount is not null))
			{
				throw new InvalidDataException(
					"The FM9 hardware block-instance catalog is invalid.");
			}
		}
	}
}
