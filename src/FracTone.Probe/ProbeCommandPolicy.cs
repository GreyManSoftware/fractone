using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	public sealed class ProbeRefusedException(string message) : Exception(message);

	// The allow-list from FracCompat's device brief (section 4): every outgoing frame is checked here before it reaches the
	// port, so a script or a bug can't send anything else. Reads: 08 0C 0D 13 03 and 1F (one block's values). Edit-buffer
	// loads: 77/78/79 addressed 7F 7F. Function 01 only as 27 (switch, to the safe slot or back to the preset found
	// showing), 09/52 (live type and value changes, never to Global or Control blocks) and 30/32/35 (grid edits, inside the
	// device's grid, never inserting a Global or Control block); 26 (store) only to the safe slot and only when level 3 was
	// opted into. Never firmware frames, never Program Change, never 01 0A/0B/0E or 01 02/03/0C/0D.
	public sealed class ProbeCommandPolicy(Gen3Device device, int safeSlot, int? presetFound, bool allowStore)
	{
		// Global, Controllers and the FC blocks: on the Axe-Fx III writes to them save at once and can't be undone.
		private static readonly int[] ProtectedEffects = [2, 199, 200, 201];

		public Gen3Device Device { get; } = device;

		// The identity step runs before the device is known: only the firmware request, in any gen-3 model's byte.
		public static void CheckIdentity(ReadOnlySpan<byte> frame)
		{
			CheckShape(frame);
			if (Gen3Device.FromModel(frame[4]) is null || frame[5] != 0x08 || frame.Length != 8)
				throw new ProbeRefusedException($"Only the firmware request may be sent before the device is identified ({Describe(frame)}).");
		}

		public void Check(ReadOnlySpan<byte> frame)
		{
			CheckShape(frame);
			if (frame[4] != Device.Model)
				throw new ProbeRefusedException($"Every frame must carry the {Device.Name}'s model byte 0x{Device.Model:X2} ({Describe(frame)}).");
			byte function = frame[5];
			switch (function)
			{
				case 0x08 or 0x13 when frame.Length == 8:
					return;
				case 0x0D when frame.Length == 10 && frame[6] == 0x7F && frame[7] == 0x7F:
					return;
				case 0x0C when frame.Length == 9 && (frame[6] == 0x7F || frame[6] < 8):
					return;
				case 0x03 when frame.Length == 10:
					return;
				case 0x1F when frame.Length == 10:
					return;
				case 0x77 when frame[6] == 0x7F && frame[7] == 0x7F:
					return;
				case 0x77:
					throw new ProbeRefusedException("A preset may be sent only to the edit buffer (header 7F 7F); storing goes through 01 26.");
				case 0x78 or 0x79:
					return;
				case 0x01 when frame.Length > 8:
					CheckFunctionOne(frame);
					return;
				default:
					throw new ProbeRefusedException($"Function 0x{function:X2} is not on the probe's allow-list ({Describe(frame)}).");
			}
		}

		// Whether a frame can change what the device holds or plays, so the run knows a restore is needed.
		public static bool ChangesState(ReadOnlySpan<byte> frame) => frame[5] switch
		{
			0x77 or 0x78 or 0x79 or 0x01 => true,
			0x0C => frame[6] != 0x7F,
			_ => false
		};

		private void CheckFunctionOne(ReadOnlySpan<byte> frame)
		{
			byte sub = frame[6];
			switch (sub)
			{
				case 0x27:
					int target = frame[12] | (frame[13] << 7);
					if (target != safeSlot && target != presetFound)
						throw new ProbeRefusedException($"01 27 may switch only to the safe slot {safeSlot} or back to the preset found showing (asked {target}).");
					return;
				case 0x26:
					int slot = frame[12] | (frame[13] << 7);
					if (!allowStore)
						throw new ProbeRefusedException("Storing (01 26) needs level 3, which is opt-in.");
					if (slot != safeSlot)
						throw new ProbeRefusedException($"01 26 may store only to the safe slot {safeSlot} (asked {slot}).");
					return;
				case 0x09 or 0x52:
					int effect = frame[8] | (frame[9] << 7);
					if (ProtectedEffects.Contains(effect))
						throw new ProbeRefusedException($"Live writes to Global or Control blocks (effect {effect}) are never sent.");
					return;
				case 0x30 when frame.Length == 23:
					CheckCell(frame[12] | (frame[13] << 7) | (frame[14] << 14) | (frame[15] << 21) | (frame[16] << 28), "01 30");
					return;
				case 0x32 when frame.Length == 23:
					int inserted = frame[8] | (frame[9] << 7);
					if (ProtectedEffects.Contains(inserted))
						throw new ProbeRefusedException($"01 32 may not insert a Global or Control block (effect {inserted}).");
					CheckCell(frame[12] | (frame[13] << 7), "01 32");
					return;
				case 0x35 when frame.Length == 26:
					// Two cells packed MSB-first into septets (FractalSysExCodec.CreateGridRoute).
					CheckCell((frame[21] << 1) | (frame[22] >> 6), "01 35");
					CheckCell(((frame[22] & 0x3F) << 2) | (frame[23] >> 5), "01 35");
					return;
				default:
					// 01 0A/0B step a value on every read, 01 0E marks the preset edited, 01 02/03/0C/0D corrupted a live amp.
					throw new ProbeRefusedException($"Function 01 sub-type 0x{sub:X2} is not on the probe's allow-list.");
			}
		}

		// The FM3's grid is 4 x 12: a cell number past the device's grid is another unit's layout or a bug.
		private void CheckCell(int cell, string what)
		{
			if (cell < 0 || cell >= Device.GridRows * Device.GridColumns)
				throw new ProbeRefusedException($"{what} names cell {cell}, outside the {Device.Name}'s {Device.GridRows} x {Device.GridColumns} grid.");
		}

		private static void CheckShape(ReadOnlySpan<byte> frame)
		{
			if (!ProbeFrames.HasValidChecksum(frame))
				throw new ProbeRefusedException("Only well-formed Fractal SysEx frames are sent.");
		}

		private static string Describe(ReadOnlySpan<byte> frame) =>
			frame.Length > 16 ? ProbeFrames.Hex(frame[..16]) + " ..." : ProbeFrames.Hex(frame);
	}
}
