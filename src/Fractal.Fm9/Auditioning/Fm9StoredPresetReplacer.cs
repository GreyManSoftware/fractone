using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fractal.Companion.Auditioning
{
	public sealed record Fm9StoredPresetReplacementReport(
		string Status,
		string FirmwareVersion,
		int DisplayedPresetNumber,
		int SceneNumber,
		string OriginalPresetName,
		string BackupPath,
		string BackupSha256,
		string RepairedPath,
		string RepairedSha256,
		string? NormalizationReferencePath,
		string? NormalizationReferenceSha256,
		IReadOnlyList<Fm9NormalizedFieldExpectation> NormalizedFields,
		string? ReadbackPath,
		string? ReadbackSha256,
		bool EntireRawPatchMatched,
		bool RestoreAttempted,
		bool RestoreVerified,
		IReadOnlyList<string> HardwareOperations,
		string? Error);

	public sealed class Fm9StoredPresetReplacer
	{
		public const int RequiredSceneNumber = 1;
		public const int DefaultMidiChannel = 1;
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		private readonly IFractalMidiTransport _transport;

		// Writes any stored slot the user names (0-511). Every write backs the slot up first, verifies the import and the
		// stored copy byte-for-byte, and restores the backup on failure; engineering probes stay on the 500-505 scratch slots.
		// A staging slot (scratch 500-505) is shown on the FM9 while the preset is imported, and the Store targets the named
		// slot by number. The FM9 can display another preset after an import (it showed #0 after loading over an empty
		// slot), so if a Store ever followed the display instead of its number, only a scratch slot could be affected; the
		// staging slot is checked byte-for-byte afterwards.
		public Fm9StoredPresetReplacer(IFractalMidiTransport transport, int displayedPresetNumber = 500, int midiChannel = DefaultMidiChannel,
			int? stagingPresetNumber = null)
		{
			// The slot range is the unit's (Axe-Fx III Mk II: 1,024); a plain transport is an FM9.
			_device = Gen3DeviceTransport.DeviceOf(transport);
			string range = $"{_device.Name} presets are 0 through {_device.Presets - 1}.";
			// Which slot may serve as staging is the caller's policy (FracTone's probes keep to scratch slots 500-505).
			if (stagingPresetNumber < 0 || stagingPresetNumber >= _device.Presets)
				throw new ArgumentOutOfRangeException(nameof(stagingPresetNumber), range);
			_stagingPresetNumber = stagingPresetNumber;
			_transport = transport ?? throw new ArgumentNullException(nameof(transport));
			if (displayedPresetNumber < 0 || displayedPresetNumber >= _device.Presets)
				throw new ArgumentOutOfRangeException(nameof(displayedPresetNumber), range);
			if (midiChannel is < 1 or > 16)
				throw new ArgumentOutOfRangeException(nameof(midiChannel), "MIDI channels are 1 through 16.");
			DisplayedPresetNumber = displayedPresetNumber;
			_midiChannel = midiChannel;
		}

		public int DisplayedPresetNumber { get; }

		// How the replacer waits for the FM9 between steps, read retries and frames; tests replace it so they need not sleep.
		public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

		// Whether the stored copy is read back and compared after the Store. The import is always checked before storing,
		// and a failed import is always undone from the copy read before the write.
		public bool VerifyStoredReadback { get; init; } = true;

		// Whether the backup, readbacks and report stay in the recovery folder after a successful write. They are always
		// kept when a write fails, because they are what recovery needs.
		public bool KeepEvidence { get; init; } = true;

		private readonly int? _stagingPresetNumber;
		private readonly Gen3Device _device;

		// The preset shown on the FM9 while importing: the staging slot when one is used, otherwise the target.
		private int ContextPresetNumber => _stagingPresetNumber ?? DisplayedPresetNumber;

		private readonly int _midiChannel;

		// Loads a preset into the volatile edit buffer for listening, using the same acknowledged import as a write, and never
		// stores: switching presets on the FM9 discards it.
		// The optional readback is what the FM9 holds after loading, usable as a write's normalization reference.
		public async Task<IReadOnlyList<string>> AuditionAsync(string presetPath, CancellationToken cancellationToken,
			string? readbackPath = null)
		{
			Fm9PresetDump dump = Fm9PresetDump.Parse(File.ReadAllBytes(Path.GetFullPath(presetPath)));
			_ = Fm9PresetBodyDecoder.Decode(dump, _device);
			List<string> operations = [];
			await SelectTargetAsync(operations, cancellationToken);
			_ = await new Fm9TargetVerifier(_transport).VerifyAsync(DisplayedPresetNumber, RequiredSceneNumber, cancellationToken);
			await LoadEditBufferAsync(dump.ForEditBufferImport(), cancellationToken);
			Fm9StoredPresetReadResult loaded = await new Fm9StoredPresetReader(_transport, delayAsync: Wait).ReadAsync(0x3FFF, cancellationToken);
			// The FM9 can drop a dump that arrives while it is still loading the preset just selected (505, 2026-10-13: the
			// buffer kept the stored preset), so check the buffer holds what was sent and send it once more if not.
			if (!Holds(dump, loaded.Dump))
			{
				await Wait(TimeSpan.FromSeconds(1), cancellationToken);
				await LoadEditBufferAsync(dump.ForEditBufferImport(), cancellationToken);
				loaded = await new Fm9StoredPresetReader(_transport, delayAsync: Wait).ReadAsync(0x3FFF, cancellationToken);
				if (Holds(dump, loaded.Dump))
					operations.Add("The FM9 dropped the first load; the second was taken.");
				else if (string.Equals(loaded.Dump.PresetName, dump.PresetName, StringComparison.Ordinal))
					// Same preset, rewritten on import (e.g. a file from older firmware, upgraded as it loads).
					operations.Add("The FM9 changed the preset as it loaded it (e.g. upgrading it from older firmware); what it plays is its own copy.");
				else
					throw new InvalidOperationException(
						$"The FM9 did not take the preset: its edit buffer still holds '{loaded.Dump.PresetName}'. Nothing was stored.");
			}
			if (readbackPath is not null)
			{
				WriteAtomically(Path.GetFullPath(readbackPath), loaded.Dump.ToArray());
				operations.Add($"Edit-buffer readback saved: {Path.GetFullPath(readbackPath)}.");
			}
			operations.Add(
				$"Edit buffer now holds '{loaded.Dump.PresetName}' with {loaded.Body.GridCells.Count(cell => cell.EffectId is not null)} " +
				$"placed blocks on preset #{DisplayedPresetNumber}; nothing was stored.");
			return operations;
		}

		// Whether the buffer holds the preset sent, allowing the values the FM9 recalculates when it loads an amp model.
		private bool Holds(Fm9PresetDump sent, Fm9PresetDump buffer)
		{
			try
			{
				_ = Fm9PresetWriteVerifier.VerifyImportedBody(sent, buffer, _device);
				return true;
			}
			catch (InvalidDataException)
			{
				return false;
			}
		}

		private async Task<Fm9PresetDump?> ReadStoredOrEmptyAsync(int presetNumber, CancellationToken cancellationToken)
		{
			try
			{
				return (await new Fm9StoredPresetReader(_transport, delayAsync: Wait).ReadAsync(presetNumber, cancellationToken)).Dump;
			}
			catch (Fm9EmptyPresetSlotException)
			{
				return null;
			}
		}

		// Puts the target preset and scene on screen so the target check below can confirm it before anything is written.
		private async Task SelectTargetAsync(List<string> operations, CancellationToken cancellationToken)
		{
			try
			{
				_ = await new Fm9TargetVerifier(_transport).VerifyAsync(ContextPresetNumber, RequiredSceneNumber, cancellationToken);
				return;
			}
			catch (InvalidOperationException) { }
			try
			{
				await _transport.SelectPresetAsync(ContextPresetNumber, _midiChannel, cancellationToken);
			}
			catch (NotSupportedException)
			{
				return;
			}
			await Wait(TimeSpan.FromSeconds(1), cancellationToken);
			await _transport.SendAsync(FractalSysExCodec.CreateSceneSelection(RequiredSceneNumber), cancellationToken);
			await Wait(TimeSpan.FromMilliseconds(500), cancellationToken);
			operations.Add($"Selected preset #{ContextPresetNumber}, Scene {RequiredSceneNumber} over MIDI channel {_midiChannel}.");
		}

		public async Task<Fm9StoredPresetReplacementReport> ReplaceAsync(
			string repairedPath,
			string? normalizationReferencePath,
			string recoveryDirectory,
			string reportPath,
			CancellationToken cancellationToken)
		{
			string repairedFullPath = Path.GetFullPath(repairedPath);
			string recoveryFullPath = Path.GetFullPath(recoveryDirectory);
			string reportFullPath = Path.GetFullPath(reportPath);
			ValidatePaths(repairedFullPath, recoveryFullPath, reportFullPath);

			byte[] repairedBytes = File.ReadAllBytes(repairedFullPath);
			Fm9PresetDump repaired = Fm9PresetDump.Parse(repairedBytes);
			_ = Fm9PresetBodyDecoder.Decode(repaired, _device);
			string repairedSha256 = CalculateSha256(repairedBytes);
			Fm9PresetNormalizationExpectation? normalizationExpectation = null;
			string? normalizationReferenceFullPath = null;
			string? normalizationReferenceSha256 = null;

			if (!string.IsNullOrWhiteSpace(normalizationReferencePath))
			{
				normalizationReferenceFullPath = Path.GetFullPath(normalizationReferencePath);
				byte[] normalizationReferenceBytes = File.ReadAllBytes(normalizationReferenceFullPath);
				Fm9PresetDump normalizationReference =
					Fm9PresetDump.Parse(normalizationReferenceBytes);
				normalizationExpectation =
					Fm9PresetWriteVerifier.CreateNormalizationExpectation(
						repaired,
						normalizationReference);
				normalizationReferenceSha256 =
					CalculateSha256(normalizationReferenceBytes);
			}
			List<string> operations = [];

			Fm9ReadOnlyProbeResult identity = await new Fm9ReadOnlyProbe(_transport)
				.ProbeAsync(cancellationToken);
			operations.Add($"Identity verified: FM9 firmware {identity.FirmwareVersion}; status dump received.");

			await SelectTargetAsync(operations, cancellationToken);
			Fm9TargetVerificationResult target = await new Fm9TargetVerifier(_transport)
				.VerifyAsync(
					ContextPresetNumber,
					RequiredSceneNumber,
					cancellationToken);
			operations.Add(
				$"Target verified before mutation: displayed preset #{target.VisiblePresetNumber}, Scene {target.SceneNumber}.");
			// The staging slot's stored contents (or the fact that it is empty), to confirm the Store leaves it unchanged.
			Fm9PresetDump? stagingBefore = null;
			if (_stagingPresetNumber is not null)
			{
				stagingBefore = await ReadStoredOrEmptyAsync(ContextPresetNumber, cancellationToken);
				operations.Add($"Staging preset #{ContextPresetNumber} ({(stagingBefore is null ? "empty" : $"'{stagingBefore.PresetName}'")}) " +
					"recorded to confirm the Store leaves it unchanged.");
			}

			Fm9StoredPresetReader reader = new(_transport, delayAsync: Wait);
			// A never-used slot holds no preset, so there is nothing to back up or restore; that fact is recorded instead.
			Fm9StoredPresetReadResult? original;
			try
			{
				original = await reader.ReadAsync(DisplayedPresetNumber, cancellationToken);
			}
			catch (Fm9EmptyPresetSlotException)
			{
				original = null;
			}
			string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
			string backupPath = Path.Combine(
				recoveryFullPath,
				original is null
					? $"fm9-preset-{DisplayedPresetNumber}-was-empty-{timestamp}.txt"
					: $"fm9-preset-{DisplayedPresetNumber}-backup-{timestamp}.syx");
			WriteAtomically(backupPath, original?.Dump.ToArray() ??
				System.Text.Encoding.UTF8.GetBytes($"FM9 preset #{DisplayedPresetNumber} was empty (erased) before this write.{Environment.NewLine}"));
			string backupSha256 = CalculateSha256(File.ReadAllBytes(backupPath));
			operations.Add(original is null
				? $"Preset #{DisplayedPresetNumber} was empty before mutation, so there was nothing to back up: {backupPath}."
				: $"Complete stored-preset backup captured before mutation: {backupPath} ({backupSha256}).");
			string originalName = original?.Dump.PresetName ?? "(empty slot)";

			// Files written for recovery; kept after success only when the caller keeps evidence.
			List<string> evidence = [backupPath];
			IReadOnlyList<Fm9NormalizedFieldExpectation> importNormalized = [];
			Fm9PresetDump expectedStored = repaired;
			Fm9StoredPresetReplacementReport Succeed(Fm9StoredPresetReplacementReport report)
			{
				if (KeepEvidence)
				{
					WriteReport(reportFullPath, report);
					return report;
				}
				foreach (string file in evidence)
					File.Delete(file);
				return report with
				{
					BackupPath = "",
					ReadbackPath = null,
					HardwareOperations = [.. report.HardwareOperations, "The backup and readbacks were not kept: keeping a backup is switched off."]
				};
			}
			bool mutationStarted = false;
			bool storeAttempted = false;
			bool restoreAttempted = false;
			bool restoreVerified = false;
			string? readbackPath = null;
			string? readbackSha256 = null;

			try
			{
				mutationStarted = true;
				await LoadEditBufferAsync(repaired.ForEditBufferImport(), cancellationToken);
				operations.Add("Preset-import frames acknowledged; verifying the complete volatile body before Store.");
				Fm9StoredPresetReadResult imported = await reader.ReadAsync(0x3FFF, cancellationToken);
				string importReadbackPath = Path.Combine(
					recoveryFullPath,
					$"fm9-preset-{DisplayedPresetNumber}-import-readback-{timestamp}.syx");
				WriteAtomically(importReadbackPath, imported.Dump.ToArray());
				evidence.Add(importReadbackPath);
				if (normalizationExpectation is null)
				{
					// Without a reference, the FM9's own import is the reference: it may only have recalculated the proven
					// model-dependent Amp values, and the stored copy must then match what it holds exactly.
					importNormalized = Fm9PresetWriteVerifier.VerifyImportedBody(repaired, imported.Dump, _device);
					if (importNormalized.Count > 0)
					{
						expectedStored = imported.Dump;
						operations.Add($"The FM9 recalculated {importNormalized.Count} model-dependent Amp value(s) on import, as it does for any new amp model.");
					}
				}
				else
				{
					try
					{
						Fm9PresetWriteVerifier.VerifyExactBodyReadback(repaired, imported.Dump);
					}
					catch (InvalidDataException)
					{
						Fm9PresetWriteVerifier.VerifyCanonicalReadback(normalizationExpectation, imported.Dump);
					}
				}
				_ = await new Fm9TargetVerifier(_transport).VerifyAsync(
					ContextPresetNumber, RequiredSceneNumber, cancellationToken);
				operations.Add($"Complete volatile import matched before Store: {importReadbackPath}.");

				storeAttempted = true;
				await _transport.SendAsync(
					FractalSysExCodec.CreateStorePreset(DisplayedPresetNumber, _device),
					cancellationToken);
				operations.Add($"Store command sent for preset #{DisplayedPresetNumber} only.");
				await Wait(TimeSpan.FromSeconds(2), cancellationToken);

				if (!VerifyStoredReadback)
				{
					operations.Add("The stored copy was not read back: verifying after a store is switched off.");
					return Succeed(new(
						"done", identity.FirmwareVersion, DisplayedPresetNumber, RequiredSceneNumber, originalName, backupPath, backupSha256,
						repairedFullPath, repairedSha256, normalizationReferenceFullPath, normalizationReferenceSha256,
						normalizationExpectation?.Fields ?? importNormalized, null, null, false, false, false, operations, null));
				}

				// Straight after a Store the FM9 can still be writing flash and answer with an incomplete dump; give it a
				// few more seconds before treating the readback as a failure (which would restore the backup).
				Fm9StoredPresetReadResult readback;
				for (int attempt = 1; ; attempt++)
				{
					try
					{
						readback = await reader.ReadAsync(DisplayedPresetNumber, cancellationToken);
						break;
					}
					catch (InvalidDataException) when (attempt < 4)
					{
						operations.Add($"Stored-preset readback was incomplete (attempt {attempt}); waiting for the FM9 to finish storing.");
						await Wait(TimeSpan.FromSeconds(3), cancellationToken);
					}
				}
				byte[] readbackBytes = readback.Dump.ToArray();
				readbackPath = Path.Combine(
					recoveryFullPath,
					$"fm9-preset-{DisplayedPresetNumber}-readback-{timestamp}.syx");
				WriteAtomically(readbackPath, readbackBytes);
				evidence.Add(readbackPath);
				readbackSha256 = CalculateSha256(File.ReadAllBytes(readbackPath));

				if (normalizationExpectation is null)
				{
					Fm9PresetWriteVerifier.VerifyExactBodyReadback(expectedStored, readback.Dump);
				}
				else
				{
					Fm9PresetWriteVerifier.VerifyCanonicalReadback(
						normalizationExpectation,
						readback.Dump);
				}

				if (_stagingPresetNumber is not null)
				{
					Fm9PresetDump? stagingAfter = await ReadStoredOrEmptyAsync(ContextPresetNumber, cancellationToken);
					bool unchanged = stagingBefore is null ? stagingAfter is null
						: stagingAfter is not null && stagingBefore.HasSameRawPatch(stagingAfter);
					if (!unchanged)
						throw new InvalidOperationException(
							$"Staging preset #{ContextPresetNumber} changed during the Store; it is a scratch slot, but stop and investigate.");
					operations.Add($"Staging preset #{ContextPresetNumber} is unchanged after the Store.");
				}

				bool entireRawPatchMatched = normalizationExpectation is null
					? expectedStored.HasSameRawPatch(readback.Dump)
					: normalizationExpectation.CanonicalReadback.HasSameRawPatch(readback.Dump);
				operations.Add(
					normalizationExpectation is null
						? entireRawPatchMatched
							? $"Complete stored-preset readback matched the repaired raw patch: {readbackPath} ({readbackSha256})."
							: $"Stored-preset readback used a different valid compression but matched the complete authored decompressed body with no stable-byte differences: {readbackPath} ({readbackSha256})."
						: entireRawPatchMatched
							? $"Complete stored-preset readback matched the exact canonical hardware-normalized raw patch: {readbackPath} ({readbackSha256})."
							: $"Stored-preset readback used a different valid compression but matched the complete canonical decompressed body with no stable-byte differences: {readbackPath} ({readbackSha256}).");
				Fm9StoredPresetReplacementReport success = new(
					"done",
					identity.FirmwareVersion,
					DisplayedPresetNumber,
					RequiredSceneNumber,
					originalName,
					backupPath,
					backupSha256,
					repairedFullPath,
					repairedSha256,
					normalizationReferenceFullPath,
					normalizationReferenceSha256,
					normalizationExpectation?.Fields ?? importNormalized,
					readbackPath,
					readbackSha256,
					entireRawPatchMatched,
					false,
					false,
					operations,
					null);
				return Succeed(success);
			}
			catch (Exception failure)
			{
				string error = failure.Message;

				if (mutationStarted && original is null)
				{
					operations.Add("The slot was empty before this attempt, so there is no earlier preset to restore.");
				}
				else if (mutationStarted && original is not null)
				{
					restoreAttempted = true;

					try
					{
						await LoadEditBufferAsync(original.Dump.ForEditBufferImport(), cancellationToken);
						Fm9StoredPresetReadResult restoredBuffer = await reader.ReadAsync(
							0x3FFF, cancellationToken);
						Fm9PresetWriteVerifier.VerifyExactBodyReadback(original.Dump, restoredBuffer.Dump);
						if (storeAttempted)
						{
							_ = await new Fm9TargetVerifier(_transport).VerifyAsync(
								ContextPresetNumber, RequiredSceneNumber, cancellationToken);
							await _transport.SendAsync(
								FractalSysExCodec.CreateStorePreset(DisplayedPresetNumber, _device),
								cancellationToken);
							await Wait(TimeSpan.FromSeconds(2), cancellationToken);
						}
						Fm9StoredPresetReadResult restored = await reader.ReadAsync(
							DisplayedPresetNumber,
							cancellationToken);
						restoreVerified = original.Dump.HasSameRawPatch(restored.Dump);
						operations.Add(restoreVerified
							? $"Failure recovery restored preset #{DisplayedPresetNumber} and verified the complete raw patch."
							: "Failure recovery ran, but the restored stored-preset raw patch did not match the backup.");
					}
					catch (Exception restoreFailure)
					{
						error += $" Restore failed: {restoreFailure.Message}";
						operations.Add($"Failure recovery did not complete: {restoreFailure.Message}");
					}
				}

				Fm9StoredPresetReplacementReport blocked = new(
					"blocked",
					identity.FirmwareVersion,
					DisplayedPresetNumber,
					RequiredSceneNumber,
					originalName,
					backupPath,
					backupSha256,
					repairedFullPath,
					repairedSha256,
					normalizationReferenceFullPath,
					normalizationReferenceSha256,
					normalizationExpectation?.Fields ?? [],
					readbackPath,
					readbackSha256,
					false,
					restoreAttempted,
					restoreVerified,
					operations,
					error);
				WriteReport(reportFullPath, blocked);
				throw new InvalidOperationException(
					$"FM9 preset #{DisplayedPresetNumber} replacement is blocked. Evidence report: {reportFullPath}. {error}",
					failure);
			}
		}

		// The acknowledged frame-by-frame load Copilot's preset authors used. Sending the preset-import preparation command
		// first made the FM9 display #0 instead of the selected preset, which blocks the target check before Store.
		private Task LoadEditBufferAsync(Fm9PresetDump dump, CancellationToken cancellationToken) =>
			new Fm9EditBufferRestoreSender(_transport, Wait).RestoreAcknowledgedAsync(dump, cancellationToken);

		private static void ValidatePaths(
			string repairedPath,
			string recoveryDirectory,
			string reportPath)
		{
			if (!File.Exists(repairedPath) ||
				!string.Equals(Path.GetExtension(repairedPath), ".syx", StringComparison.OrdinalIgnoreCase))
			{
				throw new FileNotFoundException("The repaired FM9 .syx artifact was not found.", repairedPath);
			}

			if (!Directory.Exists(recoveryDirectory))
			{
				throw new DirectoryNotFoundException(
					$"The recovery directory does not exist: {recoveryDirectory}");
			}

			if (!string.Equals(
				Path.GetDirectoryName(reportPath),
				recoveryDirectory,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					"The replacement report must be written directly in the recovery directory.",
					nameof(reportPath));
			}
		}

		private static string CalculateSha256(ReadOnlySpan<byte> bytes)
		{
			return Convert.ToHexString(SHA256.HashData(bytes));
		}

		private static void WriteReport(
			string reportPath,
			Fm9StoredPresetReplacementReport report)
		{
			WriteAtomically(
				reportPath,
				JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
		}

		private static void WriteAtomically(string path, ReadOnlySpan<byte> contents)
		{
			string directory = Path.GetDirectoryName(path)!;
			string temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(path)}.{Path.GetRandomFileName()}");

			try
			{
				using (FileStream stream = new(
					temporaryPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					4096,
					FileOptions.WriteThrough))
				{
					stream.Write(contents);
					stream.Flush(flushToDisk: true);
				}

				File.Move(temporaryPath, path, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
		}
	}
}
