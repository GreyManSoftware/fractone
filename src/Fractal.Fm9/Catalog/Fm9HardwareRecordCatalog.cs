using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareRecordLayout(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("catalogParameterCount")] int CatalogParameterCount,
		[property: JsonPropertyName("bulkValueCount")] int BulkValueCount,
		[property: JsonPropertyName("parameterColumns")] int ParameterColumns,
		[property: JsonPropertyName("parameterRows")] int ParameterRows,
		[property: JsonPropertyName("recordLength")] int RecordLength,
		[property: JsonPropertyName("sceneStatePrefixBase64")] string SceneStatePrefixBase64,
		[property: JsonPropertyName("serializedRecordBase64")] string SerializedRecordBase64,
		[property: JsonPropertyName("serializedRecordSha256")] string SerializedRecordSha256,
		[property: JsonPropertyName("defaultBulkValues")] IReadOnlyList<int> DefaultBulkValues,
		[property: JsonPropertyName("bulkMatchesSerialized")] bool BulkMatchesSerialized,
		[property: JsonPropertyName("mismatchIndices")] IReadOnlyList<int> MismatchIndices);

	public sealed record Fm9HardwareRecordCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("parameterStartWord")] int ParameterStartWord,
		[property: JsonPropertyName("recordFooterWords")] int RecordFooterWords,
		[property: JsonPropertyName("families")] IReadOnlyList<Fm9HardwareRecordLayout> Families);

	public sealed class Fm9HardwareRecordCatalog
	{
		public const int CurrentSchemaVersion = 1;
		public const int ExpectedFamilyCount = 36;

		private readonly IReadOnlyDictionary<(string BlockType, int Instance), Fm9HardwareRecordLayout> _layouts;

		private Fm9HardwareRecordCatalog(Fm9HardwareRecordCatalogDocument document)
		{
			Document = document;
			Validate(document);
			_layouts = document.Families.ToDictionary(
				layout => (layout.BlockType, layout.Instance),
				layout => layout,
				new BlockInstanceComparer());
		}

		public Fm9HardwareRecordCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareRecordLayout> Layouts => Document.Families;

		public Fm9HardwareRecordLayout GetLayout(string blockType, int instance)
		{
			if (string.IsNullOrWhiteSpace(blockType))
			{
				throw new ArgumentException("An FM9 block type is required.", nameof(blockType));
			}

			return _layouts.TryGetValue((blockType, instance), out Fm9HardwareRecordLayout? layout)
				? layout
				: throw new KeyNotFoundException(
					$"No hardware-qualified FM9 record layout exists for '{blockType} {instance}'.");
		}

		public static async Task<Fm9HardwareRecordCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareRecordCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-record-layout.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 hardware record-layout catalog is unavailable.");
			Fm9HardwareRecordCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareRecordCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 hardware record-layout catalog is empty.");
			return new Fm9HardwareRecordCatalog(document);
		}

		private static void Validate(Fm9HardwareRecordCatalogDocument document)
		{
			if (document.SchemaVersion != CurrentSchemaVersion ||
				!string.Equals(
					document.FirmwareVersion,
					Fm9Firmware12Catalog.FirmwareVersion,
					StringComparison.Ordinal) ||
				document.ParameterStartWord != 17 ||
				document.RecordFooterWords != 6 ||
				document.Families.Count != ExpectedFamilyCount)
			{
				throw new InvalidDataException(
					"The FM9 hardware record-layout catalog metadata is invalid.");
			}

			HashSet<int> effectIds = [];
			HashSet<string> blockInstances = new(StringComparer.OrdinalIgnoreCase);

			foreach (Fm9HardwareRecordLayout layout in document.Families)
			{
				byte[] sceneStatePrefix;
				byte[] serializedRecord;

				try
				{
					sceneStatePrefix = Convert.FromBase64String(layout.SceneStatePrefixBase64);
					serializedRecord = Convert.FromBase64String(layout.SerializedRecordBase64);
				}
				catch (FormatException exception)
				{
					throw new InvalidDataException(
						$"The FM9 hardware record layout for '{layout.BlockType} {layout.Instance}' contains invalid base64.",
						exception);
				}

				if (string.IsNullOrWhiteSpace(layout.BlockType) ||
					layout.Instance != 1 ||
					layout.EffectId <= 0 ||
					!effectIds.Add(layout.EffectId) ||
					!blockInstances.Add($"{layout.BlockType}:{layout.Instance}") ||
					layout.CatalogParameterCount <= 0 ||
					layout.BulkValueCount <= 0 ||
					layout.ParameterColumns <= 0 ||
					layout.ParameterRows <= 0 ||
					layout.ParameterColumns * layout.ParameterRows != layout.BulkValueCount ||
					layout.RecordLength !=
						(23 + layout.BulkValueCount) * sizeof(ushort) ||
					layout.SerializedRecordSha256.Length != 64 ||
					sceneStatePrefix.Length != sizeof(ushort) ||
					serializedRecord.Length != layout.RecordLength ||
					!string.Equals(
						Convert.ToHexString(SHA256.HashData(serializedRecord)),
						layout.SerializedRecordSha256,
						StringComparison.OrdinalIgnoreCase) ||
					layout.DefaultBulkValues.Count != layout.BulkValueCount ||
					layout.DefaultBulkValues.Any(value => value is < 0 or > 65_534) ||
					layout.MismatchIndices.Any(index =>
						index < 0 || index >= layout.BulkValueCount) ||
					layout.BulkMatchesSerialized != (layout.MismatchIndices.Count == 0))
				{
					throw new InvalidDataException(
						$"The FM9 hardware record layout for '{layout.BlockType} {layout.Instance}' is invalid.");
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
