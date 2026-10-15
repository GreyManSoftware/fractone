using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	// Every frame the probe sends, in the device's own model byte. Built from the FM9 codec and retargeted where the codec
	// covers it; 01 27 and 01 26 are built here because the codec stops at 511 and the Axe-Fx III Mk II holds 1,024.
	public static class ProbeFrames
	{
		public const int EditBuffer = 0x3FFF;

		public static byte[] Firmware(byte model) => WithModel(FractalSysExCodec.CreateFirmwareRequest().Bytes.ToArray(), model);

		public static byte[] StatusDump(Gen3Device device) => Native(device, FractalSysExCodec.CreateStatusDumpRequest());

		public static byte[] CurrentPreset(Gen3Device device) => Native(device, FractalSysExCodec.CreateCurrentPresetNameRequest());

		// 0D with a number in place of 7F 7F: the name stored in that preset. Sent only for the Axe-Fx III's Mk I/Mk II check
		// (preset 1023); the number goes low septet first, as the reply carries it (unverified on a III).
		public static byte[] StoredPresetName(Gen3Device device, int preset) => Build(device.Model, 0x0D, [Low(device, preset), High(preset)]);

		public static byte[] CurrentScene(Gen3Device device) => Native(device, FractalSysExCodec.CreateCurrentSceneRequest());

		public static byte[] SelectScene(Gen3Device device, int scene) => Native(device, FractalSysExCodec.CreateSceneSelection(scene));

		public static byte[] ReadPreset(Gen3Device device, int slot) => Native(device, FractalSysExCodec.CreateStoredPresetRequest(slot));

		// 01 27: the number at bytes 12-13, low septet first (the FM9 codec's layout, hardware-proved 2026-10-13).
		public static byte[] SwitchPreset(Gen3Device device, int slot) =>
			Build(device.Model, 0x01, [0x27, 0, 0, 0, 0, 0, Low(device, slot), High(slot), 0, 0, 0, 0, 0, 0, 0]);

		// 1F: one block's values from the edit buffer (74 header, 75 data, 76 end).
		public static byte[] BulkRead(Gen3Device device, int effectId) => Native(device, FractalSysExCodec.CreateBulkReadRequest(effectId));

		// 01 09: a type or model selected live, which loads its voicing on the channel the block plays.
		public static byte[] SelectType(Gen3Device device, int effectId, int parameterId, int ordinal) =>
			Native(device, FractalSysExCodec.CreateDiscreteParameterChange(effectId, parameterId, ordinal));

		// 01 30 then 01 32: select a cell and insert a block there, numbered by the device's grid (the FM3's has 4 rows).
		public static IReadOnlyList<byte[]> InsertBlock(Gen3Device device, int effectId, int row, int column) =>
			[.. FractalSysExCodec.CreateBlockPlacementCommands(effectId, new GridPosition(row, column), device).Select(frame => Native(device, frame))];

		// 01 35: a cable from a block to a row of the next column, cells numbered by the device's grid.
		public static byte[] Route(Gen3Device device, int fromRow, int fromColumn, int toRow) =>
			Native(device, FractalSysExCodec.CreateGridRoute(fromRow, fromColumn, toRow, device));

		// 01 26 stores the edit buffer to a slot: same number layout as the codec's CreateStorePreset.
		public static byte[] StorePreset(Gen3Device device, int slot) =>
			Build(device.Model, 0x01, [0x26, 0, 0, 0, 0, 0, Low(device, slot), High(slot), 0, 0, 0, 0, 0, 0, 0]);

		// A preset's frames aimed at the edit buffer: only the 0x77 header's address changes (to 7F 7F) and its checksum;
		// the data frames and footer go out exactly as they were, so a backup is resent as the device wrote it.
		public static IReadOnlyList<byte[]> ForEditBuffer(IReadOnlyList<byte[]> frames)
		{
			if (frames.Count == 0 || frames[0].Length < 10 || frames[0][5] != 0x77)
				throw new InvalidDataException("A preset dump starts with a 0x77 header.");
			byte[] header = (byte[])frames[0].Clone();
			header[6] = 0x7F;
			header[7] = 0x7F;
			header[^2] = Checksum(header);
			return [header, .. frames.Skip(1)];
		}

		// A dump as the FM9 parser reads it (the shared device-aware parse). The frames themselves stay verbatim.
		public static Fm9PresetDump Parse(Gen3Device device, IReadOnlyList<byte[]> frames) =>
			Fm9PresetDump.Parse([.. frames.SelectMany(frame => frame)], device);

		// Splits a .syx file into its frames.
		public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> bytes)
		{
			List<byte[]> frames = [];
			int start = 0;
			for (int index = 0; index < bytes.Length; index++)
				if (bytes[index] == 0xF7)
				{
					frames.Add(bytes[start..(index + 1)].ToArray());
					start = index + 1;
				}
			if (start != bytes.Length)
				throw new InvalidDataException("The SysEx data ends inside a frame.");
			return frames;
		}

		public static bool IsFractal(ReadOnlySpan<byte> bytes) =>
			bytes.Length >= 8 && bytes[0] == 0xF0 && bytes[^1] == 0xF7 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x74;

		public static bool HasValidChecksum(ReadOnlySpan<byte> bytes) => IsFractal(bytes) && bytes[^2] == Checksum(bytes);

		// XOR of every byte before the checksum (F0 included), 7 bits.
		public static byte Checksum(ReadOnlySpan<byte> bytes)
		{
			int value = 0;
			for (int index = 0; index < bytes.Length - 2; index++)
				value ^= bytes[index];
			return (byte)(value & 0x7F);
		}

		public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes) is { Length: > 0 } hex
			? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(index => hex.Substring(index * 2, 2)))
			: "";

		private static byte[] Native(Gen3Device device, FractalSysExFrame frame) => device.Retarget(frame).Bytes.ToArray();

		private static byte[] WithModel(byte[] bytes, byte model)
		{
			bytes[4] = model;
			bytes[^2] = Checksum(bytes);
			return bytes;
		}

		private static byte[] Build(byte model, byte function, byte[] payload)
		{
			byte[] frame = [0xF0, 0x00, 0x01, 0x74, model, function, .. payload, 0, 0xF7];
			frame[^2] = Checksum(frame);
			return frame;
		}

		private static byte Low(Gen3Device device, int slot)
		{
			if (slot < 0 || slot >= device.Presets)
				throw new ArgumentOutOfRangeException(nameof(slot), $"The {device.Name} has presets 0 to {device.Presets - 1}.");
			return (byte)(slot & 0x7F);
		}

		private static byte High(int slot) => (byte)(slot >> 7);
	}
}
