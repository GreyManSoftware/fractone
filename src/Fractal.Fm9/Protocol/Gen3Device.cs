namespace Fractal.Companion.Protocol
{
	public enum Gen3Connection
	{
		UsbMidi,
		// The FM3 talks over a USB serial (CDC) port, not MIDI; the SysEx bytes are the same.
		UsbSerial
	}

	// What differs between Fractal's gen-3 units, as far as reading, loading and checking presets goes. From FracCompat's
	// device brief (research\probe-kit\device-brief.md, 2026-10-14): FM9 values are measured on Chris's FM9, FM3 and
	// Axe-Fx III values on device-written files and firmware unless noted. Record widths are not here on purpose: read
	// columns and rows from each record, never from the device or the format version.
	public sealed record Gen3Device(
		string Name,
		byte Model,
		Gen3Connection Connection,
		int Presets,
		int GridRows,
		int GridColumns,
		int RawPatchWords,
		// Which record of the chain comes first: the FM9 writes record 200 first, the FM3 and III after record 201.
		bool Record200First,
		int AmpTypeColumn,
		// The Delay block's model (DELAY_MODEL) column, not DELAY_TYPE, the column after it.
		int DelayModelColumn,
		int LegacyCabBank,
		// The Amp words the device recomputes on the active channel when it loads a preset: CFGRID, GRIDHARDNESS and
		// CFHARDNESS (proved on the FM9; assumed on the others until a run confirms it).
		IReadOnlyList<int> RecomputedAmpParameters,
		// Firmware the probe scripts are written for; anything else gets read-only steps.
		IReadOnlyList<string> ScriptedFirmware)
	{
		public static Gen3Device Fm9 { get; } = new("FM9", 0x12, Gen3Connection.UsbMidi, 512, 6, 14, 0x2000, true, 10, 10, 3,
			[87, 109, 119], ["12.00"]);

		public static Gen3Device Fm3 { get; } = new("FM3", 0x11, Gen3Connection.UsbSerial, 512, 4, 12, 0x2000, false, 6, 6, 3,
			[83, 105, 115], ["13.00"]);

		// Mk I holds 512 presets, Mk II and Turbo 1,024; all three send model 0x10 (see HoldsPreset1023).
		public static Gen3Device AxeFx3 { get; } = new("Axe-Fx III", 0x10, Gen3Connection.UsbMidi, 1024, 6, 14, 0x4000, false, 0, 0, 4,
			[81, 103, 113], ["32.06"]);

		public static IReadOnlyList<Gen3Device> All { get; } = [Fm9, Fm3, AxeFx3];

		public static Gen3Device? FromModel(byte model) => All.FirstOrDefault(device => device.Model == model);

		// The 0x78 data frames a preset dump carries: 0x2000 words fit in 8, the Axe-Fx III's 0x4000 in 16.
		public int DumpDataFrames => RawPatchWords / 0x400;

		public bool IsScripted(string firmware) => ScriptedFirmware.Contains(firmware, StringComparer.Ordinal);

		// The same message for this device: a frame built for the FM9 with byte 4 (the model) and the checksum rewritten.
		public FractalSysExFrame Retarget(FractalSysExFrame frame) => WithModel(frame.Bytes.Span, Model);

		// A reply from this device as the FM9 would send it, so the FM9 parsers can read it. Frames from another model,
		// or anything that isn't a Fractal frame, pass through unchanged and fail the FM9 checks as they should.
		public FractalSysExFrame Normalise(FractalSysExFrame frame) =>
			IsFractalFrame(frame.Bytes.Span, Model) ? WithModel(frame.Bytes.Span, Fm9.Model) : frame;

		private static bool IsFractalFrame(ReadOnlySpan<byte> bytes, byte model) =>
			bytes.Length >= 8 && bytes[0] == 0xF0 && bytes[^1] == 0xF7 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x74 &&
			bytes[4] == model;

		private static FractalSysExFrame WithModel(ReadOnlySpan<byte> bytes, byte model)
		{
			if (!IsFractalFrame(bytes, bytes.Length > 4 ? bytes[4] : (byte)0))
				throw new ArgumentException("Not a Fractal SysEx frame.", nameof(bytes));
			byte[] copy = bytes.ToArray();
			// The checksum covers bytes 0 to the one before it, so it changes with the model byte.
			bool hadChecksum = Checksum(bytes) == bytes[^2];
			copy[4] = model;
			if (hadChecksum)
				copy[^2] = Checksum(copy);
			return new FractalSysExFrame(copy);
		}

		// XOR of every byte before the checksum, 7 bits (F0 included), as the FM9 computes it.
		private static byte Checksum(ReadOnlySpan<byte> bytes)
		{
			int value = 0;
			for (int index = 0; index < bytes.Length - 2; index++)
				value ^= bytes[index];
			return (byte)(value & 0x7F);
		}
	}
}
