using System.Globalization;
using Fractal.Application;
using Fractal.Companion.Auditioning;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Fm9
{
	public sealed record Fm9LiveChange(string Setting, string? Before, string? After);

	public sealed record Fm9LiveResult(string Block, string Channel, string? SwitchedFromChannel, string? ModelBefore, string? ModelAfter,
		IReadOnlyList<Fm9LiveChange> Changes, IReadOnlyList<string> Notes);

	public sealed record Fm9LiveBlockState(string Block, int Scene, string PlayingChannel, bool Bypassed, string Channel, string? Model,
		IReadOnlyDictionary<string, string> Settings);

	public sealed record Fm9LiveSwitchResult(string Block, int Scene, string Channel, bool Bypassed, IReadOnlyList<string> Changes);

	// Live control of the FM9's edit buffer over MIDI, as FM9-Edit does it: settings, a block's channel and bypass, and
	// scenes. Nothing is stored. Values convert exactly as the offline edits do (the same editor computes the words), and
	// everything is read back from the FM9 afterwards. Hardware-checked 2026-10-08: a parameter message lands on the
	// channel the block is playing; setting a block's channel or bypass changes the current scene; and selecting a model
	// or type over MIDI makes the FM9 load that model's voicing itself.
	public sealed class Fm9Live
	{
		private const int EditBuffer = 0x3FFF;
		private readonly Fm9Connection _connection;
		private readonly Fm9LiveHistory? _history;

		// With a state folder, every live change is recorded so it can be undone (Fm9LiveHistory).
		public Fm9Live(Fm9Connection connection, string? stateFolder = null)
		{
			_connection = connection ?? throw new ArgumentNullException(nameof(connection));
			_history = stateFolder is null ? null : new Fm9LiveHistory(stateFolder);
		}

		public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

		// A block's live settings: the channel asked for, or the one it is playing.
		public Task<Fm9LiveBlockState> GetAsync(Fm9BlockRef block, string? channel, IReadOnlyList<string>? only, CancellationToken cancellationToken) =>
			_connection.RunAsync(async (transport, token) =>
			{
				Fm9PresetDump buffer = await ReadBufferAsync(transport, token);
				Fm9UploadedPreset preset = Fm9UploadedPreset.Read(buffer);
				Fm9UploadedBlock target = Fm9Presets.Editable(preset, block);
				(int scene, int playing, bool bypassed) = await StateAsync(transport, target.EffectId, token);
				int row = channel is null ? playing : Fm9Names.Channel(channel);
				if (row >= Fm9UploadedPreset.ChannelsOf(target)) throw new ArgumentException($"{Fm9Presets.Name(target)} has no channel {Fm9Names.Letter(row)}.");
				IReadOnlyDictionary<string, string> settings = preset.GetParameters(target, row);
				SortedDictionary<string, string> chosen = new(StringComparer.Ordinal);
				foreach ((string name, string value) in settings)
					if (only is not { Count: > 0 } || only.Any(wanted => wanted.Equals(name, StringComparison.OrdinalIgnoreCase)))
						chosen[name] = value;
				return new Fm9LiveBlockState(Fm9Presets.Name(target), scene, Fm9Names.Letter(playing), bypassed, Fm9Names.Letter(row),
					Fm9Names.Model(target.BlockType, settings), chosen);
			}, cancellationToken);

		// Sets block settings live. With a channel other than the one playing, the block switches to it first (as clicking a
		// channel in FM9-Edit does), which changes the current scene. A new model or type goes first so the FM9 loads its
		// voicing; the other settings are then written over it.
		public Task<Fm9LiveResult> SetAsync(Fm9BlockRef block, IReadOnlyDictionary<string, string> settings, string? channel,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(settings);
			if (settings.Count == 0) throw new ArgumentException("Give at least one setting to change.", nameof(settings));
			return _connection.RunAsync(async (transport, token) =>
			{
				await Fm9Device.CheckFirmwareAsync(transport, token);
				(int presetNumber, string presetName) = await CurrentPresetAsync(transport, token);
				Fm9PresetDump before = await ReadBufferAsync(transport, token);
				Fm9UploadedPreset preset = Fm9UploadedPreset.Read(before);
				Fm9UploadedBlock target = Fm9Presets.Editable(preset, block);
				(_, int playing, _) = await StateAsync(transport, target.EffectId, token);
				List<Fm9LiveWord> words = [];
				string? snapshot = null;
				int row = channel is null ? playing : Fm9Names.Channel(channel);
				if (row >= Fm9UploadedPreset.ChannelsOf(target)) throw new ArgumentException($"{Fm9Presets.Name(target)} has no channel {Fm9Names.Letter(row)}.");
				string letter = Fm9Names.Letter(row);
				IReadOnlyDictionary<string, string> current = preset.GetParameters(target, row);
				Dictionary<string, string> named = settings.ToDictionary(pair => Fm9Presets.Canonical(target, current, pair.Key),
					pair => Fm9Presets.ModelOrdinal(target.BlockType, Fm9Presets.Canonical(target, current, pair.Key), pair.Value), StringComparer.Ordinal);
				List<string> notes = [];

				string? switchedFrom = null;
				if (row != playing)
				{
					await SwitchChannelAsync(transport, target, row, token);
					switchedFrom = Fm9Names.Letter(playing);
					notes.Add($"{Fm9Presets.Name(target)} now plays channel {letter} in the current scene (it played {switchedFrom}).");
				}

				Fm9PresetDump working = before;
				string? type = Fm9ModelDefaults.TypeSetting(target.BlockType);
				if (type is not null && named.TryGetValue(type, out string? model) && model != current.GetValueOrDefault(type))
				{
					// A model change reloads voicing, which values alone cannot undo: keep the whole buffer from before.
					if (_history is not null)
					{
						snapshot = _history.NewSnapshotPath($"before {Fm9Presets.Name(target)} model {DateTime.Now:yyyyMMdd-HHmmss-fff}");
						Fm9Files.WriteAtomically(snapshot, before.ToArray());
					}
					int column = TypeColumn(target.BlockType, type);
					int ordinal = int.Parse(model, CultureInfo.InvariantCulture);
					await transport.SendAsync(FractalSysExCodec.CreateDiscreteParameterChange(target.EffectId, column, ordinal), token);
					await Wait(TimeSpan.FromSeconds(1), token);
					working = await ReadBufferAsync(transport, token);
					named.Remove(type);
				}

				Dictionary<int, int> sent = [];
				if (named.Count > 0)
				{
					Fm9UploadedPreset live = Fm9UploadedPreset.Read(working);
					ToneDesignProposal proposal = live.ToProposal();
					proposal = proposal with
					{
						Blocks = proposal.Blocks.Select(candidate => candidate.BlockType == target.BlockType && candidate.Instance == target.Instance
							? candidate.WithChannel(letter, named, null) : candidate).ToArray()
					};
					Fm9UploadedPreset edited = Fm9UploadedPreset.Read(Fm9UploadedPreset.Apply(proposal));
					Fm9UploadedBlock editedBlock = edited.Find(target.BlockType, target.Instance)!;
					Fm9EffectSection? section = Section(target.BlockType);
					for (int id = 0; id < target.Record!.ParameterColumns; id++)
					{
						int from = Word(live.Body, live.Find(target.BlockType, target.Instance)!.Record!, row, id);
						int to = Word(edited.Body, editedBlock.Record!, row, id);
						if (from == to) continue;
						Fm9EffectParameter parameter = section?.Find(id)
							?? throw new NotSupportedException($"{Fm9Presets.Name(target)} column {id} has no dictionary entry, so it cannot be set live; use set_channel and audition.");
						await transport.SendAsync(Frame(target.EffectId, id, parameter, to), token);
						sent[id] = to;
						words.Add(new(target.BlockType, target.EffectId, id, from, to));
					}
					await Wait(TimeSpan.FromMilliseconds(300), token);
				}

				Fm9PresetDump after = await ReadBufferAsync(transport, token);
				Fm9UploadedPreset final = Fm9UploadedPreset.Read(after);
				Fm9UploadedBlock finalBlock = final.Find(target.BlockType, target.Instance)!;
				foreach ((int id, int word) in sent)
					if (Word(final.Body, finalBlock.Record!, row, id) != word)
						notes.Add($"Column {id} reads back as {Word(final.Body, finalBlock.Record!, row, id)}, not {word}: the FM9 adjusted it.");
				IReadOnlyDictionary<string, string> afterSettings = final.GetParameters(finalBlock, row);
				Fm9LiveChange[] changes = settings.Keys.Select(asked => Fm9Presets.Canonical(target, current, asked)).Distinct()
					.Select(name => new Fm9LiveChange(name, Fm9Names.Display(target.BlockType, name, current.GetValueOrDefault(name)),
						Fm9Names.Display(target.BlockType, name, afterSettings.GetValueOrDefault(name)))).ToArray();
				Record(presetNumber, presetName, new Fm9LiveStep(DateTimeOffset.Now,
					$"{Fm9Presets.Name(target)} {letter}: " + string.Join(", ", changes.Select(change => $"{change.Setting} {change.Before} -> {change.After}")),
					words, target.EffectId, switchedFrom is null ? null : playing, switchedFrom is null ? null : row, Snapshot: snapshot));
				return new Fm9LiveResult(Fm9Presets.Name(target), letter, switchedFrom, Fm9Names.Model(target.BlockType, current),
					Fm9Names.Model(target.BlockType, afterSettings), changes, notes);
			}, cancellationToken);
		}

		// Switches a block's channel and/or bypass in the current scene.
		public Task<Fm9LiveSwitchResult> SetBlockAsync(Fm9BlockRef block, string? channel, bool? bypassed, CancellationToken cancellationToken)
		{
			if (channel is null && bypassed is null) throw new ArgumentException("Give a channel, a bypass state or both.");
			return _connection.RunAsync(async (transport, token) =>
			{
				(int presetNumber, string presetName) = await CurrentPresetAsync(transport, token);
				Fm9PresetDump buffer = await ReadBufferAsync(transport, token);
				Fm9UploadedBlock target = Fm9Presets.Editable(Fm9UploadedPreset.Read(buffer), block);
				(int scene, int playing, bool off) = await StateAsync(transport, target.EffectId, token);
				List<string> changes = [];
				if (channel is not null)
				{
					int row = Fm9Names.Channel(channel);
					if (row >= Fm9UploadedPreset.ChannelsOf(target)) throw new ArgumentException($"{Fm9Presets.Name(target)} has no channel {Fm9Names.Letter(row)}.");
					if (row != playing)
					{
						await SwitchChannelAsync(transport, target, row, token);
						changes.Add($"channel {Fm9Names.Letter(playing)} -> {Fm9Names.Letter(row)}");
					}
				}
				if (bypassed is bool wanted && wanted != off)
				{
					IReadOnlyList<FractalSysExFrame> reply = await transport.RequestAsync(FractalSysExCodec.CreateBlockBypass(target.EffectId, wanted), token);
					if (!reply.Any(frame => FractalSysExCodec.TryGetBlockBypass(frame.Bytes.Span, out int effect, out bool state) && effect == target.EffectId && state == wanted))
						throw new InvalidOperationException($"The FM9 did not confirm {Fm9Presets.Name(target)}'s bypass.");
					changes.Add(wanted ? "bypassed" : "engaged");
				}
				(int sceneAfter, int channelAfter, bool bypassAfter) = await StateAsync(transport, target.EffectId, token);
				if (changes.Count > 0)
					Record(presetNumber, presetName, new Fm9LiveStep(DateTimeOffset.Now, $"{Fm9Presets.Name(target)} scene {scene}: {string.Join(", ", changes)}", [],
						target.EffectId, channelAfter != playing ? playing : null, channelAfter != playing ? channelAfter : null,
						bypassAfter != off ? off : null, bypassAfter != off ? bypassAfter : null));
				return new Fm9LiveSwitchResult(Fm9Presets.Name(target), sceneAfter, Fm9Names.Letter(channelAfter), bypassAfter,
					changes.Count == 0 ? ["no change: it was already like that"] : changes);
			}, cancellationToken);
		}

		public Task<int> SelectSceneAsync(int scene, CancellationToken cancellationToken)
		{
			if (scene is < 1 or > Fm9Presets.SceneCount) throw new ArgumentOutOfRangeException(nameof(scene), "Scenes are 1 to 8.");
			return _connection.RunAsync(async (transport, token) =>
			{
				await transport.SendAsync(FractalSysExCodec.CreateSceneSelection(scene), token);
				await Wait(TimeSpan.FromMilliseconds(300), token);
				int now = await CurrentSceneAsync(transport, token);
				return now == scene ? now : throw new InvalidOperationException($"The FM9 reports scene {now}, not {scene}.");
			}, cancellationToken);
		}

		// The live edits recorded for the preset showing, newest first, and the named snapshots.
		public Fm9LiveHistoryState History() => _history?.Load() is { } state
			? state with { Steps = Enumerable.Reverse(state.Steps).ToList() }
			: throw new InvalidOperationException("Live history is not kept here.");

		// After a store, an audition or a preset change, the recorded edits no longer describe the edit buffer.
		public void ResetHistory() => _history?.Reset();

		// Undoes the last live changes, newest first: words written back exactly, channel and bypass switched back, and a
		// model change undone by restoring the edit buffer from just before it.
		public Task<IReadOnlyList<string>> UndoAsync(int steps, CancellationToken cancellationToken)
		{
			if (_history is null) throw new InvalidOperationException("Live history is not kept here.");
			if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps), "Undo at least one step.");
			return _connection.RunAsync(async (transport, token) =>
			{
				(int presetNumber, string presetName) = await CurrentPresetAsync(transport, token);
				Fm9LiveHistoryState state = _history.For(presetNumber, presetName);
				if (state.Steps.Count == 0) throw new InvalidOperationException($"There are no live edits to undo on preset {presetNumber} '{presetName}'.");
				List<string> undone = [];
				for (int count = 0; count < steps && state.Steps.Count > 0; count++)
				{
					Fm9LiveStep step = state.Steps[^1];
					state.Steps.RemoveAt(state.Steps.Count - 1);
					if (step.Snapshot is { } file)
					{
						await RestoreBufferAsync(transport, Fm9PresetDump.Parse(File.ReadAllBytes(file)), token);
						undone.Add($"{step.Description} (edit buffer restored from just before it)");
						continue;
					}
					foreach (Fm9LiveWord word in Enumerable.Reverse(step.Words))
						await transport.SendAsync(Frame(word.EffectId, word.Id, Section(word.BlockType)!.Find(word.Id)!, word.Before), token);
					if (step.EffectId is int effect && step.BypassBefore is bool bypass)
						await transport.RequestAsync(FractalSysExCodec.CreateBlockBypass(effect, bypass), token);
					if (step.EffectId is int switched && step.ChannelBefore is int channel)
						await transport.RequestAsync(FractalSysExCodec.CreateBlockChannel(switched, channel), token);
					undone.Add(step.Description);
				}
				await Wait(TimeSpan.FromMilliseconds(300), token);
				_history.Save(state);
				return (IReadOnlyList<string>)undone;
			}, cancellationToken);
		}

		// Keeps the whole edit buffer under a name, to come back to with RestoreSnapshotAsync.
		public Task<Fm9LiveSnapshot> SnapshotAsync(string name, CancellationToken cancellationToken)
		{
			if (_history is null) throw new InvalidOperationException("Live history is not kept here.");
			ArgumentException.ThrowIfNullOrWhiteSpace(name);
			return _connection.RunAsync(async (transport, token) =>
			{
				(int presetNumber, string presetName) = await CurrentPresetAsync(transport, token);
				Fm9PresetDump buffer = await ReadBufferAsync(transport, token);
				string file = _history.NewSnapshotPath($"snapshot {name.Trim()}");
				Fm9Files.WriteAtomically(file, buffer.ToArray());
				Fm9LiveHistoryState state = _history.For(presetNumber, presetName);
				Fm9LiveSnapshot snapshot = new(name.Trim(), presetNumber, presetName, DateTimeOffset.Now, file);
				state.Snapshots.RemoveAll(existing => existing.Name.Equals(snapshot.Name, StringComparison.OrdinalIgnoreCase));
				state.Snapshots.Add(snapshot);
				_history.Save(state);
				return snapshot;
			}, cancellationToken);
		}

		// Puts a named snapshot back into the edit buffer. The buffer it replaces is recorded first, so this can be undone.
		public Task<string> RestoreSnapshotAsync(string name, CancellationToken cancellationToken)
		{
			if (_history is null) throw new InvalidOperationException("Live history is not kept here.");
			return _connection.RunAsync(async (transport, token) =>
			{
				(int presetNumber, string presetName) = await CurrentPresetAsync(transport, token);
				Fm9LiveHistoryState state = _history.For(presetNumber, presetName);
				Fm9LiveSnapshot snapshot = state.Snapshots.FirstOrDefault(candidate => candidate.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
					?? throw new ArgumentException($"No snapshot called '{name}'. Snapshots: {string.Join(", ", state.Snapshots.Select(candidate => candidate.Name))}.");
				Fm9PresetDump current = await ReadBufferAsync(transport, token);
				string before = _history.NewSnapshotPath($"before restoring {snapshot.Name} {DateTime.Now:yyyyMMdd-HHmmss-fff}");
				Fm9Files.WriteAtomically(before, current.ToArray());
				await RestoreBufferAsync(transport, Fm9PresetDump.Parse(File.ReadAllBytes(snapshot.File)), token);
				state.Steps.Add(new Fm9LiveStep(DateTimeOffset.Now, $"restored snapshot '{snapshot.Name}'", [], Snapshot: before));
				_history.Save(state);
				return snapshot.Preset == presetNumber
					? $"Restored '{snapshot.Name}' into the edit buffer of preset {presetNumber}."
					: $"Restored '{snapshot.Name}' (taken on preset {snapshot.Preset} '{snapshot.PresetName}') into the edit buffer of preset {presetNumber}.";
			}, cancellationToken);
		}

		private void Record(int preset, string presetName, Fm9LiveStep step)
		{
			if (_history is null) return;
			Fm9LiveHistoryState state = _history.For(preset, presetName);
			state.Steps.Add(step);
			_history.Save(state);
		}

		private async Task RestoreBufferAsync(IFractalMidiTransport transport, Fm9PresetDump dump, CancellationToken token)
		{
			await new Fm9EditBufferRestoreSender(transport).RestoreAcknowledgedAsync(dump.ForEditBufferImport(), token);
			await Wait(TimeSpan.FromMilliseconds(300), token);
		}

		private static async Task<(int Number, string Name)> CurrentPresetAsync(IFractalMidiTransport transport, CancellationToken token)
		{
			IReadOnlyList<FractalSysExFrame> reply = await transport.RequestAsync(FractalSysExCodec.CreateCurrentPresetNameRequest(), token);
			foreach (FractalSysExFrame frame in reply)
				if (FractalSysExCodec.TryGetCurrentPreset(frame.Bytes.Span, out int number, out string name)) return (number, name);
			throw new InvalidOperationException("The FM9 did not say which preset is showing.");
		}

		private async Task SwitchChannelAsync(IFractalMidiTransport transport, Fm9UploadedBlock target, int row, CancellationToken token)
		{
			IReadOnlyList<FractalSysExFrame> reply = await transport.RequestAsync(FractalSysExCodec.CreateBlockChannel(target.EffectId, row), token);
			if (!reply.Any(frame => FractalSysExCodec.TryGetBlockChannel(frame.Bytes.Span, out int effect, out int channel) && effect == target.EffectId && channel == row))
				throw new InvalidOperationException($"The FM9 did not confirm {Fm9Presets.Name(target)} on channel {Fm9Names.Letter(row)}.");
			await Wait(TimeSpan.FromMilliseconds(300), token);
		}

		private static async Task<(int Scene, int Channel, bool Bypassed)> StateAsync(IFractalMidiTransport transport, int effectId, CancellationToken token)
		{
			int scene = await CurrentSceneAsync(transport, token);
			IReadOnlyList<FractalSysExFrame> channelReply = await transport.RequestAsync(FractalSysExCodec.CreateBlockChannel(effectId, null), token);
			IReadOnlyList<FractalSysExFrame> bypassReply = await transport.RequestAsync(FractalSysExCodec.CreateBlockBypass(effectId, null), token);
			int channel = 0;
			bool bypassed = false;
			bool haveChannel = channelReply.Any(frame => FractalSysExCodec.TryGetBlockChannel(frame.Bytes.Span, out int effect, out channel) && effect == effectId);
			bool haveBypass = bypassReply.Any(frame => FractalSysExCodec.TryGetBlockBypass(frame.Bytes.Span, out int effect, out bypassed) && effect == effectId);
			if (!haveChannel || !haveBypass) throw new InvalidOperationException("The FM9 did not report the block's channel and bypass.");
			return (scene, channel, bypassed);
		}

		private static async Task<int> CurrentSceneAsync(IFractalMidiTransport transport, CancellationToken token)
		{
			IReadOnlyList<FractalSysExFrame> reply = await transport.RequestAsync(FractalSysExCodec.CreateCurrentSceneRequest(), token);
			foreach (FractalSysExFrame frame in reply)
				if (FractalSysExCodec.TryGetCurrentScene(frame.Bytes.Span, out int scene)) return scene;
			throw new InvalidOperationException("The FM9 did not say which scene is playing.");
		}

		// Retries back off with Wait, so tests that replace it don't sleep.
		private async Task<Fm9PresetDump> ReadBufferAsync(IFractalMidiTransport transport, CancellationToken token) =>
			(await new Fm9StoredPresetReader(transport, delayAsync: Wait).ReadAsync(EditBuffer, token)).Dump;

		// Continuous settings travel normalized (word / 65534); choices and whole numbers as their value, signed ones as such.
		private static FractalSysExFrame Frame(int effectId, int id, Fm9EffectParameter parameter, int word) => parameter.Kind switch
		{
			Fm9ParameterKind.Linear or Fm9ParameterKind.Logarithmic => FractalSysExCodec.CreateContinuousParameterChange(effectId, id, word / 65534f),
			Fm9ParameterKind.Choice or Fm9ParameterKind.Integer when parameter.Minimum < 0 =>
				FractalSysExCodec.CreateSignedDiscreteParameterChange(effectId, id, word > short.MaxValue ? word - 65536 : word),
			Fm9ParameterKind.Choice or Fm9ParameterKind.Integer => FractalSysExCodec.CreateDiscreteParameterChange(effectId, id, word),
			_ => throw new NotSupportedException($"Setting {id} is managed by the FM9 itself and cannot be set live.")
		};

		private static Fm9EffectSection? Section(string blockType) =>
			Fm9HardwareBlockInstanceCatalog.Bundled.Instances.FirstOrDefault(instance => instance.BlockType == blockType)?.ParameterFamily is string family
				? Fm9EffectDefinitions.Bundled.ForFamily(family) : null;

		// The record column of a block's model or type selector.
		private static int TypeColumn(string blockType, string setting) =>
			blockType == "Amp" && setting == "Amp Model" ? 10
			: Fm9BlockSettings.Find(blockType, setting)?.Id
				?? Fm9BlockRecordTemplateProfile.FindFamilyDefinition(blockType)?.Parameters.First(parameter => parameter.Name == setting).ParameterId
				?? throw new NotSupportedException($"{blockType} {setting} cannot be set live.");

		private static int Word(Fm9PresetBody body, Fm9PresetBlockRecord record, int row, int id) =>
			BitConverter.ToUInt16(body.Bytes.Span[Fm9ChannelParameterStorage.GetOffset(record, row, id)..]);
	}
}
