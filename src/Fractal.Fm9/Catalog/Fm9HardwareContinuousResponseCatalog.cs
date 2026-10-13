using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareNormalizedResponsePoint(
		[property: JsonPropertyName("requestedWireValue")] int RequestedWireValue,
		[property: JsonPropertyName("observedWireValue")] int ObservedWireValue);

	public sealed record Fm9HardwareDisplayValueResponsePoint(
		[property: JsonPropertyName("requestedValue")] double RequestedValue,
		[property: JsonPropertyName("observedWireValue")] int ObservedWireValue);

	public sealed record Fm9HardwareContinuousResponseValidation(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("parameterName")] string ParameterName,
		[property: JsonPropertyName("parameterId")] int ParameterId,
		[property: JsonPropertyName("baselineWireValue")] int BaselineWireValue,
		[property: JsonPropertyName("normalizedPoints")] IReadOnlyList<Fm9HardwareNormalizedResponsePoint> NormalizedPoints,
		[property: JsonPropertyName("displayValuePoints")] IReadOnlyList<Fm9HardwareDisplayValueResponsePoint> DisplayValuePoints,
		[property: JsonPropertyName("classification")] string Classification,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareContinuousResponseCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("mappings")] IReadOnlyList<Fm9HardwareContinuousResponseValidation> Mappings);

	public sealed class Fm9HardwareContinuousResponseCatalog
	{
		public const string ReadOnlyOrNoOp = "ReadOnlyOrNoOp";
		public const string UnsupportedEncoding = "UnsupportedEncoding";
		public const int ExpectedMappingCount = 4;

		private Fm9HardwareContinuousResponseCatalog(
			Fm9HardwareContinuousResponseCatalogDocument document,
			Fm9Firmware12Catalog firmwareCatalog,
			Fm9HardwareParameterCatalog parameterCatalog)
		{
			Document = document;
			Validate(document, firmwareCatalog, parameterCatalog);
		}

		public Fm9HardwareContinuousResponseCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareContinuousResponseValidation> Mappings =>
			Document.Mappings;

		public static async Task<Fm9HardwareContinuousResponseCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareContinuousResponseCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-continuous-response-validation.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 continuous-response catalog is unavailable.");
			Fm9HardwareContinuousResponseCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareContinuousResponseCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 continuous-response catalog is empty.");
			Fm9Firmware12Catalog firmwareCatalog =
				await Fm9Firmware12Catalog.LoadBundledAsync(cancellationToken);
			Fm9HardwareParameterCatalog parameterCatalog =
				await Fm9HardwareParameterCatalog.LoadBundledAsync(cancellationToken);
			return new Fm9HardwareContinuousResponseCatalog(
				document,
				firmwareCatalog,
				parameterCatalog);
		}

		private static void Validate(
			Fm9HardwareContinuousResponseCatalogDocument document,
			Fm9Firmware12Catalog firmwareCatalog,
			Fm9HardwareParameterCatalog parameterCatalog)
		{
			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != Fm9Firmware12Catalog.FirmwareVersion ||
				document.Mappings.Count != ExpectedMappingCount ||
				document.Mappings.Count(mapping => mapping.Classification == ReadOnlyOrNoOp) != 3 ||
				document.Mappings.Count(mapping => mapping.Classification == UnsupportedEncoding) != 1)
			{
				throw new InvalidDataException(
					"The FM9 continuous-response catalog metadata is invalid.");
			}

			foreach (Fm9HardwareContinuousResponseValidation mapping in document.Mappings)
			{
				string family = Fm9CatalogCapabilityInventory.GetParameterFamily(
					mapping.BlockType);
				Fm9ParameterDescriptor parameter = firmwareCatalog.GetParameterById(
					family,
					mapping.ParameterId);
				bool isNoOp = mapping.NormalizedPoints.All(point =>
						point.ObservedWireValue == mapping.BaselineWireValue) &&
					mapping.DisplayValuePoints.All(point =>
						point.ObservedWireValue == mapping.BaselineWireValue);

				if (mapping.Instance != 1 ||
					mapping.EffectId != firmwareCatalog.ResolveEffectId(mapping.BlockType, 1) ||
					!string.Equals(
						mapping.ParameterName,
						parameter.Name,
						StringComparison.Ordinal) ||
					parameter.TypeCode != 0 ||
					!parameterCatalog.IsRejectedContinuousCandidate(
						mapping.BlockType,
						mapping.Instance,
						mapping.ParameterId) ||
					mapping.NormalizedPoints.Count != 5 ||
					mapping.NormalizedPoints.Any(point =>
						point.RequestedWireValue is < 0 or > 65_534 ||
						point.ObservedWireValue is < 0 or > 65_534) ||
					mapping.DisplayValuePoints.Any(point =>
						!double.IsFinite(point.RequestedValue) ||
						point.ObservedWireValue is < 0 or > 65_534) ||
					mapping.Classification == ReadOnlyOrNoOp && !isNoOp ||
					mapping.Classification == UnsupportedEncoding && isNoOp ||
					!mapping.BaselineRestored)
				{
					throw new InvalidDataException(
						$"The FM9 continuous response mapping '{mapping.BlockType} " +
						$"{mapping.ParameterName}' is invalid.");
				}
			}
		}
	}
}
