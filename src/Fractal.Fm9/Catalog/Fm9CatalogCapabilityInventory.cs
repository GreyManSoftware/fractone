using Fractal.Companion.Protocol;

namespace Fractal.Companion.Catalog
{
	[Flags]
	public enum Fm9ParameterCapability
	{
		None = 0,
		Cataloged = 1,
		Modeled = 2,
		Serializable = 4,
		Validated = 8
	}

	public sealed record Fm9BlockCapability(
		Fm9BlockDescriptor Block,
		int Instance,
		string ParameterFamily,
		IReadOnlyList<Fm9ParameterCapabilityEntry> Parameters);

	public sealed record Fm9ParameterCapabilityEntry(
		Fm9ParameterDescriptor Parameter,
		Fm9ParameterCapability Capability)
	{
		public bool IsSerializable =>
			(Capability & Fm9ParameterCapability.Serializable) != 0;

		public bool IsValidated =>
			(Capability & Fm9ParameterCapability.Validated) != 0;
	}

	public sealed record Fm9UnassignedParameterFamilyCapability(
		string Family,
		IReadOnlyList<Fm9ParameterCapabilityEntry> Parameters);

	public sealed class Fm9CatalogCapabilityInventory
	{
		private static readonly Lazy<Fm9HardwareParameterCatalog> HardwareParameters =
			new(Fm9HardwareParameterCatalog.LoadBundled);

		private static readonly IReadOnlyDictionary<string, string> ParameterFamilies =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["Input"] = "INPUT",
				["Output"] = "OUTPUT",
				["Compressor"] = "COMP",
				["Graphic EQ"] = "GEQ",
				["Parametric EQ"] = "PEQ",
				["Amp"] = "DISTORT",
				["Cab"] = "CABINET",
				["Reverb"] = "REVERB",
				["Delay"] = "DELAY",
				["Multitap Delay"] = "MULTITAP",
				["Chorus"] = "CHORUS",
				["Flanger"] = "FLANGER",
				["Rotary"] = "ROTARY",
				["Phaser"] = "PHASER",
				["Wah"] = "WAH",
				["Formant"] = "FORMANT",
				["Volume/Pan"] = "VOLUME",
				["Pan/Tremolo"] = "TREMOLO",
				["Pitch"] = "PITCH",
				["Filter"] = "FILTER",
				["Drive"] = "FUZZ",
				["Enhancer"] = "ENHANCER",
				["Mixer"] = "MIXER",
				["Synth"] = "SYNTH",
				["Crossover"] = "CROSSOVER",
				["Gate/Expander"] = "GATE",
				["Ring Modulator"] = "RINGMOD",
				["Multiband Compressor"] = "MULTICOMP",
				["Ten-Tap Delay"] = "TENTAP",
				["Resonator"] = "RESONATOR",
				["Looper"] = "LOOPER",
				["Plex Delay"] = "PLEX",
				["Send"] = "FDBKSEND",
				["Return"] = "FDBKRET",
				["Multiplexer"] = "MULTIPLEXER"
			};

		private static readonly IReadOnlySet<(string Block, int ParameterId)> ValidatedParameters =
			new HashSet<(string Block, int ParameterId)>
			{
				("Amp", 10),
				("Amp", 16),
				("Amp", 17),
				("Cab", 0),
				("Cab", 4),
				("Cab", 62),
				("Cab", 66),
				("Reverb", 11)
			};

		private static readonly IReadOnlySet<(string BlockType, string ParameterName)>
			ProvenInvalidProfileMappings =
				new HashSet<(string BlockType, string ParameterName)>(
					new ProfileMappingKeyComparer())
				{
					("Amp", "DISTORT_DYNPRES"),
					("Amp", "DISTORT_DYNDEPTH"),
					("Amp", "DISTORT_RESOLUTION"),
					("Amp", "DISTORT_VCCMON"),
					("Amp", "DISTORT_INDYNMON"),
					("Amp", "DISTORT_VPLATEMON"),
					("Drive", "FUZZ_BITREDUCE"),
					("Reverb", "REVERB_SHIFT1"),
					("Reverb", "REVERB_SHIFT2"),
					("Reverb", "REVERB_TONETYPE")
				};

		private readonly IReadOnlyList<Fm9BlockCapability> _blocks;
		private readonly IReadOnlyList<Fm9UnassignedParameterFamilyCapability> _unassignedFamilies;

		private Fm9CatalogCapabilityInventory(
			IReadOnlyList<Fm9BlockCapability> blocks,
			IReadOnlyList<Fm9UnassignedParameterFamilyCapability> unassignedFamilies)
		{
			_blocks = blocks;
			_unassignedFamilies = unassignedFamilies;
		}

		public IReadOnlyList<Fm9BlockCapability> Blocks => _blocks;
		public IReadOnlyList<Fm9UnassignedParameterFamilyCapability> UnassignedFamilies =>
			_unassignedFamilies;

		public static Fm9CatalogCapabilityInventory Create(
			Fm9Firmware12Catalog catalog,
			Fm9BlockRecordTemplateProfile? serializationProfile = null,
			Fm9HardwareParameterCatalog? hardwareParameters = null,
			Fm9HardwareDirectParameterCatalog? hardwareDirectParameters = null)
		{
			ArgumentNullException.ThrowIfNull(catalog);
			Dictionary<(string Block, int Instance, int ParameterId), Fm9TemplateParameterDefinition>
				serializable = GetSerializableParameters(catalog, serializationProfile);
			List<Fm9BlockCapability> blocks = [];
			HashSet<string> assignedFamilies = new(StringComparer.OrdinalIgnoreCase);

			foreach (Fm9BlockDescriptor block in catalog.BlockDescriptors)
			{
				if (!ParameterFamilies.TryGetValue(block.Name, out string? family))
				{
					throw new InvalidDataException(
						$"FM9 block '{block.Name}' has no catalog parameter-family mapping.");
				}

				IReadOnlyList<Fm9ParameterDescriptor> parameters = catalog.GetParametersForFamily(family);
				assignedFamilies.Add(family);

				for (int instance = 1; instance <= block.Instances; instance++)
				{
					List<Fm9ParameterCapabilityEntry> entries = [];

					foreach (Fm9ParameterDescriptor parameter in parameters)
					{
						Fm9ParameterCapability capability =
							Fm9ParameterCapability.Cataloged | Fm9ParameterCapability.Modeled;
						bool hardwareAccepted = hardwareParameters?.IsAcceptedContinuous(
							block.Name,
							instance,
							parameter.ParameterId) == true;
						bool hardwareDirectAccepted =
							hardwareDirectParameters?.IsAcceptedDirect(
								block.Name,
								instance,
								parameter.ParameterId) == true;

						if (serializable.ContainsKey((block.Name, instance, parameter.ParameterId)) ||
							IsValidatedAmpSerialization(block, instance, parameter) ||
							IsValidatedCabSerialization(block, instance, parameter) ||
							hardwareAccepted)
						{
							capability |= Fm9ParameterCapability.Serializable;
						}

						if (ValidatedParameters.Contains((block.Name, parameter.ParameterId)) ||
							hardwareAccepted ||
							hardwareDirectAccepted)
						{
							capability |= Fm9ParameterCapability.Validated;
						}

						entries.Add(new Fm9ParameterCapabilityEntry(parameter, capability));
					}

					blocks.Add(new Fm9BlockCapability(block, instance, family, entries));
				}
			}

			IReadOnlyList<Fm9UnassignedParameterFamilyCapability> unassignedFamilies =
				catalog.ParameterDescriptors
					.Where(parameter => !assignedFamilies.Contains(parameter.Family))
					.GroupBy(parameter => parameter.Family, StringComparer.OrdinalIgnoreCase)
					.OrderBy(group => group.Key, StringComparer.Ordinal)
					.Select(group => new Fm9UnassignedParameterFamilyCapability(
						group.Key,
						group.Select(parameter => new Fm9ParameterCapabilityEntry(
							parameter,
							Fm9ParameterCapability.Cataloged | Fm9ParameterCapability.Modeled))
							.ToArray()))
					.ToArray();

			return new Fm9CatalogCapabilityInventory(blocks, unassignedFamilies);
		}

		public static string GetParameterFamily(string blockName)
		{
			return ParameterFamilies.TryGetValue(blockName, out string? family)
				? family
				: throw new KeyNotFoundException(
					$"FM9 block '{blockName}' has no catalog parameter-family mapping.");
		}

		public static bool IsEligibleForProfilePromotion(
			string blockType,
			Fm9ParameterDescriptor parameter)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(blockType);
			ArgumentNullException.ThrowIfNull(parameter);

			return parameter.DisplayMinimum is not null &&
					parameter.DisplayMaximum is not null &&
					parameter.IsWriteVerified &&
					parameter.IsContinuousSerializedControl &&
					!IsProvenInvalidProfileMapping(blockType, parameter.Name) &&
					!HardwareParameters.Value.IsRejectedContinuousCandidate(
						blockType,
						1,
						parameter.ParameterId);
		}

		public static bool IsProvenInvalidProfileMapping(string blockType, string parameterName)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(blockType);
			ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

			return ProvenInvalidProfileMappings.Contains((blockType, parameterName));
		}

		private static Dictionary<(string Block, int Instance, int ParameterId), Fm9TemplateParameterDefinition>
			GetSerializableParameters(
				Fm9Firmware12Catalog catalog,
				Fm9BlockRecordTemplateProfile? profile)
		{
			Dictionary<(string Block, int Instance, int ParameterId), Fm9TemplateParameterDefinition>
				parameters = [];

			if (profile is null)
			{
				return parameters;
			}

			foreach (Fm9TemplateBlockDefinition definition in profile.Definitions)
			{
				string family = GetParameterFamily(definition.BlockType);

				foreach (Fm9TemplateParameterDefinition parameter in definition.Parameters)
				{
					Fm9ParameterDescriptor catalogParameter = catalog.GetParameterById(
						family,
						parameter.ParameterId);

					if (IsEligibleForProfilePromotion(definition.BlockType, catalogParameter))
					{
						parameters.Add((definition.BlockType, definition.Instance, parameter.ParameterId), parameter);
					}
				}
			}

			return parameters;
		}

		private sealed class ProfileMappingKeyComparer : IEqualityComparer<(string BlockType, string ParameterName)>
		{
			public bool Equals(
				(string BlockType, string ParameterName) x,
				(string BlockType, string ParameterName) y)
			{
				return string.Equals(x.BlockType, y.BlockType, StringComparison.OrdinalIgnoreCase) &&
					string.Equals(x.ParameterName, y.ParameterName, StringComparison.OrdinalIgnoreCase);
			}

			public int GetHashCode((string BlockType, string ParameterName) obj)
			{
				return HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BlockType),
					StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ParameterName));
			}
		}

		private static bool IsValidatedCabSerialization(
			Fm9BlockDescriptor block,
			int instance,
			Fm9ParameterDescriptor parameter)
		{
			return instance == 1 &&
				ValidatedParameters.Contains((block.Name, parameter.ParameterId));
		}

		private static bool IsValidatedAmpSerialization(
			Fm9BlockDescriptor block,
			int instance,
			Fm9ParameterDescriptor parameter)
		{
			return string.Equals(block.Name, "Amp", StringComparison.OrdinalIgnoreCase) &&
				instance == 1 &&
				parameter.ParameterId == 10;
		}
	}
}
