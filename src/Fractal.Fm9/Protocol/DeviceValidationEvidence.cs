using System.Security.Cryptography;
using System.Text.Json;

namespace Fractal.Companion.Protocol
{
	public sealed record DeviceValidationEvidence(
		int SchemaVersion,
		string Device,
		string FirmwareVersion,
		string SourceFileName,
		long SourceLength,
		string SourceSha256,
		DateTimeOffset CapturedUtc,
		string ValidationState,
		string SafetyBoundary,
		IReadOnlyList<string> RequiredNextSteps);

	public static class DeviceValidationEvidenceCapture
	{
		public const int CurrentSchemaVersion = 1;

		private static readonly IReadOnlyDictionary<string, string> DeviceNames =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["fm9"] = "FM9",
				["fm3"] = "FM3",
				["axe-fx-iii"] = "Axe-Fx III",
				["axefx3"] = "Axe-Fx III"
			};

		public static DeviceValidationEvidence Capture(
			string device,
			string firmwareVersion,
			string sourceFileName,
			ReadOnlySpan<byte> sourceBytes,
			DateTimeOffset capturedUtc)
		{
			if (!DeviceNames.TryGetValue(device.Trim(), out string? displayName))
			{
				throw new ArgumentException(
					"Device must be fm9, fm3, or axe-fx-iii.",
					nameof(device));
			}

			if (string.IsNullOrWhiteSpace(firmwareVersion))
			{
				throw new ArgumentException("Firmware version is required.", nameof(firmwareVersion));
			}

			if (!string.Equals(Path.GetExtension(sourceFileName), ".syx", StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					"Capture evidence requires an exported .syx file.",
					nameof(sourceFileName));
			}

			if (sourceBytes.IsEmpty)
			{
				throw new InvalidDataException("The exported .syx file is empty.");
			}

			return new DeviceValidationEvidence(
				CurrentSchemaVersion,
				displayName,
				firmwareVersion.Trim(),
				Path.GetFileName(sourceFileName),
				sourceBytes.Length,
				Convert.ToHexString(SHA256.HashData(sourceBytes)),
				capturedUtc,
				"captured-unvalidated",
				"Read-only provenance evidence only. It is not a parameter map, protocol profile, output-eligibility claim, or writable preset.",
				[
					"Retain the original device export and exact firmware version.",
					"Obtain device-specific catalog and parameter metadata.",
					"Independently map the captured binary structure without borrowing another device profile.",
					"Validate each proposed mapping in the volatile edit buffer with read-back and restoration.",
					"Enable output only after template, parameter mapping, and connection protocol are all verified."
				]);
		}

		public static DeviceValidationEvidence LoadAndVerify(
			string manifestPath,
			string sourcePath)
		{
			DeviceValidationEvidence evidence =
				JsonSerializer.Deserialize<DeviceValidationEvidence>(
					File.ReadAllText(manifestPath),
					new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true
					}) ??
				throw new InvalidDataException("The device validation evidence manifest is invalid.");
			if (evidence.SchemaVersion != CurrentSchemaVersion ||
				!string.Equals(evidence.ValidationState, "captured-unvalidated", StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The device validation evidence schema or validation state is unsupported.");
			}

			byte[] sourceBytes = File.ReadAllBytes(sourcePath);
			string hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
			if (sourceBytes.LongLength != evidence.SourceLength ||
				!string.Equals(hash, evidence.SourceSha256, StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The source .syx file does not match the captured validation evidence.");
			}

			return evidence;
		}
	}
}
