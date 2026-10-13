using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareDiscreteValidation(
		[property: JsonPropertyName("parameterName")] string ParameterName,
		[property: JsonPropertyName("parameterId")] int ParameterId,
		[property: JsonPropertyName("enumerationCount")] int EnumerationCount,
		[property: JsonPropertyName("baselineWireValue")] int BaselineWireValue,
		[property: JsonPropertyName("probeOrdinal")] int ProbeOrdinal,
		[property: JsonPropertyName("observedWireValue")] int? ObservedWireValue,
		[property: JsonPropertyName("acceptedDiscrete")] bool AcceptedDiscrete,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareDiscreteFamilyValidation(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("plannedMappings")] int PlannedMappings,
		[property: JsonPropertyName("acceptedMappings")] int AcceptedMappings,
		[property: JsonPropertyName("rejectedMappings")] int RejectedMappings,
		[property: JsonPropertyName("mappings")] IReadOnlyList<Fm9HardwareDiscreteValidation> Mappings);

	public sealed record Fm9HardwareDiscreteCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("families")] IReadOnlyList<Fm9HardwareDiscreteFamilyValidation> Families);

	public sealed class Fm9HardwareDiscreteCatalog
	{
		public const int ExpectedFamilyCount = 35;
		public const int ExpectedMappingCount = 309;
		public const int ExpectedAcceptedCount = 307;
		public const int ExpectedRejectedCount = 2;

		private readonly IReadOnlyDictionary<(string BlockType, int Instance), Fm9HardwareDiscreteFamilyValidation>
			_families;

		private Fm9HardwareDiscreteCatalog(Fm9HardwareDiscreteCatalogDocument document)
		{
			Document = document;
			Validate(document);
			_families = document.Families.ToDictionary(
				family => (family.BlockType, family.Instance),
				family => family,
				new BlockInstanceComparer());
		}

		public Fm9HardwareDiscreteCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareDiscreteFamilyValidation> Families => Document.Families;

		public bool IsAccepted(string blockType, int instance, int parameterId)
		{
			return _families.TryGetValue(
					(blockType, instance),
					out Fm9HardwareDiscreteFamilyValidation? family) &&
				family.Mappings.Any(mapping =>
					mapping.ParameterId == parameterId &&
					mapping.AcceptedDiscrete);
		}

		public bool IsRejected(string blockType, int instance, int parameterId)
		{
			return _families.TryGetValue(
					(blockType, instance),
					out Fm9HardwareDiscreteFamilyValidation? family) &&
				family.Mappings.Any(mapping =>
					mapping.ParameterId == parameterId &&
					!mapping.AcceptedDiscrete);
		}

		public static async Task<Fm9HardwareDiscreteCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareDiscreteCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-discrete-validation.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 hardware discrete catalog is unavailable.");
			Fm9HardwareDiscreteCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareDiscreteCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 hardware discrete catalog is empty.");
			return new Fm9HardwareDiscreteCatalog(document);
		}

		private static void Validate(Fm9HardwareDiscreteCatalogDocument document)
		{
			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != Fm9Firmware12Catalog.FirmwareVersion ||
				document.Families.Count != ExpectedFamilyCount ||
				document.Families.Sum(family => family.PlannedMappings) != ExpectedMappingCount ||
				document.Families.Sum(family => family.AcceptedMappings) != ExpectedAcceptedCount ||
				document.Families.Sum(family => family.RejectedMappings) != ExpectedRejectedCount)
			{
				throw new InvalidDataException(
					"The FM9 hardware discrete catalog metadata is invalid.");
			}

			foreach (Fm9HardwareDiscreteFamilyValidation family in document.Families)
			{
				if (family.Instance != 1 ||
					family.PlannedMappings != family.Mappings.Count ||
					family.AcceptedMappings != family.Mappings.Count(mapping => mapping.AcceptedDiscrete) ||
					family.RejectedMappings != family.Mappings.Count(mapping => !mapping.AcceptedDiscrete) ||
					family.Mappings.Any(mapping =>
						mapping.ParameterId < 0 ||
						mapping.EnumerationCount <= 1 ||
						mapping.ProbeOrdinal < 0 ||
						mapping.ProbeOrdinal >= mapping.EnumerationCount ||
						!mapping.BaselineRestored ||
						mapping.AcceptedDiscrete &&
							mapping.ObservedWireValue != mapping.ProbeOrdinal))
				{
					throw new InvalidDataException(
						$"The FM9 hardware discrete family '{family.BlockType}' is invalid.");
				}
			}
		}

		private sealed class BlockInstanceComparer :
			IEqualityComparer<(string BlockType, int Instance)>
		{
			public bool Equals(
				(string BlockType, int Instance) left,
				(string BlockType, int Instance) right)
			{
				return left.Instance == right.Instance &&
					string.Equals(left.BlockType, right.BlockType, StringComparison.OrdinalIgnoreCase);
			}

			public int GetHashCode((string BlockType, int Instance) value)
			{
				return HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(value.BlockType),
					value.Instance);
			}
		}
	}
}
