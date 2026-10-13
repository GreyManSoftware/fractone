using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareDirectParameterValidation(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("parameterName")] string ParameterName,
		[property: JsonPropertyName("parameterId")] int ParameterId,
		[property: JsonPropertyName("baselineWireValue")] int BaselineWireValue,
		[property: JsonPropertyName("probeValue")] int ProbeValue,
		[property: JsonPropertyName("observedWireValue")] int ObservedWireValue,
		[property: JsonPropertyName("acceptedDirect")] bool AcceptedDirect,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareDirectParameterCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("mappings")] IReadOnlyList<Fm9HardwareDirectParameterValidation> Mappings);

	public sealed class Fm9HardwareDirectParameterCatalog
	{
		public const int CurrentSchemaVersion = 1;
		public const int ExpectedMappingCount = 45;

		private readonly IReadOnlySet<(string BlockType, int Instance, int ParameterId)> _accepted;

		private Fm9HardwareDirectParameterCatalog(
			Fm9HardwareDirectParameterCatalogDocument document,
			Fm9Firmware12Catalog firmwareCatalog,
			Fm9HardwareParameterCatalog parameterCatalog)
		{
			Document = document;
			Validate(document, firmwareCatalog, parameterCatalog);
			_accepted = document.Mappings
				.Where(mapping => mapping.AcceptedDirect)
				.Select(mapping => (
					mapping.BlockType,
					mapping.Instance,
					mapping.ParameterId))
				.ToHashSet(new MappingKeyComparer());
		}

		public Fm9HardwareDirectParameterCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareDirectParameterValidation> Mappings =>
			Document.Mappings;

		public bool IsAcceptedDirect(
			string blockType,
			int instance,
			int parameterId)
		{
			return _accepted.Contains((blockType, instance, parameterId));
		}

		public static async Task<Fm9HardwareDirectParameterCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareDirectParameterCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-direct-validation.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 direct-parameter catalog is unavailable.");
			Fm9HardwareDirectParameterCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareDirectParameterCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 direct-parameter catalog is empty.");
			Fm9Firmware12Catalog firmwareCatalog =
				await Fm9Firmware12Catalog.LoadBundledAsync(cancellationToken);
			Fm9HardwareParameterCatalog parameterCatalog =
				await Fm9HardwareParameterCatalog.LoadBundledAsync(cancellationToken);
			return new Fm9HardwareDirectParameterCatalog(
				document,
				firmwareCatalog,
				parameterCatalog);
		}

		private static void Validate(
			Fm9HardwareDirectParameterCatalogDocument document,
			Fm9Firmware12Catalog firmwareCatalog,
			Fm9HardwareParameterCatalog parameterCatalog)
		{
			if (document.SchemaVersion != CurrentSchemaVersion ||
				document.FirmwareVersion != Fm9Firmware12Catalog.FirmwareVersion ||
				document.Mappings.Count != ExpectedMappingCount ||
				document.Mappings.Select(mapping => (
					mapping.BlockType.ToUpperInvariant(),
					mapping.Instance,
					mapping.ParameterId)).Distinct().Count() != ExpectedMappingCount)
			{
				throw new InvalidDataException(
					"The FM9 direct-parameter catalog metadata is invalid.");
			}

			foreach (Fm9HardwareDirectParameterValidation mapping in document.Mappings)
			{
				string family = Fm9CatalogCapabilityInventory.GetParameterFamily(
					mapping.BlockType);
				Fm9ParameterDescriptor parameter = firmwareCatalog.GetParameterById(
					family,
					mapping.ParameterId);

				if (mapping.Instance != 1 ||
					mapping.EffectId != firmwareCatalog.ResolveEffectId(mapping.BlockType, 1) ||
					!string.Equals(
						mapping.ParameterName,
						parameter.Name,
						StringComparison.Ordinal) ||
					parameter.TypeCode != 16 ||
					!parameterCatalog.IsRejectedContinuousCandidate(
						mapping.BlockType,
						mapping.Instance,
						mapping.ParameterId) ||
					mapping.ProbeValue < 0 ||
					mapping.ObservedWireValue != mapping.ProbeValue ||
					!mapping.AcceptedDirect ||
					!mapping.BaselineRestored)
				{
					throw new InvalidDataException(
						$"The FM9 direct mapping '{mapping.BlockType} " +
						$"{mapping.ParameterName}' is invalid.");
				}
			}
		}

		private sealed class MappingKeyComparer :
			IEqualityComparer<(string BlockType, int Instance, int ParameterId)>
		{
			public bool Equals(
				(string BlockType, int Instance, int ParameterId) left,
				(string BlockType, int Instance, int ParameterId) right)
			{
				return left.Instance == right.Instance &&
					left.ParameterId == right.ParameterId &&
					string.Equals(
						left.BlockType,
						right.BlockType,
						StringComparison.OrdinalIgnoreCase);
			}

			public int GetHashCode(
				(string BlockType, int Instance, int ParameterId) value)
			{
				return HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(value.BlockType),
					value.Instance,
					value.ParameterId);
			}
		}
	}
}
