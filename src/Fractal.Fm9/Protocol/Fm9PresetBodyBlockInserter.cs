using Fractal.Companion.Catalog;

namespace Fractal.Companion.Protocol
{
	// Adds a block to a preset offline exactly as the FM9 does. Placing a block on hardware (FM9 edit buffer, 2026-10-04:
	// Delay 1 mid-chain, Delay 2, Ten-Tap at the end) changed only its grid cell and inserted one unit into the record chain:
	// the 12-byte prefix carrying the effect id, then the block's default record (hardware-captured) less its last 12
	// bytes, which belong to the next unit's prefix. The unit takes its place in the FM9's record order (Fm9RecordOrder:
	// family, then instance). Rebuilding those three presets this way matched the FM9's bodies byte for byte; ordering by
	// effect id, as this once did, gave the same result for them but misplaced e.g. a Compressor ahead of the Amp.
	// The grid shape and the prefix's word 1 come from the body's device (Fm9PresetBody.Device); the block order is the same
	// on all three units, and blocks sit before record 201 on each. Default records are the FM9's own, so the FM3 and
	// Axe-Fx III take a record from the caller (WithBlockRecord) until their defaults are captured.
	public static class Fm9PresetBodyBlockInserter
	{
		private const int PrefixBytes = 12;
		private const int GridOffset = 0x104;
		private const int GridCellSize = 4;
		private const int ModifierColumns = 25;
		private static readonly Lazy<Fm9HardwareRecordCatalog> FirstInstances = new(() =>
			Fm9HardwareRecordCatalog.LoadBundledAsync().GetAwaiter().GetResult());
		private static readonly Lazy<Fm9HardwareNonFirstInstanceRecordCatalog> OtherInstances = new(() =>
			Fm9HardwareNonFirstInstanceRecordCatalog.LoadBundledAsync().GetAwaiter().GetResult());

		// Whether the FM9's default record for this block is known (every hardware-captured first instance and 45 others).
		public static bool CanInsert(string blockType, int instance) => DefaultRecord(blockType, instance) is not null;

		public static byte[] WithBlock(Fm9PresetBody body, string blockType, int instance, GridPosition cell)
		{
			ArgumentNullException.ThrowIfNull(body);
			cell.Validate(body.Device);
			int effectId = Fm9BlockSettings.EffectIdOf(blockType, instance)
				?? throw new NotSupportedException($"{blockType} {instance} is not a block the FM9 accepts.");
			if (body.Device != Gen3Device.Fm9)
				throw new NotSupportedException($"FracTone does not have the {body.Device.Name}'s default {blockType} {instance} record yet.");
			byte[] record = DefaultRecord(blockType, instance)
				?? throw new NotSupportedException($"FracTone does not have the FM9's default {blockType} {instance} record.");
			return Insert(body, effectId, record, cell, $"{blockType} {instance}");
		}

		// Adds a block from a record the caller has: the record's header and parameters followed by 12 bytes (the next
		// unit's prefix, as a decoded Fm9PresetBlockRecord's span ends), e.g. one captured from the device itself.
		public static byte[] WithBlockRecord(Fm9PresetBody body, int effectId, ReadOnlySpan<byte> record, GridPosition cell)
		{
			ArgumentNullException.ThrowIfNull(body);
			cell.Validate(body.Device);
			if (!Fm9RecordOrder.IsBlock(effectId))
				throw new ArgumentOutOfRangeException(nameof(effectId), $"{effectId} is not a block's effect id.");
			if (record.Length < 46 || (BitConverter.ToUInt16(record[30..]) * BitConverter.ToUInt16(record[32..]) * 2) + 46 != record.Length)
				throw new ArgumentException("The record's length does not match its columns and rows.", nameof(record));
			return Insert(body, effectId, record.ToArray(), cell, $"block {effectId}");
		}

		private static byte[] Insert(Fm9PresetBody body, int effectId, byte[] record, GridPosition cell, string label)
		{
			if (body.GridCells.Any(existing => existing.EffectId == effectId))
				throw new InvalidOperationException($"The preset already has {label}.");
			int gridOffset = CellOffset(body.Device, cell);
			if (BitConverter.ToUInt16(body.Bytes.Span[gridOffset..]) != 0)
				throw new InvalidOperationException($"Grid row {cell.Row} column {cell.Column} is already in use.");

			// The FM9 keeps block records in a fixed family order, then by instance (Fm9RecordOrder), so the unit goes before the
			// first block record that sorts after it, or after the last block record (before record 201, which follows them).
			Fm9PresetBlockRecord[] blocks = body.BlockRecords.Where(existing => existing.Offset >= PrefixBytes &&
				(existing.ParameterColumns != ModifierColumns || existing.ParameterRows != 1) &&
				Fm9RecordOrder.IsBlock(BitConverter.ToUInt16(body.Bytes.Span[(existing.Offset - PrefixBytes)..]))).ToArray();
			if (blocks.Length == 0) throw new InvalidOperationException("The preset has no block records to place the new block among.");
			(int Rank, int Instance) key = Fm9RecordOrder.Key(effectId);
			int position = blocks.FirstOrDefault(existing =>
				Fm9RecordOrder.Key(BitConverter.ToUInt16(body.Bytes.Span[(existing.Offset - PrefixBytes)..])).CompareTo(key) > 0)?.Offset - PrefixBytes
				?? blocks[^1].Offset + blocks[^1].Length - PrefixBytes;

			byte[] unit = new byte[record.Length];
			BitConverter.TryWriteBytes(unit, (ushort)effectId);
			// Zero on the FM9 and FM3; the Axe-Fx III marks some families with 0xFFFF.
			BitConverter.TryWriteBytes(unit.AsSpan(sizeof(ushort)), Fm9RecordOrder.PrefixWord1(body.Device, effectId));
			record.AsSpan(0, record.Length - PrefixBytes).CopyTo(unit.AsSpan(PrefixBytes));
			byte[] bytes = [.. body.Bytes.Span[..position], .. unit, .. body.Bytes.Span[position..]];
			BitConverter.TryWriteBytes(bytes.AsSpan(gridOffset), (ushort)effectId);
			return bytes;
		}

		// Takes a block out as the FM9 deletes one (its record unit removed: rebuilding the FM9's own delete of Gate 1 this way
		// matched byte for byte), leaving a shunt that keeps the cell's cables so the signal still passes. The shunt takes
		// the lowest free id from 1024 up, as the FM9 numbers the shunts it places.
		public static byte[] WithoutBlock(Fm9PresetBody body, int effectId)
		{
			ArgumentNullException.ThrowIfNull(body);
			Fm9PresetGridCell cell = body.GridCells.SingleOrDefault(candidate => candidate.EffectId == effectId)
				?? throw new InvalidOperationException($"The preset has no block {effectId} on its grid.");
			Fm9PresetBlockRecord record = body.BlockRecords.SingleOrDefault(candidate => candidate.Offset >= PrefixBytes &&
				BitConverter.ToUInt16(body.Bytes.Span[(candidate.Offset - PrefixBytes)..]) == effectId)
				?? throw new InvalidDataException($"Block {effectId} has no record of its own.");
			int start = record.Offset - PrefixBytes;
			byte[] bytes = [.. body.Bytes.Span[..start], .. body.Bytes.Span[(start + record.Length)..]];
			int shunt = Enumerable.Range(FirstShuntId, 1024).First(id => body.GridCells.All(candidate => candidate.EffectId != id));
			BitConverter.TryWriteBytes(bytes.AsSpan(CellOffset(body.Device, new(cell.Row, cell.Column))), (ushort)shunt);
			return bytes;
		}

		// Puts a block in a shunt's cell, keeping the shunt's cables (the inverse of WithoutBlock).
		public static byte[] WithBlockInShunt(Fm9PresetBody body, string blockType, int instance, GridPosition cell) =>
			InShunt(body, cell, cleared => WithBlock(cleared, blockType, instance, cell));

		public static byte[] WithBlockRecordInShunt(Fm9PresetBody body, int effectId, ReadOnlyMemory<byte> record, GridPosition cell) =>
			InShunt(body, cell, cleared => WithBlockRecord(cleared, effectId, record.Span, cell));

		private static byte[] InShunt(Fm9PresetBody body, GridPosition cell, Func<Fm9PresetBody, byte[]> insert)
		{
			ArgumentNullException.ThrowIfNull(body);
			cell.Validate(body.Device);
			int gridOffset = CellOffset(body.Device, cell);
			if (BitConverter.ToUInt16(body.Bytes.Span[gridOffset..]) < FirstShuntId)
				throw new InvalidOperationException($"Grid row {cell.Row} column {cell.Column} holds no shunt.");
			byte[] cleared = body.Bytes.ToArray();
			BitConverter.TryWriteBytes(cleared.AsSpan(gridOffset), (ushort)0);
			ushort routes = BitConverter.ToUInt16(body.Bytes.Span[(gridOffset + sizeof(ushort))..]);
			byte[] inserted = insert(body with
			{
				Bytes = cleared,
				GridCells = body.GridCells.Select(existing => existing.Row == cell.Row && existing.Column == cell.Column
					? existing with { EffectId = null } : existing).ToArray()
			});
			BitConverter.TryWriteBytes(inserted.AsSpan(gridOffset + sizeof(ushort)), routes);
			return inserted;
		}

		private static int CellOffset(Gen3Device device, GridPosition cell) =>
			GridOffset + (((cell.Column - 1) * device.GridRows + (cell.Row - 1)) * GridCellSize);

		private const int FirstShuntId = 1024;

		// Channel A's parameter words in the FM9's own default record for a block (what a newly placed block holds).
		public static IReadOnlyList<int>? DefaultChannelWords(string blockType, int instance)
		{
			if (DefaultRecord(blockType, instance) is not byte[] record || Fm9BlockSettings.RecordShape(blockType) is not { } shape)
				return null;
			int[] words = new int[shape.Columns];
			for (int id = 0; id < words.Length && (17 + id) * 2 + 1 < record.Length; id++)
				words[id] = BitConverter.ToUInt16(record, (17 + id) * 2);
			return words;
		}

		private static byte[]? DefaultRecord(string blockType, int instance)
		{
			try
			{
				return Convert.FromBase64String(instance == 1
					? FirstInstances.Value.GetLayout(blockType, 1).SerializedRecordBase64
					: OtherInstances.Value.GetRecord(blockType, instance).SerializedRecordBase64);
			}
			catch (KeyNotFoundException)
			{
				return null;
			}
		}
	}
}
