using System.Diagnostics;
using System.Globalization;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace FracTone.Probe
{
	// Ends the run on purpose (a rule said stop); restore still runs.
	public sealed class ProbeStopException(string message) : Exception(message);

	// A step that didn't get its answer; the run records it and moves on unless Stop is set.
	public sealed class ProbeStepFailedException(string message, bool stop = false) : Exception(message)
	{
		public bool Stop { get; } = stop;
	}

	// Runs one probe: identify, back up, the script's steps up to the level asked for, then restore and verify, always.
	// Every rule here is one an agent applied by judgement on the FM9 (docs/fractone-probe-kit-plan.md).
	public sealed partial class ProbeEngine(IFractalMidiTransport port, IProbeClock? clock = null, Action<string>? log = null)
	{
		private readonly IProbeClock _clock = clock ?? SystemProbeClock.Instance;

		public async Task<ProbeRun> RunAsync(ProbeOptions options, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(options);
			if (options.Level is < 1 or > 3)
				throw new ArgumentOutOfRangeException(nameof(options), "Levels are 1, 2 and 3.");
			if (options.Level == 3 && !options.AllowStore)
				throw new ArgumentException("Level 3 stores to the safe slot and must be opted into (AllowStore).", nameof(options));
			ProbeRun run = new() { StartedAt = _clock.Now, LevelRequested = options.Level, Slot = options.Slot };
			Session session = new(run, new ProbeLink(port, _clock, options.Pacing), options);
			try
			{
				await IdentifyAsync(session, cancellationToken);
				await SnapshotAsync(session, cancellationToken);
				foreach (ProbeStep step in session.Script!.Steps.Where(step => step.Level <= run.LevelRun))
				{
					cancellationToken.ThrowIfCancellationRequested();
					await RunScriptStepAsync(session, step, cancellationToken);
				}
				run.Outcome = run.Steps.Any(step => step.Status == ProbeStepStatus.Failed) ? ProbeOutcome.CompletedWithFailures : ProbeOutcome.Completed;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				run.Outcome = ProbeOutcome.Cancelled;
				run.StopReason = "Cancelled by the user.";
			}
			catch (Exception exception) when (exception is ProbeStopException or ProbeWedgedException or ProbeRefusedException)
			{
				run.Outcome = ProbeOutcome.Stopped;
				run.StopReason = exception.Message;
			}
			catch (Exception exception)
			{
				run.Outcome = ProbeOutcome.Crashed;
				run.StopReason = $"{exception.GetType().Name}: {exception.Message}";
			}
			finally
			{
				if (run.StopReason is not null)
					Log(session, $"Stopped: {run.StopReason}");
				Report(session, "restore", "restore", null, null);
				run.Restore = await RestoreAsync(session);
				session.Completed++;
				Report(session, "restore", "restore", run.Restore.LeftAsFound ? ProbeStepStatus.Passed : ProbeStepStatus.Failed,
					run.Restore.LeftAsFound ? "Left as found." : "Not verified as found.");
				run.Exchanges = session.Link.Exchanges;
				run.FinishedAt = _clock.Now;
				Log(session, run.Restore.LeftAsFound ? "The device was left as found." : "The device may NOT be as found: see the restore notes.");
			}
			return run;
		}

		private async Task IdentifyAsync(Session session, CancellationToken cancellationToken)
		{
			await StepAsync(session, "identify", "identify", 1, async data =>
			{
				List<byte> candidates = [.. Gen3Device.All.Select(device => device.Model)];
				if (session.Options.ModelHint is byte hint && candidates.Remove(hint))
					candidates.Insert(0, hint);
				foreach (byte candidate in candidates)
				{
					byte[]? reply = await AskFirmwareAsync(session, candidate, cancellationToken);
					if (reply is null) continue;
					data["model"] = $"0x{reply[4]:X2}";
					Gen3Device device = Gen3Device.FromModel(reply[4])
						?? throw new ProbeStopException($"The device answered as Fractal model 0x{reply[4]:X2}, which this program doesn't know. Nothing else was sent.");
					// It answered (e.g. "wrong model") in its own byte: ask again in that one, once.
					if (reply[4] != candidate)
						reply = await AskFirmwareAsync(session, device.Model, cancellationToken);
					if (reply is null || reply[4] != device.Model ||
						!FractalSysExCodec.TryGetFirmwareVersion(device.Normalise(new FractalSysExFrame(reply)).Bytes.Span, out string firmware))
						throw new ProbeStopException($"The {device.Name} did not report its firmware. Nothing else was sent.");
					session.Device = device;
					session.Run.Device = device.Name;
					session.Run.Model = $"0x{device.Model:X2}";
					session.Run.Firmware = firmware;
					data["device"] = device.Name;
					data["firmware"] = firmware;
					return $"{device.Name}, firmware {firmware}.";
				}
				throw new ProbeStopException(
					"No Fractal device answered the firmware request. Quit Axe-Edit, FM3-Edit, FM9-Edit and Fractal-Bot (they hold the port), " +
					"check the USB cable and that the unit is on, and don't run during a firmware update.");
			}, cancellationToken);

			Gen3Device device = session.Device!;
			ProbeScript? script = ProbeScripts.Find(device, session.Run.Firmware!);
			session.Script = script ?? ProbeScripts.ReadOnly(device, session.Run.Firmware!);
			session.Run.Script = session.Script.Id;
			session.Run.FirmwareScripted = script is not null;
			session.Run.LevelRun = script is null ? 1 : session.Options.Level;
			if (script is null)
				Log(session, $"No probe script for {device.Name} firmware {session.Run.Firmware}: read-only steps only, labelled unverified.");
			session.Total = 6 + session.Script.Steps.Count(step => step.Level <= session.Run.LevelRun);
			int slot = session.Options.Slot;
			if (SafeSlotProblem(device, slot) is string problem)
				throw new ProbeStopException(problem);
			session.Link.Policy = new ProbeCommandPolicy(device, slot, null, session.Options.AllowStore && session.Run.LevelRun == 3);
		}

		private static async Task<byte[]?> AskFirmwareAsync(Session session, byte model, CancellationToken cancellationToken)
		{
			try
			{
				// A unit ignores another model's frames, so these timeouts say nothing about a wedge.
				IReadOnlyList<byte[]> replies = await session.Link.RequestAsync(ProbeFrames.Firmware(model), session.Options.QueryTimeout, cancellationToken, countsTowardsWedge: false);
				return replies.FirstOrDefault(frame => ProbeFrames.HasValidChecksum(frame));
			}
			catch (ProbeTimeoutException)
			{
				return null;
			}
		}

		// Reads only: where the device is, its edit buffer and the safe slot (both kept verbatim), and whether the buffer
		// holds unsaved edits, which a level-2 run would discard, so it stops instead.
		private async Task SnapshotAsync(Session session, CancellationToken cancellationToken)
		{
			Gen3Device device = session.Device!;
			ProbeRun run = session.Run;
			await StepAsync(session, "where", "currentPresetAndScene", 1, async data =>
			{
				(int preset, string name) = await CurrentPresetAsync(session, cancellationToken);
				int scene = await CurrentSceneAsync(session, cancellationToken);
				run.PresetFound = preset;
				run.PresetFoundName = name;
				run.SceneFound = scene;
				session.Link.Policy = new ProbeCommandPolicy(device, session.Options.Slot, preset, session.Options.AllowStore && run.LevelRun == 3);
				data["preset"] = preset.ToString(CultureInfo.InvariantCulture);
				data["scene"] = scene.ToString(CultureInfo.InvariantCulture);
				return $"Preset {preset}, scene {scene}.";
			}, cancellationToken);

			await StepAsync(session, "backup-edit-buffer", "readEditBuffer", 1, async data =>
			{
				session.EditBufferBackup = await ReadDumpAsync(session, ProbeFrames.EditBuffer, ProbeContent.User, cancellationToken)
					?? throw new ProbeStepFailedException("The edit buffer read back empty.", stop: true);
				session.Buffer = session.EditBufferBackup;
				run.Readbacks.Add(new ProbeReadback("backup-edit-buffer.syx", session.EditBufferBackup.Frames, ProbeContent.User));
				Describe(session.EditBufferBackup, data);
				return $"Edit buffer backed up ({session.EditBufferBackup.Frames.Count} frames, verbatim).";
			}, cancellationToken);

			await StepAsync(session, "backup-safe-slot", "readSafeSlot", 1, async data =>
			{
				int slot = session.Options.Slot;
				session.SafeSlotBackup = await ReadDumpAsync(session, slot, ProbeContent.User, cancellationToken);
				if (session.SafeSlotBackup is null)
				{
					data["empty"] = "true";
					return $"Safe slot {slot} is empty (erased); level 2 needs a slot holding a preset.";
				}
				run.Readbacks.Add(new ProbeReadback($"backup-slot-{slot:000}.syx", session.SafeSlotBackup.Frames, ProbeContent.User));
				Describe(session.SafeSlotBackup, data);
				return $"Safe slot {slot} backed up ({session.SafeSlotBackup.Frames.Count} frames, verbatim).";
			}, cancellationToken);

			await StepAsync(session, "unsaved-edits", "unsavedEditsCheck", 1, async data =>
			{
				int preset = run.PresetFound!.Value;
				Captured? stored = preset == session.Options.Slot
					? session.SafeSlotBackup
					: await ReadDumpAsync(session, preset, ProbeContent.User, cancellationToken);
				run.UnsavedEdits = stored is null || !ReadbackComparer.SameContent(device, stored.Dump, session.EditBufferBackup!.Dump);
				data["unsavedEdits"] = run.UnsavedEdits.Value ? "true" : "false";
				if (run.UnsavedEdits.Value && run.LevelRun >= 2)
					throw new ProbeStopException(
						$"The preset showing ({preset}) has unsaved edits (or its slot can't be read to check). Store or discard them on the " +
						"device, then run again; this program never discards them.");
				return run.UnsavedEdits.Value ? "The edit buffer has unsaved edits; read-only steps only." : "No unsaved edits.";
			}, cancellationToken);
		}

		private async Task RunScriptStepAsync(Session session, ProbeStep step, CancellationToken cancellationToken)
		{
			string kind = KindName(step.Kind);
			TimeSpan? timeout = step.TimeoutMs is int ms ? TimeSpan.FromMilliseconds(ms) : null;
			switch (step.Kind)
			{
				case ProbeStepKind.StatusDump:
					await StepAsync(session, step.Id, kind, step.Level, async data =>
					{
						byte[] reply = await QueryAsync(session, ProbeFrames.StatusDump(session.Device!), 0x13, cancellationToken, timeout);
						int entries = (reply.Length - 8) / 3;
						data["entries"] = entries.ToString(CultureInfo.InvariantCulture);
						data["effectIds"] = string.Join(' ', Enumerable.Range(0, entries).Select(index => reply[6 + (index * 3)] | (reply[7 + (index * 3)] << 7)));
						return $"{entries} block(s) listed.";
					}, cancellationToken);
					return;
				case ProbeStepKind.DecodeLayout:
					await StepAsync(session, step.Id, kind, step.Level, data =>
					{
						Captured? source = step.Source == "safeSlot" ? session.SafeSlotBackup : session.EditBufferBackup;
						if (source is null)
							throw new ProbeStepFailedException($"Nothing to decode: the {step.Source} read is empty.");
						Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(source.Dump, session.Device!);
						data["gridCells"] = body.GridCells.Count.ToString(CultureInfo.InvariantCulture);
						data["gridBlocks"] = body.GridCells.Count(cell => cell.EffectId is not null).ToString(CultureInfo.InvariantCulture);
						data["records"] = body.BlockRecords.Count.ToString(CultureInfo.InvariantCulture);
						data["recordShapes"] = string.Join(' ', body.BlockRecords.Select(record =>
							$"{EffectBefore(body, record)}:{record.ParameterColumns}x{record.ParameterRows}"));
						return Task.FromResult($"{data["gridBlocks"]} blocks on a {session.Device!.GridRows}x{session.Device.GridColumns} grid, {body.BlockRecords.Count} records.");
					}, cancellationToken);
					return;
				case ProbeStepKind.SwitchToSafeSlot:
					await StepAsync(session, step.Id, kind, step.Level, async data =>
					{
						if (session.SafeSlotBackup is null)
							throw new ProbeStepFailedException("The safe slot is empty; choose a slot holding a preset for level 2.", stop: true);
						Captured buffer = await SwitchAsync(session, session.Options.Slot, cancellationToken);
						if (!ReadbackComparer.SameContent(session.Device!, buffer.Dump, session.SafeSlotBackup.Dump))
							throw new ProbeStepFailedException("After 01 27 the edit buffer doesn't hold the safe slot as stored.", stop: true);
						session.OnSafeSlot = true;
						session.Buffer = buffer;
						data["confirmedBy"] = "0D 7F 7F";
						return $"On the safe slot {session.Options.Slot}; its buffer equals the backup.";
					}, cancellationToken);
					return;
				case ProbeStepKind.LoadTemplate:
					await StepAsync(session, step.Id, kind, step.Level, async data =>
					{
						RequireSafeSlot(session);
						await ConfirmOnSafeSlotAsync(session, cancellationToken);
						IReadOnlyList<byte[]> template = ProbeFrames.ForEditBuffer(session.Script!.LoadTemplate());
						Fm9PresetDump sent = ProbeFrames.Parse(session.Device!, template);
						Captured readBack = await LoadAndReadBackAsync(session, template, sent, ProbeContent.Ours, timeout, data, cancellationToken);
						session.Run.Readbacks.Add(new ProbeReadback("template-sent.syx", template, ProbeContent.Ours));
						session.Run.Readbacks.Add(new ProbeReadback("template-readback.syx", readBack.Frames, ProbeContent.Ours));
						ReadbackComparison comparison = ReadbackComparer.Compare(session.Device!, session.Script, sent, readBack.Dump);
						data["identical"] = comparison.Identical ? "true" : "false";
						data["recomputed"] = string.Join("; ", comparison.Recomputed.Select(word =>
							$"effect {word.EffectId} {(char)('A' + word.Channel)} id {word.ParameterId}: {word.Sent} -> {word.ReadBack}"));
						if (comparison.UnexpectedOffsets.Count > 0)
							data["unexpectedOffsets"] = string.Join(' ', comparison.UnexpectedOffsets.Take(64));
						if (!comparison.Matches)
							throw new ProbeStepFailedException(comparison.Summary);
						return comparison.Summary;
					}, cancellationToken);
					return;
				case ProbeStepKind.VoicingCheck:
					await VoicingCheckAsync(session, step, kind, cancellationToken);
					return;
				case ProbeStepKind.FreshInserts:
					await FreshInsertsAsync(session, step, kind, cancellationToken);
					return;
				case ProbeStepKind.CableCheck:
					await CableCheckAsync(session, step, kind, cancellationToken);
					return;
				case ProbeStepKind.StoreCheck:
					await StepAsync(session, step.Id, kind, step.Level, async data =>
					{
						RequireSafeSlot(session);
						await ConfirmOnSafeSlotAsync(session, cancellationToken);
						IReadOnlyList<byte[]> template = ProbeFrames.ForEditBuffer(session.Script!.LoadTemplate());
						Fm9PresetDump sent = ProbeFrames.Parse(session.Device!, template);
						await LoadAndReadBackAsync(session, template, sent, ProbeContent.Ours, timeout, data, cancellationToken);
						session.SlotDirty = true;
						await session.Link.SendAsync(ProbeFrames.StorePreset(session.Device!, session.Options.Slot), cancellationToken);
						await session.Link.WaitAsync(session.Options.SwitchSettle, cancellationToken);
						Captured stored = await ReadDumpAsync(session, session.Options.Slot, ProbeContent.Ours, cancellationToken)
							?? throw new ProbeStepFailedException("The safe slot read back empty after the store.", stop: true);
						session.Run.Readbacks.Add(new ProbeReadback("store-readback.syx", stored.Frames, ProbeContent.Ours));
						ReadbackComparison comparison = ReadbackComparer.Compare(session.Device!, session.Script, sent, stored.Dump);
						data["storedMatches"] = comparison.Matches ? "true" : "false";
						data["storedSummary"] = comparison.Summary;
						await PutSlotBackAsync(session, cancellationToken);
						data["slotRestored"] = "true";
						if (!comparison.Matches)
							throw new ProbeStepFailedException($"Stored, but the slot read back differently: {comparison.Summary}");
						return $"Stored and read back: {comparison.Summary} The slot's backup is back and verified.";
					}, cancellationToken);
					return;
				default:
					throw new ProbeRefusedException($"The script asks for an unknown step kind {step.Kind}.");
			}
		}

		// Always: put the device back as found and say whether that was verified. Runs without the caller's token, so a
		// cancel still restores; each check has its own timeout.
		private async Task<ProbeRestore> RestoreAsync(Session session)
		{
			List<string> notes = [];
			if (session.Device is null || session.EditBufferBackup is null || session.Run.PresetFound is not int found)
			{
				bool untouched = !session.Link.ChangedState;
				notes.Add(untouched ? "Nothing that changes the device was sent." : "State changed before the backups were complete.");
				return new ProbeRestore(false, untouched, null, null, null, null, notes);
			}
			Gen3Device device = session.Device;
			session.Link.Step = "restore";
			session.Link.Content = ProbeContent.User;
			CancellationToken none = CancellationToken.None;
			bool? slotRestored = null, safeSlotVerified = null, bufferVerified = null, sceneVerified = null;
			try
			{
				if (session.SlotDirty)
				{
					slotRestored = await Attempt(notes, "put the safe slot back", async () =>
					{
						await PutSlotBackAsync(session, none);
						return true;
					});
				}
				if (!session.Link.ChangedState)
				{
					// Nothing was sent that changes the device, so it is as found whatever this check says; a difference
					// means someone changed it during the run.
					bool same = await Attempt(notes, "check the edit buffer", async () =>
					{
						(int preset, _) = await CurrentPresetAsync(session, none);
						Captured? buffer = await ReadDumpAsync(session, ProbeFrames.EditBuffer, ProbeContent.User, none);
						return preset == found && buffer is not null && ReadbackComparer.SameContent(device, buffer.Dump, session.EditBufferBackup.Dump);
					});
					notes.Add(same
						? "Nothing that changes the device was sent; the edit buffer still equals its backup."
						: "Nothing that changes the device was sent, but the device changed during the run (not by this program).");
					Log(session, "Restore: nothing to put back.");
					return new ProbeRestore(true, true, null, same, null, null, notes);
				}
				else
				{
					if (session.SafeSlotBackup is not null)
						safeSlotVerified = await Attempt(notes, "switch to the safe slot and verify it", async () =>
						{
							Captured buffer = await SwitchAsync(session, session.Options.Slot, none);
							return ReadbackComparer.SameContent(device, buffer.Dump, session.SafeSlotBackup.Dump);
						});
					bufferVerified = await Attempt(notes, "return to the preset found and verify the edit buffer", async () =>
					{
						Captured buffer = found == session.Options.Slot && safeSlotVerified == true
							? (await ReadDumpAsync(session, ProbeFrames.EditBuffer, ProbeContent.User, none))!
							: await SwitchAsync(session, found, none);
						return ReadbackComparer.SameContent(device, buffer.Dump, session.EditBufferBackup.Dump);
					});
					if (session.Run.SceneFound is int scene)
						sceneVerified = await Attempt(notes, "select the scene found", async () =>
						{
							if (await CurrentSceneAsync(session, none) != scene)
							{
								await session.Link.SendAsync(ProbeFrames.SelectScene(device, scene), none);
								await session.Link.WaitAsync(session.Options.LoadSettle, none);
							}
							return await CurrentSceneAsync(session, none) == scene;
						});
				}
			}
			catch (Exception exception)
			{
				notes.Add($"Restore stopped: {exception.Message}");
			}
			bool leftAsFound = slotRestored is not false && safeSlotVerified is not false && bufferVerified == true && sceneVerified is not false;
			if (!leftAsFound && session.SafeSlotBackup is not null)
				notes.Add($"The backups of the safe slot and the edit buffer are saved with the report; nothing was stored over them unless level 3 ran.");
			Log(session, $"Restore: safe slot {Verdict(safeSlotVerified)}, edit buffer {Verdict(bufferVerified)}, scene {Verdict(sceneVerified)}" +
				(slotRestored is null ? "." : $", stored slot {Verdict(slotRestored)}."));
			return new ProbeRestore(true, leftAsFound, safeSlotVerified, bufferVerified, sceneVerified, slotRestored, notes);
		}

		private async Task<bool> Attempt(List<string> notes, string what, Func<Task<bool>> action)
		{
			try
			{
				bool ok = await action();
				if (!ok) notes.Add($"Could not {what}: the readback differs from the backup.");
				return ok;
			}
			catch (Exception exception)
			{
				notes.Add($"Could not {what}: {exception.Message}");
				return false;
			}
		}

		// 01 27, settle, confirm with 0D (asked again once if it hasn't landed yet), then read the buffer.
		private async Task<Captured> SwitchAsync(Session session, int slot, CancellationToken cancellationToken)
		{
			await session.Link.SendAsync(ProbeFrames.SwitchPreset(session.Device!, slot), cancellationToken);
			await session.Link.WaitAsync(session.Options.SwitchSettle, cancellationToken);
			(int preset, _) = await CurrentPresetAsync(session, cancellationToken);
			if (preset != slot)
			{
				await session.Link.WaitAsync(session.Options.SwitchSettle, cancellationToken);
				(preset, _) = await CurrentPresetAsync(session, cancellationToken);
			}
			if (preset != slot)
				throw new ProbeStepFailedException($"01 27 to {slot} landed on {preset}.", stop: true);
			return await ReadDumpAsync(session, ProbeFrames.EditBuffer, ProbeContent.User, cancellationToken)
				?? throw new ProbeStepFailedException("The edit buffer read back empty after the switch.", stop: true);
		}

		// Level 3's undo: the backup's own frames back into the buffer (header addressed 7F 7F, data verbatim), checked, then
		// stored to the safe slot and read back.
		private async Task PutSlotBackAsync(Session session, CancellationToken cancellationToken)
		{
			Captured backup = session.SafeSlotBackup!;
			await LoadAndReadBackAsync(session, ProbeFrames.ForEditBuffer(backup.Frames), backup.Dump, ProbeContent.User, null, null, cancellationToken);
			await session.Link.SendAsync(ProbeFrames.StorePreset(session.Device!, session.Options.Slot), cancellationToken);
			await session.Link.WaitAsync(session.Options.SwitchSettle, cancellationToken);
			Captured stored = await ReadDumpAsync(session, session.Options.Slot, ProbeContent.User, cancellationToken)
				?? throw new ProbeStepFailedException("The safe slot read back empty after putting the backup back.", stop: true);
			if (!ReadbackComparer.SameContent(session.Device!, stored.Dump, backup.Dump))
				throw new ProbeStepFailedException("The safe slot doesn't match its backup after putting it back.", stop: true);
			session.SlotDirty = false;
		}

		// Sends a preset to the edit buffer and reads it back. A load that isn't acknowledged, or whose readback still holds
		// what the buffer held before (a change that didn't take), is sent once more; then the run stops.
		private async Task<Captured> LoadAndReadBackAsync(Session session, IReadOnlyList<byte[]> frames, Fm9PresetDump expected,
			ProbeContent content, TimeSpan? timeout, Dictionary<string, string>? data, CancellationToken cancellationToken)
		{
			Gen3Device device = session.Device!;
			Captured? previous = session.Buffer;
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				ProbeContent was = session.Link.Content;
				session.Link.Content = content;
				string? refused = null;
				try
				{
					foreach (byte[] frame in frames)
					{
						IReadOnlyList<byte[]> replies;
						try
						{
							replies = await session.Link.RequestAsync(frame, timeout ?? session.Options.QueryTimeout, cancellationToken);
						}
						catch (ProbeTimeoutException exception)
						{
							refused = $"frame 0x{frame[5]:X2}: {exception.Message}";
							break;
						}
						byte[]? answer = replies.FirstOrDefault(reply => ProbeFrames.HasValidChecksum(reply) && reply[4] == device.Model && reply[5] == 0x64 && reply.Length > 8 && reply[6] == frame[5]);
						if (answer is null || answer[7] != 0)
						{
							refused = answer is null ? $"frame 0x{frame[5]:X2} was not acknowledged" : $"frame 0x{frame[5]:X2}: {ErrorName(answer[7])}";
							break;
						}
					}
				}
				finally
				{
					session.Link.Content = was;
				}
				await session.Link.WaitAsync(session.Options.LoadSettle, cancellationToken);
				Captured? readBack = await ReadDumpAsync(session, ProbeFrames.EditBuffer, content, cancellationToken);
				bool dropped = refused is not null || readBack is null ||
					(previous is not null && ReadbackComparer.SameContent(device, readBack.Dump, previous.Dump) && !ReadbackComparer.SameContent(device, previous.Dump, expected));
				if (!dropped)
				{
					session.Buffer = readBack;
					if (data is not null) data["loadAttempts"] = attempt.ToString(CultureInfo.InvariantCulture);
					return readBack!;
				}
				Log(session, attempt == 1
					? $"The load didn't take ({refused ?? "the buffer still holds what it held before"}); sending it once more."
					: "The load didn't take twice; stopping.");
			}
			throw new ProbeStepFailedException("The device dropped the load twice.", stop: true);
		}

		// Before every write: the device must still be on the safe slot, or someone changed preset on it.
		private async Task ConfirmOnSafeSlotAsync(Session session, CancellationToken cancellationToken)
		{
			(int preset, _) = await CurrentPresetAsync(session, cancellationToken);
			if (preset != session.Options.Slot)
				throw new ProbeStopException($"The device is on preset {preset}, not the safe slot {session.Options.Slot}: it was changed during the run.");
		}

		private static void RequireSafeSlot(Session session)
		{
			if (!session.OnSafeSlot)
				throw new ProbeStepFailedException("This step needs the switch to the safe slot first; the script is out of order.", stop: true);
		}

		private async Task<(int Preset, string Name)> CurrentPresetAsync(Session session, CancellationToken cancellationToken)
		{
			byte[] reply = await QueryAsync(session, ProbeFrames.CurrentPreset(session.Device!), 0x0D, cancellationToken);
			if (!FractalSysExCodec.TryGetCurrentPreset(session.Device!.Normalise(new FractalSysExFrame(reply)).Bytes.Span, out int preset, out string name))
				throw new ProbeStepFailedException("The current-preset reply is malformed.");
			return (preset, name);
		}

		private async Task<int> CurrentSceneAsync(Session session, CancellationToken cancellationToken)
		{
			byte[] reply = await QueryAsync(session, ProbeFrames.CurrentScene(session.Device!), 0x0C, cancellationToken);
			if (!FractalSysExCodec.TryGetCurrentScene(session.Device!.Normalise(new FractalSysExFrame(reply)).Bytes.Span, out int scene))
				throw new ProbeStepFailedException("The current-scene reply is malformed.");
			return scene;
		}

		// A short request with one retry: a timeout or a reply without the answer is asked again once.
		private async Task<byte[]> QueryAsync(Session session, byte[] frame, byte function, CancellationToken cancellationToken, TimeSpan? timeout = null)
		{
			string problem = "";
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				try
				{
					IReadOnlyList<byte[]> replies = await session.Link.RequestAsync(frame, timeout ?? session.Options.QueryTimeout, cancellationToken);
					byte[]? answer = replies.FirstOrDefault(reply => IsReply(session, reply, function));
					if (answer is not null) return answer;
					byte[]? error = replies.FirstOrDefault(reply => IsReply(session, reply, 0x64) && reply.Length > 8 && reply[6] == function);
					problem = error is null ? "no answer among the replies" : ErrorName(error[7]);
				}
				catch (ProbeTimeoutException exception)
				{
					problem = exception.Message;
				}
			}
			throw new ProbeStepFailedException($"Function 0x{function:X2}: {problem} (asked twice).");
		}

		// A preset read (03), retried once when it times out or comes back incomplete. Null for an erased slot.
		private async Task<Captured?> ReadDumpAsync(Session session, int slot, ProbeContent content, CancellationToken cancellationToken)
		{
			Gen3Device device = session.Device!;
			ProbeContent was = session.Link.Content;
			session.Link.Content = content;
			string problem = "";
			try
			{
				for (int attempt = 1; attempt <= 2; attempt++)
				{
					try
					{
						IReadOnlyList<byte[]> replies = await session.Link.RequestAsync(ProbeFrames.ReadPreset(device, slot), session.Options.DumpTimeout, cancellationToken);
						byte[][] frames = [.. replies.Where(reply => IsReply(session, reply, 0x77) || IsReply(session, reply, 0x78) || IsReply(session, reply, 0x79))];
						if (frames.Length != device.DumpDataFrames + 2)
						{
							problem = $"{frames.Length} of {device.DumpDataFrames + 2} frames";
							continue;
						}
						return new Captured(frames, ProbeFrames.Parse(device, frames));
					}
					catch (Fm9EmptyPresetSlotException)
					{
						return null;
					}
					catch (InvalidDataException exception)
					{
						problem = exception.Message;
					}
					catch (ProbeTimeoutException exception)
					{
						problem = exception.Message;
					}
				}
			}
			finally
			{
				session.Link.Content = was;
			}
			throw new ProbeStepFailedException($"Reading {(slot == ProbeFrames.EditBuffer ? "the edit buffer" : $"preset {slot}")} failed twice: {problem}");
		}

		private static bool IsReply(Session session, byte[] reply, byte function) =>
			ProbeFrames.HasValidChecksum(reply) && reply[4] == session.Device!.Model && reply[5] == function;

		private async Task StepAsync(Session session, string id, string kind, int level, Func<Dictionary<string, string>, Task<string>> body, CancellationToken cancellationToken)
		{
			session.Link.Step = id;
			Dictionary<string, string> data = new(StringComparer.Ordinal);
			DateTimeOffset started = _clock.Now;
			Stopwatch watch = Stopwatch.StartNew();
			void Add(ProbeStepStatus status, string message)
			{
				session.Run.Steps.Add(new ProbeStepResult(id, kind, level, status, message, started, Math.Round(watch.Elapsed.TotalMilliseconds, 1), data));
				Log(session, $"[{status}] {id}: {message}");
				session.Completed++;
				Report(session, id, kind, status, message);
			}
			Report(session, id, kind, null, null);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				Add(ProbeStepStatus.Passed, await body(data));
			}
			catch (ProbeStepFailedException exception)
			{
				Add(exception.Stop ? ProbeStepStatus.Stopped : ProbeStepStatus.Failed, exception.Message);
				if (exception.Stop) throw new ProbeStopException(exception.Message);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				Add(ProbeStepStatus.Stopped, "Cancelled.");
				throw;
			}
			catch (Exception exception) when (exception is ProbeStopException or ProbeWedgedException or ProbeRefusedException)
			{
				Add(ProbeStepStatus.Stopped, exception.Message);
				throw;
			}
			catch (Exception exception) when (exception is InvalidDataException or ProbeTimeoutException)
			{
				Add(ProbeStepStatus.Failed, exception.Message);
			}
			catch (Exception exception)
			{
				Add(ProbeStepStatus.Failed, $"{exception.GetType().Name}: {exception.Message}");
				throw;
			}
		}

		// Why the slot can't be the safe slot on this device, or null when it can. The GUI checks the same rule up front.
		public static string? SafeSlotProblem(Gen3Device device, int slot)
		{
			ArgumentNullException.ThrowIfNull(device);
			if (slot < 0 || slot >= device.Presets)
				return $"The {device.Name} has presets 0 to {device.Presets - 1}; the safe slot {slot} isn't one.";
			if (slot >= 512)
				return "Slots above 511 need an Axe-Fx III Mk II, which can't be told from a Mk I yet; choose a slot from 0 to 511.";
			return null;
		}

		// Read-only: asks for the firmware (the only frame allowed before the device is known) and says which device
		// answered. Throws ProbeStopException when nothing or an unknown model answers.
		public async Task<ProbeIdentity> IdentifyAsync(byte? modelHint, CancellationToken cancellationToken)
		{
			ProbeOptions options = new(0) { ModelHint = modelHint };
			// Slot 0 exists on every gen-3 unit, so the identity step's slot check always passes here.
			Session session = new(new ProbeRun { StartedAt = _clock.Now }, new ProbeLink(port, _clock, options.Pacing), options);
			await IdentifyAsync(session, cancellationToken);
			return new ProbeIdentity(session.Device!, session.Run.Firmware!, session.Run.FirmwareScripted == true);
		}

		// Read-only: the name of the preset stored in a slot (03 read, nothing sent that changes the device), or null for
		// an erased slot. Lets the user see what's in the slot they're about to name as safe.
		public async Task<string?> ReadPresetNameAsync(ProbeIdentity identity, int slot, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(identity);
			if (SafeSlotProblem(identity.Device, slot) is string problem)
				throw new ArgumentOutOfRangeException(nameof(slot), problem);
			ProbeOptions options = new(slot);
			Session session = new(new ProbeRun { StartedAt = _clock.Now, Slot = slot }, new ProbeLink(port, _clock, options.Pacing), options)
			{
				Device = identity.Device
			};
			session.Link.Policy = new ProbeCommandPolicy(identity.Device, slot, null, allowStore: false);
			session.Link.Step = "read-slot-name";
			Captured? captured = await ReadDumpAsync(session, slot, ProbeContent.User, cancellationToken);
			return captured?.Dump.PresetName;
		}

		private static void Report(Session session, string step, string kind, ProbeStepStatus? status, string? message) =>
			session.Options.Progress?.Report(new ProbeProgress(session.Completed, session.Total, step, kind, status, message));

		private void Log(Session session, string line)
		{
			session.Run.Log.Add($"{_clock.Now:HH:mm:ss.fff} {line}");
			log?.Invoke(line);
		}

		private static void Describe(Captured captured, Dictionary<string, string> data)
		{
			data["frames"] = captured.Frames.Count.ToString(CultureInfo.InvariantCulture);
			data["formatVersion"] = $"0x{captured.Dump.FormatVersion:X4}";
		}

		private static int EffectBefore(Fm9PresetBody body, Fm9PresetBlockRecord record) =>
			record.Offset >= 12 ? BitConverter.ToUInt16(body.Bytes.Span[(record.Offset - 12)..]) : -1;

		private static string KindName(ProbeStepKind kind) => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

		private static string Verdict(bool? value) => value switch { true => "verified", false => "NOT verified", null => "n/a" };

		// The multipurpose reply's codes (FracCompat's brief, the same table on all three units).
		private static string ErrorName(byte code) => code switch
		{
			0x02 => "error 0x02 wrong model",
			0x05 => "error 0x05 invalid effect",
			0x06 => "error 0x06 invalid parameter",
			0x07 => "error 0x07 effect not in preset",
			0x0B => "error 0x0B bad grid position",
			0x0C => "error 0x0C DSP overload",
			0x0E => "error 0x0E invalid patch number",
			0x1B => "error 0x1B flash write failed",
			_ => $"error 0x{code:X2}"
		};

		internal sealed record Captured(IReadOnlyList<byte[]> Frames, Fm9PresetDump Dump);

		private sealed class Session(ProbeRun run, ProbeLink link, ProbeOptions options)
		{
			public ProbeRun Run { get; } = run;
			public ProbeLink Link { get; } = link;
			public ProbeOptions Options { get; } = options;
			public Gen3Device? Device { get; set; }
			public ProbeScript? Script { get; set; }
			public Captured? EditBufferBackup { get; set; }
			public Captured? SafeSlotBackup { get; set; }
			// What the edit buffer was last read to hold, to spot a load that didn't take.
			public Captured? Buffer { get; set; }
			public bool OnSafeSlot { get; set; }
			// The template was stored over the safe slot and the backup isn't back yet.
			public bool SlotDirty { get; set; }
			// Steps finished so far and the run's step count (0 until the script is known), for progress events.
			public int Completed { get; set; }
			public int Total { get; set; }
		}
	}
}
