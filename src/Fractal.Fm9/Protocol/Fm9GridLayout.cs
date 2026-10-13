namespace Fractal.Companion.Protocol
{
	// RouteMask has a bit per source row in the previous column (bit 0 = row 1), as in the preset body's grid.
	public sealed record Fm9GridCell(
		int Row,
		int Column,
		int? EffectId,
		bool IsShunt,
		ushort RouteMask);

	// The live grid (function 01 2E): a bitstream of 7-bit SysEx bytes ending at the checksum. From bit 45 each cell is
	// two little-endian 16-bit words, effect id then route mask, column by column, as the preset body stores the grid.
	// Hardware-checked 2026-10-08: all 84 cells of a preset with Drive 1 (id 146), shunts 1024-1025 and a two-row route
	// matched its stored grid. Reading from bit 46 (as this once did) kept only the id's low 7 bits and shifted the
	// route mask, so Send/Return (182-189) read back as PEQ and Amp ids and the MIDI block as Cab 1.
	public static class Fm9GridLayout
	{
		public const int FirstShuntId = 1024;
		private const int Rows = 6;
		private const int Columns = 14;
		private const int BaseBit = 45;
		private const int CellBits = 32;

		public static IReadOnlyList<Fm9GridCell>? TryParse(ReadOnlySpan<byte> bytes)
		{
			if (!FractalSysExCodec.IsValidFm9Frame(bytes) || bytes[5] != 0x01 || bytes[6] != 0x2E) return null;

			int regionByteCount = (BaseBit + (Columns * Rows * CellBits) + 6) / 7;
			int regionOffset = bytes.Length - 2 - regionByteCount;
			if (regionOffset < 350) return null;

			ReadOnlySpan<byte> region = bytes.Slice(regionOffset, regionByteCount);
			List<Fm9GridCell> cells = [];
			for (int column = 0; column < Columns; column++)
			{
				for (int row = 0; row < Rows; row++)
				{
					int cellBit = BaseBit + (((column * Rows) + row) * CellBits);
					int effectId = Word(region, cellBit);
					if (effectId == 0) continue;
					bool isShunt = effectId >= FirstShuntId;
					cells.Add(new Fm9GridCell(row + 1, column + 1, isShunt ? null : effectId, isShunt, (ushort)Word(region, cellBit + 16)));
				}
			}

			return cells;
		}

		public static bool HasIncomingCable(Fm9GridCell destination, int sourceRow)
		{
			ArgumentNullException.ThrowIfNull(destination);
			if (sourceRow is < 1 or > Rows) throw new ArgumentOutOfRangeException(nameof(sourceRow), "FM9 grid source rows are 1 through 6.");
			return (destination.RouteMask & (1 << (sourceRow - 1))) != 0;
		}

		private static int Word(ReadOnlySpan<byte> data, int bitOffset) =>
			ReadBits(data, bitOffset, 8) | (ReadBits(data, bitOffset + 8, 8) << 8);

		private static int ReadBits(ReadOnlySpan<byte> data, int bitOffset, int bitCount)
		{
			int value = 0;
			for (int index = 0; index < bitCount; index++)
			{
				int bit = bitOffset + index;
				value = (value << 1) | ((data[bit / 7] >> (6 - (bit % 7))) & 1);
			}

			return value;
		}
	}
}
