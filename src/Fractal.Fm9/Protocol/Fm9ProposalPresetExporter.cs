using Fractal.Application;
using Fractal.Companion.Catalog;

namespace Fractal.Companion.Protocol
{
	public sealed record Fm9ProposalPresetExportRequest(
		string ProposalPath,
		string TemplatePath,
		string OutputPath,
		string PresetName);

	public static class Fm9ProposalPresetExporter
	{
		private sealed record RideTemplateBlock(string BlockType, int Instance, ushort EffectId);

		private static readonly IReadOnlyList<RideTemplateBlock> RideTemplateBlocks =
		[
			new("Input", 1, 37),
			new("Gate/Expander", 1, 146),
			new("Drive", 1, 118),
			new("Amp", 1, 58),
			new("Cab", 1, 62),
			new("Parametric EQ", 1, 54),
			new("Reverb", 1, 66),
			new("Output", 1, 42)
		];

		public static void Export(Fm9ProposalPresetExportRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);
			ValidatePaths(request);

			ToneDesignProposal proposal = ToneDesignProposalParser.Parse(
				File.ReadAllText(request.ProposalPath));
			Fm9PresetDump template = Fm9PresetDump.Parse(File.ReadAllBytes(request.TemplatePath));
			Fm9PresetDump exported = Apply(template, proposal, request.PresetName);
			WriteAtomically(request.OutputPath, exported.ToArray());
		}

		public static Fm9PresetDump Apply(
			Fm9PresetDump template,
			ToneDesignProposal proposal,
			string presetName)
		{
			ArgumentNullException.ThrowIfNull(template);
			ArgumentNullException.ThrowIfNull(proposal);
			// An uploaded preset is edited in place instead of rebuilt on the template.
			if (proposal.BasePreset is not null)
				return Fm9UploadedPreset.Apply(proposal);
			proposal = proposal with { Blocks = proposal.Blocks.Select(Fm9QualifiedModelCatalog.Normalize).ToArray() };

			if (string.IsNullOrWhiteSpace(presetName))
			{
				throw new ArgumentException("An FM9 preset name is required.", nameof(presetName));
			}

			// A changed Amp or Drive model gets the voicing the FM9 loads with it before any setting is written.
			template = Catalog.Fm9ModelDefaults.Prepare(template, proposal);
			(ToneDesignProposal templatePart, bool extended) = SplitForTemplate(proposal);
			Fm9PresetDump built = ApplyTemplate(template, templatePart, presetName);
			return extended ? Extend(built, proposal, templatePart) : built;
		}

		// Blocks beyond the Ride template, and settings beyond the template's curated ones, are added in a second stage: the
		// template part exports as it always has, then the other blocks are inserted as the FM9 inserts them
		// (Fm9PresetBodyBlockInserter), cabled from the proposal's routes, and every remaining setting, channel and scene
		// state is written by the upload writer as differences from that preset.
		private static (ToneDesignProposal TemplatePart, bool Extended) SplitForTemplate(ToneDesignProposal proposal)
		{
			ToneDesignBlockProposal[] extras = proposal.Blocks.Where(block => !IsTemplateBlock(block)).ToArray();
			ToneDesignBlockProposal[] templateBlocks = proposal.Blocks.Where(IsTemplateBlock).Select(WithoutDictionarySettings).ToArray();
			bool stripped = templateBlocks.Zip(proposal.Blocks.Where(IsTemplateBlock)).Any(pair =>
				pair.First.GetAuthoredChannels().Any(channel => pair.First.GetParameters(channel).Count != pair.Second.GetParameters(channel).Count));
			HashSet<(int Row, int Column)> extraCells = extras.Select(block => (block.Row, block.Column)).ToHashSet();
			ToneDesignRoute[] routes = proposal.Routes.Where(route => !extraCells.Contains((route.SourceRow, route.SourceColumn)) &&
				!extraCells.Contains((route.DestinationRow, route.SourceColumn + 1))).ToArray();
			ToneDesignSceneProposal[] scenes = proposal.Scenes.Select(scene => scene with
			{
				BlockSettings = scene.BlockSettings.Where(setting => !extras.Any(block =>
					string.Equals(block.BlockType, setting.BlockType, StringComparison.OrdinalIgnoreCase) && block.Instance == setting.Instance)).ToArray()
			}).ToArray();
			// Modifiers are written by the upload writer in the second stage.
			return (proposal with { Blocks = templateBlocks, Routes = routes, Scenes = scenes, Modifiers = null },
				extras.Length > 0 || stripped || proposal.Modifiers is { Count: > 0 });
		}

		// A generated proposal names only the effect settings the model chose; the rest play the FM9's defaults. This adds
		// those defaults (read back from the built preset) so the designer shows and edits every setting of each effect. The
		// proposal's own values win; a proposal that does not build is returned unchanged for export to report.
		public static ToneDesignProposal CompleteEffects(Fm9PresetDump template, ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(template);
			ArgumentNullException.ThrowIfNull(proposal);
			// On an upload the blocks to complete are those just added (no settings yet); on a generated preset, its effects.
			bool upload = proposal.BasePreset is not null;
			bool Completes(ToneDesignBlockProposal block) => upload ? block.Parameters.Count == 0 : ToneDesignEffects.IsEffect(block);
			if (!proposal.Blocks.Any(Completes))
				return proposal;
			Fm9UploadedPreset built;
			try
			{
				built = Fm9UploadedPreset.Read(upload ? Fm9UploadedPreset.Apply(proposal) : Apply(template, proposal, "Completion"));
			}
			catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or InvalidOperationException or ArgumentException)
			{
				return proposal;
			}
			return proposal with
			{
				Blocks = proposal.Blocks.Select(block =>
				{
					if (!Completes(block) || built.Find(block.BlockType, block.Instance) is not { IsEditable: true } target)
						return block;
					Dictionary<string, string> parameters = new(built.GetParameters(target, 0), StringComparer.Ordinal);
					foreach (KeyValuePair<string, string> pair in block.Parameters)
						parameters[pair.Key] = pair.Value;
					return block with { Parameters = parameters, MaximumChannels = Fm9UploadedPreset.ChannelsOf(target) };
				}).ToArray()
			};
		}

		private static bool IsTemplateBlock(ToneDesignBlockProposal block) => RideTemplateBlocks.Any(candidate =>
			candidate.Instance == block.Instance && string.Equals(candidate.BlockType, block.BlockType, StringComparison.OrdinalIgnoreCase));

		private static ToneDesignBlockProposal WithoutDictionarySettings(ToneDesignBlockProposal block)
		{
			Dictionary<string, string> Curated(IReadOnlyDictionary<string, string> parameters) => parameters
				.Where(pair => Fm9BlockSettings.Find(block.BlockType, pair.Key) is null).ToDictionary(pair => pair.Key, pair => pair.Value);
			return block with
			{
				Parameters = Curated(block.Parameters),
				Channels = block.Channels.Select(channel => channel with { Parameters = Curated(channel.Parameters) }).ToArray()
			};
		}

		private static Fm9PresetDump Extend(Fm9PresetDump built, ToneDesignProposal proposal, ToneDesignProposal templatePart)
		{
			byte[] bytes = Fm9PresetBodyDecoder.Decode(built).Bytes.ToArray();
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block => !IsTemplateBlock(block)))
			{
				if (!Fm9PresetBodyBlockInserter.CanInsert(block.BlockType, block.Instance))
					throw new NotSupportedException($"{block.BlockType} {block.Instance} cannot be added to an FM9 preset yet.");
				bytes = Fm9PresetBodyBlockInserter.WithBlock(Fm9PresetBodyDecoder.Decode(Fm9PresetBodyEncoder.Reencode(built, bytes)),
					block.BlockType, block.Instance, new GridPosition(block.Row, block.Column));
			}
			foreach (ToneDesignRoute route in proposal.Routes.Except(templatePart.Routes))
			{
				Fm9PresetBody current = Fm9PresetBodyDecoder.Decode(Fm9PresetBodyEncoder.Reencode(built, bytes));
				GridPosition destination = new(route.DestinationRow, route.SourceColumn + 1);
				destination.Validate();
				Fm9PresetGridCell cell = current.GridCells.Single(candidate => candidate.Row == destination.Row && candidate.Column == destination.Column);
				if (current.GridCells.Single(candidate => candidate.Row == route.SourceRow && candidate.Column == route.SourceColumn).EffectId is null)
					throw new InvalidDataException($"A route starts at the empty grid cell row {route.SourceRow} column {route.SourceColumn}.");
				bytes = Fm9PresetBodyGridEditor.WithRoute(current, destination, (ushort)(cell.RouteMask | (1 << (route.SourceRow - 1))));
			}
			// The name was written by the template stage; the upload writer would otherwise rename to the proposal's own.
			return Fm9UploadedPreset.Apply(proposal with { BasePreset = Fm9PresetBodyEncoder.Reencode(built, bytes).ToArray(), PresetName = null });
		}

		private static Fm9PresetDump ApplyTemplate(Fm9PresetDump template, ToneDesignProposal proposal, string presetName)
		{
			Validate(template, proposal);

			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(template);
			Fm9BlockRecordTemplateProfile profile =
				Fm9BlockRecordTemplateProfile.CreateRideBaseline(body);
			Dictionary<RideTemplateBlock, Fm9PresetGridCell> blocks = ResolveBlocks(body, proposal, profile);
			byte[] editedBody = ApplyGridMoves(body, proposal, blocks);
			editedBody = ApplyRoutes(template, editedBody, proposal);
			editedBody = ApplyProfileParameters(template, editedBody, proposal, profile);
			editedBody = ApplyAmpSettings(template, editedBody, proposal);
			editedBody = ApplyCabSettings(template, editedBody, proposal);
			editedBody = ApplyScenes(template, editedBody, proposal);
			editedBody = MoveChangedModelsToChannelA(template, body, profile, editedBody, proposal);
			editedBody = Fm9PresetBodyGridEditor.WithInputOutputInstances(
				Fm9PresetBodyDecoder.Decode(Fm9PresetBodyEncoder.Reencode(template, editedBody)),
				proposal.InputBlockInstance, proposal.OutputBlockInstance);

			Fm9PresetDump exported = Fm9PresetBodyEncoder.Reencode(template, editedBody)
				.WithPresetName(presetName);
			Fm9PresetDump reparsed = Fm9PresetDump.Parse(exported.ToArray());
			Fm9PresetBodyDecoder.Decode(reparsed);

			if (!string.Equals(reparsed.PresetName, presetName, StringComparison.Ordinal))
			{
				throw new InvalidDataException("The exported FM9 preset name did not validate.");
			}

			return reparsed;
		}

		// Checks export prerequisites without editing a body or serializing a preset.
		public static void Validate(Fm9PresetDump template, ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(template);
			ArgumentNullException.ThrowIfNull(proposal);
			if (proposal.BasePreset is not null)
			{
				_ = Fm9UploadedPreset.Apply(proposal);
				return;
			}
			proposal = proposal with { Blocks = proposal.Blocks.Select(Fm9QualifiedModelCatalog.Normalize).ToArray() };
			(ToneDesignProposal templatePart, bool extended) = SplitForTemplate(proposal);
			if (extended)
			{
				// Building it is the check: block placement, cells, routes, settings, channels and scenes all validate there.
				_ = Apply(template, proposal, "Validation");
				return;
			}
			proposal = templatePart;
			Fm9PresetBodyGridEditor.ValidateInputOutputInstances(proposal.InputBlockInstance, proposal.OutputBlockInstance);
			Fm9PresetBodyGridEditor.ValidateInputOutputInstances(proposal.InputBlockInstance, proposal.OutputBlockInstance);
			if (proposal.Blocks.Count > 16 || proposal.Routes.Count > 24 || proposal.Scenes.Count > 8)
				throw new InvalidDataException("The proposal exceeds the bounded export contract.");
			EnsureProductionSafety(proposal);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(template);
			Fm9BlockRecordTemplateProfile profile = Fm9BlockRecordTemplateProfile.CreateRideBaseline(body);
			Dictionary<RideTemplateBlock, Fm9PresetGridCell> resolved = ResolveBlocks(body, proposal, profile);
			HashSet<GridPosition> sources = [];
			HashSet<GridPosition> targets = [];
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				GridPosition target = new(block.Row, block.Column);
				target.Validate();
				if (!targets.Add(target) || block.GetAuthoredChannels().Any(channel => block.GetParameters(channel).Count > 32))
					throw new InvalidDataException("Block positions must be unique and parameters bounded.");
				RideTemplateBlock identity = resolved.Keys.Single(candidate =>
					candidate.Instance == block.Instance && string.Equals(candidate.BlockType, block.BlockType, StringComparison.OrdinalIgnoreCase));
				Fm9PresetGridCell source = resolved[identity];
				if (source.Row != block.Row || source.Column != block.Column)
					sources.Add(new(source.Row, source.Column));
				foreach (string channel in block.GetAuthoredChannels())
				{
					int row = ChannelRow(channel);
					IReadOnlyDictionary<string, string> parameters = block.GetParameters(channel);
					if (string.Equals(block.BlockType, "Cab", StringComparison.OrdinalIgnoreCase))
					{
						Fm9CabSettings cab = Fm9PresetBodyParameterEditor.GetCabSettings(body, row);
						foreach (KeyValuePair<string, string> parameter in parameters)
							cab = ApplyCabParameter(cab, parameter);
						Fm9PresetBodyParameterEditor.ValidateCabSettings(cab);
						continue;
					}
					foreach (KeyValuePair<string, string> parameter in parameters)
					{
						if (IsAmpSetting(block.BlockType, parameter.Key))
						{
							if (IsAmpModel(block.BlockType, parameter.Key)) _ = ParseAmpInteger(parameter, 0, Fm9QualifiedModelCatalog.MaximumAmpModel);
							else _ = ApplyAmpOutputEqParameter(Fm9PresetBodyParameterEditor.GetAmpOutputEq(body, row), parameter);
						}
						else
						{
							Fm9TemplateParameterDefinition definition = profile.GetParameter(block.BlockType, block.Instance, parameter.Key);
							Fm9PresetBodyParameterEditor.ValidateProfileValue(definition, parameter.Value, block.BlockType, block.Instance);
							_ = profile.GetRecordForExistingBlock(body, block.BlockType, block.Instance);
						}
					}
				}
			}
			EnsureSupportedChannels(profile, proposal);
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				Fm9PresetGridCell? occupied = body.GridCells.SingleOrDefault(cell => cell.Row == block.Row && cell.Column == block.Column);
				RideTemplateBlock identity = resolved.Keys.Single(candidate =>
					candidate.Instance == block.Instance && string.Equals(candidate.BlockType, block.BlockType, StringComparison.OrdinalIgnoreCase));
				if (occupied?.EffectId is not null && occupied.EffectId != identity.EffectId &&
					!sources.Contains(new(block.Row, block.Column)))
					throw new InvalidOperationException("An FM9 block can only move to an empty cell or another requested source.");
			}
			Dictionary<GridPosition, int> finalGrid = body.GridCells
				.Where(cell => cell.EffectId.HasValue && !sources.Contains(new(cell.Row, cell.Column)))
				.ToDictionary(cell => new GridPosition(cell.Row, cell.Column), cell => cell.EffectId!.Value);
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
				finalGrid[new(block.Row, block.Column)] = resolved.Keys.Single(candidate =>
					candidate.Instance == block.Instance && string.Equals(candidate.BlockType, block.BlockType, StringComparison.OrdinalIgnoreCase)).EffectId;
			foreach (ToneDesignRoute route in proposal.Routes)
			{
				new GridPosition(route.SourceRow, route.SourceColumn).Validate();
				if (route.SourceColumn == 14)
					throw new NotSupportedException("Offline FM9 export only supports routes to the next grid column.");
				new GridPosition(route.DestinationRow, route.SourceColumn + 1).Validate();
				if (!finalGrid.TryGetValue(new(route.SourceRow, route.SourceColumn), out int source) ||
					!finalGrid.TryGetValue(new(route.DestinationRow, route.SourceColumn + 1), out int destination) ||
					!IsRideTemplateEffect(source) || !IsRideTemplateEffect(destination))
					throw new NotSupportedException("Offline FM9 export only routes existing Ride-template blocks.");
			}
			if (proposal.Scenes.Count > 0)
				Fm9PresetBodySceneAuthor.ValidateRideScenes(body, CreateScenes(proposal));
			EnsureScenesPlayAuthoredModelChannels(body, profile, proposal);
		}

		// An unauthored channel keeps the template's model, so an active scene on it would hear a different model than the
		// one the proposal changed; every active scene must play an authored channel of a changed block.
		private static void EnsureScenesPlayAuthoredModelChannels(
			Fm9PresetBody body,
			Fm9BlockRecordTemplateProfile profile,
			ToneDesignProposal proposal)
		{
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block => block.Instance == 1))
			{
				if (block.GetAuthoredChannels().Any(channel => ChangesModel(body, profile, block, channel)) &&
					!ModelSelectorIsWritable(proposal, block.BlockType, block.Instance))
					throw new NotSupportedException(
						$"The {block.BlockType} model can only change when every active scene plays an authored channel " +
						$"({string.Join(", ", block.GetAuthoredChannels())}).");
			}
		}

		// Without authored scenes the export moves a changed block to channel A everywhere (see MoveChangedModelsToChannelA).
		public static bool ModelSelectorIsWritable(
			ToneDesignProposal proposal,
			string blockType,
			int instance)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			ToneDesignBlockProposal? block = proposal.Blocks.FirstOrDefault(candidate =>
				string.Equals(candidate.BlockType, blockType, StringComparison.OrdinalIgnoreCase) && candidate.Instance == instance);
			return instance == 1 && proposal.Scenes.SelectMany(scene => scene.BlockSettings).All(setting =>
				!string.Equals(setting.BlockType, blockType, StringComparison.OrdinalIgnoreCase) ||
				setting.Instance != instance || setting.IsBypassed ||
				(block?.HasChannel(setting.Channel) ?? setting.Channel == ToneDesignChannels.A));
		}

		// Channels B-D write their own row, so any known setting may differ from channel A.
		private static void EnsureSupportedChannels(
			Fm9BlockRecordTemplateProfile profile,
			ToneDesignProposal proposal)
		{
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block => block.Channels.Count > 0))
			{
				if (block.Channels.Select(channel => channel.Channel).Distinct(StringComparer.Ordinal).Count() != block.Channels.Count ||
					block.Channels.Any(channel => channel.Channel is not ("B" or "C" or "D")))
					throw new InvalidDataException($"{block.BlockType} {block.Instance} channels must be unique B, C, or D.");
				if (block.Instance != 1 || block.BlockType is "Input" or "Output")
					throw new NotSupportedException($"Channels for '{block.BlockType} {block.Instance}' are not verified for offline FM9 export.");
				foreach (ToneDesignBlockChannel channel in block.Channels)
				{
					foreach (string name in channel.Parameters.Keys)
						_ = GetParameterId(profile, block, name);
				}
			}
		}

		public static bool IsStoredPerChannel(Fm9PresetDump template, string blockType, int instance, string parameterName)
		{
			ArgumentNullException.ThrowIfNull(template);
			return IsStoredPerChannel(Fm9BlockRecordTemplateProfile.CreateRideBaseline(Fm9PresetBodyDecoder.Decode(template)),
				blockType, instance, parameterName);
		}

		// Every setting the export knows is stored per channel; an unknown one throws NotSupportedException.
		public static bool IsStoredPerChannel(Fm9BlockRecordTemplateProfile profile, string blockType, int instance, string parameterName)
		{
			ArgumentNullException.ThrowIfNull(profile);
			_ = GetParameterId(profile, new ToneDesignBlockProposal(blockType, instance, 0, 0, new Dictionary<string, string>()), parameterName);
			return true;
		}

		private static int GetParameterId(Fm9BlockRecordTemplateProfile profile, ToneDesignBlockProposal block, string name) =>
			string.Equals(block.BlockType, "Cab", StringComparison.OrdinalIgnoreCase)
				? Fm9PresetBodyParameterEditor.GetCabParameterId(name)
				: IsAmpSetting(block.BlockType, name)
					? Fm9PresetBodyParameterEditor.GetAmpSettingParameterId(name)
					: profile.GetParameter(block.BlockType, 1, name).ParameterId;

		// Ids 0-5 open each channel row but were long treated as one block-wide value that followed channel A (Amp Level,
		// Drive type and Drive, Cab type, Reverb Mix...). A channel B-D that does not state one keeps channel A's value
		// rather than the template's, so proposals saved under that rule export as they always have.
		private static IReadOnlyList<KeyValuePair<string, string>> ChannelParameters(
			Fm9BlockRecordTemplateProfile? profile, ToneDesignBlockProposal block, string channel)
		{
			IReadOnlyDictionary<string, string> own = block.GetParameters(channel);
			if (channel == ToneDesignChannels.A)
				return own.ToArray();
			bool IsRowStart(string name)
			{
				try
				{
					int id = profile is null
						? Fm9PresetBodyParameterEditor.GetCabParameterId(name)
						: string.Equals(block.BlockType, "Cab", StringComparison.OrdinalIgnoreCase) || IsAmpSetting(block.BlockType, name)
							? int.MaxValue
							: profile.GetParameter(block.BlockType, block.Instance, name).ParameterId;
					return id < Fm9ChannelParameterStorage.RowStartParameterCount;
				}
				catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or KeyNotFoundException)
				{
					return false;
				}
			}
			return
			[
				.. own,
				.. block.Parameters.Where(parameter => IsRowStart(parameter.Key) &&
					!own.Keys.Any(key => key.Equals(parameter.Key, StringComparison.OrdinalIgnoreCase)))
			];
		}

		private static int ChannelRow(string channel) => channel switch
		{
			"A" => 0,
			"B" => 1,
			"C" => 2,
			"D" => 3,
			_ => throw new InvalidDataException("A block channel must be A, B, C, or D.")
		};

		// Inherited template scenes may run a block on B-D; a changed model would then be heard in only some scenes.
		private static byte[] MoveChangedModelsToChannelA(
			Fm9PresetDump template,
			Fm9PresetBody templateBody,
			Fm9BlockRecordTemplateProfile profile,
			byte[] editedBody,
			ToneDesignProposal proposal)
		{
			if (proposal.Scenes.Count > 0)
				return editedBody;
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block => block.Instance == 1))
			{
				if (!ChangesModel(templateBody, profile, block, ToneDesignChannels.A))
					continue;
				Fm9PresetBody current = Fm9PresetBodyDecoder.Decode(Fm9PresetBodyEncoder.Reencode(template, editedBody));
				editedBody = Fm9PresetBodySceneAuthor.WithBlockOnChannelAInEveryScene(
					current,
					profile.GetRecordForExistingBlock(current, block.BlockType, block.Instance));
			}
			return editedBody;
		}

		private static bool ChangesModel(
			Fm9PresetBody body,
			Fm9BlockRecordTemplateProfile profile,
			ToneDesignBlockProposal block,
			string channel)
		{
			int row = ChannelRow(channel);
			IReadOnlyDictionary<string, string> parameters = block.GetParameters(channel);
			if (string.Equals(block.BlockType, "Cab", StringComparison.OrdinalIgnoreCase))
			{
				Fm9CabSettings template = Fm9PresetBodyParameterEditor.GetCabSettings(body, row);
				Fm9CabSettings requested = template;
				foreach (KeyValuePair<string, string> parameter in parameters)
					requested = ApplyCabParameter(requested, parameter);
				return requested.Bank != template.Bank || requested.TypeOrdinal != template.TypeOrdinal;
			}

			if (string.Equals(block.BlockType, "Amp", StringComparison.OrdinalIgnoreCase))
			{
				return parameters.Any(parameter => IsAmpModel(block.BlockType, parameter.Key) &&
					ParseAmpInteger(parameter, 0, Fm9QualifiedModelCatalog.MaximumAmpModel) != Fm9PresetBodyParameterEditor.GetAmpModel(body, row));
			}

			if (string.Equals(block.BlockType, "Drive", StringComparison.OrdinalIgnoreCase))
			{
				return parameters.Any(parameter =>
					string.Equals(parameter.Key, "Drive Model", StringComparison.OrdinalIgnoreCase) &&
					ParseAmpInteger(parameter, 0, 86) !=
						Fm9PresetBodyParameterEditor.GetProfileParameterRaw(body, profile, "Drive", 1, 0, row));
			}

			return false;
		}

		public static Fm9TemplateParameterDefinition GetParameterDefinition(Fm9PresetDump template, string blockType, int instance, string name) =>
			Fm9BlockRecordTemplateProfile.CreateRideBaseline(Fm9PresetBodyDecoder.Decode(template)).GetParameter(blockType, instance, name);

		// These read inherited template state, not selected-model factory defaults.
		public static int? GetTemplateSelector(Fm9PresetDump template, string blockType, int instance = 1)
		{
			ArgumentNullException.ThrowIfNull(template);
			ArgumentNullException.ThrowIfNull(blockType);
			if (instance != 1)
				return null;
			if (!blockType.Equals("Amp", StringComparison.OrdinalIgnoreCase) &&
				!blockType.Equals("Drive", StringComparison.OrdinalIgnoreCase))
				return null;
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(template);
			Fm9BlockRecordTemplateProfile profile = Fm9BlockRecordTemplateProfile.CreateRideBaseline(body);
			return blockType.Equals("Amp", StringComparison.OrdinalIgnoreCase)
				? Fm9PresetBodyParameterEditor.GetAmpModel(body)
				: Fm9PresetBodyParameterEditor.GetProfileParameterRaw(body, profile, "Drive", 1, 0);
		}

		public static Fm9CabSettings GetTemplateCabSettings(Fm9PresetDump template)
		{
			ArgumentNullException.ThrowIfNull(template);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(template);
			_ = Fm9BlockRecordTemplateProfile.CreateRideBaseline(body);
			return Fm9PresetBodyParameterEditor.GetCabSettings(body);
		}

		private static void EnsureProductionSafety(ToneDesignProposal proposal)
		{
			List<ToneDesignBlockProposal> blocks = [];
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				blocks.Add(block.MapChannels(channel =>
				{
					Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase);
					foreach (KeyValuePair<string, string> parameter in channel.Parameters)
					{
						if (!parameters.TryAdd(parameter.Key, parameter.Value))
							throw new InvalidDataException($"'{block.BlockType} {block.Instance}' contains duplicate parameter identities.");
					}
					return channel with { Parameters = parameters };
				}));
			}
			Fm9ProductionSafetyPolicy.EnsureSafeForExport(proposal with { Blocks = blocks });
		}

		private static byte[] ApplyScenes(
			Fm9PresetDump template,
			ReadOnlySpan<byte> bodyBytes,
			ToneDesignProposal proposal)
		{
			if (proposal.Scenes.Count == 0)
			{
				return bodyBytes.ToArray();
			}

			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(
				Fm9PresetBodyEncoder.Reencode(template, bodyBytes));
			return Fm9PresetBodySceneAuthor.WithRideScenes(body, CreateScenes(proposal));
		}

		private static IReadOnlyList<Fm9RideScene> CreateScenes(ToneDesignProposal proposal)
		{
			List<Fm9RideScene> scenes = [];

			for (int index = 0; index < proposal.Scenes.Count; index++)
			{
				ToneDesignSceneProposal proposalScene = proposal.Scenes[index]
					?? throw new InvalidDataException("A proposed FM9 scene cannot be null.");
				List<Fm9RideSceneBlockState> states = [];

				foreach (ToneDesignSceneBlockSetting setting in proposalScene.BlockSettings)
				{
					if (setting is null)
					{
						throw new InvalidDataException("A proposed FM9 scene block setting cannot be null.");
					}

					if (setting.Instance != 1 ||
						!TryGetRideSceneBlock(setting.BlockType, out Fm9RideSceneBlock block))
					{
						throw new NotSupportedException(
							$"Scene block '{setting.BlockType} {setting.Instance}' is not a supported existing Ride-template block.");
					}

					if (!TryGetSceneChannel(setting.Channel, out Fm9SceneChannel channel))
					{
						throw new InvalidDataException(
							$"Scene channel for '{setting.BlockType} {setting.Instance}' must be A, B, C, or D.");
					}

					states.Add(new Fm9RideSceneBlockState(block, channel, setting.IsBypassed));
				}

				scenes.Add(new Fm9RideScene(index + 1, proposalScene.Name, states));
			}

			return scenes;
		}

		private static Dictionary<RideTemplateBlock, Fm9PresetGridCell> ResolveBlocks(
			Fm9PresetBody body,
			ToneDesignProposal proposal,
			Fm9BlockRecordTemplateProfile profile)
		{
			Dictionary<RideTemplateBlock, Fm9PresetGridCell> resolved = [];

			foreach (ToneDesignBlockProposal proposalBlock in proposal.Blocks)
			{
				RideTemplateBlock templateBlock = RideTemplateBlocks.SingleOrDefault(candidate =>
					candidate.Instance == proposalBlock.Instance &&
					string.Equals(candidate.BlockType, proposalBlock.BlockType, StringComparison.OrdinalIgnoreCase))
					?? throw new NotSupportedException(
						$"Block placement for '{proposalBlock.BlockType} {proposalBlock.Instance}' is not supported by the Ride template.");

				if (!resolved.TryAdd(templateBlock, FindUniquePlacedBlock(body, templateBlock)))
				{
					throw new InvalidOperationException(
						$"The proposal contains '{proposalBlock.BlockType} {proposalBlock.Instance}' more than once.");
				}

				foreach (string channel in proposalBlock.GetAuthoredChannels())
					ValidateParameters(proposalBlock.ForChannel(channel), profile);
			}

			return resolved;
		}

		private static Fm9PresetGridCell FindUniquePlacedBlock(
			Fm9PresetBody body,
			RideTemplateBlock templateBlock)
		{
			List<Fm9PresetGridCell> matches = body.GridCells
				.Where(cell => cell.EffectId == templateBlock.EffectId)
				.ToList();

			return matches.Count == 1
				? matches[0]
				: throw new InvalidOperationException(
					$"The template does not contain exactly one existing Ride {templateBlock.BlockType} {templateBlock.Instance} block.");
		}

		private static void ValidateParameters(
			ToneDesignBlockProposal block,
			Fm9BlockRecordTemplateProfile profile)
		{
			if (block.Parameters.Count == 0)
			{
				return;
			}

			if (string.Equals(block.BlockType, "Cab", StringComparison.OrdinalIgnoreCase))
			{
				if (block.Instance != 1)
				{
					throw new NotSupportedException(
						$"Parameter mappings for 'Cab {block.Instance}' are not verified for offline FM9 export.");
				}

				foreach (string name in block.Parameters.Keys)
				{
					if (!IsCabSetting(name))
					{
						throw new NotSupportedException(
							$"Cab parameter '{name}' is not verified for offline FM9 export.");
					}
				}

				return;
			}

			foreach (string name in block.Parameters.Keys)
			{
				if (IsAmpSetting(block.BlockType, name))
				{
					if (block.Instance != 1)
					{
						throw new NotSupportedException(
							$"Amp model selection for 'Amp {block.Instance}' is not verified for offline FM9 export.");
					}

					continue;
				}

				_ = profile.GetParameter(block.BlockType, block.Instance, name);
			}
		}

		private static byte[] ApplyGridMoves(
			Fm9PresetBody body,
			ToneDesignProposal proposal,
			IReadOnlyDictionary<RideTemplateBlock, Fm9PresetGridCell> blocks)
		{
			List<Fm9PresetGridMove> moves = [];

			foreach (ToneDesignBlockProposal proposalBlock in proposal.Blocks)
			{
				RideTemplateBlock templateBlock = RideTemplateBlocks.Single(candidate =>
					candidate.Instance == proposalBlock.Instance &&
					string.Equals(candidate.BlockType, proposalBlock.BlockType, StringComparison.OrdinalIgnoreCase));
				Fm9PresetGridCell existing = blocks[templateBlock];
				GridPosition target = new(proposalBlock.Row, proposalBlock.Column);
				target.Validate();

				if (existing.Row != target.Row || existing.Column != target.Column)
				{
					moves.Add(new Fm9PresetGridMove(
						new GridPosition(existing.Row, existing.Column),
						target,
						templateBlock.EffectId));
				}
			}

			return Fm9PresetBodyGridEditor.MoveExistingBlocks(body, moves);
		}

		private static byte[] ApplyRoutes(
			Fm9PresetDump template,
			ReadOnlySpan<byte> bodyBytes,
			ToneDesignProposal proposal)
		{
			Fm9PresetDump currentDump = Fm9PresetBodyEncoder.Reencode(template, bodyBytes);
			Fm9PresetBody currentBody = Fm9PresetBodyDecoder.Decode(currentDump);
			byte[] editedBody = currentBody.Bytes.ToArray();

			foreach (ToneDesignRoute route in proposal.Routes)
			{
				GridPosition source = new(route.SourceRow, route.SourceColumn);
				source.Validate();

				if (source.Column == 14)
				{
					throw new NotSupportedException(
						"Offline FM9 export only supports routes to the next grid column.");
				}

				GridPosition destination = new(route.DestinationRow, source.Column + 1);
				destination.Validate();
				Fm9PresetGridCell? sourceCell = currentBody.GridCells.SingleOrDefault(cell =>
					cell.Row == source.Row && cell.Column == source.Column);
				Fm9PresetGridCell? destinationCell = currentBody.GridCells.SingleOrDefault(cell =>
					cell.Row == destination.Row && cell.Column == destination.Column);

				if (!IsRideTemplateEffect(sourceCell?.EffectId) ||
					!IsRideTemplateEffect(destinationCell?.EffectId))
				{
					throw new NotSupportedException(
						"Offline FM9 export only routes existing Ride-template blocks.");
				}

				ushort routeMask = (ushort)(destinationCell!.RouteMask | (1 << (source.Row - 1)));
				currentBody = Fm9PresetBodyDecoder.Decode(
					Fm9PresetBodyEncoder.Reencode(template, editedBody));
				editedBody = Fm9PresetBodyGridEditor.WithRoute(currentBody, destination, routeMask);
				currentDump = Fm9PresetBodyEncoder.Reencode(template, editedBody);
				currentBody = Fm9PresetBodyDecoder.Decode(currentDump);
			}

			return editedBody;
		}

		private static byte[] ApplyProfileParameters(
			Fm9PresetDump template,
			ReadOnlySpan<byte> bodyBytes,
			ToneDesignProposal proposal,
			Fm9BlockRecordTemplateProfile profile)
		{
			byte[] editedBody = bodyBytes.ToArray();

			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(candidate =>
				!string.Equals(candidate.BlockType, "Cab", StringComparison.OrdinalIgnoreCase)))
			{
				foreach (string channel in block.GetAuthoredChannels())
				{
					int row = ChannelRow(channel);
					foreach (KeyValuePair<string, string> parameter in ChannelParameters(profile, block, channel))
					{
						if (IsAmpSetting(block.BlockType, parameter.Key))
						{
							continue;
						}

						Fm9PresetBody currentBody = Fm9PresetBodyDecoder.Decode(
							Fm9PresetBodyEncoder.Reencode(template, editedBody));

						if (!double.TryParse(
							parameter.Value,
							System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture,
							out double value))
						{
							throw new InvalidDataException(
								$"Parameter '{block.BlockType} {block.Instance} {parameter.Key}' must be an invariant numeric value.");
						}

						editedBody = Fm9PresetBodyParameterEditor.WithProfileParameter(
							currentBody,
							profile,
							block.BlockType,
							block.Instance,
							parameter.Key,
							value,
							row);
					}
				}
			}

			return editedBody;
		}

		private static byte[] ApplyAmpSettings(
			Fm9PresetDump template,
			ReadOnlySpan<byte> bodyBytes,
			ToneDesignProposal proposal)
		{
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(
				Fm9PresetBodyEncoder.Reencode(template, bodyBytes));
			List<Fm9AmpChannelSettings> channels = [];
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(block =>
				string.Equals(block.BlockType, "Amp", StringComparison.OrdinalIgnoreCase) &&
				block.Instance == 1))
			{
				foreach (string channel in block.GetAuthoredChannels())
				{
					int row = ChannelRow(channel);
					int? typeOrdinal = null;
					Fm9AmpOutputEqSettings outputEq = Fm9PresetBodyParameterEditor.GetAmpOutputEq(body, row);
					bool hasOutputEq = false;

					foreach (KeyValuePair<string, string> setting in block.GetParameters(channel)
						.Where(parameter => IsAmpSetting("Amp", parameter.Key)))
					{
						if (string.Equals(setting.Key, "Amp Model", StringComparison.OrdinalIgnoreCase))
						{
							typeOrdinal = ParseAmpInteger(setting, 0, Fm9QualifiedModelCatalog.MaximumAmpModel);
						}
						else
						{
							outputEq = ApplyAmpOutputEqParameter(outputEq, setting);
							hasOutputEq = true;
						}
					}

					if (typeOrdinal.HasValue || hasOutputEq)
						channels.Add(new Fm9AmpChannelSettings(row, typeOrdinal, hasOutputEq ? outputEq : null));
				}
			}

			return channels.Count == 0
				? bodyBytes.ToArray()
				: Fm9PresetBodyParameterEditor.WithAmpChannelSettings(body, channels);
		}

		internal static int ParseAmpModel(KeyValuePair<string, string> parameter) => ParseAmpInteger(parameter, 0, Fm9QualifiedModelCatalog.MaximumAmpModel);

		internal static Fm9AmpOutputEqSettings ApplyAmpOutputEqParameter(
			Fm9AmpOutputEqSettings settings,
			KeyValuePair<string, string> parameter)
		{
			if (string.Equals(parameter.Key, "Output EQ 80 Hz", StringComparison.OrdinalIgnoreCase))
				return settings with
				{
					Band80Hz = ParseAmpGain(parameter),
					Type = Fm9AmpOutputEqType.Mark5Band
				};
			if (string.Equals(parameter.Key, "Output EQ 240 Hz", StringComparison.OrdinalIgnoreCase))
				return settings with
				{
					Band240Hz = ParseAmpGain(parameter),
					Type = Fm9AmpOutputEqType.Mark5Band
				};
			if (string.Equals(parameter.Key, "Output EQ 750 Hz", StringComparison.OrdinalIgnoreCase))
				return settings with
				{
					Band750Hz = ParseAmpGain(parameter),
					Type = Fm9AmpOutputEqType.Mark5Band
				};
			if (string.Equals(parameter.Key, "Output EQ 2200 Hz", StringComparison.OrdinalIgnoreCase))
				return settings with
				{
					Band2200Hz = ParseAmpGain(parameter),
					Type = Fm9AmpOutputEqType.Mark5Band
				};
			if (string.Equals(parameter.Key, "Output EQ 6600 Hz", StringComparison.OrdinalIgnoreCase))
				return settings with
				{
					Band6600Hz = ParseAmpGain(parameter),
					Type = Fm9AmpOutputEqType.Mark5Band
				};
			if (string.Equals(parameter.Key, "Output EQ Type", StringComparison.OrdinalIgnoreCase))
				return settings with { Type = ParseAmpOutputEqType(parameter) };
			if (string.Equals(parameter.Key, "Output EQ Location", StringComparison.OrdinalIgnoreCase))
				return settings with { Location = ParseAmpEnum<Fm9AmpOutputEqLocation>(parameter) };
			if (string.Equals(parameter.Key, "Output EQ Enabled", StringComparison.OrdinalIgnoreCase))
				return settings with { Enabled = ParseAmpInteger(parameter, 0, 1) == 1 };

			throw new NotSupportedException($"Amp parameter '{parameter.Key}' is not verified for offline FM9 export.");
		}

		private static double ParseAmpGain(KeyValuePair<string, string> parameter)
		{
			if (!double.TryParse(parameter.Value, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out double value) ||
				!double.IsFinite(value) || value < -12 || value > 12)
			{
				throw new InvalidDataException($"Amp parameter '{parameter.Key}' must be from -12 through 12 dB.");
			}

			return value;
		}

		private static int ParseAmpInteger(KeyValuePair<string, string> parameter, int minimum, int maximum)
		{
			if (!int.TryParse(parameter.Value, System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int value) ||
				value < minimum || value > maximum)
			{
				throw new InvalidDataException(
					$"Amp parameter '{parameter.Key}' must be an integer from {minimum} through {maximum}.");
			}

			return value;
		}

		private static Fm9AmpOutputEqType ParseAmpOutputEqType(KeyValuePair<string, string> parameter)
		{
			Fm9AmpOutputEqType value = ParseAmpEnum<Fm9AmpOutputEqType>(parameter);
			if (value != Fm9AmpOutputEqType.Mark5Band)
			{
				throw new InvalidDataException(
					$"Amp parameter '{parameter.Key}' only supports Mark5Band for offline FM9 export.");
			}

			return value;
		}

		private static T ParseAmpEnum<T>(KeyValuePair<string, string> parameter) where T : struct, Enum
		{
			if (!Enum.TryParse(parameter.Value, ignoreCase: true, out T value) || !Enum.IsDefined(value))
			{
				throw new InvalidDataException($"Amp parameter '{parameter.Key}' has an invalid value.");
			}

			return value;
		}

		private static byte[] ApplyCabSettings(
			Fm9PresetDump template,
			ReadOnlySpan<byte> bodyBytes,
			ToneDesignProposal proposal)
		{
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(
				Fm9PresetBodyEncoder.Reencode(template, bodyBytes));
			Dictionary<int, Fm9CabSettings> channels = [];
			foreach (ToneDesignBlockProposal block in proposal.Blocks.Where(candidate =>
				string.Equals(candidate.BlockType, "Cab", StringComparison.OrdinalIgnoreCase) &&
				candidate.Instance == 1))
			{
				foreach (string channel in block.GetAuthoredChannels())
				{
					IReadOnlyList<KeyValuePair<string, string>> parameters = ChannelParameters(null, block, channel);
					if (parameters.Count == 0)
						continue;
					int row = ChannelRow(channel);
					Fm9CabSettings settings = channels.GetValueOrDefault(row) ?? Fm9PresetBodyParameterEditor.GetCabSettings(body, row);
					foreach (KeyValuePair<string, string> parameter in parameters)
					{
						settings = ApplyCabParameter(settings, parameter);
					}
					channels[row] = settings;
				}
			}

			return channels.Count == 0
				? bodyBytes.ToArray()
				: Fm9PresetBodyParameterEditor.WithCabChannelSettings(body, channels);
		}

		internal static Fm9CabSettings ApplyCabParameter(
			Fm9CabSettings settings,
			KeyValuePair<string, string> parameter)
		{
			if (string.Equals(parameter.Key, "Bank", StringComparison.OrdinalIgnoreCase))
			{
				return settings with { Bank = ParseCabInteger(parameter, 0, 4) };
			}

			if (string.Equals(parameter.Key, "Cab Type", StringComparison.OrdinalIgnoreCase))
			{
				return settings with { TypeOrdinal = ParseCabInteger(parameter, 0, 1023) };
			}

			if (string.Equals(parameter.Key, "Low Cut", StringComparison.OrdinalIgnoreCase))
			{
				return settings with { LowCut = ParseCabFrequency(parameter, 20, 200) };
			}

			if (string.Equals(parameter.Key, "High Cut", StringComparison.OrdinalIgnoreCase))
			{
				return settings with { HighCut = ParseCabFrequency(parameter, 2_000, 20_000) };
			}

			throw new NotSupportedException(
				$"Cab parameter '{parameter.Key}' is not verified for offline FM9 export.");
		}

		private static int ParseCabInteger(
			KeyValuePair<string, string> parameter,
			int minimum,
			int maximum)
		{
			if (!int.TryParse(
				parameter.Value,
				System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture,
				out int value) ||
				value < minimum ||
				value > maximum)
			{
				throw new InvalidDataException(
					$"Cab parameter '{parameter.Key}' must be an integer from {minimum} through {maximum}.");
			}

			return value;
		}

		private static double ParseCabFrequency(
			KeyValuePair<string, string> parameter,
			double minimum,
			double maximum)
		{
			if (!double.TryParse(
				parameter.Value,
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture,
				out double value) ||
				!double.IsFinite(value) ||
				value < minimum ||
				value > maximum)
			{
				throw new InvalidDataException(
					$"Cab parameter '{parameter.Key}' must be a frequency from {minimum} through {maximum} Hz.");
			}

			return value;
		}

		private static bool IsCabSetting(string name)
		{
			return string.Equals(name, "Bank", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(name, "Cab Type", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(name, "Low Cut", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(name, "High Cut", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsAmpModel(string blockType, string parameterName)
		{
			return string.Equals(blockType, "Amp", StringComparison.OrdinalIgnoreCase) &&
				string.Equals(parameterName, "Amp Model", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsAmpSetting(string blockType, string parameterName)
		{
			return IsAmpModel(blockType, parameterName) ||
				(string.Equals(blockType, "Amp", StringComparison.OrdinalIgnoreCase) &&
					(parameterName.StartsWith("Output EQ ", StringComparison.OrdinalIgnoreCase)));
		}

		private static bool IsRideTemplateEffect(int? effectId)
		{
			return effectId is not null &&
				RideTemplateBlocks.Any(block => block.EffectId == effectId.Value);
		}

		private static bool TryGetRideSceneBlock(string blockType, out Fm9RideSceneBlock block)
		{
			if (string.Equals(blockType, "Input", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Input;
				return true;
			}

			if (string.Equals(blockType, "Gate/Expander", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Gate;
				return true;
			}

			if (string.Equals(blockType, "Drive", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Drive;
				return true;
			}

			if (string.Equals(blockType, "Amp", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Amp;
				return true;
			}

			if (string.Equals(blockType, "Cab", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Cab;
				return true;
			}

			if (string.Equals(blockType, "Parametric EQ", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.ParametricEq;
				return true;
			}

			if (string.Equals(blockType, "Reverb", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Reverb;
				return true;
			}

			if (string.Equals(blockType, "Output", StringComparison.OrdinalIgnoreCase))
			{
				block = Fm9RideSceneBlock.Output;
				return true;
			}

			block = default;
			return false;
		}

		private static bool TryGetSceneChannel(string channel, out Fm9SceneChannel value)
		{
			switch (channel)
			{
				case "A":
					value = Fm9SceneChannel.A;
					return true;
				case "B":
					value = Fm9SceneChannel.B;
					return true;
				case "C":
					value = Fm9SceneChannel.C;
					return true;
				case "D":
					value = Fm9SceneChannel.D;
					return true;
				default:
					value = default;
					return false;
			}
		}

		private static void ValidatePaths(Fm9ProposalPresetExportRequest request)
		{
			if (!File.Exists(request.ProposalPath))
			{
				throw new FileNotFoundException("The tone-design proposal file was not found.", request.ProposalPath);
			}

			if (!File.Exists(request.TemplatePath))
			{
				throw new FileNotFoundException("The FM9 template .syx file was not found.", request.TemplatePath);
			}

			if (!string.Equals(Path.GetExtension(request.TemplatePath), ".syx", StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(Path.GetExtension(request.OutputPath), ".syx", StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("The template and output paths must both use the .syx extension.");
			}

			if (string.Equals(Path.GetFullPath(request.TemplatePath), Path.GetFullPath(request.OutputPath), StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("The output .syx path must differ from the template .syx path.");
			}

			string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));

			if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
			{
				throw new DirectoryNotFoundException("The output .syx directory does not exist.");
			}
		}

		private static void WriteAtomically(string outputPath, ReadOnlySpan<byte> contents)
		{
			string fullOutputPath = Path.GetFullPath(outputPath);
			string temporaryPath = Path.Combine(
				Path.GetDirectoryName(fullOutputPath)!,
				$".{Path.GetFileName(fullOutputPath)}.{Path.GetRandomFileName()}");

			try
			{
				File.WriteAllBytes(temporaryPath, contents.ToArray());
				File.Move(temporaryPath, fullOutputPath, overwrite: true);
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
