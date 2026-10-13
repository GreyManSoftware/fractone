using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9ParameterDescriptor(
		string Family,
		int ParameterId,
		string Name,
		string? DisplayLabel,
		string Unit,
		double? DisplayMinimum,
		double? DisplayMaximum,
		bool UsesLogarithmicScaling,
		bool UsesDirectBulkValue,
		int? EnumerationCount,
		int? TypeCode)
	{
		public bool IsWriteVerified => !string.Equals(Unit, "unverified", StringComparison.OrdinalIgnoreCase);
		public bool IsDiscrete => string.Equals(Unit, "enum", StringComparison.OrdinalIgnoreCase);
		public bool IsContinuousSerializedControl =>
			!IsDiscrete &&
			!UsesDirectBulkValue &&
			!string.Equals(Unit, "count", StringComparison.OrdinalIgnoreCase);
	}

	public sealed record Fm9BlockDescriptor(
		string Name,
		string GroupCode,
		int FirstEffectId,
		int Instances);

	public sealed class Fm9Firmware12Catalog
	{
		public const string FirmwareVersion = "12";

		private static readonly IReadOnlyList<Fm9BlockDescriptor> Blocks =
		[
			new("Input", "IN", 37, 5),
			new("Output", "OUT", 42, 4),
			new("Compressor", "CMP", 46, 4),
			new("Graphic EQ", "GEQ", 50, 4),
			new("Parametric EQ", "PEQ", 54, 4),
			new("Amp", "AMP", 58, 4),
			new("Cab", "CAB", 62, 4),
			new("Reverb", "REV", 66, 4),
			new("Delay", "DLY", 70, 4),
			new("Multitap Delay", "MTD", 74, 4),
			new("Chorus", "CHO", 78, 4),
			new("Flanger", "FLG", 82, 4),
			new("Rotary", "ROT", 86, 4),
			new("Phaser", "PHA", 90, 4),
			new("Wah", "WAH", 94, 4),
			new("Formant", "FRM", 98, 4),
			new("Volume/Pan", "VOL", 102, 4),
			new("Pan/Tremolo", "PTR", 106, 4),
			new("Pitch", "PIT", 110, 4),
			new("Filter", "FIL", 114, 4),
			new("Drive", "FUZ", 118, 4),
			new("Enhancer", "ENH", 122, 4),
			new("Mixer", "MIX", 126, 4),
			new("Synth", "SYN", 130, 4),
			new("Crossover", "XOV", 142, 4),
			new("Gate/Expander", "GAT", 146, 4),
			new("Ring Modulator", "RNG", 150, 4),
			new("Multiband Compressor", "MBC", 154, 4),
			new("Ten-Tap Delay", "TTD", 158, 4),
			new("Resonator", "RES", 162, 4),
			new("Looper", "LPR", 166, 4),
			new("Plex Delay", "PLX", 178, 4),
			new("Send", "SND", 182, 4),
			new("Return", "RTN", 186, 4),
			new("Multiplexer", "MUX", 191, 4)
		];

		private readonly IReadOnlyDictionary<string, Fm9ParameterDescriptor> _parameters;
		private readonly IReadOnlyDictionary<(int Bank, int Ordinal), string> _cabNames;
		private readonly IReadOnlyDictionary<(string ParameterName, int Ordinal), string>
			_enumerationNames;

		private Fm9Firmware12Catalog(
			IReadOnlyDictionary<string, Fm9ParameterDescriptor> parameters,
			IReadOnlyDictionary<(int Bank, int Ordinal), string> cabNames,
			IReadOnlyDictionary<(string ParameterName, int Ordinal), string> enumerationNames)
		{
			_parameters = parameters;
			_cabNames = cabNames;
			_enumerationNames = enumerationNames;
		}

		public IReadOnlyList<Fm9BlockDescriptor> BlockDescriptors => Blocks;

		public IReadOnlyList<Fm9ParameterDescriptor> ParameterDescriptors =>
			_parameters.Values
				.OrderBy(parameter => parameter.Family, StringComparer.Ordinal)
				.ThenBy(parameter => parameter.ParameterId)
				.ToArray();

		public IReadOnlyList<Fm9ParameterDescriptor> GetParametersForFamily(string family)
		{
			if (string.IsNullOrWhiteSpace(family))
			{
				throw new ArgumentException("A parameter family is required.", nameof(family));
			}

			return ParameterDescriptors
				.Where(parameter => string.Equals(
					parameter.Family,
					family,
					StringComparison.OrdinalIgnoreCase))
				.ToArray();
		}

		public Fm9ParameterDescriptor GetParameterById(string family, int parameterId)
		{
			return GetParametersForFamily(family).SingleOrDefault(parameter =>
				parameter.ParameterId == parameterId)
				?? throw new KeyNotFoundException(
					$"The firmware-{FirmwareVersion} bundled catalog does not contain parameter ID {parameterId} in '{family}'.");
		}

		public static async Task<Fm9Firmware12Catalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9Firmware12Catalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-community-beta.json")
				?? throw new InvalidOperationException("The bundled FM9 firmware-12 catalog is unavailable.");

			return await LoadAsync(stream, cancellationToken);
		}

		public static async Task<Fm9Firmware12Catalog> LoadAsync(
			Stream stream,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(stream);
			using JsonDocument document = await JsonDocument.ParseAsync(
				stream,
				cancellationToken: cancellationToken);
			JsonElement root = document.RootElement;

			if (!root.TryGetProperty("device", out JsonElement device) ||
				!string.Equals(device.GetString(), "fm9", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("The bundled catalog is not an FM9 catalog.");
			}

			if (!root.TryGetProperty("data", out JsonElement data) ||
				!data.TryGetProperty("FM9_PARAMS", out JsonElement parameters) ||
				parameters.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("The bundled FM9 catalog does not contain parameter definitions.");
			}

			Dictionary<string, Fm9ParameterDescriptor> descriptors = new(
				StringComparer.OrdinalIgnoreCase);
			JsonElement ranges = data.TryGetProperty("FM9_RANGES", out JsonElement rangeData)
				? rangeData
				: default;

			foreach (JsonElement parameter in parameters.EnumerateArray())
			{
				string family = ReadRequiredString(parameter, "family");
				string name = ReadRequiredString(parameter, "name");
				string? displayLabel = ReadOptionalString(parameter, "displayLabel");
				string unit = ReadRequiredString(parameter, "unit");

				if (!parameter.TryGetProperty("paramId", out JsonElement parameterId) ||
					!parameterId.TryGetInt32(out int id) ||
					id < 0)
				{
					throw new InvalidDataException($"The FM9 parameter '{name}' has an invalid parameter ID.");
				}

				(double? displayMinimum, double? displayMaximum) = ReadDisplayRange(ranges, family, id);
				int? enumerationCount = ReadEnumerationCount(ranges, family, id);
				int? typeCode = ReadTypeCode(ranges, family, id);
				Fm9ParameterDescriptor descriptor = new(
					family,
					id,
					name,
					displayLabel,
					unit,
					displayMinimum,
					displayMaximum,
					UsesLogarithmicScaling(family, name),
					UsesDirectBulkValue(family, name),
					enumerationCount,
					typeCode);

				if (!descriptors.TryAdd(CreateParameterKey(family, name), descriptor))
				{
					throw new InvalidDataException($"The bundled catalog contains a duplicate parameter '{family}.{name}'.");
				}
			}

			return new Fm9Firmware12Catalog(
				descriptors,
				ReadCabNames(data),
				ReadEnumerationNames(data));
		}

		public string ResolveCabName(int bank, int ordinal)
		{
			return TryResolveCabName(bank, ordinal, out string? name)
				? name
				: throw new KeyNotFoundException(
					$"The firmware-{FirmwareVersion} bundled catalog does not contain Cab bank {bank}, ordinal {ordinal}.");
		}

		public bool TryResolveCabName(int bank, int ordinal, [NotNullWhen(true)] out string? name) =>
			_cabNames.TryGetValue((bank, ordinal), out name);

		public bool TryResolveEnumerationName(
			string parameterName,
			int ordinal,
			out string? name)
		{
			if (string.IsNullOrWhiteSpace(parameterName))
			{
				throw new ArgumentException(
					"An FM9 parameter name is required.",
					nameof(parameterName));
			}

			return _enumerationNames.TryGetValue(
				(parameterName, ordinal),
				out name);
		}

		public int ResolveEffectId(string blockName, int instance = 1)
		{
			if (string.IsNullOrWhiteSpace(blockName))
			{
				throw new ArgumentException("A block name is required.", nameof(blockName));
			}

			Fm9BlockDescriptor? block = Blocks.SingleOrDefault(candidate =>
				string.Equals(candidate.Name, blockName, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(candidate.GroupCode, blockName, StringComparison.OrdinalIgnoreCase));

			if (block is null)
			{
				throw new KeyNotFoundException(
					$"The firmware-{FirmwareVersion} bundled catalog does not expose a verified FM9 block ID for '{blockName}'.");
			}

			if (instance < 1 || instance > block.Instances)
			{
				throw new ArgumentOutOfRangeException(
					nameof(instance),
					$"FM9 {block.Name} instances are numbered 1 through {block.Instances}.");
			}

			return block.FirstEffectId + instance - 1;
		}

		// The id the live grid read reports: the full effect id (Fm9GridLayout reads all 16 bits).
		public int ResolveGridBlockIdentifier(string blockName, int instance = 1)
		{
			return ResolveEffectId(blockName, instance);
		}

		public Fm9ParameterDescriptor ResolveWritableParameter(string family, string name)
		{
			if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(name))
			{
				throw new ArgumentException("A parameter family and name are required.");
			}

			if (!_parameters.TryGetValue(CreateParameterKey(family, name), out Fm9ParameterDescriptor? parameter))
			{
				throw new KeyNotFoundException(
					$"The firmware-{FirmwareVersion} bundled catalog does not contain '{family}.{name}'.");
			}

			if (!parameter.IsWriteVerified && !IsHardwareConfirmedWritable(family, name))
			{
				throw new InvalidOperationException(
					$"The parameter '{family}.{name}' is marked unverified and cannot be written.");
			}

			return parameter;
		}

		public float NormalizeContinuousDisplayValue(
			Fm9ParameterDescriptor parameter,
			double displayValue)
		{
			ArgumentNullException.ThrowIfNull(parameter);

			if (parameter.IsDiscrete)
			{
				throw new InvalidOperationException(
					$"The parameter '{parameter.Family}.{parameter.Name}' is discrete and requires an ordinal.");
			}

			(double minimum, double maximum) = GetDisplayRange(parameter);

			if (!double.IsFinite(displayValue) || displayValue < minimum || displayValue > maximum)
			{
				throw new ArgumentOutOfRangeException(
					nameof(displayValue),
					$"The parameter '{parameter.Family}.{parameter.Name}' accepts values from {minimum} to {maximum}.");
			}

			double normalized = parameter.UsesLogarithmicScaling
				? Math.Log10(displayValue / minimum) / Math.Log10(maximum / minimum)
				: (displayValue - minimum) / (maximum - minimum);

			return (float)normalized;
		}

		public int ValidateDiscreteOrdinal(
			Fm9ParameterDescriptor parameter,
			int ordinal)
		{
			ArgumentNullException.ThrowIfNull(parameter);

			if (!parameter.IsDiscrete)
			{
				throw new InvalidOperationException(
					$"The parameter '{parameter.Family}.{parameter.Name}' is continuous and requires a display value.");
			}

			(double minimum, double maximum) = GetDisplayRange(parameter);

			if (ordinal < minimum || ordinal > maximum)
			{
				throw new ArgumentOutOfRangeException(
					nameof(ordinal),
					$"The parameter '{parameter.Family}.{parameter.Name}' accepts ordinals from {minimum} to {maximum}.");
			}

			return ordinal;
		}

		public int ValidateDirectOrdinal(
			Fm9ParameterDescriptor parameter,
			int ordinal)
		{
			ArgumentNullException.ThrowIfNull(parameter);
			(double minimum, double maximum) = GetDisplayRange(parameter);

			if (ordinal < minimum || ordinal > maximum)
			{
				throw new ArgumentOutOfRangeException(
					nameof(ordinal),
					$"The parameter '{parameter.Family}.{parameter.Name}' accepts ordinals from {minimum} to {maximum}.");
			}

			return ordinal;
		}

		private static (double? Minimum, double? Maximum) ReadDisplayRange(
			JsonElement ranges,
			string family,
			int parameterId)
		{
			if (ranges.ValueKind != JsonValueKind.Object ||
				!ranges.TryGetProperty(family, out JsonElement familyRanges) ||
				!familyRanges.TryGetProperty(parameterId.ToString(), out JsonElement range) ||
				!range.TryGetProperty("displayMin", out JsonElement minimum) ||
				!range.TryGetProperty("displayMax", out JsonElement maximum) ||
				!minimum.TryGetDouble(out double minimumValue) ||
				!maximum.TryGetDouble(out double maximumValue) ||
				maximumValue <= minimumValue)
			{
				return (null, null);
			}

			return (minimumValue, maximumValue);
		}

		private static IReadOnlyDictionary<(int Bank, int Ordinal), string> ReadCabNames(
			JsonElement data)
		{
			if (!data.TryGetProperty("FM9_CAB_ROSTERS_BY_BANK", out JsonElement rosters) ||
				rosters.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException(
					"The bundled FM9 catalog does not contain bank-conditioned Cab names.");
			}

			Dictionary<(int Bank, int Ordinal), string> names = [];

			foreach (JsonProperty bankProperty in rosters.EnumerateObject())
			{
				if (!int.TryParse(bankProperty.Name, out int bank) ||
					bankProperty.Value.ValueKind != JsonValueKind.Object)
				{
					throw new InvalidDataException(
						"The bundled FM9 catalog contains an invalid Cab bank.");
				}

				foreach (JsonProperty cabProperty in bankProperty.Value.EnumerateObject())
				{
					if (!int.TryParse(cabProperty.Name, out int ordinal) ||
						cabProperty.Value.ValueKind != JsonValueKind.String ||
						string.IsNullOrWhiteSpace(cabProperty.Value.GetString()))
					{
						throw new InvalidDataException(
							$"The bundled FM9 catalog contains an invalid Cab entry in bank {bank}.");
					}

					names.Add((bank, ordinal), cabProperty.Value.GetString()!);
				}
			}

			return names;
		}

		private static IReadOnlyDictionary<(string ParameterName, int Ordinal), string>
			ReadEnumerationNames(JsonElement data)
		{
			if (!data.TryGetProperty("FM9_ENUM_OVERRIDES", out JsonElement overrides) ||
				overrides.ValueKind != JsonValueKind.Object)
			{
				return new Dictionary<(string ParameterName, int Ordinal), string>(
					new EnumerationNameKeyComparer());
			}

			Dictionary<(string ParameterName, int Ordinal), string> names =
				new(new EnumerationNameKeyComparer());

			foreach (JsonProperty parameterProperty in overrides.EnumerateObject())
			{
				if (parameterProperty.Value.ValueKind != JsonValueKind.Object)
				{
					throw new InvalidDataException(
						$"The bundled FM9 enumeration roster '{parameterProperty.Name}' is invalid.");
				}

				foreach (JsonProperty ordinalProperty in parameterProperty.Value.EnumerateObject())
				{
					if (!int.TryParse(ordinalProperty.Name, out int ordinal) ||
						ordinal < 0 ||
						ordinalProperty.Value.ValueKind != JsonValueKind.String ||
						string.IsNullOrWhiteSpace(ordinalProperty.Value.GetString()) ||
						!names.TryAdd(
							(parameterProperty.Name, ordinal),
							ordinalProperty.Value.GetString()!))
					{
						throw new InvalidDataException(
							$"The bundled FM9 enumeration roster '{parameterProperty.Name}' contains an invalid ordinal.");
					}
				}
			}

			return names;
		}

		private static int? ReadEnumerationCount(
			JsonElement ranges,
			string family,
			int parameterId)
		{
			if (ranges.ValueKind != JsonValueKind.Object ||
				!ranges.TryGetProperty(family, out JsonElement familyRanges) ||
				!familyRanges.TryGetProperty(parameterId.ToString(), out JsonElement range) ||
				!range.TryGetProperty("enumCount", out JsonElement count) ||
				!count.TryGetInt32(out int value) ||
				value <= 0)
			{
				return null;
			}

			return value;
		}

		private static int? ReadTypeCode(
			JsonElement ranges,
			string family,
			int parameterId)
		{
			if (ranges.ValueKind != JsonValueKind.Object ||
				!ranges.TryGetProperty(family, out JsonElement familyRanges) ||
				!familyRanges.TryGetProperty(parameterId.ToString(), out JsonElement range) ||
				!range.TryGetProperty("typecode", out JsonElement typeCode) ||
				!typeCode.TryGetInt32(out int value))
			{
				return null;
			}

			return value;
		}

		private static (double Minimum, double Maximum) GetDisplayRange(
			Fm9ParameterDescriptor parameter)
		{
			if (parameter.DisplayMinimum is not double minimum ||
				parameter.DisplayMaximum is not double maximum)
			{
				throw new InvalidOperationException(
					$"The parameter '{parameter.Family}.{parameter.Name}' has no verified display range.");
			}

			return (minimum, maximum);
		}

		private sealed class EnumerationNameKeyComparer :
			IEqualityComparer<(string ParameterName, int Ordinal)>
		{
			public bool Equals(
				(string ParameterName, int Ordinal) left,
				(string ParameterName, int Ordinal) right)
			{
				return left.Ordinal == right.Ordinal &&
					string.Equals(
						left.ParameterName,
						right.ParameterName,
						StringComparison.OrdinalIgnoreCase);
			}

			public int GetHashCode((string ParameterName, int Ordinal) value)
			{
				return HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(value.ParameterName),
					value.Ordinal);
			}
		}

		private static bool UsesLogarithmicScaling(string family, string name)
		{
			return (family, name) switch
			{
				// Input Trim is a multiplicative gain: hardware unity is the 32,767 midpoint.
				("DISTORT", "DISTORT_TRIM" or "DISTORT_HPFREQ" or "DISTORT_LPFREQ") => true,
				("CABINET", "CABINET_LOCUT" or "CABINET_HICUT" or
					"CABINET_LOCUT1" or "CABINET_HICUT1" or
					"CABINET_LOCUT2" or "CABINET_HICUT2") => true,
				("GATE", "GATE_ATTACK" or "GATE_HOLD" or "GATE_RELEASE") => true,
				("PEQ", "PEQ_FREQ1" or "PEQ_FREQ2" or "PEQ_FREQ3" or "PEQ_FREQ4" or "PEQ_FREQ5") => true,
				("REVERB", "REVERB_LOWCUT" or "REVERB_HICUT") => true,
				// Delay Time is linear (typecode 0x430); only its cuts are log.
				("DELAY", "DELAY_LOCUT" or "DELAY_HICUT") => true,
				_ => false
			};
		}

		private static bool UsesDirectBulkValue(string family, string name)
		{
			return (family, name) switch
			{
				("CABINET", "CABINET_TYPE1" or "CABINET_TYPE2" or "CABINET_TYPE3" or "CABINET_TYPE4") => true,
				("FUZZ", "FUZZ_TYPE") => true,
				_ => false
			};
		}

		private static bool IsHardwareConfirmedWritable(string family, string name)
		{
			return string.Equals(family, "FUZZ", StringComparison.OrdinalIgnoreCase) &&
				string.Equals(name, "FUZZ_TYPE", StringComparison.OrdinalIgnoreCase);
		}

		private static string ReadRequiredString(JsonElement parent, string propertyName)
		{
			if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
				property.ValueKind != JsonValueKind.String ||
				string.IsNullOrWhiteSpace(property.GetString()))
			{
				throw new InvalidDataException($"The FM9 catalog requires a non-empty '{propertyName}' string.");
			}

			return property.GetString()!;
		}

		private static string? ReadOptionalString(
			JsonElement parent,
			string propertyName)
		{
			return parent.TryGetProperty(propertyName, out JsonElement property) &&
				property.ValueKind == JsonValueKind.String &&
				!string.IsNullOrWhiteSpace(property.GetString())
					? property.GetString()
					: null;
		}

		private static string CreateParameterKey(string family, string name)
		{
			return $"{family}.{name}";
		}
	}
}
