namespace Fractal.Companion.Protocol
{
	public enum Fm9AmpChannel
	{
		A,
		B,
		C,
		D
	}

	public enum Fm9AmpKnob
	{
		// Output volume in dB (-80..20), stored per channel like every other parameter.
		Level = 1,
		Gain = 11,
		Bass = 12,
		Mid = 13,
		Treble = 14,
		MasterVolume = 15
	}

	public enum Fm9AmpOutputEqType
	{
		Graphic8Band = 0,
		Graphic10Band = 1,
		Mark5Band = 2,
		Parametric = 3
	}

	public enum Fm9AmpOutputEqLocation
	{
		Output = 0,
		PrePowerAmp = 1
	}

	public sealed record Fm9AmpOutputEqSettings(
		double Band80Hz,
		double Band240Hz,
		double Band750Hz,
		double Band2200Hz,
		double Band6600Hz,
		Fm9AmpOutputEqType Type,
		Fm9AmpOutputEqLocation Location,
		bool Enabled);

	public sealed record Fm9CabSettings(
		int Bank,
		int TypeOrdinal,
		double LowCut,
		double HighCut);

	// One Amp channel's model and Output EQ; null leaves that part of the channel as it is.
	public sealed record Fm9AmpChannelSettings(
		int Channel,
		int? TypeOrdinal,
		Fm9AmpOutputEqSettings? OutputEq);

	public static class Fm9PresetBodyParameterEditor
	{
		private const int BlockHeaderWordCount = 23;
		private const int FirstStoredParameterId = 6;
		private const int AmpParameterColumns = 147;
		private const int AmpChannelCount = 4;
		private const int AmpTypeParameterId = 10;
		private const int AmpOutputEqFirstBandParameterId = 61;
		private const int AmpOutputEqTypeParameterId = 98;
		private const int AmpOutputEqLocationParameterId = 116;
		private const int AmpOutputEqEnabledParameterId = 132;
		// EMPTY.syx Amp A stores documented DISTORT_TYPE 314 at column 4.
		private const ushort RideBaselineAmpType = 314;
		private const int CabParameterColumns = 106;
		private const int CabChannelCount = 4;
		private const int CabBankParameterId = 0;
		private const int CabTypeParameterId = 4;
		private const int CabLowCutParameterId = 62;
		private const int CabHighCutParameterId = 66;
		private const int CabFactory1Bank = 0;
		private const int CabFactory2Bank = 1;
		private const int CabLegacyBank = 3;
		// EMPTY.syx is hardware-confirmed as Bank 1 / Type 1018 / 80 Hz / 9 kHz.
		private const ushort RideBaselineCabBank = 1;
		private const ushort RideBaselineCabType = 1018;
		private const ushort RideBaselineCabLowCut = 39455;
		private const ushort RideBaselineCabHighCut = 42808;
		private const int ReverbTimeParameterId = 11;
		private const double ReverbTimeMinimumSeconds = 0.1;
		private const double ReverbTimeMaximumSeconds = 100;
		public const ushort ContinuousWireMaximum = 65_534;

		public static ushort GetAmpKnobRaw(
			Fm9PresetBody source,
			Fm9AmpChannel channel,
			Fm9AmpKnob knob)
		{
			ArgumentNullException.ThrowIfNull(source);
			Fm9PresetBlockRecord record = FindRideBaselineAmpRecord(source);
			return ReadUInt16(source.Bytes.Span, GetParameterOffset(record, channel, knob));
		}

		public static ushort GetAmpModel(Fm9PresetBody source, int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			return GetAmpModel(source, FindAmpRecord(source), channel);
		}

		// Any Amp record (either instance, any preset layout), addressed directly.
		public static ushort GetAmpModel(Fm9PresetBody source, Fm9PresetBlockRecord record, int channel)
		{
			ArgumentNullException.ThrowIfNull(source);
			EnsureRecordShape(source, record, AmpParameterColumns, AmpChannelCount, "Amp");
			return ReadParameter(source.Bytes.Span, record, channel, AmpTypeParameterId);
		}

		public static Fm9AmpOutputEqSettings GetAmpOutputEq(Fm9PresetBody source, int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			return GetAmpOutputEq(source, FindAmpRecord(source), channel);
		}

		public static Fm9AmpOutputEqSettings GetAmpOutputEq(Fm9PresetBody source, Fm9PresetBlockRecord record, int channel)
		{
			ArgumentNullException.ThrowIfNull(source);
			EnsureRecordShape(source, record, AmpParameterColumns, AmpChannelCount, "Amp");

			return new Fm9AmpOutputEqSettings(
				DenormalizeLinear(ReadParameter(source.Bytes.Span, record, channel, 61), -12, 12),
				DenormalizeLinear(ReadParameter(source.Bytes.Span, record, channel, 62), -12, 12),
				DenormalizeLinear(ReadParameter(source.Bytes.Span, record, channel, 63), -12, 12),
				DenormalizeLinear(ReadParameter(source.Bytes.Span, record, channel, 64), -12, 12),
				DenormalizeLinear(ReadParameter(source.Bytes.Span, record, channel, 65), -12, 12),
				(Fm9AmpOutputEqType)ReadParameter(source.Bytes.Span, record, channel, AmpOutputEqTypeParameterId),
				(Fm9AmpOutputEqLocation)ReadParameter(source.Bytes.Span, record, channel, AmpOutputEqLocationParameterId),
				ReadParameter(source.Bytes.Span, record, channel, AmpOutputEqEnabledParameterId) == 1);
		}

		public static byte[] WithAmpModel(Fm9PresetBody source, int typeOrdinal)
		{
			return WithAmpChannelASettings(source, typeOrdinal, null);
		}

		public static byte[] WithAmpOutputEq(
			Fm9PresetBody source,
			Fm9AmpOutputEqSettings settings)
		{
			ArgumentNullException.ThrowIfNull(settings);
			return WithAmpChannelASettings(source, null, settings);
		}

		public static byte[] WithAmpChannelASettings(
			Fm9PresetBody source,
			int? typeOrdinal,
			Fm9AmpOutputEqSettings? outputEq)
		{
			return WithAmpChannelSettings(source, [new Fm9AmpChannelSettings(0, typeOrdinal, outputEq)]);
		}

		// Writes every requested channel in one pass: the Ride-baseline check reads channel A's model, which a write changes.
		public static byte[] WithAmpChannelSettings(
			Fm9PresetBody source,
			IReadOnlyList<Fm9AmpChannelSettings> channels)
		{
			ArgumentNullException.ThrowIfNull(source);
			return WithAmpChannelSettings(source, FindRideBaselineAmpRecord(source), channels);
		}

		public static byte[] WithAmpChannelSettings(
			Fm9PresetBody source,
			Fm9PresetBlockRecord record,
			IReadOnlyList<Fm9AmpChannelSettings> channels)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(channels);
			EnsureRecordShape(source, record, AmpParameterColumns, AmpChannelCount, "Amp");

			foreach (Fm9AmpChannelSettings channel in channels)
			{
				if (channel.Channel is < 0 or >= AmpChannelCount ||
					channels.Count(other => other.Channel == channel.Channel) != 1)
				{
					throw new ArgumentOutOfRangeException(nameof(channels), "Each Amp channel must be A-D and appear once.");
				}

				if (channel.TypeOrdinal.HasValue && channel.TypeOrdinal.Value is < 0 or > Fractal.Application.Fm9QualifiedModelCatalog.MaximumAmpModel)
				{
					throw new ArgumentOutOfRangeException(
						nameof(channels),
						$"An FM9 firmware-12 Amp model ordinal must be from 0 through {Fractal.Application.Fm9QualifiedModelCatalog.MaximumAmpModel}.");
				}

				if (channel.OutputEq is not null)
				{
					ValidateAmpOutputEq(channel.OutputEq);
				}
			}

			byte[] bytes = source.Bytes.ToArray();

			foreach ((int channel, int? typeOrdinal, Fm9AmpOutputEqSettings? outputEq) in channels)
			{
				if (typeOrdinal.HasValue)
				{
					WriteParameter(bytes, record, channel, AmpTypeParameterId, checked((ushort)typeOrdinal.Value));
				}

				if (outputEq is not null)
				{
					double[] bands =
					[
						outputEq.Band80Hz,
						outputEq.Band240Hz,
						outputEq.Band750Hz,
						outputEq.Band2200Hz,
						outputEq.Band6600Hz
					];

					for (int index = 0; index < bands.Length; index++)
					{
						WriteParameter(
							bytes,
							record,
							channel,
							AmpOutputEqFirstBandParameterId + index,
							NormalizeLinear(bands[index], -12, 12));
					}

					WriteParameter(bytes, record, channel, AmpOutputEqTypeParameterId, (ushort)outputEq.Type);
					WriteParameter(bytes, record, channel, AmpOutputEqLocationParameterId, (ushort)outputEq.Location);
					WriteParameter(bytes, record, channel, AmpOutputEqEnabledParameterId, outputEq.Enabled ? (ushort)1 : (ushort)0);
				}
			}

			return bytes;
		}

		public static byte[] WithAmpKnob(
			Fm9PresetBody source,
			Fm9AmpChannel channel,
			Fm9AmpKnob knob,
			double value)
		{
			ArgumentNullException.ThrowIfNull(source);

			if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 10)
			{
				throw new ArgumentOutOfRangeException(
					nameof(value),
					"An FM9 amp knob value must be between 0 and 10.");
			}

			Fm9PresetBlockRecord record = FindRideBaselineAmpRecord(source);
			byte[] bytes = source.Bytes.ToArray();
			ushort rawValue = checked((ushort)Math.Round(
				value / 10 * ContinuousWireMaximum,
				MidpointRounding.AwayFromZero));
			WriteUInt16(bytes, GetParameterOffset(record, channel, knob), rawValue);
			return bytes;
		}

		public static byte[] WithProfileParameter(
			Fm9PresetBody source,
			Fm9BlockRecordTemplateProfile profile,
			string blockType,
			int instance,
			string parameterName,
			double value,
			int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(profile);
			Fm9TemplateParameterDefinition parameter = profile.GetParameter(
				blockType,
				instance,
				parameterName);

			return WithRecordParameter(source, profile.GetRecordForExistingBlock(source, blockType, instance), blockType, instance,
				parameter, value, channel);
		}

		// Writes one parameter into a block addressed by its own record, so presets with blocks the Ride template does not
		// know (for example a user-added USB Input) can still be edited.
		public static byte[] WithRecordParameter(Fm9PresetBody source, Fm9PresetBlockRecord record, string blockType, int instance,
			Fm9TemplateParameterDefinition parameter, double value, int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(record);
			ArgumentNullException.ThrowIfNull(parameter);
			ValidateProfileValue(parameter, value, blockType, instance);
			ushort rawValue = parameter.IsDiscrete
				? checked((ushort)value)
				: IsReverbTime(blockType, parameter)
				? EncodeReverbTime(value)
				: IsAmpInputTrim(blockType, parameter)
					? checked((ushort)Math.Round(
						(float)(Math.Log10(value / 0.1) / 2) * ContinuousWireMaximum))
					: EncodeProfileValue(parameter, value);
			byte[] bytes = source.Bytes.ToArray();
			WriteParameter(bytes, record, channel, parameter.ParameterId, rawValue);
			return bytes;
		}

		public static void ValidateProfileValue(Fm9TemplateParameterDefinition parameter, string text, string blockType, int instance)
		{
			if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
				throw new InvalidDataException($"'{blockType} {instance}' {parameter.Name} must be an invariant numeric value.");
			ValidateProfileValue(parameter, value, blockType, instance);
		}

		private static void ValidateProfileValue(Fm9TemplateParameterDefinition parameter, double value, string blockType, int instance)
		{
			if (!double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum ||
				(parameter.IsDiscrete && value != Math.Truncate(value)))
			{
				throw new ArgumentOutOfRangeException(
					nameof(value),
					$"'{blockType} {instance}' {parameter.Name} must be from {parameter.Minimum} through {parameter.Maximum}.");
			}
		}

		public static ushort GetProfileParameterRaw(
			Fm9PresetBody source,
			Fm9BlockRecordTemplateProfile profile,
			string blockType,
			int instance,
			int parameterId,
			int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(profile);
			Fm9PresetBlockRecord record = profile.GetRecordForExistingBlock(source, blockType, instance);
			return ReadParameter(source.Bytes.Span, record, channel, parameterId);
		}

		public static double GetProfileParameter(
			Fm9PresetBody source,
			Fm9BlockRecordTemplateProfile profile,
			string blockType,
			int instance,
			string parameterName,
			int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(profile);
			Fm9TemplateParameterDefinition parameter = profile.GetParameter(
				blockType,
				instance,
				parameterName);
			Fm9PresetBlockRecord record = profile.GetRecordForExistingBlock(
				source,
				blockType,
				instance);
			ushort rawValue = ReadParameter(
				source.Bytes.Span,
				record,
				channel,
				parameter.ParameterId);
			if (parameter.IsDiscrete)
				return rawValue;

			if (IsReverbTime(blockType, parameter))
			{
				return ReverbTimeMinimumSeconds +
					(rawValue / (double)ContinuousWireMaximum *
						(ReverbTimeMaximumSeconds - ReverbTimeMinimumSeconds));
			}

			if (IsAmpInputTrim(blockType, parameter))
			{
				return 0.1 * Math.Pow(100, rawValue / (double)ContinuousWireMaximum);
			}

			double normalized = rawValue / (double)ContinuousWireMaximum;
			return parameter.UsesLogarithmicScaling
				? parameter.Minimum * Math.Pow(parameter.Maximum / parameter.Minimum, normalized)
				: parameter.Minimum + ((parameter.Maximum - parameter.Minimum) * normalized);
		}

		public static Fm9CabSettings GetCabSettings(Fm9PresetBody source, int channel = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			return GetCabSettings(source, FindCabRecord(source), channel);
		}

		public static Fm9CabSettings GetCabSettings(Fm9PresetBody source, Fm9PresetBlockRecord record, int channel)
		{
			ArgumentNullException.ThrowIfNull(source);
			// Older firmware wrote narrower Cab records (101 columns); columns were only appended, so ids up to the cuts hold.
			EnsureRecordShape(source, record, CabParameterColumns, CabChannelCount, "Cab", CabHighCutParameterId + 1);

			return new Fm9CabSettings(
				ReadParameter(source.Bytes.Span, record, channel, CabBankParameterId),
				ReadParameter(source.Bytes.Span, record, channel, CabTypeParameterId),
				DenormalizeLogarithmic(
					ReadParameter(source.Bytes.Span, record, channel, CabLowCutParameterId),
					20,
					200),
				DenormalizeLogarithmic(
					ReadParameter(source.Bytes.Span, record, channel, CabHighCutParameterId),
					2_000,
					20_000));
		}

		public static byte[] WithCabSettings(
			Fm9PresetBody source,
			Fm9CabSettings settings)
		{
			ArgumentNullException.ThrowIfNull(settings);
			return WithCabChannelSettings(source, new Dictionary<int, Fm9CabSettings> { [0] = settings });
		}

		// Writes every requested channel in one pass: the Ride-baseline check reads channel A, which a write changes.
		public static byte[] WithCabChannelSettings(
			Fm9PresetBody source,
			IReadOnlyDictionary<int, Fm9CabSettings> channels)
		{
			ArgumentNullException.ThrowIfNull(source);
			return WithCabChannelSettings(source, FindRideBaselineCabRecord(source), channels);
		}

		public static byte[] WithCabChannelSettings(
			Fm9PresetBody source,
			Fm9PresetBlockRecord record,
			IReadOnlyDictionary<int, Fm9CabSettings> channels)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(channels);
			EnsureRecordShape(source, record, CabParameterColumns, CabChannelCount, "Cab", CabHighCutParameterId + 1);
			foreach ((int channel, Fm9CabSettings settings) in channels)
			{
				ArgumentNullException.ThrowIfNull(settings);
				if (channel is < 0 or >= CabChannelCount)
					throw new ArgumentOutOfRangeException(nameof(channels), "Each Cab channel must be A-D.");
				ValidateCabSettings(settings);
			}

			byte[] bytes = source.Bytes.ToArray();
			foreach ((int channel, Fm9CabSettings settings) in channels.OrderBy(pair => pair.Key))
			{
				WriteParameter(bytes, record, channel, CabBankParameterId, checked((ushort)settings.Bank));
				WriteParameter(bytes, record, channel, CabTypeParameterId, checked((ushort)settings.TypeOrdinal));
				WriteParameter(
					bytes,
					record,
					channel,
					CabLowCutParameterId,
					NormalizeLogarithmic(settings.LowCut, 20, 200));
				WriteParameter(
					bytes,
					record,
					channel,
					CabHighCutParameterId,
					NormalizeLogarithmic(settings.HighCut, 2_000, 20_000));
			}
			return bytes;
		}

		public static int GetCabParameterId(string parameterName) => parameterName.ToUpperInvariant() switch
		{
			"BANK" => CabBankParameterId,
			"CAB TYPE" => CabTypeParameterId,
			"LOW CUT" => CabLowCutParameterId,
			"HIGH CUT" => CabHighCutParameterId,
			_ => throw new NotSupportedException($"Cab parameter '{parameterName}' is not verified for offline FM9 export.")
		};

		public static int GetAmpSettingParameterId(string parameterName) => parameterName.ToUpperInvariant() switch
		{
			"AMP MODEL" => AmpTypeParameterId,
			"OUTPUT EQ 80 HZ" => AmpOutputEqFirstBandParameterId,
			"OUTPUT EQ 240 HZ" => AmpOutputEqFirstBandParameterId + 1,
			"OUTPUT EQ 750 HZ" => AmpOutputEqFirstBandParameterId + 2,
			"OUTPUT EQ 2200 HZ" => AmpOutputEqFirstBandParameterId + 3,
			"OUTPUT EQ 6600 HZ" => AmpOutputEqFirstBandParameterId + 4,
			"OUTPUT EQ TYPE" => AmpOutputEqTypeParameterId,
			"OUTPUT EQ LOCATION" => AmpOutputEqLocationParameterId,
			"OUTPUT EQ ENABLED" => AmpOutputEqEnabledParameterId,
			_ => throw new NotSupportedException($"Amp parameter '{parameterName}' is not verified for offline FM9 export.")
		};

		// A record narrower than firmware 12's (older firmware) is accepted down to minimumColumns: later firmware only
		// appended columns (every choice in the shared columns of 256 backup presets held a valid ordinal).
		private static void EnsureRecordShape(Fm9PresetBody source, Fm9PresetBlockRecord record, int columns, int rows, string blockType,
			int? minimumColumns = null)
		{
			ArgumentNullException.ThrowIfNull(record);
			if (record.ParameterColumns > columns || record.ParameterColumns < (minimumColumns ?? columns) ||
				record.ParameterRows != rows || !IsWithinBody(source.Bytes.Span, record))
				throw new InvalidDataException($"The record is not a verified FM9 {blockType} record.");
		}

		private static Fm9PresetBlockRecord FindRideBaselineAmpRecord(Fm9PresetBody source)
		{
			Fm9PresetBlockRecord record = FindAmpRecord(source);

			return ReadParameter(source.Bytes.Span, record, 0, AmpTypeParameterId) == RideBaselineAmpType
				? record
				: throw new InvalidOperationException(
					"The preset does not contain the verified Ride-baseline Amp model.");
		}

		private static Fm9PresetBlockRecord FindAmpRecord(Fm9PresetBody source)
		{
			Fm9PresetBlockRecord profiledRecord =
				Fm9BlockRecordTemplateProfile.CreateRideBaseline(source)
					.GetRecordForExistingBlock(source, "Amp", 1);
			List<Fm9PresetBlockRecord> matches = source.BlockRecords
				.Where(record =>
					record.Offset == profiledRecord.Offset &&
					record.ParameterColumns == AmpParameterColumns &&
					record.ParameterRows == AmpChannelCount &&
					IsWithinBody(source.Bytes.Span, record))
				.ToList();

			return matches.Count == 1
				? matches[0]
				: throw new InvalidOperationException(
					"The preset does not contain exactly one Ride-template Amp record.");
		}

		private static int GetParameterOffset(
			Fm9PresetBlockRecord record,
			Fm9AmpChannel channel,
			Fm9AmpKnob knob)
		{
			int column = (int)knob - FirstStoredParameterId;

			if ((int)channel < 0 || (int)channel >= record.ParameterRows)
			{
				throw new ArgumentOutOfRangeException(nameof(channel));
			}

			if (column < 0 || column >= record.ParameterColumns)
			{
				throw new ArgumentOutOfRangeException(nameof(knob));
			}

			return record.Offset + ((BlockHeaderWordCount +
				(((int)channel * record.ParameterColumns) + column)) * sizeof(ushort));
		}

		private static Fm9PresetBlockRecord FindRideBaselineCabRecord(Fm9PresetBody source)
		{
			Fm9PresetBlockRecord profiledRecord =
				Fm9BlockRecordTemplateProfile.CreateRideBaseline(source)
					.GetRecordForExistingBlock(source, "Cab", 1);
			List<Fm9PresetBlockRecord> matches = source.BlockRecords
				.Where(record =>
					record.Offset == profiledRecord.Offset &&
					record.ParameterColumns == CabParameterColumns &&
					record.ParameterRows == CabChannelCount &&
					IsWithinBody(source.Bytes.Span, record) &&
					ReadParameter(source.Bytes.Span, record, 0, CabBankParameterId) == RideBaselineCabBank &&
					ReadParameter(source.Bytes.Span, record, 0, CabTypeParameterId) == RideBaselineCabType &&
					ReadParameter(source.Bytes.Span, record, 0, CabLowCutParameterId) == RideBaselineCabLowCut &&
					ReadParameter(source.Bytes.Span, record, 0, CabHighCutParameterId) == RideBaselineCabHighCut)
				.ToList();

			return matches.Count == 1
				? matches[0]
				: throw new InvalidOperationException(
					"The preset does not contain exactly one verified Ride-baseline Cab record.");
		}

		private static Fm9PresetBlockRecord FindCabRecord(Fm9PresetBody source)
		{
			Fm9PresetBlockRecord profiledRecord =
				Fm9BlockRecordTemplateProfile.CreateRideBaseline(source)
					.GetRecordForExistingBlock(source, "Cab", 1);
			List<Fm9PresetBlockRecord> matches = source.BlockRecords
				.Where(record =>
					record.Offset == profiledRecord.Offset &&
					record.ParameterColumns == CabParameterColumns &&
					record.ParameterRows == CabChannelCount &&
					IsWithinBody(source.Bytes.Span, record))
				.ToList();

			return matches.Count == 1
				? matches[0]
				: throw new InvalidOperationException(
					"The preset does not contain exactly one Ride-template Cab record.");
		}

		public static void ValidateCabSettings(Fm9CabSettings settings)
		{
			// USER (2) and SCRATCHPAD (4) hold device-local IRs, so a portable preset may only reference firmware banks.
			int rosterSize = settings.Bank switch
			{
				CabFactory1Bank or CabFactory2Bank => 1_024,
				CabLegacyBank => 189,
				_ => throw new ArgumentOutOfRangeException(
					nameof(settings),
					"Cab Bank must be a firmware bank: Factory 1 (0), Factory 2 (1) or Legacy (3).")
			};

			if (settings.TypeOrdinal < 0 || settings.TypeOrdinal >= rosterSize)
			{
				throw new ArgumentOutOfRangeException(
					nameof(settings),
					$"Cab Type must be from 0 through {rosterSize - 1} in bank {settings.Bank}.");
			}

			ValidateFrequency(settings.LowCut, 20, 200, nameof(settings));
			ValidateFrequency(settings.HighCut, 2_000, 20_000, nameof(settings));
		}

		private static void ValidateFrequency(double value, double minimum, double maximum, string parameterName)
		{
			if (!double.IsFinite(value) || value < minimum || value > maximum)
			{
				throw new ArgumentOutOfRangeException(
					parameterName,
					$"Cab frequency values must be from {minimum} through {maximum} Hz.");
			}
		}

		private static void ValidateAmpOutputEq(Fm9AmpOutputEqSettings settings)
		{
			double[] bands =
				[
					settings.Band80Hz,
					settings.Band240Hz,
					settings.Band750Hz,
					settings.Band2200Hz,
					settings.Band6600Hz
				];

			if (bands.Any(value => !double.IsFinite(value) || value < -12 || value > 12))
			{
				throw new ArgumentOutOfRangeException(
					nameof(settings),
					"Amp Output EQ band gains must be from -12 through 12 dB.");
			}

			if (!Enum.IsDefined(settings.Type))
			{
				throw new ArgumentOutOfRangeException(nameof(settings), "Amp Output EQ Type is invalid.");
			}

			if (!Enum.IsDefined(settings.Location))
			{
				throw new ArgumentOutOfRangeException(nameof(settings), "Amp Output EQ Location is invalid.");
			}
		}

		private static ushort ReadParameter(
			ReadOnlySpan<byte> bytes,
			Fm9PresetBlockRecord record,
			int channel,
			int parameterId)
		{
			return ReadUInt16(bytes, GetParameterOffset(record, channel, parameterId));
		}

		private static void WriteParameter(
			byte[] bytes,
			Fm9PresetBlockRecord record,
			int channel,
			int parameterId,
			ushort value)
		{
			WriteUInt16(bytes, GetParameterOffset(record, channel, parameterId), value);
		}

		private static int GetParameterOffset(
			Fm9PresetBlockRecord record,
			int channel,
			int parameterId) =>
			Fm9ChannelParameterStorage.GetOffset(record, channel, parameterId);

		private static ushort NormalizeLogarithmic(double value, double minimum, double maximum)
		{
			double normalized = Math.Log10(value / minimum) / Math.Log10(maximum / minimum);
			return checked((ushort)Math.Round(
				normalized * ContinuousWireMaximum,
				MidpointRounding.AwayFromZero));
		}

		private static ushort NormalizeLinear(double value, double minimum, double maximum)
		{
			return checked((ushort)Math.Round(
				(value - minimum) / (maximum - minimum) * ContinuousWireMaximum,
				MidpointRounding.AwayFromZero));
		}

		private static ushort EncodeProfileValue(
			Fm9TemplateParameterDefinition parameter,
			double value)
		{
			double normalized = parameter.UsesLogarithmicScaling
				? Math.Log10(value / parameter.Minimum) / Math.Log10(parameter.Maximum / parameter.Minimum)
				: (value - parameter.Minimum) / (parameter.Maximum - parameter.Minimum);
			return checked((ushort)Math.Round(
				normalized * ContinuousWireMaximum,
				MidpointRounding.AwayFromZero));
		}

		private static ushort EncodeReverbTime(double seconds)
		{
			return checked((ushort)Math.Round(
				(seconds - ReverbTimeMinimumSeconds) /
					(ReverbTimeMaximumSeconds - ReverbTimeMinimumSeconds) *
					ContinuousWireMaximum,
				MidpointRounding.AwayFromZero));
		}

		private static bool IsReverbTime(
			string blockType,
			Fm9TemplateParameterDefinition parameter)
		{
			return string.Equals(blockType, "Reverb", StringComparison.OrdinalIgnoreCase) &&
				parameter.ParameterId == ReverbTimeParameterId &&
				(string.Equals(parameter.Name, "Time", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(parameter.Name, "REVERB_TIME", StringComparison.Ordinal));
		}

		private static bool IsAmpInputTrim(
			string blockType,
			Fm9TemplateParameterDefinition parameter)
		{
			return string.Equals(blockType, "Amp", StringComparison.OrdinalIgnoreCase) &&
				parameter.ParameterId == 53;
		}

		private static double DenormalizeLinear(ushort value, double minimum, double maximum)
		{
			return minimum + ((maximum - minimum) * value / ContinuousWireMaximum);
		}

		private static double DenormalizeLogarithmic(ushort value, double minimum, double maximum)
		{
			double normalized = value / (double)ContinuousWireMaximum;
			return minimum * Math.Pow(maximum / minimum, normalized);
		}

		private static bool IsWithinBody(ReadOnlySpan<byte> body, Fm9PresetBlockRecord record)
		{
			return record.Offset >= 0 && record.Length > 0 &&
				record.Offset + record.Length <= body.Length;
		}

		private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
		{
			return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
		}

		private static void WriteUInt16(byte[] bytes, int offset, ushort value)
		{
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
		}
	}
}
