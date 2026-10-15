using Fractal.Core;

namespace Fractal.Application
{
	public interface IToneDesignChatClient
	{
		Task<ToneDesignChatResponse> CreateProposalAsync(
			ToneDesignChatRequest request,
			CancellationToken cancellationToken);
	}

	public sealed record ToneDesignChatRequest(string UserPrompt)
	{
		private const int MaximumSections = 16;

		public FractalDeviceProfile? DeviceProfile { get; init; }

		public int? MaximumUserPromptCharacters { get; init; }

		public string? ResearchQueryText { get; init; }

		public IProgress<ToneDesignProgressUpdate>? Progress { get; init; }

		// Called once per provider call, including calls whose answer is later rejected, so spend is never under-counted.
		public Action<ToneDesignTokenUsage>? UsageObserver { get; init; }

		public IReadOnlyList<ToneDesignSectionRequest> Sections { get; init; } = [];

		public ToneDesignRefinementContext? Refinement { get; init; }

		// Lets the model add effect blocks (described from the device's dictionary); without it a proposal stays on the
		// eight Ride blocks.
		public IToneDesignEffectCatalog? Effects { get; init; }

		// The player allows a DynaCab (multi-mic cab) in place of a factory cab; without it the Cab stays a factory cab.
		public bool AllowDynaCab { get; init; }

		public string GetPromptWithDeviceProfile()
		{
			ValidateSections();
			List<string> context = [UserPrompt];

			if (Sections.Count > 0)
			{
				context.Add($"""
					Requested musical sections (role metadata only):
					- {string.Join(
						Environment.NewLine + "- ",
						Sections.Select(section =>
							$"{section.Name} [{section.Role}]: {section.Purpose}"))}
					""");
			}

			if (DeviceProfile is not null)
			{
				context.Add($"""
					Device profile output eligibility: {DeviceProfile.IsOutputEligible}
					Verified firmware profiles: {string.Join(", ", DeviceProfile.VerifiedFirmwareProfiles)}
					Device profile validation status: templates={DeviceProfile.TemplateState}; parameter mappings={DeviceProfile.ParameterMappingState}; connection={DeviceProfile.ConnectionState}.
					Device profile constraint: {DeviceProfile.ValidationRequirement}
					""");
				if (DeviceProfile.IsBeta)
					context.Add($"Beta generation enabled for the {DeviceProfile.DisplayName}: follow the {DeviceProfile.DisplayName} limits in the system message.");
			}

			return string.Join(Environment.NewLine, context);
		}

		public void ValidateFinalUserPrompt(string finalUserPrompt)
		{
			if (string.IsNullOrWhiteSpace(finalUserPrompt))
			{
				throw new ToneDesignChatRequestValidationException(
					"The final provider user message must not be empty.");
			}

			if (MaximumUserPromptCharacters is <= 0)
			{
				throw new ToneDesignChatRequestValidationException(
					"The maximum provider user-message length must be greater than zero.");
			}

			if (MaximumUserPromptCharacters is int maximum &&
				finalUserPrompt.Length > maximum)
			{
				throw new ToneDesignChatRequestValidationException(
					$"The final provider user message exceeds the {maximum:N0}-character maximum.");
			}
		}

		private void ValidateSections()
		{
			if (Sections.Count > MaximumSections)
			{
				throw new ToneDesignChatRequestValidationException(
					$"A tone-design request supports at most {MaximumSections} musical sections.");
			}

			HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
			foreach (ToneDesignSectionRequest section in Sections)
			{
				if (section is null ||
					string.IsNullOrWhiteSpace(section.Name) ||
					string.IsNullOrWhiteSpace(section.Purpose))
				{
					throw new ToneDesignChatRequestValidationException(
						"Every requested musical section requires a non-empty name and purpose.");
				}

				if (!ToneDesignSectionRoles.IsSupported(section.Role))
				{
					throw new ToneDesignChatRequestValidationException(
						$"Section '{section.Name}' must use a supported role: {string.Join(", ", ToneDesignSectionRoles.All)}.");
				}

				if (!names.Add(section.Name))
				{
					throw new ToneDesignChatRequestValidationException(
						$"Requested musical section names must be unique; '{section.Name}' is duplicated.");
				}
			}
		}
	}

	public sealed class ToneDesignChatRequestValidationException : ArgumentException
	{
		public ToneDesignChatRequestValidationException(string message)
			: base(message)
		{
		}
	}

	public enum ToneDesignProgressStage
	{
		Clarification,
		EvidenceRetrieval,
		InitialGeneration,
		CorrectionAttempt,
		ValidationExport
	}

	public sealed record ToneDesignTokenUsage(
		string Provider,
		int InputTokens,
		int OutputTokens,
		string? Model = null);

	public sealed record ToneDesignProgressUpdate(
		ToneDesignProgressStage Stage,
		string Message);

	public enum ToneDesignProviderErrorKind
	{
		Unavailable,
		Timeout,
		RateLimited,
		InvalidResponse,
		Internal
	}

	public sealed class ToneDesignProviderException : Exception
	{
		public ToneDesignProviderException(
			ToneDesignProviderErrorKind kind,
			string provider,
			string message,
			Exception? innerException = null)
			: base(message, innerException)
		{
			Kind = kind;
			Provider = provider;
		}

		public ToneDesignProviderErrorKind Kind { get; }

		public string Provider { get; }

		public TimeSpan? RetryAfter { get; init; }
	}

	public sealed record ToneDesignChatResponse(ToneDesignProposal Proposal);

	public sealed record ToneDesignRefinementContext(
		ToneDesignProposal OriginalProposal,
		string Instruction);

	public static class ToneDesignSectionRoles
	{
		public const string Rhythm = "rhythm";
		public const string Clean = "clean";
		public const string Lead = "lead";
		public const string Harmony = "harmony";
		public const string Intro = "intro";
		public const string Transition = "transition";

		public static IReadOnlyList<string> All { get; } =
		[
			Rhythm,
			Clean,
			Lead,
			Harmony,
			Intro,
			Transition
		];

		public static bool IsSupported(string? role) =>
			!string.IsNullOrWhiteSpace(role) &&
			All.Contains(role, StringComparer.Ordinal);
	}

	public sealed record ToneDesignSectionRequest(
		string Name,
		string Role,
		string Purpose);

	public sealed record ToneDesignProposal(
		string Summary,
		IReadOnlyList<ToneDesignBlockProposal> Blocks,
		IReadOnlyList<string> Cautions)
	{
		public IReadOnlyList<ToneDesignRoute> Routes { get; init; } = [];
		public IReadOnlyList<ToneDesignSceneProposal> Scenes { get; init; } = [];
		public IReadOnlyList<ToneDesignEvidenceConclusion> Evidence { get; init; } = [];
		// Endpoint blocks retain their logical slots; these select the exported I/O instances.
		public int InputBlockInstance { get; init; } = 1;
		public int OutputBlockInstance { get; init; } = 1;
		// The preset's name (as on the FM9, see Fm9PresetName): suggested from the request, kept from an upload, renameable.
		public string? PresetName { get; init; }
		// The unit the preset is for; null is the FM9 (every proposal saved before other devices existed). Left out of the
		// stored JSON when null so FM9 proposals serialise exactly as before.
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public FractalDeviceId? Device { get; init; }
		// An uploaded preset this proposal edits in place: only changed settings are written, and its layout, routing and
		// every block FracTone does not model are kept exactly. Null for presets built on FracTone's own template.
		public byte[]? BasePreset { get; init; }
		// Blocks of the uploaded preset that FracTone shows in the chain but does not edit.
		public IReadOnlyList<ToneDesignPreservedBlock> PreservedBlocks { get; init; } = [];
		// Grid cells of the uploaded preset that hold a shunt (the signal passes straight through).
		public IReadOnlyList<ToneDesignGridCell> Shunts { get; init; } = [];
		// Blocks taken out of the uploaded preset; each leaves a shunt so the signal keeps its path.
		public IReadOnlyList<ToneDesignPreservedBlock> RemovedBlocks { get; init; } = [];
		// Modifiers attached to block settings (the FM9 keeps up to 32 per preset). Null means not described (a proposal
		// saved before modifiers were read, or none authored): an upload then keeps its modifiers exactly as uploaded.
		public IReadOnlyList<ToneDesignModifier>? Modifiers { get; init; }
	}

	// A modifier: a source (a pedal, LFO, envelope...) driving one block setting between Minimum and Maximum (percent of
	// the setting's travel), with its curve points (percent), on all channels or one.
	public sealed record ToneDesignModifier(string Source, string BlockType, int Instance, string Parameter)
	{
		public double Minimum { get; init; }
		public double Maximum { get; init; } = 100;
		public double Start { get; init; }
		public double Mid { get; init; } = 50;
		public double End { get; init; } = 100;
		// "All", or the one channel (A-D) the modifier applies to.
		public string Channel { get; init; } = "All";
	}

	public sealed record ToneDesignGridCell(int Row, int Column);

	public sealed record ToneDesignPreservedBlock(string BlockType, int Instance, int Row, int Column);

	public sealed record ToneDesignEvidenceConclusion(
		string Claim,
		string Classification,
		string Confidence,
		string SourceUrl,
		string SourceDate,
		string Rationale);

	public static class ToneDesignChannels
	{
		public const string A = "A";

		public static IReadOnlyList<string> All { get; } = ["A", "B", "C", "D"];

		public static bool IsSupported(string? channel) =>
			channel is not null && All.Contains(channel, StringComparer.Ordinal);

		public static string Label(string blockType, string channel) =>
			channel == A ? blockType : $"{blockType} channel {channel}";
	}

	public sealed record ToneDesignBlockProposal(
		string BlockType,
		int Instance,
		int Row,
		int Column,
		IReadOnlyDictionary<string, string> Parameters)
	{
		public string Rationale { get; init; } = string.Empty;
		public ToneDesignModelSelection? ModelSelection { get; init; }
		// Channels B-D only. Channel A stays in Parameters and ModelSelection, so a single-channel proposal (including
		// every proposal stored before channels existed) is simply a block without additional channels.
		public IReadOnlyList<ToneDesignBlockChannel> Channels { get; init; } = [];
		// How many channels the block's record holds: an uploaded Looper, Send or Crossover keeps only channel A editable.
		public int MaximumChannels { get; init; } = ToneDesignChannels.All.Count;

		public IReadOnlyList<string> GetAuthoredChannels() =>
			[ToneDesignChannels.A, .. Channels.Select(channel => channel.Channel)];

		public bool HasChannel(string channel) => channel == ToneDesignChannels.A || FindChannel(channel) is not null;

		// An unauthored channel plays template values the proposal does not describe; channel A is the closest description.
		public IReadOnlyDictionary<string, string> GetParameters(string channel) =>
			channel == ToneDesignChannels.A ? Parameters : FindChannel(channel)?.Parameters ?? Parameters;

		public ToneDesignModelSelection? GetModelSelection(string channel) =>
			channel == ToneDesignChannels.A ? ModelSelection : FindChannel(channel) is { } found ? found.ModelSelection : ModelSelection;

		// A single-channel view of one channel, for checks written against a block's own Parameters and ModelSelection.
		public ToneDesignBlockProposal ForChannel(string channel) =>
			this with { Parameters = GetParameters(channel), ModelSelection = GetModelSelection(channel), Channels = [] };

		public ToneDesignBlockProposal WithChannel(
			string channel,
			IReadOnlyDictionary<string, string> parameters,
			ToneDesignModelSelection? selection)
		{
			if (channel == ToneDesignChannels.A)
				return this with { Parameters = parameters, ModelSelection = selection };
			if (!ToneDesignChannels.IsSupported(channel))
				throw new ArgumentException("A block channel must be A, B, C, or D.", nameof(channel));
			ToneDesignBlockChannel replacement = new(channel, parameters) { ModelSelection = selection };
			return this with
			{
				Channels = FindChannel(channel) is null
					? [.. Channels, replacement]
					: Channels.Select(existing => existing.Channel == channel ? replacement : existing).ToArray()
			};
		}

		// Applies a single-channel transformation to every authored channel; channel A's result supplies the block itself.
		public ToneDesignBlockProposal MapChannels(Func<ToneDesignBlockProposal, ToneDesignBlockProposal> map)
		{
			ArgumentNullException.ThrowIfNull(map);
			ToneDesignBlockProposal mapped = map(ForChannel(ToneDesignChannels.A));
			return mapped with
			{
				Channels = Channels.Select(channel =>
				{
					ToneDesignBlockProposal view = map(ForChannel(channel.Channel));
					return channel with { Parameters = view.Parameters, ModelSelection = view.ModelSelection };
				}).ToArray()
			};
		}

		private ToneDesignBlockChannel? FindChannel(string channel) =>
			Channels.FirstOrDefault(candidate => candidate.Channel == channel);
	}

	public sealed record ToneDesignBlockChannel(
		string Channel,
		IReadOnlyDictionary<string, string> Parameters)
	{
		public ToneDesignModelSelection? ModelSelection { get; init; }
	}

	public sealed record ToneDesignModelSelection(
		string Name,
		string Equipment,
		string EvidenceBasis,
		string SourceUrl,
		string Adaptation);

	public sealed record ToneDesignRoute(
		int SourceRow,
		int SourceColumn,
		int DestinationRow);

	public sealed record ToneDesignSceneProposal(
		string Name,
		string Purpose,
		IReadOnlyList<ToneDesignSceneBlockSetting> BlockSettings)
	{
		public string Role { get; init; } = string.Empty;

		public IReadOnlyList<string> Sections { get; init; } = [];
	}

	public sealed record ToneDesignSceneBlockSetting(
		string BlockType,
		int Instance,
		string Channel,
		bool IsBypassed);

	public static class ToneDesignSceneChannels
	{
		// The channel whose authored values a scene actually plays for a block; unauthored channels are described by A.
		public static string Effective(ToneDesignSceneProposal scene, ToneDesignBlockProposal block)
		{
			ArgumentNullException.ThrowIfNull(scene);
			ArgumentNullException.ThrowIfNull(block);
			ToneDesignSceneBlockSetting? setting = Find(scene, block.BlockType, block.Instance);
			return setting is not null && block.HasChannel(setting.Channel) ? setting.Channel : ToneDesignChannels.A;
		}

		public static ToneDesignSceneBlockSetting? Find(ToneDesignSceneProposal scene, string blockType, int instance) =>
			scene.BlockSettings.FirstOrDefault(setting =>
				setting.BlockType.Equals(blockType, StringComparison.OrdinalIgnoreCase) && setting.Instance == instance);

		public static IReadOnlyList<ToneDesignSceneProposal> Using(
			ToneDesignProposal proposal,
			ToneDesignBlockProposal block,
			string channel) =>
			proposal.Scenes.Where(scene => Find(scene, block.BlockType, block.Instance) is { IsBypassed: false } setting &&
				setting.Channel == channel).ToArray();
	}
}
