using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fractal.Companion.Protocol
{
	public sealed record Fm9EditBufferApplyArtifactManifest(
		string SchemaVersion,
		string ArtifactId,
		DateTimeOffset CreatedUtc,
		DateTimeOffset ExpiresUtc,
		string Device,
		string FirmwareProfile,
		string Capability,
		string AuthorizationCodeSha256,
		string PayloadFileName,
		string PayloadSha256,
		string PresetName);

	public sealed record Fm9EditBufferApplyArtifact(
		Fm9EditBufferApplyArtifactManifest Manifest,
		byte[] DumpBytes,
		Fm9PresetDump Dump)
	{
		public const string Schema = "fractal.fm9.apply-artifact/v1";
		public const string DeviceName = "FM9";
		public const string Firmware12RideBaselineProfile = "fm9-firmware-12-ride-baseline";
		public const string VolatileEditBufferRestoreCapability = "volatile-edit-buffer-restore";
		public const string ManifestFileName = "manifest.json";
		public const string PayloadFileName = "preset.syx";

		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true
		};

		public static byte[] Create(
			ReadOnlySpan<byte> dumpBytes,
			DateTimeOffset createdUtc,
			DateTimeOffset expiresUtc)
		{
			return Create(
				dumpBytes,
				createdUtc,
				expiresUtc,
				Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
		}

		public static byte[] Create(
			ReadOnlySpan<byte> dumpBytes,
			DateTimeOffset createdUtc,
			DateTimeOffset expiresUtc,
			string authorizationCode)
		{
			if (expiresUtc <= createdUtc)
			{
				throw new ArgumentException(
					"An FM9 apply artifact must expire after it is created.",
					nameof(expiresUtc));
			}

			if (string.IsNullOrWhiteSpace(authorizationCode))
			{
				throw new ArgumentException(
					"An FM9 apply artifact requires an authorization code.",
					nameof(authorizationCode));
			}

			byte[] payload = dumpBytes.ToArray();
			Fm9PresetDump dump = Fm9PresetDump.Parse(payload);
			Fm9EditBufferApplyArtifactManifest manifest = new(
				Schema,
				Guid.NewGuid().ToString("N"),
				createdUtc,
				expiresUtc,
				DeviceName,
				Firmware12RideBaselineProfile,
				VolatileEditBufferRestoreCapability,
				Convert.ToHexString(SHA256.HashData(
					System.Text.Encoding.UTF8.GetBytes(authorizationCode))),
				PayloadFileName,
				Convert.ToHexString(SHA256.HashData(payload)),
				dump.PresetName);

			using MemoryStream output = new();
			using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
			{
				WriteEntry(
					archive,
					ManifestFileName,
					JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
				WriteEntry(archive, PayloadFileName, payload);
			}

			return output.ToArray();
		}

		public static Fm9EditBufferApplyArtifact LoadValidated(
			ReadOnlySpan<byte> artifactBytes,
			DateTimeOffset nowUtc)
		{
			using MemoryStream input = new(artifactBytes.ToArray(), writable: false);
			using ZipArchive archive = new(input, ZipArchiveMode.Read, false);

			if (archive.Entries.Count != 2)
			{
				throw new InvalidDataException(
					"An FM9 apply artifact must contain exactly manifest.json and preset.syx.");
			}

			ZipArchiveEntry? manifestEntry = archive.GetEntry(ManifestFileName);
			ZipArchiveEntry? payloadEntry = archive.GetEntry(PayloadFileName);

			if (manifestEntry is null || payloadEntry is null)
			{
				throw new InvalidDataException(
					"An FM9 apply artifact is missing its manifest or preset payload.");
			}

			Fm9EditBufferApplyArtifactManifest manifest;
			using (Stream manifestStream = manifestEntry.Open())
			{
				manifest = JsonSerializer.Deserialize<Fm9EditBufferApplyArtifactManifest>(
					manifestStream,
					JsonOptions)
					?? throw new InvalidDataException("The FM9 apply artifact manifest is invalid.");
			}

			ValidateManifest(manifest, nowUtc);

			byte[] payload;
			using (Stream payloadStream = payloadEntry.Open())
			using (MemoryStream payloadBuffer = new())
			{
				payloadStream.CopyTo(payloadBuffer);
				payload = payloadBuffer.ToArray();
			}

			string actualHash = Convert.ToHexString(SHA256.HashData(payload));

			if (!string.Equals(actualHash, manifest.PayloadSha256, StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The FM9 apply artifact payload failed SHA-256 integrity validation.");
			}

			Fm9PresetDump dump = Fm9PresetDump.Parse(payload);

			if (!string.Equals(dump.PresetName, manifest.PresetName, StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The FM9 apply artifact preset identity does not match its manifest.");
			}

			return new Fm9EditBufferApplyArtifact(manifest, payload, dump);
		}

		public static Fm9EditBufferApplyArtifact LoadValidatedAuthorized(
			ReadOnlySpan<byte> artifactBytes,
			DateTimeOffset nowUtc,
			string authorizationCode)
		{
			if (string.IsNullOrWhiteSpace(authorizationCode))
			{
				throw new InvalidDataException(
					"An FM9 apply artifact requires its separate authorization code.");
			}

			Fm9EditBufferApplyArtifact artifact = LoadValidated(artifactBytes, nowUtc);
			byte[] expected = Convert.FromHexString(artifact.Manifest.AuthorizationCodeSha256);
			byte[] actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authorizationCode));

			if (!CryptographicOperations.FixedTimeEquals(expected, actual))
			{
				throw new InvalidDataException(
					"The FM9 apply artifact authorization code is invalid.");
			}

			return artifact;
		}

		public static Fm9EditBufferApplyArtifact LoadValidated(
			string artifactPath,
			DateTimeOffset nowUtc)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
			string fullPath = Path.GetFullPath(artifactPath);

			if (!File.Exists(fullPath))
			{
				throw new FileNotFoundException("The FM9 apply artifact was not found.", fullPath);
			}

			return LoadValidated(File.ReadAllBytes(fullPath), nowUtc);
		}

		private static void ValidateManifest(
			Fm9EditBufferApplyArtifactManifest manifest,
			DateTimeOffset nowUtc)
		{
			if (!string.Equals(manifest.SchemaVersion, Schema, StringComparison.Ordinal) ||
				!string.Equals(manifest.Device, DeviceName, StringComparison.Ordinal) ||
				!string.Equals(
					manifest.FirmwareProfile,
					Firmware12RideBaselineProfile,
					StringComparison.Ordinal) ||
				!string.Equals(
					manifest.Capability,
					VolatileEditBufferRestoreCapability,
					StringComparison.Ordinal) ||
				!string.Equals(manifest.PayloadFileName, PayloadFileName, StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The FM9 apply artifact does not declare a supported volatile edit-buffer capability.");
			}

			if (!Guid.TryParseExact(manifest.ArtifactId, "N", out _))
			{
				throw new InvalidDataException("The FM9 apply artifact identity is invalid.");
			}

			if (manifest.ExpiresUtc <= manifest.CreatedUtc)
			{
				throw new InvalidDataException("The FM9 apply artifact expiry is invalid.");
			}

			if (nowUtc > manifest.ExpiresUtc)
			{
				throw new InvalidDataException("The FM9 apply artifact has expired.");
			}

			if (string.IsNullOrWhiteSpace(manifest.PresetName) ||
				!IsSha256(manifest.PayloadSha256) ||
				!IsSha256(manifest.AuthorizationCodeSha256))
			{
				throw new InvalidDataException(
					"The FM9 apply artifact manifest has invalid identity or integrity metadata.");
			}
		}

		private static bool IsSha256(string value)
		{
			return value.Length == 64 && value.All(Uri.IsHexDigit);
		}

		private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
		{
			ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
			using Stream entryStream = entry.Open();
			entryStream.Write(contents);
		}
	}
}
