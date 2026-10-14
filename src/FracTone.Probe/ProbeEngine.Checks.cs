using System.Globalization;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	// Level 2's spot checks, all on the safe slot's edit buffer: voicing (select a type live, read the block back with 1F),
	// fresh inserts (one block at a time on an empty preset) and cable codes (01 35, read back as the grid's route masks).
	// Every write is read back; one that didn't take is sent once more, then recorded as a failure and the step goes on.
	public sealed partial class ProbeEngine
	{
		private const int RecordPrefixBytes = 12, RecordHeaderWords = 17;

		private async Task VoicingCheckAsync(Session session, ProbeStep step, string kind, CancellationToken cancellationToken)
		{
			if (step.Types is not { Count: > 0 } || step.EffectId is null || step.ParameterId is null)
			{
				// Kept in the script so the order is fixed; the types come when someone knows what to compare them with.
				session.Run.Steps.Add(new ProbeStepResult(step.Id, kind, step.Level, ProbeStepStatus.Skipped,
					$"No types listed yet: {step.Description}", _clock.Now, 0, new Dictionary<string, string>()));
				session.Completed++;
				Report(session, step.Id, kind, ProbeStepStatus.Skipped, "No types listed yet.");
				return;
			}
			int effect = step.EffectId.Value, parameter = step.ParameterId.Value, tolerance = step.Tolerance ?? 1;
			await CheckStepAsync(session, step, kind, async data =>
			{
				Gen3Device device = session.Device!;
				if (FindRecord(Body(session), effect) is null)
				{
					ProbeInsert insert = step.Inserts?.FirstOrDefault(candidate => candidate.EffectId == effect)
						?? throw new ProbeStepFailedException($"The edit buffer has no {step.Block} (effect {effect}) and the script inserts none.");
					if (!await InsertAsync(session, insert, data, cancellationToken))
						throw new ProbeStepFailedException($"{insert.Block} could not be inserted, so its types were not checked.");
				}
				Fm9RecordUnit record = FindRecord(Body(session), effect)!;
				List<string> failures = [];
				int compared = 0;
				foreach (ProbeVoicing type in step.Types)
				{
					string key = type.Ordinal.ToString(CultureInfo.InvariantCulture);
					if (type.Name is not null) data[$"{key}.name"] = type.Name;
					// The type word comes back as sent once the change took; the bulk read starts at channel A, the channel the
					// templates' scenes play, which is where a live change lands.
					IReadOnlyList<int>? values = null;
					for (int attempt = 1; attempt <= 2 && values is null; attempt++)
					{
						await session.Link.SendAsync(ProbeFrames.SelectType(device, effect, parameter, type.Ordinal), cancellationToken);
						await session.Link.WaitAsync(session.Options.LoadSettle, cancellationToken);
						IReadOnlyList<int> read = await BulkReadAsync(session, effect, cancellationToken);
						if (parameter < read.Count && read[parameter] == type.Ordinal)
							values = read;
						else
							Log(session, attempt == 1
								? $"{step.Block} type {type.Ordinal} didn't take; sending it once more."
								: $"{step.Block} type {type.Ordinal} didn't take twice.");
					}
					if (values is null)
					{
						data[$"{key}.taken"] = "false";
						failures.Add($"type {type.Ordinal} was not selected after two tries");
						continue;
					}
					IEnumerable<int> words = step.Words ?? Enumerable.Range(0, Math.Min(record.Columns, values.Count));
					data[$"{key}.words"] = string.Join(' ', words.Where(word => word < values.Count).Select(word => $"{word}={values[word]}"));
					if ((type.Expect ?? ExpectedVoicing(session, step, type.Ordinal)) is not { } expected)
						continue;
					compared++;
					string[] mismatches = [.. expected.Where(pair => pair.Key >= values.Count || Math.Abs(values[pair.Key] - pair.Value) > tolerance)
						.Select(pair => $"{pair.Key}: {(pair.Key < values.Count ? values[pair.Key].ToString(CultureInfo.InvariantCulture) : "missing")} want {pair.Value}")];
					data[$"{key}.expected"] = expected.Count.ToString(CultureInfo.InvariantCulture);
					if (mismatches.Length > 0)
					{
						data[$"{key}.mismatches"] = string.Join("; ", mismatches);
						failures.Add($"type {type.Ordinal}: {mismatches.Length} of {expected.Count} words differ");
					}
				}
				data["types"] = step.Types.Count.ToString(CultureInfo.InvariantCulture);
				if (failures.Count > 0)
					throw new ProbeStepFailedException($"{step.Block}: {string.Join("; ", failures)}.");
				return compared == 0
					? $"{step.Types.Count} {step.Block} type(s) selected and recorded (nothing to compare with)."
					: $"{step.Types.Count} {step.Block} type(s) selected; {compared} matched their expected words.";
			}, cancellationToken);
		}

		private async Task FreshInsertsAsync(Session session, ProbeStep step, string kind, CancellationToken cancellationToken)
		{
			await CheckStepAsync(session, step, kind, async data =>
			{
				// Block state lives in RAM: only the first insert after a power cycle gives a block's defaults, and the probe
				// can't tell whether this is one, so the words are recorded, not judged.
				data["sincePowerCycle"] = "unknown";
				List<string> failed = [];
				foreach (ProbeInsert insert in step.Inserts ?? [])
				{
					if (!await InsertAsync(session, insert, data, cancellationToken))
					{
						failed.Add(insert.Block);
						continue;
					}
					Fm9PresetBody body = Body(session);
					Fm9RecordUnit record = FindRecord(body, insert.EffectId)!;
					ReadOnlySpan<byte> bytes = body.Bytes.Span[(record.Offset + RecordPrefixBytes)..(record.Offset + record.Length)];
					data[$"{insert.Block}.shape"] = $"{record.Columns}x{record.Rows}";
					data[$"{insert.Block}.header"] = Words(bytes[..(RecordHeaderWords * 2)]);
					data[$"{insert.Block}.words"] = Words(bytes[(RecordHeaderWords * 2)..]);
				}
				session.Run.Readbacks.Add(new ProbeReadback($"{step.Id}-readback.syx", session.Buffer!.Frames, ProbeContent.Ours));
				if (failed.Count > 0)
					throw new ProbeStepFailedException($"Not inserted after two tries: {string.Join(", ", failed)}.");
				return $"{step.Inserts?.Count ?? 0} block(s) inserted one at a time and recorded (first insert since a power cycle: unknown).";
			}, cancellationToken);
		}

		private async Task CableCheckAsync(Session session, ProbeStep step, string kind, CancellationToken cancellationToken)
		{
			await CheckStepAsync(session, step, kind, async data =>
			{
				Gen3Device device = session.Device!;
				foreach (ProbeInsert insert in step.Inserts ?? [])
					if (!await InsertAsync(session, insert, data, cancellationToken))
						throw new ProbeStepFailedException($"{insert.Block} could not be inserted, so the cables were not checked.");
				List<string> failures = [];
				Fm9PresetBody before = Body(session);
				foreach (ProbeRoute route in step.Routes ?? [])
				{
					byte[] frame;
					try
					{
						frame = ProbeFrames.Route(device, route.FromRow, route.FromColumn, route.ToRow);
					}
					catch (ArgumentOutOfRangeException exception)
					{
						failures.Add($"{route.Id}: {exception.Message}");
						continue;
					}
					int beforeMask = Mask(before, route.ToRow, route.FromColumn + 1);
					// The FM9, FM3 and III all mark a cable as bit (source row - 1) of the destination's mask.
					int expected = route.ExpectMask ?? (beforeMask | (1 << (route.FromRow - 1)));
					Fm9PresetBody after = before;
					for (int attempt = 1; attempt <= 2; attempt++)
					{
						await session.Link.SendAsync(frame, cancellationToken);
						await session.Link.WaitAsync(session.Options.LoadSettle, cancellationToken);
						after = await ReadBodyAsync(session, cancellationToken);
						if (expected == beforeMask || Mask(after, route.ToRow, route.FromColumn + 1) != beforeMask)
							break;
						Log(session, attempt == 1 ? $"Cable {route.Id} didn't take; sending it once more." : $"Cable {route.Id} didn't take twice.");
					}
					int mask = Mask(after, route.ToRow, route.FromColumn + 1);
					string[] changed = [.. after.GridCells.Zip(before.GridCells)
						.Where(pair => pair.First.RouteMask != pair.Second.RouteMask)
						.Select(pair => $"r{pair.First.Row}c{pair.First.Column} 0x{pair.Second.RouteMask:X2}->0x{pair.First.RouteMask:X2}")];
					data[$"{route.Id}.frame"] = ProbeFrames.Hex(frame.AsSpan(21, 3));
					data[$"{route.Id}.mask"] = $"0x{mask:X2}";
					data[$"{route.Id}.changed"] = changed.Length == 0 ? "none" : string.Join(", ", changed);
					bool others = after.GridCells.Zip(before.GridCells).Any(pair => pair.First.RouteMask != pair.Second.RouteMask &&
						(pair.First.Row, pair.First.Column) != (route.ToRow, route.FromColumn + 1));
					if (mask != expected || others)
						failures.Add($"{route.Id}: mask 0x{mask:X2}, expected 0x{expected:X2}{(others ? ", and other cells changed" : "")}");
					before = after;
				}
				session.Run.Readbacks.Add(new ProbeReadback($"{step.Id}-readback.syx", session.Buffer!.Frames, ProbeContent.Ours));
				if (failures.Count > 0)
					throw new ProbeStepFailedException($"{failures.Count} of {step.Routes?.Count ?? 0} cables differ: {string.Join("; ", failures)}.");
				return $"{step.Routes?.Count ?? 0} cable(s) read back as expected ({device.GridRows}-row cell numbering).";
			}, cancellationToken);
		}

		// What every check does first: be on the safe slot (still), mark expectations carried over from the FM9, and load
		// the step's template. Exchanges inside are our content: the buffer holds our template from here on.
		private async Task CheckStepAsync(Session session, ProbeStep step, string kind, Func<Dictionary<string, string>, Task<string>> body,
			CancellationToken cancellationToken)
		{
			await StepAsync(session, step.Id, kind, step.Level, async data =>
			{
				ProbeContent was = session.Link.Content;
				session.Link.Content = ProbeContent.Ours;
				try
				{
					RequireSafeSlot(session);
					await ConfirmOnSafeSlotAsync(session, cancellationToken);
					if (step.Unverified == true) data["unverified"] = "true";
					if (step.Template is string template)
					{
						IReadOnlyList<byte[]> frames = ProbeFrames.ForEditBuffer(session.Script!.LoadTemplate(template));
						Fm9PresetDump sent = ProbeFrames.Parse(session.Device!, frames);
						TimeSpan? timeout = step.TimeoutMs is int ms ? TimeSpan.FromMilliseconds(ms) : null;
						Captured readBack = await LoadAndReadBackAsync(session, frames, sent, ProbeContent.Ours, timeout, data, cancellationToken);
						ReadbackComparison comparison = ReadbackComparer.Compare(session.Device!, session.Script, sent, readBack.Dump);
						data["template"] = template;
						if (!comparison.Matches)
							throw new ProbeStepFailedException($"The {template} template read back differently: {comparison.Summary}");
					}
					return await body(data);
				}
				finally
				{
					session.Link.Content = was;
				}
			}, cancellationToken);
		}

		// 01 30 + 01 32, then the buffer read back: the block must sit in its cell with a record. Sent once more if not.
		private async Task<bool> InsertAsync(Session session, ProbeInsert insert, Dictionary<string, string> data, CancellationToken cancellationToken)
		{
			Gen3Device device = session.Device!;
			IReadOnlyList<byte[]> frames;
			try
			{
				frames = ProbeFrames.InsertBlock(device, insert.EffectId, insert.Row, insert.Column);
			}
			catch (ArgumentOutOfRangeException exception)
			{
				throw new ProbeStepFailedException($"{insert.Block} at r{insert.Row}c{insert.Column}: {exception.Message}");
			}
			data[$"{insert.Block}.cell"] = $"r{insert.Row}c{insert.Column}";
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				foreach (byte[] frame in frames)
					await session.Link.SendAsync(frame, cancellationToken);
				await session.Link.WaitAsync(session.Options.LoadSettle, cancellationToken);
				Fm9PresetBody body = await ReadBodyAsync(session, cancellationToken);
				if (body.GridCells.Any(cell => cell.Row == insert.Row && cell.Column == insert.Column && cell.EffectId == insert.EffectId) &&
					FindRecord(body, insert.EffectId) is not null)
				{
					data[$"{insert.Block}.attempts"] = attempt.ToString(CultureInfo.InvariantCulture);
					return true;
				}
				Log(session, attempt == 1 ? $"{insert.Block} didn't land; inserting it once more." : $"{insert.Block} didn't land twice.");
			}
			data[$"{insert.Block}.attempts"] = "2";
			return false;
		}

		// 1F: one block's words as the device holds them (channel A first, then B to D), retried once.
		private async Task<IReadOnlyList<int>> BulkReadAsync(Session session, int effectId, CancellationToken cancellationToken)
		{
			Gen3Device device = session.Device!;
			string problem = "";
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				try
				{
					IReadOnlyList<byte[]> replies = await session.Link.RequestAsync(ProbeFrames.BulkRead(device, effectId), session.Options.QueryTimeout, cancellationToken);
					FractalSysExFrame[] frames = [.. replies.Where(reply => IsReply(session, reply, 0x74) || IsReply(session, reply, 0x75) || IsReply(session, reply, 0x76))
						.Select(reply => device.Normalise(new FractalSysExFrame(reply)))];
					if (Fm9BulkRead.TryParse(frames, out Fm9BulkReadResult? result) && result!.EffectId == effectId)
						return result.Values;
					byte[]? error = replies.FirstOrDefault(reply => IsReply(session, reply, 0x64) && reply.Length > 8 && reply[6] == 0x1F);
					problem = error is null ? "no complete block read among the replies" : ErrorName(error[7]);
				}
				catch (ProbeTimeoutException exception)
				{
					problem = exception.Message;
				}
			}
			throw new ProbeStepFailedException($"Reading effect {effectId} (1F) failed twice: {problem}");
		}

		private async Task<Fm9PresetBody> ReadBodyAsync(Session session, CancellationToken cancellationToken)
		{
			session.Buffer = await ReadDumpAsync(session, ProbeFrames.EditBuffer, ProbeContent.Ours, cancellationToken)
				?? throw new ProbeStepFailedException("The edit buffer read back empty.");
			return Body(session);
		}

		// The FM9's own captures are the expectation where the script asks for them; other units only by explicit words.
		private static IReadOnlyDictionary<int, int>? ExpectedVoicing(Session session, ProbeStep step, int ordinal)
		{
			if (step.ExpectFrom is null) return null;
			if (step.ExpectFrom != "fm9ModelDefaults" || session.Device != Gen3Device.Fm9 || step.Block is null)
				throw new ProbeStepFailedException($"Expectations '{step.ExpectFrom}' exist only for the FM9's own blocks.");
			return Fm9ModelDefaults.For(step.Block, ordinal);
		}

		private static Fm9PresetBody Body(Session session) => Fm9PresetBodyDecoder.Decode(session.Buffer!.Dump, session.Device!);

		private static Fm9RecordUnit? FindRecord(Fm9PresetBody body, int effectId) =>
			Fm9RecordOrder.Units(body).FirstOrDefault(unit => unit.EffectId == effectId);

		private static int Mask(Fm9PresetBody body, int row, int column) =>
			body.GridCells.FirstOrDefault(cell => cell.Row == row && cell.Column == column)?.RouteMask ?? 0;

		private static string Words(ReadOnlySpan<byte> bytes)
		{
			string[] words = new string[bytes.Length / 2];
			for (int index = 0; index < words.Length; index++)
				words[index] = BitConverter.ToUInt16(bytes[(index * 2)..]).ToString(CultureInfo.InvariantCulture);
			return string.Join(' ', words);
		}
	}
}
