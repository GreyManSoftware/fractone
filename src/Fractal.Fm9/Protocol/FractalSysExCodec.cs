namespace Fractal.Companion.Protocol
{
	public readonly record struct GridPosition(int Row, int Column)
	{
		public void Validate()
		{
			if (Row is < 1 or > 6)
			{
				throw new ArgumentOutOfRangeException(nameof(Row), "FM9 grid rows are 1 through 6.");
			}

			if (Column is < 1 or > 14)
			{
				throw new ArgumentOutOfRangeException(nameof(Column), "FM9 grid columns are 1 through 14.");
			}
		}
	}

	public readonly record struct FractalSysExFrame(ReadOnlyMemory<byte> Bytes);

	public static class FractalSysExCodec
	{
		private const byte StartOfExclusive = 0xF0;
		private const byte EndOfExclusive = 0xF7;
		private const byte ManufacturerByte1 = 0x00;
		private const byte ManufacturerByte2 = 0x01;
		private const byte ManufacturerByte3 = 0x74;
		private const byte Fm9Model = 0x12;
		private const byte ParameterFunction = 0x01;
		private const byte FirmwareFunction = 0x08;
		private const byte StoredPresetRequestFunction = 0x03;
		private const byte PresetNameFunction = 0x0D;
		private const byte SceneFunction = 0x0C;
		private const byte BlockBypassFunction = 0x0A;
		private const byte BlockChannelFunction = 0x0B;
		private const byte StatusDumpFunction = 0x13;
		private const byte BulkReadFunction = 0x1F;
		private const byte BroadcastHeaderFunction = 0x74;
		private const byte ContinuousParameterSubcommand = 0x52;
		private const byte DiscreteParameterSubcommand = 0x09;
		private const byte SelectGridCellSubcommand = 0x30;
		private const byte InsertGridBlockSubcommand = 0x32;
		private const byte GridLayoutSubcommand = 0x2E;
		private const byte GridRoutingSubcommand = 0x35;
		private const byte SwitchPresetSubcommand = 0x27;
		private const byte StorePresetSubcommand = 0x26;
		private const byte MultipurposeResponseFunction = 0x64;

		public static IReadOnlyList<FractalSysExFrame> CreateBlockPlacementCommands(
			int blockId,
			GridPosition position)
		{
			if (blockId < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(blockId), "A block ID cannot be negative.");
			}

			position.Validate();

			return
			[
				CreateGridCellSelection(position),
				CreateGridBlockInsertion(blockId, position)
			];
		}

		public static IReadOnlyList<FractalSysExFrame> CreateBlockClearCommands(
			GridPosition position)
		{
			position.Validate();

			return
			[
				CreateGridCellSelection(position),
				CreateGridBlockInsertion(0, position)
			];
		}

		public static FractalSysExFrame CreateContinuousParameterChange(
			int effectId,
			int parameterId,
			float normalizedValue)
		{
			ValidateParameterAddress(effectId, parameterId);

			if (!float.IsFinite(normalizedValue) || normalizedValue is < 0 or > 1)
			{
				throw new ArgumentOutOfRangeException(
					nameof(normalizedValue),
					"Continuous parameter values must be finite and normalized between 0 and 1.");
			}

			return CreateParameterFrame(
				ContinuousParameterSubcommand,
				effectId,
				parameterId,
				normalizedValue);
		}

		// Despite carrying an IEEE float, the FM9 treats this value as normalized 0..1 (raw = value * 65534),
		// clamping outside that range; it is not a display value in Hz, ms or knob units (live probe, 2026-10-03).
		public static FractalSysExFrame CreateContinuousParameterValueChange(
			int effectId,
			int parameterId,
			float value)
		{
			ValidateParameterAddress(effectId, parameterId);

			if (!float.IsFinite(value))
			{
				throw new ArgumentOutOfRangeException(
					nameof(value),
					"Continuous parameter values must be finite.");
			}

			return CreateParameterFrame(
				ContinuousParameterSubcommand,
				effectId,
				parameterId,
				value);
		}

		public static FractalSysExFrame CreateDiscreteParameterChange(
			int effectId,
			int parameterId,
			int ordinal)
		{
			ValidateParameterAddress(effectId, parameterId);

			if (ordinal < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(ordinal), "Discrete parameter ordinals cannot be negative.");
			}

			return CreateParameterFrame(
				DiscreteParameterSubcommand,
				effectId,
				parameterId,
				ordinal);
		}

		public static FractalSysExFrame CreateSignedDiscreteParameterChange(
			int effectId,
			int parameterId,
			int value)
		{
			ValidateParameterAddress(effectId, parameterId);

			if (value is < short.MinValue or > short.MaxValue)
			{
				throw new ArgumentOutOfRangeException(
					nameof(value),
					"Signed discrete parameter values must fit in a 16-bit signed integer.");
			}

			return CreateParameterFrame(
				DiscreteParameterSubcommand,
				effectId,
				parameterId,
				value);
		}

		public static FractalSysExFrame CreateBulkReadRequest(int effectId)
		{
			if (effectId < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(effectId), "Effect IDs cannot be negative.");
			}

			List<byte> payload = Encode14Bit(effectId);
			return CreateFrame(BulkReadFunction, payload);
		}

		public static FractalSysExFrame CreateGridLayoutRequest()
		{
			return CreateFrame(
				ParameterFunction,
				[GridLayoutSubcommand, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
		}

		// Cables a block to one in the next column. The code is the source and destination cells (column-major, 6 rows per
		// column) as two bytes packed MSB-first into septets, as the live grid read packs its words (BigSaurus's reading).
		// Hardware-checked on 505 (2026-10-13) for same-row, cross-row and even-column routes; the formula this replaced
		// was right only for same-row routes on row 3.
		public static FractalSysExFrame CreateGridRoute(
			int sourceRow,
			int sourceColumn,
			int destinationRow)
		{
			GridPosition source = new(sourceRow, sourceColumn);
			source.Validate();

			if (sourceColumn >= 14)
			{
				throw new ArgumentOutOfRangeException(
					nameof(sourceColumn),
					"Grid routing requires a destination column to the right of the source.");
			}

			if (destinationRow is < 1 or > 6)
			{
				throw new ArgumentOutOfRangeException(
					nameof(destinationRow),
					"FM9 grid destination rows are 1 through 6.");
			}

			int sourceCell = ToGridPosition(source);
			int destinationCell = ToGridPosition(new GridPosition(destinationRow, sourceColumn + 1));

			return CreateFrame(
				ParameterFunction,
				[
					GridRoutingSubcommand, 0x00,
					0x00, 0x00, 0x00, 0x00,
					0x01,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
					0x02, 0x00,
					(byte)(sourceCell >> 1),
					(byte)(((sourceCell & 1) << 6) | (destinationCell >> 2)),
					(byte)((destinationCell & 3) << 5)
				]);
		}

		public static FractalSysExFrame CreateFirmwareRequest()
		{
			return CreateFrame(FirmwareFunction, []);
		}

		public static FractalSysExFrame CreateStoredPresetRequest(int wirePresetNumber)
		{
			if (wirePresetNumber is < 0 or > 0x3FFF)
			{
				throw new ArgumentOutOfRangeException(
					nameof(wirePresetNumber),
					"FM9 stored preset request numbers must fit in 14 bits.");
			}

			return CreateFrame(
				StoredPresetRequestFunction,
				[
					(byte)((wirePresetNumber >> 7) & 0x7F),
					(byte)(wirePresetNumber & 0x7F)
				]);
		}

		// 01 27 switches the FM9 to a preset (number at bytes 12-13, low septet first), dropping unsaved edits. It was once
		// named "preset import preparation"; an all-zero frame jumps to preset 0 (hardware-checked 2026-10-13).
		public static FractalSysExFrame CreateSwitchPreset(int displayedPresetNumber)
		{
			if (displayedPresetNumber is < 0 or > 511)
				throw new ArgumentOutOfRangeException(nameof(displayedPresetNumber), "FM9 presets are 0 through 511.");
			return CreateFrame(
				ParameterFunction,
				[SwitchPresetSubcommand, 0x00, 0x00, 0x00, 0x00, 0x00,
					(byte)(displayedPresetNumber & 0x7F), (byte)(displayedPresetNumber >> 7),
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
		}

		// Which slots may be written is the caller's decision: probes enforce the 500-505 scratch policy, and the stored-preset
		// replacer writes only a slot the user named, after backing it up.
		public static FractalSysExFrame CreateStorePreset(int displayedPresetNumber)
		{
			if (displayedPresetNumber is < 0 or > 511)
				throw new ArgumentOutOfRangeException(nameof(displayedPresetNumber), "FM9 presets are 0 through 511.");

			List<byte> payload =
			[
				StorePresetSubcommand,
				0x00, 0x00, 0x00, 0x00, 0x00
			];
			payload.AddRange(Encode14Bit(displayedPresetNumber));
			payload.AddRange([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
			return CreateFrame(ParameterFunction, payload);
		}

		public static FractalSysExFrame CreateStatusDumpRequest()
		{
			return CreateFrame(StatusDumpFunction, []);
		}

		public static FractalSysExFrame CreateCurrentPresetNameRequest()
		{
			return CreateFrame(PresetNameFunction, [0x7F, 0x7F]);
		}

		public static FractalSysExFrame CreateCurrentSceneRequest()
		{
			return CreateFrame(SceneFunction, [0x7F]);
		}

		public static FractalSysExFrame CreateSceneSelection(int sceneNumber)
		{
			if (sceneNumber is < 1 or > 8)
			{
				throw new ArgumentOutOfRangeException(nameof(sceneNumber), "FM9 scenes are 1 through 8.");
			}
			return CreateFrame(SceneFunction, [checked((byte)(sceneNumber - 1))]);
		}

		// A block's bypass (0x0A) and channel (0x0B) in the current scene: effect id, then the new state or 0x7F to ask. The
		// FM9 answers either with the block's state (hardware-checked 2026-10-08: setting them changes the current scene's
		// record in the edit buffer, and a parameter change then lands on the channel the block plays).
		public static FractalSysExFrame CreateBlockBypass(int effectId, bool? bypassed)
		{
			if (effectId < 0) throw new ArgumentOutOfRangeException(nameof(effectId));
			return CreateFrame(BlockBypassFunction, [.. Encode14Bit(effectId), bypassed is bool on ? (byte)(on ? 1 : 0) : (byte)0x7F]);
		}

		public static FractalSysExFrame CreateBlockChannel(int effectId, int? channel)
		{
			if (effectId < 0) throw new ArgumentOutOfRangeException(nameof(effectId));
			if (channel is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(channel), "Channels are A-D (0-3).");
			return CreateFrame(BlockChannelFunction, [.. Encode14Bit(effectId), channel is int row ? (byte)row : (byte)0x7F]);
		}

		public static bool TryGetBlockBypass(ReadOnlySpan<byte> bytes, out int effectId, out bool bypassed)
		{
			bypassed = false;
			bool found = TryGetBlockState(bytes, BlockBypassFunction, out effectId, out int state);
			bypassed = state == 1;
			return found;
		}

		public static bool TryGetBlockChannel(ReadOnlySpan<byte> bytes, out int effectId, out int channel) =>
			TryGetBlockState(bytes, BlockChannelFunction, out effectId, out channel);

		private static bool TryGetBlockState(ReadOnlySpan<byte> bytes, byte function, out int effectId, out int state)
		{
			effectId = 0;
			state = 0;
			if (!IsValidFm9Frame(bytes) || bytes[5] != function || bytes.Length < 11) return false;
			effectId = bytes[6] | (bytes[7] << 7);
			state = bytes[8];
			return true;
		}

		public static bool TryGetFirmwareVersion(
			ReadOnlySpan<byte> bytes,
			out string firmwareVersion)
		{
			firmwareVersion = string.Empty;

			if (!IsValidFm9Frame(bytes) ||
				bytes[5] != FirmwareFunction ||
				bytes.Length < 10)
			{
				return false;
			}

			firmwareVersion = $"{bytes[6]}.{bytes[7]:00}";
			return true;
		}

		public static bool IsStatusDumpResponse(ReadOnlySpan<byte> bytes)
		{
			return IsValidFm9Frame(bytes) && bytes[5] == StatusDumpFunction;
		}

		public static bool IsSuccessfulAcknowledgement(
			ReadOnlySpan<byte> bytes,
			byte acknowledgedFunction)
		{
			return IsValidFm9Frame(bytes) &&
				bytes[5] == MultipurposeResponseFunction &&
				bytes.Length > 8 &&
				bytes[6] == acknowledgedFunction &&
				bytes[7] == 0;
		}

		public static bool TryGetCurrentPreset(
			ReadOnlySpan<byte> bytes,
			out int visiblePresetNumber,
			out string presetName)
		{
			visiblePresetNumber = default;
			presetName = string.Empty;

			if (!IsValidFm9Frame(bytes) ||
				bytes[5] != PresetNameFunction ||
				bytes.Length < 42)
			{
				return false;
			}

			int wirePresetNumber = bytes[6] | (bytes[7] << 7);
			ReadOnlySpan<byte> rawName = bytes.Slice(8, 32);
			int nameLength = rawName.IndexOf((byte)0);

			if (nameLength < 0)
			{
				nameLength = rawName.Length;
			}

			visiblePresetNumber = wirePresetNumber;
			presetName = System.Text.Encoding.ASCII.GetString(rawName[..nameLength]).TrimEnd();
			return true;
		}

		public static bool TryGetCurrentScene(
			ReadOnlySpan<byte> bytes,
			out int sceneNumber)
		{
			sceneNumber = default;

			if (!IsValidFm9Frame(bytes) ||
				bytes[5] != SceneFunction ||
				bytes.Length < 8)
			{
				return false;
			}

			sceneNumber = bytes[6] + 1;
			return true;
		}


		public static bool IsValidFm9Frame(ReadOnlySpan<byte> bytes)
		{
			if (bytes.Length < 8 ||
				bytes[0] != StartOfExclusive ||
				bytes[^1] != EndOfExclusive ||
				bytes[1] != ManufacturerByte1 ||
				bytes[2] != ManufacturerByte2 ||
				bytes[3] != ManufacturerByte3 ||
				bytes[4] != Fm9Model)
			{
				return false;
			}

			return bytes[^2] == CalculateChecksum(bytes[1..^2]);
		}

		public static bool TryGetBroadcastHeaderEffectId(
			ReadOnlySpan<byte> bytes,
			out int effectId)
		{
			effectId = default;

			if (!IsValidFm9Frame(bytes) ||
				bytes[5] != BroadcastHeaderFunction ||
				bytes.Length < 10)
			{
				return false;
			}

			effectId = bytes[6] | (bytes[7] << 7);
			return true;
		}

		private static FractalSysExFrame CreateGridCellSelection(GridPosition position)
		{
			int gridPosition = ToGridPosition(position);
			List<byte> payload =
			[
				SelectGridCellSubcommand, 0x00, 0x00, 0x00, 0x00, 0x00
			];
			payload.AddRange(EncodeFiveSeptetInteger(gridPosition));
			payload.AddRange([0x00, 0x00, 0x00, 0x00]);

			return CreateFrame(ParameterFunction, payload);
		}

		private static FractalSysExFrame CreateGridBlockInsertion(int blockId, GridPosition position)
		{
			int gridPosition = ToGridPosition(position);
			List<byte> payload =
			[
				InsertGridBlockSubcommand, 0x00
			];
			payload.AddRange(Encode14Bit(blockId));
			payload.AddRange([0x00, 0x00]);
			payload.AddRange(Encode14Bit(gridPosition));
			payload.AddRange([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

			return CreateFrame(ParameterFunction, payload);
		}

		private static FractalSysExFrame CreateParameterFrame(
			byte subcommand,
			int effectId,
			int parameterId,
			float value)
		{
			List<byte> payload =
			[
				subcommand, 0x00
			];
			payload.AddRange(Encode14Bit(effectId));
			payload.AddRange(Encode14Bit(parameterId));
			payload.AddRange(EncodeFloatSeptets(value));
			payload.AddRange([0x00, 0x00, 0x00, 0x00]);

			return CreateFrame(ParameterFunction, payload);
		}

		private static FractalSysExFrame CreateFrame(byte function, IReadOnlyList<byte> payload)
		{
			List<byte> body =
			[
				ManufacturerByte1,
				ManufacturerByte2,
				ManufacturerByte3,
				Fm9Model,
				function
			];
			body.AddRange(payload);

			List<byte> frame =
			[
				StartOfExclusive
			];
			frame.AddRange(body);
			frame.Add(CalculateChecksum(body.ToArray()));
			frame.Add(EndOfExclusive);

			return new FractalSysExFrame(frame.ToArray());
		}

		private static List<byte> Encode14Bit(int value)
		{
			return
			[
				(byte)(value & 0x7F),
				(byte)((value >> 7) & 0x7F)
			];
		}

		private static List<byte> EncodeFiveSeptetInteger(int value)
		{
			List<byte> result = [];

			for (int index = 0; index < 5; index++)
			{
				result.Add((byte)((value >> (index * 7)) & 0x7F));
			}

			return result;
		}

		private static List<byte> EncodeFloatSeptets(float value)
		{
			uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
			List<byte> result = [];

			for (int index = 0; index < 5; index++)
			{
				result.Add((byte)((bits >> (index * 7)) & 0x7F));
			}

			return result;
		}

		private static byte CalculateChecksum(ReadOnlySpan<byte> body)
		{
			byte checksum = StartOfExclusive;

			foreach (byte value in body)
			{
				checksum ^= value;
			}

			return (byte)(checksum & 0x7F);
		}

		private static int ToGridPosition(GridPosition position)
		{
			return (position.Column - 1) * 6 + position.Row - 1;
		}

		private static void ValidateParameterAddress(int effectId, int parameterId)
		{
			if (effectId < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(effectId), "Effect IDs cannot be negative.");
			}

			if (parameterId < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(parameterId), "Parameter IDs cannot be negative.");
			}
		}
	}
}
