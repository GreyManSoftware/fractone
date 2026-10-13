using System.Globalization;
using System.Text;
using Fractal.Application;
using Fractal.Companion.Catalog;

namespace Fractal.Companion.Protocol
{
	public sealed record Fm9UploadedBlock(int EffectId, string BlockType, int Instance, int Row, int Column,
		Fm9PresetBlockRecord? Record, bool IsEditable);

	// Any FM9 firmware-12 preset, read block by block. Every block record carries its effect id six words before its
	// header, which ties it to its grid cell exactly (two Amps share a record shape, so shape alone cannot); this held for
	// all 256 presets of a full FM9 backup. Every block whose record has its firmware-12 shape is editable: the Ride
	// template's curated settings plus every other setting in FM9-Edit's parameter dictionary (Fm9BlockSettings). Edits
	// are written as differences from the upload, so a setting nobody changed is never rewritten.
	public sealed class Fm9UploadedPreset
	{
		public const int SceneCount = 8;
		// The FM9 firmware-12 preset format (as the export template). Presets last saved by earlier firmware use older format
		// numbers but the same record layout wherever a block's record has the firmware-12 shape, so those blocks stay
		// editable; a block whose record shape differs is shown and kept as is.
		public const ushort SupportedFormatVersion = 0x0146;
		public const ushort OldestFormatVersion = 0x013F;
		private const int EffectIdBytesBeforeRecord = 12;
		private const int FirstBlockEffectId = 37;
		private const int FirstShuntEffectId = 1024;
		private const int SceneNameOffset = 4;
		private const int SceneNameLength = 32;
		private static readonly string[] OutputEqBands =
			["Output EQ 80 Hz", "Output EQ 240 Hz", "Output EQ 750 Hz", "Output EQ 2200 Hz", "Output EQ 6600 Hz"];

		private readonly Fm9BlockRecordTemplateProfile _profile;

		private Fm9UploadedPreset(Fm9PresetDump dump, Fm9PresetBody body, IReadOnlyList<Fm9UploadedBlock> blocks)
		{
			Dump = dump;
			Body = body;
			Blocks = blocks;
			_profile = Fm9BlockRecordTemplateProfile.Create(body, blocks
				.Where(block => block.IsEditable && Fm9BlockRecordTemplateProfile.FindFamilyDefinition(block.BlockType) is not null)
				.Select(block => Fm9BlockRecordTemplateProfile.FindFamilyDefinition(block.BlockType)! with
				{
					Instance = block.Instance,
					EffectId = checked((ushort)block.EffectId),
					RecordOffset = block.Record!.Offset
				}).ToArray());
		}

		public Fm9PresetDump Dump { get; }
		public Fm9PresetBody Body { get; }
		public IReadOnlyList<Fm9UploadedBlock> Blocks { get; }
		public string Name => Dump.PresetName.Trim();

		public static Fm9UploadedPreset Read(ReadOnlySpan<byte> bytes) => Read(Fm9PresetDump.Parse(bytes));

		public static Fm9UploadedPreset Read(Fm9PresetDump dump)
		{
			ArgumentNullException.ThrowIfNull(dump);
			if (dump.FormatVersion is < OldestFormatVersion or > SupportedFormatVersion)
				throw new NotSupportedException(
					$"This preset uses FM9 preset format 0x{dump.FormatVersion:X4}, which FracAI cannot read yet.");
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump);
			Dictionary<int, Fm9PresetBlockRecord?> records = [];
			foreach (Fm9PresetBlockRecord record in body.BlockRecords.Where(record => record.Offset >= EffectIdBytesBeforeRecord))
			{
				int effect = BitConverter.ToUInt16(body.Bytes.Span[(record.Offset - EffectIdBytesBeforeRecord)..]);
				if (effect is < FirstBlockEffectId or >= FirstShuntEffectId) continue;
				// Two records claiming one block make it unaddressable; it is then shown but not edited.
				records[effect] = records.ContainsKey(effect) ? null : record;
			}
			List<Fm9UploadedBlock> blocks = [];
			foreach (Fm9PresetGridCell cell in body.GridCells
				.Where(cell => cell.EffectId is int effect && effect is >= FirstBlockEffectId and < FirstShuntEffectId)
				.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column))
			{
				int effect = cell.EffectId!.Value;
				(string type, int instance) = Fm9BlockSettings.Identify(effect) ?? ($"Block {effect}", 0);
				Fm9PresetBlockRecord? record = records.GetValueOrDefault(effect);
				// Older firmware wrote some records narrower: later firmware only appended columns (every choice in the shared
				// columns of a 256-preset backup held a valid ordinal), so a narrower record is edited in the columns it has,
				// provided it holds every setting FracAI's curated code addresses.
				bool editable = record is not null && Fm9BlockSettings.HasFamily(type) &&
					Fm9BlockSettings.RecordShape(type) is { } shape &&
					record.ParameterColumns <= shape.Columns && record.ParameterColumns > CuratedMaximumId(type) &&
					record.ParameterRows == shape.Rows &&
					record.Length == (23 + (record.ParameterColumns * record.ParameterRows)) * sizeof(ushort) &&
					body.GridCells.Count(other => other.EffectId == effect) == 1;
				blocks.Add(new(effect, type, instance, cell.Row, cell.Column, record, editable));
			}
			return new(dump, body, blocks);
		}

		// The highest column the curated code reads for a block type (the Ride template's settings, Amp model/output EQ,
		// Cab bank/type/cuts); a narrower record cannot be edited safely.
		private static int CuratedMaximumId(string blockType) =>
			Math.Max(Fm9BlockRecordTemplateProfile.FindFamilyDefinition(blockType)?.Parameters.Select(parameter => parameter.ParameterId)
				.DefaultIfEmpty(-1).Max() ?? -1, blockType switch { "Amp" => 132, "Cab" => 66, _ => -1 });

		// A record's rows are its channels: the FM9's status dump reports Send/Return as one channel, Megatap and Ring
		// Modulator as two (A/B, which scenes select) and the Multiplexer as six (2026-10-04). The designer edits up to A-D.
		public static int ChannelsOf(Fm9UploadedBlock block) =>
			Math.Clamp((int)(block.Record?.ParameterRows ?? 1), 1, Fm9ChannelParameterStorage.ChannelCount);

		// Scene states of a block, when every scene plays a channel the designer can edit (a Multiplexer scene on E or F,
		// for instance, leaves that block's scene states as uploaded).
		private IReadOnlyList<Fm9PresetBlockSceneState>? SceneStates(Fm9UploadedBlock block)
		{
			try
			{
				Fm9PresetBlockSceneState[] states = Enumerable.Range(1, SceneCount).Select(scene => GetSceneState(block, scene)).ToArray();
				return states.All(state => (int)state.Channel < ChannelsOf(block)) ? states : null;
			}
			catch (InvalidDataException)
			{
				return null;
			}
		}

		// Every setting FracAI edits on one channel of an editable block, as proposal parameters (invariant text).
		public IReadOnlyDictionary<string, string> GetParameters(Fm9UploadedBlock block, int channel) =>
			ReadParameters(Body, block, channel);

		private IReadOnlyDictionary<string, string> ReadParameters(Fm9PresetBody body, Fm9UploadedBlock block, int channel)
		{
			EnsureEditable(block);
			if (channel < 0 || channel >= ChannelsOf(block))
				throw new NotSupportedException($"{block.BlockType} {block.Instance} has only channel A.");
			Dictionary<string, string> parameters = new(StringComparer.Ordinal);
			if (block.BlockType == "Cab")
			{
				Fm9CabSettings cab = Fm9PresetBodyParameterEditor.GetCabSettings(body, block.Record!, channel);
				parameters["Bank"] = Integer(cab.Bank);
				parameters["Cab Type"] = Integer(cab.TypeOrdinal);
				parameters["Low Cut"] = Number(cab.LowCut);
				parameters["High Cut"] = Number(cab.HighCut);
			}
			if (block.BlockType == "Amp")
			{
				parameters["Amp Model"] = Integer(Fm9PresetBodyParameterEditor.GetAmpModel(body, block.Record!, channel));
				Fm9AmpOutputEqSettings eq = Fm9PresetBodyParameterEditor.GetAmpOutputEq(body, block.Record!, channel);
				parameters["Output EQ Enabled"] = eq.Enabled ? "1" : "0";
				// The five Mark bands only describe a Mark 5-band EQ; other output EQ types keep their own bands untouched.
				if (eq.Type == Fm9AmpOutputEqType.Mark5Band)
				{
					double[] bands = [eq.Band80Hz, eq.Band240Hz, eq.Band750Hz, eq.Band2200Hz, eq.Band6600Hz];
					for (int index = 0; index < bands.Length; index++)
						parameters[OutputEqBands[index]] = Number(bands[index]);
					parameters["Output EQ Location"] = eq.Location.ToString();
					// Generated amps state the type too; reading it back lets a later export stage compare instead of refuse.
					parameters["Output EQ Type"] = nameof(Fm9AmpOutputEqType.Mark5Band);
				}
			}
			foreach (Fm9TemplateParameterDefinition parameter in Fm9BlockRecordTemplateProfile.FindFamilyDefinition(block.BlockType)?.Parameters ?? [])
			{
				double value = Fm9PresetBodyParameterEditor.GetProfileParameter(body, _profile, block.BlockType, block.Instance, parameter.Name, channel);
				parameters[parameter.Name] = parameter.IsDiscrete ? Integer((int)Math.Round(value)) : Number(value);
			}
			foreach (Fm9BlockSetting setting in Fm9BlockSettings.For(block.BlockType).Where(setting => setting.Id < block.Record!.ParameterColumns))
			{
				if (setting.Read(ReadWord(body, block.Record!, channel, setting.Id)) is string value)
					parameters[setting.Name] = value;
			}
			return parameters;
		}

		private static int ReadWord(Fm9PresetBody body, Fm9PresetBlockRecord record, int channel, int id) =>
			BitConverter.ToUInt16(body.Bytes.Span[Fm9ChannelParameterStorage.GetOffset(record, channel, id)..]);

		public Fm9PresetBlockSceneState GetSceneState(Fm9UploadedBlock block, int sceneNumber)
		{
			EnsureEditable(block);
			return Fm9PresetBodySceneAuthor.GetSceneState(Body, block.Record!, sceneNumber);
		}

		public string GetSceneName(int sceneNumber)
		{
			if (sceneNumber is < 1 or > SceneCount) throw new ArgumentOutOfRangeException(nameof(sceneNumber));
			ReadOnlySpan<byte> name = Body.Bytes.Span.Slice(SceneNameOffset + ((sceneNumber - 1) * SceneNameLength), SceneNameLength);
			int end = name.IndexOf((byte)0);
			return Encoding.ASCII.GetString(end < 0 ? name : name[..end]).Trim();
		}

		// The preset as a proposal the designer can edit. Channels B-D appear when a scene plays them or they differ from A.
		public ToneDesignProposal ToProposal()
		{
			Fm9UploadedBlock[] editable = Blocks.Where(block => block.IsEditable).ToArray();
			Dictionary<Fm9UploadedBlock, IReadOnlyList<Fm9PresetBlockSceneState>> sceneStates = editable
				.Select(block => (block, states: SceneStates(block))).Where(entry => entry.states is not null)
				.ToDictionary(entry => entry.block, entry => entry.states!);
			List<ToneDesignSceneProposal> scenes = [];
			for (int scene = 1; scene <= SceneCount; scene++)
			{
				scenes.Add(new ToneDesignSceneProposal(SceneLabel(scene), string.Empty,
					sceneStates.Select(entry =>
					{
						Fm9PresetBlockSceneState state = entry.Value[scene - 1];
						return new ToneDesignSceneBlockSetting(entry.Key.BlockType, entry.Key.Instance, ((char)('A' + (int)state.Channel)).ToString(), state.Bypassed);
					}).ToArray()));
			}
			List<ToneDesignBlockProposal> proposalBlocks = [];
			foreach (Fm9UploadedBlock block in editable)
			{
				IReadOnlyDictionary<string, string> channelA = GetParameters(block, 0);
				List<ToneDesignBlockChannel> channels = [];
				for (int channel = 1; channel < ChannelsOf(block); channel++)
				{
					string letter = ((char)('A' + channel)).ToString();
					IReadOnlyDictionary<string, string> values = GetParameters(block, channel);
					bool played = scenes.Any(scene => ToneDesignSceneChannels.Find(scene, block.BlockType, block.Instance)?.Channel == letter);
					if (played || values.Any(pair => channelA[pair.Key] != pair.Value))
						channels.Add(new ToneDesignBlockChannel(letter, values));
				}
				proposalBlocks.Add(new ToneDesignBlockProposal(block.BlockType, block.Instance, block.Row, block.Column, channelA)
				{
					Rationale = "From your uploaded preset.",
					Channels = channels,
					MaximumChannels = ChannelsOf(block)
				});
			}
			Fm9UploadedBlock[] kept = Blocks.Where(block => !block.IsEditable).ToArray();
			string[] keptNames = kept.Where(block => block.BlockType is not ("Input" or "Output"))
				.Select(block => block.Instance > 0 ? $"{block.BlockType} {block.Instance}" : block.BlockType).ToArray();
			return new ToneDesignProposal($"Uploaded preset \"{Name}\".", proposalBlocks,
				keptNames.Length == 0 ? [] : [$"{string.Join(", ", keptNames)} {(keptNames.Length == 1 ? "is" : "are")} kept exactly as uploaded; edit {(keptNames.Length == 1 ? "it" : "them")} on the FM9."])
			{
				Scenes = scenes,
				PresetName = Fm9PresetName.Normalize(Name),
				Modifiers = Fm9PresetModifiers.Read(Body).Select(entry => entry.Modifier).ToArray(),
				BasePreset = Dump.ToArray(),
				PreservedBlocks = kept.Select(block => new ToneDesignPreservedBlock(block.BlockType, block.Instance, block.Row, block.Column)).ToArray(),
				Shunts = Body.GridCells.Where(cell => cell.EffectId is >= FirstShuntEffectId)
					.Select(cell => new ToneDesignGridCell(cell.Row, cell.Column)).OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray(),
				InputBlockInstance = FirstInstance("Input"),
				OutputBlockInstance = FirstInstance("Output")
			};
		}

		// Brings a proposal saved from an upload up to what the reader understands now: blocks once kept as uploaded become
		// editable and blocks gain every setting since added. Saved values win; anything new is read from the upload itself,
		// which is what an unedited setting plays, so the preset sounds exactly as before.
		public static ToneDesignProposal Upgrade(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			if (proposal.BasePreset is null) return proposal;
			ToneDesignProposal fresh;
			try { fresh = Read(proposal.BasePreset).ToProposal(); }
			catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or InvalidOperationException)
			{
				return proposal;
			}
			bool Removed(string blockType, int instance) =>
				proposal.RemovedBlocks.Any(removed => SameBlock(removed.BlockType, removed.Instance, blockType, instance));
			ToneDesignBlockProposal[] blocks = fresh.Blocks.Where(current => !Removed(current.BlockType, current.Instance)).Select(current =>
			{
				ToneDesignBlockProposal? saved = proposal.Blocks.FirstOrDefault(block => block.BlockType == current.BlockType && block.Instance == current.Instance);
				if (saved is null) return current;
				ToneDesignBlockProposal merged = saved with
				{
					Parameters = Merge(current.Parameters, saved.Parameters),
					MaximumChannels = current.MaximumChannels,
					Channels = []
				};
				foreach (string letter in saved.GetAuthoredChannels().Union(current.GetAuthoredChannels()).Where(letter => letter != ToneDesignChannels.A))
				{
					if (letter[0] - 'A' >= current.MaximumChannels) continue;
					IReadOnlyDictionary<string, string> savedChannel = saved.HasChannel(letter) ? saved.GetParameters(letter) : new Dictionary<string, string>();
					merged = merged.WithChannel(letter, Merge(current.GetParameters(letter), savedChannel), saved.GetModelSelection(letter));
				}
				return merged;
			}).Concat(proposal.Blocks.Where(saved => fresh.Blocks.All(current => !SameBlock(current.BlockType, current.Instance, saved.BlockType, saved.Instance))))
			.ToArray();
			ToneDesignSceneProposal[] scenes = proposal.Scenes.Select((scene, index) => index >= fresh.Scenes.Count ? scene : scene with
			{
				BlockSettings = [.. scene.BlockSettings.Where(setting => blocks.Any(block => block.BlockType == setting.BlockType && block.Instance == setting.Instance)),
					.. fresh.Scenes[index].BlockSettings.Where(setting => !Removed(setting.BlockType, setting.Instance) && !scene.BlockSettings.Any(saved =>
						saved.BlockType == setting.BlockType && saved.Instance == setting.Instance))]
			}).ToArray();
			return proposal with
			{
				Blocks = blocks,
				Scenes = scenes,
				Modifiers = proposal.Modifiers ?? fresh.Modifiers,
				PreservedBlocks = fresh.PreservedBlocks.Where(kept => !Removed(kept.BlockType, kept.Instance)).ToArray(),
				// Removed blocks leave shunts; added blocks fill theirs.
				Shunts = fresh.Shunts.Concat(proposal.RemovedBlocks.Select(removed => new ToneDesignGridCell(removed.Row, removed.Column)))
					.Where(cell => !blocks.Any(block => block.Row == cell.Row && block.Column == cell.Column)).Distinct().ToArray()
			};
		}

		private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> saved)
		{
			Dictionary<string, string> merged = new(current, StringComparer.Ordinal);
			foreach (KeyValuePair<string, string> pair in saved) merged[pair.Key] = pair.Value;
			return merged;
		}

		// Writes a proposal over its uploaded preset: only settings that differ from the upload change.
		public static Fm9PresetDump Apply(ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			if (proposal.BasePreset is null)
				throw new InvalidOperationException("The proposal is not based on an uploaded preset.");
			Fm9UploadedPreset upload = Read(proposal.BasePreset);
			if (proposal.InputBlockInstance != upload.FirstInstance("Input") || proposal.OutputBlockInstance != upload.FirstInstance("Output"))
				throw new NotSupportedException("Change the routing of an uploaded preset on the FM9.");
			if (proposal.Scenes.Count > SceneCount)
				throw new InvalidDataException($"An FM9 preset has {SceneCount} scenes.");
			Fm9PresetBody body = upload.Body;
			// Blocks put in a shunt's cell and blocks taken out are applied after every edit of the existing blocks, then the
			// new blocks' settings and scene states are written in a second pass over the result.
			ToneDesignBlockProposal[] added = proposal.Blocks.Where(block => upload.Find(block.BlockType, block.Instance) is null).ToArray();
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Except(added))
			{
				Fm9UploadedBlock target = upload.Find(block.BlockType, block.Instance)!;
				if (proposal.RemovedBlocks.Any(removed => SameBlock(removed.BlockType, removed.Instance, block.BlockType, block.Instance)))
					throw new InvalidDataException($"{block.BlockType} {block.Instance} cannot be both kept and removed.");
				if (!target.IsEditable)
					throw new NotSupportedException($"{block.BlockType} {block.Instance} cannot be edited here.");
				if (target.Row != block.Row || target.Column != block.Column)
					throw new NotSupportedException("Move blocks of an uploaded preset on the FM9.");
				foreach (string letter in block.GetAuthoredChannels())
				{
					int channel = letter[0] - 'A';
					if (channel >= ChannelsOf(target))
						throw new NotSupportedException($"{block.BlockType} {block.Instance} has only channel A.");
					IReadOnlyDictionary<string, string> before = upload.GetParameters(target, channel);
					IReadOnlyDictionary<string, string> after = block.GetParameters(letter);
					// A new model or type loads its voicing first, so settings are compared with the voiced channel: a value
					// that matches the old model's but not the new one's must still be written, or the voicing replaces it.
					if (Fm9ModelDefaults.WithVoicing(upload.Body.Bytes.Span, target.Record!, channel, target.BlockType, after) is byte[] voiced)
						before = upload.ReadParameters(upload.Body with { Bytes = voiced }, target, channel);
					KeyValuePair<string, string>[] changed = after.Where(pair =>
					{
						if (before.TryGetValue(pair.Key, out string? original))
							return !SameValue(original, pair.Value);
						// A dictionary setting whose stored word was not a readable value (e.g. a channel added as a copy of A,
						// over a word older firmware stored in another form) is simply written.
						// So is an Amp output EQ setting on a channel whose EQ is not yet the Mark 5-band (a channel the designer
						// or a generated preset fills in): writing the type and bands makes it one.
						return Fm9BlockSettings.Find(block.BlockType, pair.Key) is not null ||
							(block.BlockType == "Amp" && pair.Key.StartsWith("Output EQ ", StringComparison.Ordinal))
							? true
							: throw new NotSupportedException($"{block.BlockType} {pair.Key} cannot be edited on this preset.");
					}).ToArray();
					if (changed.Length > 0)
						body = upload.WithChanges(body, target, channel, changed);
				}
			}
			for (int index = 0; index < proposal.Scenes.Count; index++)
			{
				int scene = index + 1;
				ToneDesignSceneProposal proposed = proposal.Scenes[index];
				if (proposed.Name != upload.SceneLabel(scene))
					body = body with { Bytes = Fm9PresetBodySceneAuthor.WithSceneName(body, scene, proposed.Name) };
				foreach (ToneDesignSceneBlockSetting setting in proposed.BlockSettings.Where(setting =>
					!added.Any(block => SameBlock(block.BlockType, block.Instance, setting.BlockType, setting.Instance))))
				{
					Fm9UploadedBlock target = upload.Find(setting.BlockType, setting.Instance) is { IsEditable: true } found ? found
						: throw new NotSupportedException($"Scene {scene} names {setting.BlockType} {setting.Instance}, which cannot be edited here.");
					if (!ToneDesignChannels.IsSupported(setting.Channel))
						throw new InvalidDataException($"Scene {scene} must play channel A, B, C or D.");
					Fm9PresetBlockSceneState current = Fm9PresetBodySceneAuthor.GetSceneState(body, target.Record!, scene);
					Fm9SceneChannel channel = (Fm9SceneChannel)(setting.Channel[0] - 'A');
					if (current.Channel != channel && (int)channel >= ChannelsOf(target))
						throw new NotSupportedException($"{setting.BlockType} {setting.Instance} has only channel A.");
					if (current.Channel != channel)
						body = body with { Bytes = Fm9PresetBodySceneAuthor.WithSceneChannel(body, target.Record!, scene, channel) };
					if (current.Bypassed != setting.IsBypassed)
						body = body with { Bytes = Fm9PresetBodySceneAuthor.WithSceneBypass(body, target.Record!, scene, setting.IsBypassed) };
				}
			}
			// Modifier slots change only when the set of modifiers does (a reordered list writes nothing).
			if (proposal.Modifiers is { } modifiers)
			{
				ToneDesignModifier[] current = Fm9PresetModifiers.Read(body).Select(entry => entry.Modifier).ToArray();
				if (current.Length != modifiers.Count || current.Except(modifiers).Any())
					body = body with { Bytes = Fm9PresetModifiers.Write(body, modifiers) };
			}
			Fm9PresetDump edited = Fm9PresetBodyEncoder.Reencode(upload.Dump, body.Bytes.ToArray());
			if (Fm9PresetName.Normalize(proposal.PresetName) is { } name && name != upload.Name)
				edited = edited.WithPresetName(name);
			_ = Fm9PresetBodyDecoder.Decode(Fm9PresetDump.Parse(edited.ToArray()));
			if (added.Length == 0 && proposal.RemovedBlocks.Count == 0)
				return edited;
			foreach (ToneDesignPreservedBlock removed in proposal.RemovedBlocks)
			{
				Fm9UploadedBlock target = upload.Find(removed.BlockType, removed.Instance)
					?? throw new InvalidDataException($"{removed.BlockType} {removed.Instance} is not in the uploaded preset.");
				if (target.BlockType is "Input" or "Output")
					throw new NotSupportedException("Inputs and outputs carry the routing; change them on the FM9.");
				edited = Fm9PresetBodyEncoder.Reencode(edited, Fm9PresetBodyBlockInserter.WithoutBlock(Fm9PresetBodyDecoder.Decode(edited), target.EffectId));
			}
			foreach (ToneDesignBlockProposal block in added)
			{
				Fm9PresetBody current = Fm9PresetBodyDecoder.Decode(edited);
				if (current.GridCells.SingleOrDefault(cell => cell.Row == block.Row && cell.Column == block.Column)?.EffectId is not >= FirstShuntEffectId)
					throw new NotSupportedException($"Put {block.BlockType} {block.Instance} in a shunt's cell; other changes to the layout are made on the FM9.");
				edited = Fm9PresetBodyEncoder.Reencode(edited, Fm9PresetBodyBlockInserter.WithBlockInShunt(
					current, block.BlockType, block.Instance, new GridPosition(block.Row, block.Column)));
			}
			return Apply(proposal with { BasePreset = edited.ToArray(), RemovedBlocks = [] });
		}

		private static bool SameBlock(string firstType, int firstInstance, string secondType, int secondInstance) =>
			firstInstance == secondInstance && string.Equals(firstType, secondType, StringComparison.OrdinalIgnoreCase);

		private Fm9PresetBody WithChanges(Fm9PresetBody body, Fm9UploadedBlock block, int channel, IReadOnlyList<KeyValuePair<string, string>> changes)
		{
			// A new model or type brings the voicing the FM9 loads with it; the changes are written over it.
			if (Fm9ModelDefaults.WithVoicing(body.Bytes.Span, block.Record!, channel, block.BlockType, changes) is byte[] voiced)
				body = body with { Bytes = voiced };
			// Dictionary settings are single words; the curated ones keep their own (model, output EQ, cab) handling.
			KeyValuePair<string, string>[] direct = changes.Where(change => Fm9BlockSettings.Find(block.BlockType, change.Key) is not null).ToArray();
			if (direct.Length > 0)
			{
				byte[] bytes = body.Bytes.ToArray();
				foreach (KeyValuePair<string, string> change in direct)
				{
					Fm9BlockSetting setting = Fm9BlockSettings.Find(block.BlockType, change.Key)!;
					BitConverter.TryWriteBytes(bytes.AsSpan(Fm9ChannelParameterStorage.GetOffset(block.Record!, channel, setting.Id)),
						checked((ushort)setting.Write(change.Value)));
				}
				body = body with { Bytes = bytes };
				changes = changes.Except(direct).ToArray();
				if (changes.Count == 0) return body;
			}
			if (block.BlockType == "Cab")
			{
				Fm9CabSettings cab = Fm9PresetBodyParameterEditor.GetCabSettings(body, block.Record!, channel);
				foreach (KeyValuePair<string, string> change in changes)
					cab = Fm9ProposalPresetExporter.ApplyCabParameter(cab, change);
				Fm9PresetBodyParameterEditor.ValidateCabSettings(cab);
				return body with { Bytes = Fm9PresetBodyParameterEditor.WithCabChannelSettings(body, block.Record!,
					new Dictionary<int, Fm9CabSettings> { [channel] = cab }) };
			}
			int? model = null;
			Fm9AmpOutputEqSettings? eq = null;
			foreach (KeyValuePair<string, string> change in changes)
			{
				if (block.BlockType == "Amp" && change.Key == "Amp Model")
				{
					model = Fm9ProposalPresetExporter.ParseAmpModel(change);
				}
				else if (block.BlockType == "Amp" && change.Key.StartsWith("Output EQ ", StringComparison.Ordinal))
				{
					eq = Fm9ProposalPresetExporter.ApplyAmpOutputEqParameter(
						eq ?? Fm9PresetBodyParameterEditor.GetAmpOutputEq(body, block.Record!, channel), change);
				}
				else
				{
					if (!double.TryParse(change.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
						throw new InvalidDataException($"{block.BlockType} {change.Key} must be a number.");
					body = body with { Bytes = Fm9PresetBodyParameterEditor.WithProfileParameter(
						body, _profile, block.BlockType, block.Instance, change.Key, value, channel) };
				}
			}
			if (model is not null || eq is not null)
				body = body with { Bytes = Fm9PresetBodyParameterEditor.WithAmpChannelSettings(body, block.Record!,
					[new Fm9AmpChannelSettings(channel, model, eq)]) };
			return body;
		}

		public Fm9UploadedBlock? Find(string blockType, int instance) => Blocks.FirstOrDefault(block =>
			string.Equals(block.BlockType, blockType, StringComparison.OrdinalIgnoreCase) && block.Instance == instance);

		// A blank scene name shows as "Scene N" (and is written back only if changed).
		private string SceneLabel(int scene) => GetSceneName(scene) is { Length: > 0 } name ? name : $"Scene {scene}";

		private int FirstInstance(string blockType) =>
			Blocks.FirstOrDefault(block => block.BlockType == blockType && block.Instance > 0)?.Instance ?? 1;

		private static void EnsureEditable(Fm9UploadedBlock block)
		{
			ArgumentNullException.ThrowIfNull(block);
			if (!block.IsEditable) throw new NotSupportedException($"{block.BlockType} cannot be edited here.");
		}

		private static bool SameValue(string first, string second) =>
			string.Equals(first, second, StringComparison.Ordinal) ||
			(double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
			 double.TryParse(second, NumberStyles.Float, CultureInfo.InvariantCulture, out double right) && left == right);

		private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);

		// About the FM9's own display precision: the device stores 16-bit steps, so 90 Hz reads back as 90.001 and 7 as 6.997.
		// Display rounding is safe because a value nobody changed is never written back.
		private static string Number(double value)
		{
			double magnitude = Math.Abs(value);
			int decimals = magnitude < 1 ? 3 : magnitude < 100 ? 2 : magnitude < 1_000 ? 1 : 0;
			string text = Math.Round(value, decimals).ToString("0.###", CultureInfo.InvariantCulture);
			return text == "-0" ? "0" : text;
		}
	}
}
