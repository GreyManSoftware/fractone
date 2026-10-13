using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareNonFirstInstanceRecord(
		[property: JsonPropertyName("blockType")] string BlockType,
		[property: JsonPropertyName("instance")] int Instance,
		[property: JsonPropertyName("effectId")] int EffectId,
		[property: JsonPropertyName("representativeEffectId")] int RepresentativeEffectId,
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
		[property: JsonPropertyName("mismatchIndices")] IReadOnlyList<int> MismatchIndices,
		[property: JsonPropertyName("dimensionsMatchRepresentative")] bool DimensionsMatchRepresentative,
		[property: JsonPropertyName("serializedRecordMatchesRepresentative")] bool SerializedRecordMatchesRepresentative,
		[property: JsonPropertyName("bulkDefaultsMatchRepresentative")] bool BulkDefaultsMatchRepresentative,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored);

	public sealed record Fm9HardwareNonFirstInstanceRecordCatalogDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("records")] IReadOnlyList<Fm9HardwareNonFirstInstanceRecord> Records);

	public sealed class Fm9HardwareNonFirstInstanceRecordCatalog
	{
		public const int CurrentSchemaVersion = 1;
		public const int ExpectedRecordCount = 45;
		public const int ExpectedExactRecordMatchCount = 28;
		public const int ExpectedBulkDefaultsMatchCount = 30;

		private readonly IReadOnlyDictionary<(string BlockType, int Instance), Fm9HardwareNonFirstInstanceRecord>
			_records;

		private Fm9HardwareNonFirstInstanceRecordCatalog(
			Fm9HardwareNonFirstInstanceRecordCatalogDocument document,
			Fm9HardwareRecordCatalog representativeCatalog,
			Fm9HardwareBlockInstanceCatalog instanceCatalog)
		{
			Document = document;
			Validate(document, representativeCatalog, instanceCatalog);
			_records = document.Records.ToDictionary(
				record => (record.BlockType, record.Instance),
				record => record,
				new BlockInstanceComparer());
		}

		public Fm9HardwareNonFirstInstanceRecordCatalogDocument Document { get; }
		public IReadOnlyList<Fm9HardwareNonFirstInstanceRecord> Records => Document.Records;

		public Fm9HardwareNonFirstInstanceRecord GetRecord(string blockType, int instance)
		{
			if (string.IsNullOrWhiteSpace(blockType))
			{
				throw new ArgumentException("An FM9 block type is required.", nameof(blockType));
			}

			return _records.TryGetValue(
				(blockType, instance),
				out Fm9HardwareNonFirstInstanceRecord? record)
				? record
				: throw new KeyNotFoundException(
					$"No hardware-qualified non-first FM9 record exists for " +
					$"'{blockType} {instance}'.");
		}

		public static async Task<Fm9HardwareNonFirstInstanceRecordCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareNonFirstInstanceRecordCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-non-first-instance-records.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 non-first-instance record catalog is unavailable.");
			Fm9HardwareNonFirstInstanceRecordCatalogDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareNonFirstInstanceRecordCatalogDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException(
					"The bundled FM9 non-first-instance record catalog is empty.");
			Fm9HardwareRecordCatalog representativeCatalog =
				await Fm9HardwareRecordCatalog.LoadBundledAsync(cancellationToken);
			Fm9HardwareBlockInstanceCatalog instanceCatalog =
				await Fm9HardwareBlockInstanceCatalog.LoadBundledAsync(cancellationToken);
			return new Fm9HardwareNonFirstInstanceRecordCatalog(
				document,
				representativeCatalog,
				instanceCatalog);
		}

		private static void Validate(
			Fm9HardwareNonFirstInstanceRecordCatalogDocument document,
			Fm9HardwareRecordCatalog representativeCatalog,
			Fm9HardwareBlockInstanceCatalog instanceCatalog)
		{
			if (document.SchemaVersion != CurrentSchemaVersion ||
				document.FirmwareVersion != Fm9Firmware12Catalog.FirmwareVersion ||
				document.Records.Count != ExpectedRecordCount ||
				document.Records.Count(record => record.SerializedRecordMatchesRepresentative) !=
					ExpectedExactRecordMatchCount ||
				document.Records.Count(record => record.BulkDefaultsMatchRepresentative) !=
					ExpectedBulkDefaultsMatchCount ||
				document.Records.Select(record => record.EffectId).Distinct().Count() !=
					ExpectedRecordCount)
			{
				throw new InvalidDataException(
					"The FM9 non-first-instance record catalog metadata is invalid.");
			}

			IReadOnlyDictionary<int, Fm9HardwareBlockInstance> acceptedByEffectId =
				instanceCatalog.AcceptedInstances.ToDictionary(instance => instance.EffectId);

			if (acceptedByEffectId.Values.Count(instance => instance.Instance > 1) !=
				ExpectedRecordCount)
			{
				throw new InvalidDataException(
					"The accepted FM9 instance catalog does not match the non-first record count.");
			}

			foreach (Fm9HardwareNonFirstInstanceRecord record in document.Records)
			{
				byte[] sceneStatePrefix;
				byte[] serializedRecord;

				try
				{
					sceneStatePrefix = Convert.FromBase64String(record.SceneStatePrefixBase64);
					serializedRecord = Convert.FromBase64String(record.SerializedRecordBase64);
				}
				catch (FormatException exception)
				{
					throw new InvalidDataException(
						$"The FM9 record for '{record.BlockType} {record.Instance}' contains invalid base64.",
						exception);
				}

				Fm9HardwareRecordLayout representative =
					representativeCatalog.GetLayout(record.BlockType, 1);
				bool recordMatchesRepresentative = string.Equals(
					record.SerializedRecordBase64,
					representative.SerializedRecordBase64,
					StringComparison.Ordinal);
				bool bulkMatchesRepresentative =
					record.DefaultBulkValues.SequenceEqual(representative.DefaultBulkValues);
				IReadOnlyList<int> serializedValues = Enumerable.Range(
						0,
						record.BulkValueCount)
					.Select(index =>
					{
						int offset = (17 + index) * sizeof(ushort);
						return serializedRecord[offset] |
							(serializedRecord[offset + 1] << 8);
					})
					.ToArray();
				int[] mismatchIndices = record.DefaultBulkValues
					.Select((value, index) => (value, index))
					.Where(item => item.value != serializedValues[item.index])
					.Select(item => item.index)
					.ToArray();

				if (!acceptedByEffectId.TryGetValue(
						record.EffectId,
						out Fm9HardwareBlockInstance? accepted) ||
					!accepted.Accepted ||
					!string.Equals(
						accepted.BlockType,
						record.BlockType,
						StringComparison.OrdinalIgnoreCase) ||
					accepted.Instance != record.Instance ||
					record.Instance <= 1 ||
					record.RepresentativeEffectId != representative.EffectId ||
					record.CatalogParameterCount != accepted.CatalogParameterCount ||
					record.BulkValueCount != accepted.BulkValueCount ||
					record.ParameterColumns != representative.ParameterColumns ||
					record.ParameterRows != representative.ParameterRows ||
					record.RecordLength != representative.RecordLength ||
					record.ParameterColumns * record.ParameterRows != record.BulkValueCount ||
					record.RecordLength != (23 + record.BulkValueCount) * sizeof(ushort) ||
					sceneStatePrefix.Length != sizeof(ushort) ||
					serializedRecord.Length != record.RecordLength ||
					!string.Equals(
						Convert.ToHexString(SHA256.HashData(serializedRecord)),
						record.SerializedRecordSha256,
						StringComparison.OrdinalIgnoreCase) ||
					record.DefaultBulkValues.Count != record.BulkValueCount ||
					record.DefaultBulkValues.Any(value => value is < 0 or > 65_534) ||
					!record.DimensionsMatchRepresentative ||
					record.SerializedRecordMatchesRepresentative != recordMatchesRepresentative ||
					record.BulkDefaultsMatchRepresentative != bulkMatchesRepresentative ||
					record.BulkMatchesSerialized != (mismatchIndices.Length == 0) ||
					!record.MismatchIndices.SequenceEqual(mismatchIndices) ||
					!record.BaselineRestored)
				{
					throw new InvalidDataException(
						$"The FM9 record for '{record.BlockType} {record.Instance}' is invalid.");
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
