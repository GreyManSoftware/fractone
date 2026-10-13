using System.Globalization;
using System.Text;
using Fractal.Companion.Auditioning;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Fm9
{
	public sealed record Fm9Status(string Firmware, int Preset, string PresetName, int Scene);

	public sealed record Fm9ReadResult(int? Slot, string Path, Fm9PresetSummary Summary);

	public sealed record Fm9BackupResult(string Folder, IReadOnlyList<string> Files, IReadOnlyList<int> EmptySlots, IReadOnlyList<string> Failed);

	public sealed record Fm9AuditionResult(int Preset, string Loaded, string Replaced, IReadOnlyList<string> Operations);

	public sealed record Fm9StoreResult(int Slot, string Stored, string Replaced, string? Backup, bool Verified,
		IReadOnlyList<string> RecalculatedByFm9, IReadOnlyList<string> Operations);

	// Everything that talks to the FM9: reads, backups, auditions (edit buffer only) and stores, with the user's safeguards.
	public sealed class Fm9Device
	{
		private const int EditBuffer = 0x3FFF;
		private const string SupportedFirmwareMajor = "12";
		private readonly Fm9Connection _connection;
		private readonly Fm9Settings _settings;

		public Fm9Device(Fm9Connection connection, Fm9Settings settings)
		{
			_connection = connection ?? throw new ArgumentNullException(nameof(connection));
			_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		}

		// How the device waits for the FM9 between steps; tests replace it so they need not sleep.
		public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

		private string StateFolder => Path.Combine(_settings.WorkFolder, ".state");
		// What the FM9 held straight after these tools last loaded its edit buffer.
		private string LastLoadPath => Path.Combine(StateFolder, "last-load.syx");

		public Task<Fm9Status> StatusAsync(CancellationToken cancellationToken) =>
			_connection.RunAsync((transport, token) => StatusAsync(transport, token), cancellationToken);

		public Task<Fm9ReadResult> ReadPresetAsync(int slot, string? output, CancellationToken cancellationToken)
		{
			CheckSlot(slot);
			return _connection.RunAsync(async (transport, token) =>
			{
				Fm9StoredPresetReadResult read = await new Fm9StoredPresetReader(transport).ReadAsync(slot, token);
				string path = output is null ? DefaultReadPath(slot, read.Dump.PresetName) : _settings.Resolve(output);
				Fm9Files.WriteAtomically(path, read.Dump.ToArray());
				return new Fm9ReadResult(slot, path, Fm9Presets.Describe(Fm9UploadedPreset.Read(read.Dump)));
			}, cancellationToken);
		}

		public Task<Fm9ReadResult> ReadEditBufferAsync(string? output, CancellationToken cancellationToken) =>
			_connection.RunAsync(async (transport, token) =>
			{
				Fm9StoredPresetReadResult read = await new Fm9StoredPresetReader(transport).ReadAsync(EditBuffer, token);
				string path = output is null
					? Path.Combine(_settings.WorkFolder, "reads", $"edit buffer {Stamp()} {FileName(read.Dump.PresetName)}.syx")
					: _settings.Resolve(output);
				Fm9Files.WriteAtomically(path, read.Dump.ToArray());
				return new Fm9ReadResult(null, path, Fm9Presets.Describe(Fm9UploadedPreset.Read(read.Dump)));
			}, cancellationToken);

		// Reads every preset in a range into a folder, one .syx per slot; erased slots are listed, not written.
		public Task<Fm9BackupResult> BackupAsync(int first, int last, string? folder, IProgress<int>? progress, CancellationToken cancellationToken)
		{
			CheckSlot(first);
			CheckSlot(last);
			if (last < first) throw new ArgumentException("The last slot comes before the first.");
			string target = folder is null ? Path.Combine(_settings.BackupFolder, $"full {Stamp()}") : _settings.Resolve(folder);
			return _connection.RunAsync(async (transport, token) =>
			{
				Fm9StoredPresetReader reader = new(transport);
				List<string> files = [];
				List<int> empty = [];
				List<string> failed = [];
				for (int slot = first; slot <= last; slot++)
				{
					// A dump can come back incomplete (another program talking to the FM9, a busy moment); one bad slot is
					// retried after a pause and, failing that, listed rather than ending the backup.
					for (int attempt = 1; ; attempt++)
					{
						try
						{
							Fm9StoredPresetReadResult read = await reader.ReadAsync(slot, token);
							string path = Path.Combine(target, $"{slot:000} {FileName(read.Dump.PresetName)}.syx");
							Fm9Files.WriteAtomically(path, read.Dump.ToArray());
							files.Add(path);
							break;
						}
						catch (Fm9EmptyPresetSlotException)
						{
							empty.Add(slot);
							break;
						}
						catch (Exception exception) when (exception is InvalidDataException or TimeoutException)
						{
							if (attempt < 3)
							{
								await Wait(TimeSpan.FromSeconds(1), token);
								continue;
							}
							failed.Add($"{slot}: {exception.Message}");
							break;
						}
					}
					progress?.Report(slot);
				}
				return new Fm9BackupResult(Path.GetFullPath(target), files, empty, failed);
			}, cancellationToken);
		}

		// The preset in a slot, without saving it; null for erased flash.
		public Task<Fm9PresetDump?> ReadSlotAsync(int slot, CancellationToken cancellationToken)
		{
			CheckSlot(slot);
			return _connection.RunAsync<Fm9PresetDump?>(async (transport, token) =>
			{
				try { return (await new Fm9StoredPresetReader(transport).ReadAsync(slot, token)).Dump; }
				catch (Fm9EmptyPresetSlotException) { return null; }
			}, cancellationToken);
		}

		// What the last load by these tools left in the edit buffer, if anything.
		public string LastLoad => LastLoadPath;

		// The name of the preset in a slot, without saving it ("(empty slot)" for erased flash).
		public Task<string> PeekAsync(int slot, CancellationToken cancellationToken)
		{
			CheckSlot(slot);
			return _connection.RunAsync(async (transport, token) =>
			{
				try { return (await new Fm9StoredPresetReader(transport).ReadAsync(slot, token)).Dump.PresetName; }
				catch (Fm9EmptyPresetSlotException) { return "(empty slot)"; }
			}, cancellationToken);
		}

		public Task SelectAsync(int slot, int scene, CancellationToken cancellationToken)
		{
			CheckSlot(slot);
			if (scene is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(scene), "Scenes are 1 to 8.");
			return _connection.RunAsync(async (transport, token) =>
			{
				// Selecting a preset (even the one showing) discards unsaved edits, so it is guarded like a load.
				await CheckBufferAsync(transport, await StatusAsync(transport, token), token);
				await transport.SelectPresetAsync(slot, _settings.Midi.Channel, token);
				await Wait(TimeSpan.FromSeconds(1), token);
				await transport.SendAsync(FractalSysExCodec.CreateSceneSelection(scene), token);
				await Wait(TimeSpan.FromMilliseconds(500), token);
				return true;
			}, cancellationToken);
		}

		// Loads a preset into the edit buffer to listen to it; nothing is stored, and changing presets on the FM9 discards it.
		// With a slot, that preset is selected first (its own unsaved edits are lost); otherwise the preset showing is used.
		public Task<Fm9AuditionResult> AuditionAsync(string path, int? slot, CancellationToken cancellationToken)
		{
			string file = _settings.Resolve(path);
			Fm9PresetDump dump = Fm9PresetDump.Parse(File.ReadAllBytes(file));
			if (slot is int chosen) CheckSlot(chosen);
			return _connection.RunAsync(async (transport, token) =>
			{
				await CheckFirmwareAsync(transport, token);
				Fm9Status status = await StatusAsync(transport, token);
				await CheckBufferAsync(transport, status, token);
				int preset = slot ?? status.Preset;
				Directory.CreateDirectory(StateFolder);
				IReadOnlyList<string> operations = await new Fm9StoredPresetReplacer(transport, preset, _settings.Midi.Channel) { Wait = Wait }
					.AuditionAsync(file, token, LastLoadPath);
				// What the edit buffer held before (the preset showing; a selected slot's own contents when one was named).
				string replaced = slot is int named && named != status.Preset ? $"preset {named}'s edit buffer" : $"'{status.PresetName}' (any unsaved edits to it)";
				return new Fm9AuditionResult(preset, dump.PresetName, replaced, operations);
			}, cancellationToken);
		}

		// Stores a preset into a slot and reports what it replaced. The import is checked before the Store and undone from
		// the slot's previous contents if it fails; the safeguards decide whether a backup is kept and the stored copy read back.
		public Task<Fm9StoreResult> StoreAsync(string path, int slot, CancellationToken cancellationToken) =>
			StoreAsync(path, slot, null, cancellationToken);

		// Stores what the FM9 is playing now (the edit buffer, unsaved edits included) into a slot, through the same checked
		// store: the buffer is saved to a file first, and that file is what is stored and verified.
		public async Task<(Fm9StoreResult Result, string File)> StoreEditBufferAsync(int slot, string? file, CancellationToken cancellationToken)
		{
			Fm9ReadResult read = await ReadEditBufferAsync(file ?? Path.Combine("live", $"edit buffer {Stamp()}.syx"), cancellationToken);
			Fm9PresetDump captured = Fm9PresetDump.Parse(File.ReadAllBytes(read.Path));
			return (await StoreCapturedAsync(read.Path, slot, captured, cancellationToken), read.Path);
		}

		// Stores a file read from the edit buffer; the unsaved-edits guard accepts the buffer if it still matches the capture.
		public Task<Fm9StoreResult> StoreCapturedAsync(string path, int slot, Fm9PresetDump? captured, CancellationToken cancellationToken) =>
			StoreAsync(path, slot, captured, cancellationToken);

		private Task<Fm9StoreResult> StoreAsync(string path, int slot, Fm9PresetDump? captured, CancellationToken cancellationToken)
		{
			CheckSlot(slot);
			if (!_settings.Safeguards.AllowsSlot(slot))
				throw new InvalidOperationException(
					$"Preset {slot} is not writable under the current settings (writable slots: {string.Join(", ", _settings.Safeguards.WritableSlots)}).");
			string file = _settings.Resolve(path);
			if (!File.Exists(file)) throw new FileNotFoundException("The preset file was not found.", file);
			return _connection.RunAsync(async (transport, token) =>
			{
				await CheckFirmwareAsync(transport, token);
				Fm9Status status = await StatusAsync(transport, token);
				await CheckBufferAsync(transport, status, token, captured);
				Directory.CreateDirectory(_settings.BackupFolder);
				string report = Path.Combine(_settings.BackupFolder, $"fm9-preset-{slot}-store-{Stamp()}.json");
				Fm9StoredPresetReplacer replacer = new(transport, slot, _settings.Midi.Channel)
				{
					Wait = Wait,
					KeepEvidence = _settings.Safeguards.BackupBeforeStore,
					VerifyStoredReadback = _settings.Safeguards.VerifyAfterStore
				};
				Fm9StoredPresetReplacementReport done = await replacer.ReplaceAsync(file, null, _settings.BackupFolder, report, token);
				return new Fm9StoreResult(slot, Fm9PresetDump.Parse(File.ReadAllBytes(file)).PresetName, done.OriginalPresetName,
					string.IsNullOrEmpty(done.BackupPath) ? null : done.BackupPath, done.ReadbackPath is not null,
					done.NormalizedFields.Select(field => field.CatalogName).Distinct().ToArray(), done.HardwareOperations);
			}, cancellationToken);
		}

		private static async Task<Fm9Status> StatusAsync(IFractalMidiTransport transport, CancellationToken token)
		{
			Fm9ReadOnlyProbeResult identity = await new Fm9ReadOnlyProbe(transport).ProbeAsync(token);
			IReadOnlyList<FractalSysExFrame> presetReplies = await transport.RequestAsync(FractalSysExCodec.CreateCurrentPresetNameRequest(), token);
			FractalSysExFrame preset = presetReplies.FirstOrDefault(frame => FractalSysExCodec.TryGetCurrentPreset(frame.Bytes.Span, out _, out _));
			if (preset.Bytes.IsEmpty || !FractalSysExCodec.TryGetCurrentPreset(preset.Bytes.Span, out int number, out string name))
				throw new InvalidOperationException("The FM9 did not say which preset is showing.");
			IReadOnlyList<FractalSysExFrame> sceneReplies = await transport.RequestAsync(FractalSysExCodec.CreateCurrentSceneRequest(), token);
			FractalSysExFrame scene = sceneReplies.FirstOrDefault(frame => FractalSysExCodec.TryGetCurrentScene(frame.Bytes.Span, out _));
			if (scene.Bytes.IsEmpty || !FractalSysExCodec.TryGetCurrentScene(scene.Bytes.Span, out int sceneNumber))
				throw new InvalidOperationException("The FM9 did not say which scene is playing.");
			return new(identity.FirmwareVersion, number, name, sceneNumber);
		}

		// The preset format and dictionary these tools carry are firmware 12's; writing under other firmware could corrupt.
		internal static async Task CheckFirmwareAsync(IFractalMidiTransport transport, CancellationToken token)
		{
			string firmware = (await new Fm9ReadOnlyProbe(transport).ProbeAsync(token)).FirmwareVersion;
			if (firmware.Split('.')[0] != SupportedFirmwareMajor)
				throw new NotSupportedException(
					$"The FM9 runs firmware {firmware}; these tools write only under firmware {SupportedFirmwareMajor}. Reading still works.");
		}

		// With the safeguard on, a load goes ahead only when the edit buffer holds nothing unsaved: it matches what these
		// tools last loaded, or the stored copy of the preset showing.
		private async Task CheckBufferAsync(IFractalMidiTransport transport, Fm9Status status, CancellationToken token, Fm9PresetDump? captured = null)
		{
			if (!_settings.Safeguards.RefuseLoadIfBufferChanged) return;
			Fm9StoredPresetReader reader = new(transport);
			Fm9PresetDump buffer = (await reader.ReadAsync(EditBuffer, token)).Dump;
			// Storing the edit buffer itself: its edits are what is being kept, provided nothing changed since it was read.
			if (captured is not null && SameBody(buffer, captured)) return;
			if (File.Exists(LastLoadPath) && SameBody(buffer, Fm9PresetDump.Parse(File.ReadAllBytes(LastLoadPath)))) return;
			try
			{
				if (SameBody(buffer, (await reader.ReadAsync(status.Preset, token)).Dump)) return;
			}
			catch (Fm9EmptyPresetSlotException) { }
			throw new InvalidOperationException(
				$"The FM9's edit buffer ('{buffer.PresetName}' on preset {status.Preset}) has unsaved changes, made on the FM9, in FM9-Edit " +
				"or live with these tools. Keep them (store_edit_buffer or Store on the FM9) or discard them on the FM9, or switch off " +
				"refuseLoadIfBufferChanged, then try again.");
		}

		// Body word 0 is the scene playing (hardware-checked: selecting scene 3 set it to 2 and changed nothing else), so
		// stepping through scenes is not an unsaved edit.
		private static bool SameBody(Fm9PresetDump left, Fm9PresetDump right) =>
			left.PresetName == right.PresetName &&
			Fm9PresetBodyDecoder.Decode(left).Bytes.Span[2..].SequenceEqual(Fm9PresetBodyDecoder.Decode(right).Bytes.Span[2..]);

		private string DefaultReadPath(int slot, string name) =>
			Path.Combine(_settings.WorkFolder, "reads", $"{slot:000} {FileName(name)} {Stamp()}.syx");

		private static void CheckSlot(int slot)
		{
			if (slot is < 0 or > 511) throw new ArgumentOutOfRangeException(nameof(slot), "FM9 presets are 0 to 511.");
		}

		private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

		// A preset name as a safe file name.
		public static string FileName(string name)
		{
			StringBuilder builder = new();
			foreach (char character in name.Trim())
				builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
			return builder.Length == 0 ? "untitled" : builder.ToString();
		}
	}
}
