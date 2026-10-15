using System.Text;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace FracTone.Probe.Tests
{
	// A simulated gen-3 unit speaking its own model byte: firmware, status, the preset and scene showing, preset reads
	// (stored slots and the edit buffer, as the frames it holds), acknowledged edit-buffer loads, 01 27 switches, scene
	// changes and 01 26 stores, plus the level-2 edits on the edit buffer: 01 09 type changes (with a voicing hook), 1F
	// block reads, 01 30/32 inserts and 01 35 cables, numbered by the unit's own grid. Faults are injected per test: dropped
	// loads and writes, silent requests, a hook on every frame.
	internal sealed class SimulatedGen3 : IFractalMidiTransport
	{
		private readonly List<byte[]> _import = [];

		public SimulatedGen3(byte model, (byte Major, byte Minor) firmware)
		{
			Model = model;
			Firmware = firmware;
		}

		public byte Model { get; }
		public (byte Major, byte Minor) Firmware { get; set; }
		public Dictionary<int, IReadOnlyList<byte[]>> Slots { get; } = [];
		public IReadOnlyList<byte[]> EditBuffer { get; set; } = [];
		public int Preset { get; set; }
		public int Scene { get; set; } = 1;
		public int Stores { get; private set; }
		public int Loads { get; private set; }
		public int Switches { get; private set; }
		public List<byte[]> Received { get; } = [];
		// Loads to ignore (acknowledged, but the buffer keeps what it held), as the FM9 did once after a preset change.
		public int DropLoads { get; set; }
		// Requests matching this stay silent (the remaining count says how many).
		public Func<byte[], bool>? Silent { get; set; }
		public int SilentCount { get; set; }
		// What the unit does to a loaded preset before holding it.
		public Func<IReadOnlyList<byte[]>, IReadOnlyList<byte[]>> OnLoad { get; set; } = frames => frames;
		public Action<byte[]>? OnFrame { get; set; }
		// Answers in its own model byte whatever model a frame carries (as a unit replying "wrong model" might).
		public bool AnswersAnyModel { get; set; }
		// The words a type change loads on channel A, by effect id and ordinal (the unit's voicing table).
		public Func<int, int, IReadOnlyDictionary<int, int>?> Voicing { get; set; } = (_, _) => null;
		// Edits (01 09, 01 32, 01 35) to ignore, as a unit that missed one would.
		public int DropWrites { get; set; }
		// Inserts take a block's record from this preset when it has one (else a blank 20 x 4 record).
		public IReadOnlyList<byte[]>? Donor { get; set; }
		// How many presets the unit holds, for 0D asked by number: an Axe-Fx III Mk I has 512 and answers 64 0D 0E past them.
		public int? PresetCount { get; set; }
		private Gen3Device Device => Gen3Device.FromModel(Model)!;
		private int _selectedCell = -1;

		public static IReadOnlyList<byte[]> Load(string file) =>
			ProbeFrames.Split(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Presets", file)));

		public Task SelectPresetAsync(int presetNumber, int midiChannel, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Program Change must never be used.");

		public Task SendAsync(FractalSysExFrame frame, CancellationToken cancellationToken)
		{
			byte[] bytes = Accept(frame);
			if (bytes[4] != Model) return Task.CompletedTask;
			if (bytes[5] == 0x0C && bytes[6] < 8)
			{
				Scene = bytes[6] + 1;
				return Task.CompletedTask;
			}
			if (bytes[5] == 0x01 && bytes[6] == 0x27)
			{
				Preset = bytes[12] | (bytes[13] << 7);
				EditBuffer = Slots[Preset];
				Scene = 1;
				Switches++;
				return Task.CompletedTask;
			}
			if (bytes[5] == 0x01 && bytes[6] == 0x26)
			{
				Slots[bytes[12] | (bytes[13] << 7)] = EditBuffer;
				Stores++;
				return Task.CompletedTask;
			}
			if (bytes[5] == 0x01 && bytes[6] == 0x30)
			{
				_selectedCell = bytes[12] | (bytes[13] << 7);
				return Task.CompletedTask;
			}
			if (bytes[5] == 0x01 && bytes[6] is 0x09 or 0x32 or 0x35)
			{
				if (DropWrites > 0)
					DropWrites--;
				else if (bytes[6] == 0x09)
					SelectType(bytes[8] | (bytes[9] << 7), bytes[10] | (bytes[11] << 7), (int)BitConverter.Int32BitsToSingle(Septets(bytes, 12)));
				else if (bytes[6] == 0x32)
					Insert(bytes[8] | (bytes[9] << 7), bytes[12] | (bytes[13] << 7));
				else
					Route((bytes[21] << 1) | (bytes[22] >> 6), ((bytes[22] & 0x3F) << 2) | (bytes[23] >> 5));
				return Task.CompletedTask;
			}
			throw new InvalidOperationException($"Unexpected frame sent without a reply: {ProbeFrames.Hex(bytes)}");
		}

		public Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(FractalSysExFrame request, CancellationToken cancellationToken)
		{
			byte[] bytes = Accept(request);
			if ((Silent?.Invoke(bytes) ?? false) && SilentCount > 0)
			{
				SilentCount--;
				return Task.FromException<IReadOnlyList<FractalSysExFrame>>(Unanswered());
			}
			// A unit ignores another model's frames.
			if (bytes[4] != Model && !AnswersAnyModel)
			{
				return Task.FromException<IReadOnlyList<FractalSysExFrame>>(Unanswered());
			}
			IReadOnlyList<byte[]> reply = bytes[5] switch
			{
				0x08 => [Frame(0x08, [Firmware.Major, Firmware.Minor])],
				0x13 => [Frame(0x13, [37, 0, 0x10, 58, 0, 0x10, 62, 0, 0x10])],
				0x0D when bytes[6] != 0x7F || bytes[7] != 0x7F => StoredName(bytes[6] | (bytes[7] << 7)),
				0x0D => [Frame(0x0D, [(byte)(Preset & 0x7F), (byte)(Preset >> 7), .. Name()])],
				0x0C => [Frame(0x0C, [(byte)(Scene - 1)])],
				0x03 => (bytes[6] << 7 | bytes[7]) == ProbeFrames.EditBuffer ? EditBuffer : Slots[bytes[6] << 7 | bytes[7]],
				0x77 or 0x78 or 0x79 => Import(bytes),
				0x1F => BulkRead(bytes[6] | (bytes[7] << 7)),
				_ => throw new InvalidOperationException($"Unexpected request {ProbeFrames.Hex(bytes)}")
			};
			return Task.FromResult<IReadOnlyList<FractalSysExFrame>>([.. reply.Select(frame => new FractalSysExFrame(frame))]);
		}

		// What a real transport throws when nothing answers before its timeout; throwing at once spares each test the
		// real-time wait for the probe's query timeout (the probe treats both alike).
		private static TimeoutException Unanswered() => new("The simulated unit did not answer.");

		private byte[] Accept(FractalSysExFrame frame)
		{
			byte[] bytes = frame.Bytes.ToArray();
			Received.Add(bytes);
			OnFrame?.Invoke(bytes);
			return bytes;
		}

		private IReadOnlyList<byte[]> Import(byte[] frame)
		{
			if (frame[5] == 0x77) _import.Clear();
			_import.Add(frame);
			if (frame[5] == 0x79)
			{
				if (DropLoads > 0)
					DropLoads--;
				else
				{
					EditBuffer = OnLoad([.. _import]);
					Loads++;
				}
			}
			return [Frame(0x64, [frame[5], 0])];
		}

		private Fm9PresetDump Dump => ProbeFrames.Parse(Device, EditBuffer);

		private Fm9PresetBody Body => Fm9PresetBodyDecoder.Decode(Dump, Device);

		private void Write(byte[] body) =>
			EditBuffer = [.. Fm9PresetBodyEncoder.Reencode(Dump, body).Frames.Select(frame => Device.Retarget(frame).Bytes.ToArray())];

		// Channel A's words start 12 (prefix) + 34 (header) bytes into the unit.
		private static int Words(Fm9RecordUnit unit) => unit.Offset + 46;

		private void SelectType(int effect, int parameter, int ordinal)
		{
			Fm9PresetBody body = Body;
			if (Fm9RecordOrder.Units(body).FirstOrDefault(unit => unit.EffectId == effect) is not { } record) return;
			byte[] bytes = body.Bytes.ToArray();
			BitConverter.TryWriteBytes(bytes.AsSpan(Words(record) + (parameter * 2)), (ushort)ordinal);
			foreach ((int id, int word) in Voicing(effect, ordinal) ?? new Dictionary<int, int>())
				BitConverter.TryWriteBytes(bytes.AsSpan(Words(record) + (id * 2)), (ushort)word);
			Write(bytes);
		}

		// As the FM9 inserts: the cell takes the effect id and the chain gains the block's unit, here just before record 201.
		private void Insert(int effect, int cell)
		{
			if (cell != _selectedCell) return;
			Fm9PresetBody body = Body;
			byte[] bytes = body.Bytes.ToArray();
			BitConverter.TryWriteBytes(bytes.AsSpan(0x104 + (cell * 4)), (ushort)effect);
			IReadOnlyList<Fm9RecordUnit> units = Fm9RecordOrder.Units(body);
			if (units.All(unit => unit.EffectId != effect))
			{
				byte[] record = DonorRecord(effect);
				byte[] unit = new byte[12 + record.Length];
				BitConverter.TryWriteBytes(unit, (ushort)effect);
				BitConverter.TryWriteBytes(unit.AsSpan(2), Fm9RecordOrder.PrefixWord1(Device, effect));
				record.CopyTo(unit, 12);
				int at = units.First(existing => existing.EffectId == 201).Offset;
				bytes = [.. bytes[..at], .. unit, .. bytes[at..]];
			}
			Write(bytes);
		}

		private byte[] DonorRecord(int effect)
		{
			if (Donor is not null)
			{
				Fm9PresetBody donor = Fm9PresetBodyDecoder.Decode(ProbeFrames.Parse(Device, Donor), Device);
				if (Fm9RecordOrder.Units(donor).FirstOrDefault(unit => unit.EffectId == effect) is { } found)
					return donor.Bytes.Span[(found.Offset + 12)..(found.Offset + found.Length)].ToArray();
			}
			byte[] blank = new byte[34 + (20 * 4 * 2)];
			BitConverter.TryWriteBytes(blank.AsSpan(30), (ushort)20);
			BitConverter.TryWriteBytes(blank.AsSpan(32), (ushort)4);
			return blank;
		}

		// A cable to the next column sets bit (source row - 1) of the destination's mask; anything else is ignored.
		private void Route(int source, int destination)
		{
			int rows = Device.GridRows;
			Fm9PresetBody body = Body;
			if (destination / rows != (source / rows) + 1 || destination >= body.GridCells.Count) return;
			if (body.GridCells[source].EffectId is null || body.GridCells[destination].EffectId is null) return;
			byte[] bytes = body.Bytes.ToArray();
			BitConverter.TryWriteBytes(bytes.AsSpan(0x104 + (destination * 4) + 2), (ushort)(body.GridCells[destination].RouteMask | (1 << (source % rows))));
			Write(bytes);
		}

		// 74 header, 75 data (three septets a word), 76 end; an effect the buffer lacks gets 64 1F 07.
		private IReadOnlyList<byte[]> BulkRead(int effect)
		{
			Fm9PresetBody body = Body;
			if (Fm9RecordOrder.Units(body).FirstOrDefault(unit => unit.EffectId == effect) is not { } record)
				return [Frame(0x64, [0x1F, 0x07])];
			int[] words = [.. Enumerable.Range(0, record.Columns * record.Rows).Select(index => (int)BitConverter.ToUInt16(body.Bytes.Span[(Words(record) + (index * 2))..]))];
			List<byte[]> frames = [Frame(0x74, [(byte)(effect & 0x7F), (byte)(effect >> 7)])];
			foreach (int[] chunk in words.Chunk(64))
				frames.Add(Frame(0x75, [0, 0, .. chunk.SelectMany(word => new[] { (byte)(word & 0x7F), (byte)((word >> 7) & 0x7F), (byte)((word >> 14) & 0x03) })]));
			frames.Add(Frame(0x76, []));
			return frames;
		}

		private static int Septets(byte[] bytes, int start)
		{
			int value = 0;
			for (int index = 0; index < 5; index++)
				value |= bytes[start + index] << (index * 7);
			return value;
		}

		private IReadOnlyList<byte[]> StoredName(int preset)
		{
			if (preset >= (PresetCount ?? Device.Presets))
				return [Frame(0x64, [0x0D, 0x0E])];
			byte[] name = new byte[32];
			if (Slots.TryGetValue(preset, out IReadOnlyList<byte[]>? frames))
				Encoding.ASCII.GetBytes(ProbeFrames.Parse(Device, frames).PresetName).CopyTo(name, 0);
			return [Frame(0x0D, [(byte)(preset & 0x7F), (byte)(preset >> 7), .. name])];
		}

		private byte[] Name()
		{
			byte[] bytes = new byte[32];
			Encoding.ASCII.GetBytes(ProbeFrames.Parse(Gen3Device.FromModel(Model)!, EditBuffer).PresetName).CopyTo(bytes, 0);
			return bytes;
		}

		private byte[] Frame(byte function, byte[] payload)
		{
			byte[] frame = [0xF0, 0x00, 0x01, 0x74, Model, function, .. payload, 0, 0xF7];
			frame[^2] = ProbeFrames.Checksum(frame);
			return frame;
		}
	}

	// Time passes only when the engine waits, so pacing is measured without sleeping.
	internal sealed class FakeClock : IProbeClock
	{
		public DateTimeOffset Now { get; private set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
		public List<TimeSpan> Delays { get; } = [];

		public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (delay > TimeSpan.Zero)
			{
				Delays.Add(delay);
				Now += delay;
			}
			return Task.CompletedTask;
		}

		public void Advance(TimeSpan by) => Now += by;
	}
}
