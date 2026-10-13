using System.Text;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Fm9.Tests
{
	// A simulated FM9 that answers the SysEx the tools send: firmware and status, the preset and scene showing, preset
	// reads (stored slots and the edit buffer), acknowledged imports into the edit buffer, scene changes and Stores.
	internal sealed class FakeFm9 : IFractalMidiTransport, IDisposable
	{
		private readonly List<byte[]> _import = [];
		private readonly Fm9PresetDump _erasedTemplate;

		public FakeFm9(Fm9PresetDump erasedTemplate) => _erasedTemplate = erasedTemplate;

		public Dictionary<int, Fm9PresetDump> Slots { get; } = [];
		public Fm9PresetDump? EditBuffer { get; set; }
		public int Preset { get; set; } = 500;
		public int Scene { get; set; } = 1;
		public (byte Major, byte Minor) Firmware { get; set; } = (12, 0);
		public int Stores { get; private set; }
		public int Imports { get; private set; }
		public int Opens { get; set; }
		// What the FM9 does to an imported preset before holding it (it recalculates some Amp values for a new model).
		public Func<Fm9PresetDump, Fm9PresetDump> OnImport { get; set; } = dump => dump;

		public Task SelectPresetAsync(int presetNumber, int midiChannel, CancellationToken cancellationToken)
		{
			Preset = presetNumber;
			Scene = 1;
			EditBuffer = Slots.GetValueOrDefault(presetNumber);
			return Task.CompletedTask;
		}

		public Task SendAsync(FractalSysExFrame frame, CancellationToken cancellationToken)
		{
			ReadOnlySpan<byte> bytes = frame.Bytes.Span;
			for (int scene = 1; scene <= 8; scene++)
				if (bytes.SequenceEqual(FractalSysExCodec.CreateSceneSelection(scene).Bytes.Span))
				{
					// As on the FM9, the scene playing is body word 0.
					Scene = scene;
					if (EditBuffer is not null)
					{
						byte[] body = Fm9PresetBodyDecoder.Decode(EditBuffer).Bytes.ToArray();
						BitConverter.TryWriteBytes(body.AsSpan(0, 2), (ushort)(scene - 1));
						EditBuffer = Fm9PresetBodyEncoder.Reencode(EditBuffer, body);
					}
					return Task.CompletedTask;
				}
			// A live parameter change (function 0x01, subcommand 0x52 continuous / 0x09 discrete) lands on the channel the
			// block plays in the current scene; an Amp model change loads that model's voicing, as the FM9 does.
			if (bytes[5] == 0x01 && bytes[6] is 0x52 or 0x09)
			{
				int effect = bytes[8] | (bytes[9] << 7), id = bytes[10] | (bytes[11] << 7);
				uint bits = 0;
				for (int index = 0; index < 5; index++) bits |= (uint)bytes[12 + index] << (index * 7);
				float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
				int word = bytes[6] == 0x52 ? (int)Math.Round(value * 65534) : (int)value < 0 ? (int)value + 65536 : (int)value;
				ParameterChanges.Add((effect, id, word));
				Mutate(effect, (body, record, channel) =>
				{
					if (effect == 58 && id == 10 && Fractal.Companion.Catalog.Fm9ModelDefaults.For("Amp", word) is { } voicing)
						foreach ((int column, int voiced) in voicing) Put(body, record, channel, column, voiced);
					Put(body, record, channel, id, word);
				});
				return Task.CompletedTask;
			}
			for (int slot = 0; slot < 512; slot++)
				if (bytes.SequenceEqual(FractalSysExCodec.CreateStorePreset(slot).Bytes.Span))
				{
					Slots[slot] = EditBuffer ?? throw new InvalidOperationException("Store with an empty edit buffer.");
					Stores++;
					return Task.CompletedTask;
				}
			throw new InvalidOperationException($"Unexpected SysEx function 0x{bytes[5]:X2} sent without a reply.");
		}

		public Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(FractalSysExFrame request, CancellationToken cancellationToken)
		{
			ReadOnlySpan<byte> bytes = request.Bytes.Span;
			byte function = bytes[5];
			IReadOnlyList<FractalSysExFrame> reply = function switch
			{
				0x08 => [Frame(0x08, [Firmware.Major, Firmware.Minor])],
				0x13 => [Frame(0x13, [])],
				0x0D => [Frame(0x0D, [(byte)(Preset & 0x7F), (byte)(Preset >> 7), .. Name(EditBuffer?.PresetName ?? "")])],
				0x0C => [Frame(0x0C, [(byte)(Scene - 1)])],
				0x0A or 0x0B => BlockState(function, bytes[6] | (bytes[7] << 7), bytes[8]),
				0x03 => Read((bytes[6] << 7) | bytes[7]),
				0x77 or 0x78 or 0x79 => Import(request),
				_ => throw new InvalidOperationException($"Unexpected request function 0x{function:X2}.")
			};
			return Task.FromResult(reply);
		}

		public List<(int Effect, int Id, int Word)> ParameterChanges { get; } = [];

		// Block bypass (0x0A) and channel (0x0B) in the current scene; 0x7F asks.
		private IReadOnlyList<FractalSysExFrame> BlockState(byte function, int effect, byte value)
		{
			Fm9UploadedPreset preset = Fm9UploadedPreset.Read(EditBuffer!);
			Fm9UploadedBlock block = preset.Blocks.Single(candidate => candidate.EffectId == effect);
			if (value != 0x7F)
			{
				byte[] bytes = function == 0x0B
					? Fm9PresetBodySceneAuthor.WithSceneChannel(preset.Body, block.Record!, Scene, (Fm9SceneChannel)value)
					: Fm9PresetBodySceneAuthor.WithSceneBypass(preset.Body, block.Record!, Scene, value == 1);
				EditBuffer = Fm9PresetBodyEncoder.Reencode(EditBuffer!, bytes);
				preset = Fm9UploadedPreset.Read(EditBuffer);
				block = preset.Blocks.Single(candidate => candidate.EffectId == effect);
			}
			Fm9PresetBlockSceneState state = preset.GetSceneState(block, Scene);
			return [Frame(function, [(byte)(effect & 0x7F), (byte)(effect >> 7), function == 0x0B ? (byte)state.Channel : (byte)(state.Bypassed ? 1 : 0)])];
		}

		private void Mutate(int effect, Action<byte[], Fm9PresetBlockRecord, int> change)
		{
			Fm9UploadedPreset preset = Fm9UploadedPreset.Read(EditBuffer!);
			Fm9UploadedBlock block = preset.Blocks.Single(candidate => candidate.EffectId == effect);
			byte[] bytes = preset.Body.Bytes.ToArray();
			change(bytes, block.Record!, (int)preset.GetSceneState(block, Scene).Channel);
			EditBuffer = Fm9PresetBodyEncoder.Reencode(EditBuffer!, bytes);
		}

		private static void Put(byte[] body, Fm9PresetBlockRecord record, int channel, int id, int word) =>
			BitConverter.TryWriteBytes(body.AsSpan(Fm9ChannelParameterStorage.GetOffset(record, channel, id)), checked((ushort)word));

		private IReadOnlyList<FractalSysExFrame> Read(int slot)
		{
			if (slot == 0x3FFF) return (EditBuffer ?? throw new InvalidOperationException("The edit buffer is empty.")).Frames;
			return Slots.TryGetValue(slot, out Fm9PresetDump? stored) ? stored.Frames : Erased(_erasedTemplate.Frames);
		}

		private IReadOnlyList<FractalSysExFrame> Import(FractalSysExFrame frame)
		{
			byte function = frame.Bytes.Span[5];
			if (function == 0x77) _import.Clear();
			_import.Add(frame.Bytes.ToArray());
			if (function == 0x79)
			{
				EditBuffer = OnImport(Fm9PresetDump.Parse(_import.SelectMany(bytes => bytes).ToArray()));
				Imports++;
			}
			return [Frame(0x64, [function, 0])];
		}

		public void Dispose() { }

		private static byte[] Name(string name)
		{
			byte[] bytes = new byte[32];
			Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
			return bytes;
		}

		// Erased flash: every data word reads 0xFFFF.
		private static IReadOnlyList<FractalSysExFrame> Erased(IReadOnlyList<FractalSysExFrame> frames) =>
			frames.Select(frame =>
			{
				byte[] bytes = frame.Bytes.ToArray();
				if (bytes[5] != 0x78) return frame;
				for (int index = 8; index + 2 < bytes.Length - 2; index += 3)
					(bytes[index], bytes[index + 1], bytes[index + 2]) = (0x7F, 0x7F, 0x03);
				byte checksum = 0;
				for (int index = 0; index < bytes.Length - 2; index++) checksum ^= bytes[index];
				bytes[^2] = (byte)(checksum & 0x7F);
				return new FractalSysExFrame(bytes);
			}).ToArray();

		private static FractalSysExFrame Frame(byte function, byte[] payload)
		{
			byte[] body = [0x00, 0x01, 0x74, 0x12, function, .. payload];
			byte checksum = 0xF0;
			foreach (byte value in body) checksum ^= value;
			byte[] frame = [0xF0, .. body, (byte)(checksum & 0x7F), 0xF7];
			return new FractalSysExFrame(frame);
		}
	}
}
