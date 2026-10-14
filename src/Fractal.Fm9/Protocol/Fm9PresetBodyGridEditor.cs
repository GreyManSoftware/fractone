using Fractal.Companion.Catalog;

namespace Fractal.Companion.Protocol
{
	// Grid edits follow the body's device (Fm9PresetBody.Device): 6 x 14 on the FM9 and Axe-Fx III, 4 x 12 on the FM3. The
	// Input/Output instance swap stays FM9-only, since it checks against the FM9's Ride template and port catalogue.
	public static class Fm9PresetBodyGridEditor
	{
		private const int GridOffset = 0x104;
		private const int GridCellSize = 4;
		private static readonly Lazy<IReadOnlyDictionary<(string BlockType, int Instance), int>> PortEffects =
			new(() => Task.Run(async () =>
			{
				Fm9HardwareBlockInstanceCatalog instances = await Fm9HardwareBlockInstanceCatalog.LoadBundledAsync();
				Fm9HardwareNonFirstInstanceRecordCatalog records = await Fm9HardwareNonFirstInstanceRecordCatalog.LoadBundledAsync();
				return instances.AcceptedInstances
					.Where(instance => instance.BlockType is "Input" or "Output")
					.Where(instance => instance.Instance == 1 || records.GetRecord(instance.BlockType, instance.Instance) is
						{ SerializedRecordMatchesRepresentative: true, DimensionsMatchRepresentative: true, BulkDefaultsMatchRepresentative: true })
					.ToDictionary(instance => (instance.BlockType, instance.Instance), instance => instance.EffectId);
			}).GetAwaiter().GetResult());

		public static IReadOnlyList<int> InputBlockInstances =>
			PortEffects.Value.Keys.Where(key => key.BlockType == "Input").Select(key => key.Instance).Order().ToArray();
		public static IReadOnlyList<int> OutputBlockInstances =>
			PortEffects.Value.Keys.Where(key => key.BlockType == "Output").Select(key => key.Instance).Order().ToArray();

		public static void ValidateInputOutputInstances(int input, int output)
		{
			_ = ResolvePort("Input", input);
			_ = ResolvePort("Output", output);
		}

		internal static ushort ResolveInputOutputEffect(Fm9PresetBody source, string blockType)
		{
			int[] allowed = PortEffects.Value.Where(pair => pair.Key.BlockType == blockType).Select(pair => pair.Value).ToArray();
			Fm9PresetGridCell[] cells = source.GridCells.Where(cell => cell.EffectId.HasValue && allowed.Contains(cell.EffectId.Value)).ToArray();
			return cells.Length == 1 ? checked((ushort)cells[0].EffectId!.Value)
				: throw new InvalidOperationException($"The export requires exactly one supported {blockType} block.");
		}

		public static byte[] WithInputOutputInstances(Fm9PresetBody source, int input, int output)
		{
			ArgumentNullException.ThrowIfNull(source);
			if (source.Device != Gen3Device.Fm9)
				throw new NotSupportedException($"Input and Output instances are swapped on FM9 presets only, not the {source.Device.Name}'s.");
			ValidateInputOutputInstances(input, output);
			EnsureBlockRecordsArePreserved(source);
			Fm9BlockRecordTemplateProfile profile = Fm9BlockRecordTemplateProfile.CreateRideBaseline(source);
			byte[] bytes = source.Bytes.ToArray();
			foreach ((string blockType, int instance) in new[] { ("Input", input), ("Output", output) })
			{
				_ = profile.GetRecordForExistingBlock(source, blockType, 1);
				int existingEffect = ResolveInputOutputEffect(source, blockType);
				Fm9PresetGridCell[] cells = source.GridCells.Where(cell => cell.EffectId == existingEffect).ToArray();
				if (cells.Length != 1)
					throw new InvalidOperationException($"The export requires exactly one existing {blockType} block.");
				WriteUInt16(bytes, GetCellOffset(new(cells[0].Row, cells[0].Column), source.Device),
					checked((ushort)ResolvePort(blockType, instance)));
			}
			return bytes;
		}

		private static int ResolvePort(string blockType, int instance) =>
			PortEffects.Value.TryGetValue((blockType, instance), out int effect)
				? effect : throw new NotSupportedException($"{blockType} {instance} is not supported by this FM9 export.");

		public static byte[] WithRoute(
			Fm9PresetBody source,
			GridPosition position,
			ushort routeMask)
		{
			ArgumentNullException.ThrowIfNull(source);
			position.Validate(source.Device);
			ValidateRouteMask(routeMask, source.Device);
			EnsureBlockRecordsArePreserved(source);

			byte[] bytes = source.Bytes.ToArray();
			int offset = GetCellOffset(position, source.Device);

			if (ReadUInt16(bytes, offset) == 0)
			{
				throw new InvalidOperationException(
					"An FM9 grid route can only be changed for an existing placed block.");
			}

			WriteUInt16(bytes, offset + sizeof(ushort), routeMask);
			return bytes;
		}

		public static byte[] MovePlacedBlock(
			Fm9PresetBody source,
			GridPosition from,
			GridPosition to)
		{
			ArgumentNullException.ThrowIfNull(source);
			from.Validate(source.Device);
			to.Validate(source.Device);
			EnsureBlockRecordsArePreserved(source);

			if (from == to)
			{
				throw new ArgumentException(
					"The source and destination grid cells must differ.",
					nameof(to));
			}

			byte[] bytes = source.Bytes.ToArray();
			int sourceOffset = GetCellOffset(from, source.Device);
			int destinationOffset = GetCellOffset(to, source.Device);
			ushort effectId = ReadUInt16(bytes, sourceOffset);

			if (effectId == 0)
			{
				throw new InvalidOperationException(
					"Only an existing placed FM9 block can be moved.");
			}

			if (ReadUInt16(bytes, destinationOffset) != 0)
			{
				throw new InvalidOperationException(
					"An FM9 block can only be moved to an empty grid cell.");
			}

			ushort routeMask = ReadUInt16(bytes, sourceOffset + sizeof(ushort));
			WriteUInt16(bytes, sourceOffset, 0);
			WriteUInt16(bytes, sourceOffset + sizeof(ushort), 0);
			WriteUInt16(bytes, destinationOffset, effectId);
			WriteUInt16(bytes, destinationOffset + sizeof(ushort), routeMask);
			return bytes;
		}

		public static byte[] MoveExistingBlocks(
			Fm9PresetBody source,
			IReadOnlyList<Fm9PresetGridMove> moves)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(moves);
			EnsureBlockRecordsArePreserved(source);

			if (moves.Count == 0)
			{
				return source.Bytes.ToArray();
			}

			HashSet<GridPosition> sources = [];
			HashSet<GridPosition> destinations = [];

			foreach (Fm9PresetGridMove move in moves)
			{
				move.From.Validate(source.Device);
				move.To.Validate(source.Device);

				if (!sources.Add(move.From) || !destinations.Add(move.To))
				{
					throw new InvalidOperationException(
						"Each FM9 grid source and destination must be unique.");
				}
			}

			byte[] bytes = source.Bytes.ToArray();

			foreach (Fm9PresetGridMove move in moves)
			{
				int sourceOffset = GetCellOffset(move.From, source.Device);

				if (ReadUInt16(bytes, sourceOffset) != move.ExpectedEffectId)
				{
					throw new InvalidOperationException(
						"The requested FM9 grid move does not match the template block.");
				}

				int destinationOffset = GetCellOffset(move.To, source.Device);

				if (!sources.Contains(move.To) &&
					ReadUInt16(bytes, destinationOffset) != 0)
				{
					throw new InvalidOperationException(
						"An FM9 block can only be moved to an empty grid cell or another requested source cell.");
				}
			}

			List<(ushort EffectId, ushort RouteMask)> values = moves
				.Select(move =>
				{
					int offset = GetCellOffset(move.From, source.Device);
					return (ReadUInt16(bytes, offset), ReadUInt16(bytes, offset + sizeof(ushort)));
				})
				.ToList();

			foreach (Fm9PresetGridMove move in moves)
			{
				int offset = GetCellOffset(move.From, source.Device);
				WriteUInt16(bytes, offset, 0);
				WriteUInt16(bytes, offset + sizeof(ushort), 0);
			}

			for (int index = 0; index < moves.Count; index++)
			{
				int offset = GetCellOffset(moves[index].To, source.Device);
				WriteUInt16(bytes, offset, values[index].EffectId);
				WriteUInt16(bytes, offset + sizeof(ushort), values[index].RouteMask);
			}

			return bytes;
		}

		private static void EnsureBlockRecordsArePreserved(Fm9PresetBody source)
		{
			if (source.BlockRecords.Count == 0)
			{
				throw new InvalidOperationException(
					"The FM9 preset has no serialized block records to preserve.");
			}

			foreach (Fm9PresetBlockRecord record in source.BlockRecords)
			{
				if (record.Offset < 0 || record.Length <= 0 ||
					record.Offset + record.Length > source.Bytes.Length)
				{
					throw new InvalidDataException(
						"The FM9 preset contains an invalid serialized block record.");
				}
			}
		}

		private static int GetCellOffset(GridPosition position, Gen3Device device)
		{
			return GridOffset + (((position.Column - 1) * device.GridRows + (position.Row - 1)) * GridCellSize);
		}

		// A bit per source row in the previous column, so only the device's rows.
		private static void ValidateRouteMask(ushort routeMask, Gen3Device device)
		{
			if ((routeMask & ~((1 << device.GridRows) - 1)) != 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(routeMask),
					$"An {device.Name} grid route mask can only address rows 1 through {device.GridRows}.");
			}
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

	public sealed record Fm9PresetGridMove(
		GridPosition From,
		GridPosition To,
		ushort ExpectedEffectId);
}
