using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareParameterValidation(
		[property: JsonPropertyName("parameterName")] string ParameterName,
		[property: JsonPropertyName("parameterId")] int ParameterId,
		[property: JsonPropertyName("baselineWireValue")] int BaselineWireValue,
		[property: JsonPropertyName("probeWireValue")] int ProbeWireValue,
		[property: JsonPropertyName("observedWireValue")] int ObservedWireValue,
		[property: JsonPropertyName("acceptedContinuous")] bool AcceptedContinuous,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareParameterFamilyValidation(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("plannedMappings")] int PlannedMappings,
		[property: JsonPropertyName("acceptedMappings")] int AcceptedMappings,
		[property: JsonPropertyName("rejectedMappings")] int RejectedMappings,
		[property: JsonPropertyName("mappings")] IReadOnlyList<Fm9HardwareParameterValidation> Mappings);

	public sealed record Fm9HardwareParameterCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("families")] IReadOnlyList<Fm9HardwareParameterFamilyValidation> Families);

	public sealed class Fm9HardwareParameterCatalog
	{
		public const int CurrentSchemaVersion = 1;
		public const int ExpectedFamilyCount = 35;
		public const int ExpectedMappingCount = 897;
		public const int ExpectedAcceptedCount = 848;
		public const int ExpectedRejectedCount = 49;

		private readonly IReadOnlyDictionary<(string BlockType, int Instance), Fm9HardwareParameterFamilyValidation>
			_families;

		private Fm9HardwareParameterCatalog(Fm9HardwareParameterCatalogDocument document)
		{
			Document = document;
			Validate(document);
			_families = document.Families.ToDictionary(
				family => (family.BlockType, family.Instance),
				family => family,
				new BlockInstanceComparer());
		}

		public Fm9HardwareParameterCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareParameterFamilyValidation> Families => Document.Families;

		public bool IsAcceptedContinuous(
			string blockType,
			int instance,
			int parameterId)
		{
			return _families.TryGetValue(
					(blockType, instance),
					out Fm9HardwareParameterFamilyValidation? family) &&
				family.Mappings.Any(mapping =>
					mapping.ParameterId == parameterId &&
					mapping.AcceptedContinuous);
		}

		public bool IsRejectedContinuousCandidate(
			string blockType,
			int instance,
			int parameterId)
		{
			return _families.TryGetValue(
					(blockType, instance),
					out Fm9HardwareParameterFamilyValidation? family) &&
				family.Mappings.Any(mapping =>
					mapping.ParameterId == parameterId &&
					!mapping.AcceptedContinuous);
		}

		public Fm9HardwareParameterFamilyValidation GetFamily(
			string blockType,
			int instance)
		{
			return _families.TryGetValue(
					(blockType, instance),
					out Fm9HardwareParameterFamilyValidation? family)
				? family
				: throw new KeyNotFoundException(
					$"No hardware parameter validation exists for '{blockType} {instance}'.");
		}

		public static async Task<Fm9HardwareParameterCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			return await LoadBundledDocumentAsync(cancellationToken);
		}

		public static Fm9HardwareParameterCatalog LoadBundled()
		{
			Assembly assembly = typeof(Fm9HardwareParameterCatalog).Assembly;
			using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-parameter-validation.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 hardware parameter catalog is unavailable.");
			Fm9HardwareParameterCatalogDocument document =
				JsonSerializer.Deserialize<Fm9HardwareParameterCatalogDocument>(stream)
				?? throw new InvalidDataException(
					"The bundled FM9 hardware parameter catalog is empty.");
			return new Fm9HardwareParameterCatalog(document);
		}

		private static async Task<Fm9HardwareParameterCatalog> LoadBundledDocumentAsync(
			CancellationToken cancellationToken)
		{
			Assembly assembly = typeof(Fm9HardwareParameterCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-parameter-validation.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 hardware parameter catalog is unavailable.");
			Fm9HardwareParameterCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareParameterCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 hardware parameter catalog is empty.");
			return new Fm9HardwareParameterCatalog(document);
		}

		private static void Validate(Fm9HardwareParameterCatalogDocument document)
		{
			if (document.SchemaVersion != CurrentSchemaVersion ||
				!string.Equals(
					document.FirmwareVersion,
					Fm9Firmware12Catalog.FirmwareVersion,
					StringComparison.Ordinal) ||
				document.Families.Count != ExpectedFamilyCount)
			{
				throw new InvalidDataException(
					"The FM9 hardware parameter catalog metadata is invalid.");
			}

			HashSet<int> effectIds = [];
			HashSet<string> blockInstances = new(StringComparer.OrdinalIgnoreCase);
			int mappingCount = 0;
			int acceptedCount = 0;
			int rejectedCount = 0;

			foreach (Fm9HardwareParameterFamilyValidation family in document.Families)
			{
				if (string.IsNullOrWhiteSpace(family.BlockType) ||
					family.Instance != 1 ||
					family.EffectId <= 0 ||
					!effectIds.Add(family.EffectId) ||
					!blockInstances.Add($"{family.BlockType}:{family.Instance}") ||
					family.PlannedMappings != family.Mappings.Count ||
					family.AcceptedMappings != family.Mappings.Count(mapping =>
						mapping.AcceptedContinuous) ||
					family.RejectedMappings != family.Mappings.Count(mapping =>
						!mapping.AcceptedContinuous) ||
					family.Mappings.Select(mapping => mapping.ParameterId).Distinct().Count() !=
						family.Mappings.Count)
				{
					throw new InvalidDataException(
						$"The FM9 hardware parameter family '{family.BlockType} {family.Instance}' is invalid.");
				}

				foreach (Fm9HardwareParameterValidation mapping in family.Mappings)
				{
					if (string.IsNullOrWhiteSpace(mapping.ParameterName) ||
						mapping.ParameterId < 0 ||
						mapping.BaselineWireValue is < 0 or > 65_534 ||
						mapping.ProbeWireValue is < 0 or > 65_534 ||
						mapping.ObservedWireValue is < 0 or > 65_534 ||
						!mapping.BaselineRestored ||
						mapping.AcceptedContinuous &&
							mapping.ObservedWireValue != mapping.ProbeWireValue)
					{
						throw new InvalidDataException(
							$"The FM9 hardware parameter mapping '{family.BlockType} {mapping.ParameterName}' is invalid.");
					}
				}

				mappingCount += family.PlannedMappings;
				acceptedCount += family.AcceptedMappings;
				rejectedCount += family.RejectedMappings;
			}

			if (mappingCount != ExpectedMappingCount ||
				acceptedCount != ExpectedAcceptedCount ||
				rejectedCount != ExpectedRejectedCount)
			{
				throw new InvalidDataException(
					"The FM9 hardware parameter catalog totals are invalid.");
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
					string.Equals(
						left.BlockType,
						right.BlockType,
						StringComparison.OrdinalIgnoreCase);
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
