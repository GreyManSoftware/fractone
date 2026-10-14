using Fractal.Companion.Auditioning;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Fm9.Tests
{
	public sealed class Gen3DeviceTests
	{
		[Theory]
		// FracCompat's measured edit-buffer headers (device-written templates): only the model byte and checksum differ
		// between the FM9 and the FM3, whose presets are the same size.
		[InlineData("F0 00 01 74 12 77 7F 7F 00 40 00 20 F7", "F0 00 01 74 11 77 7F 7F 00 40 00 23 F7")]
		public void AnFm9MessageBecomesTheFm3sByItsModelByteAndChecksum(string fm9, string fm3)
		{
			FractalSysExFrame retargeted = Gen3Device.Fm3.Retarget(new FractalSysExFrame(Hex(fm9)));

			Assert.Equal(Hex(fm3), retargeted.Bytes.ToArray());
			Assert.Equal(Hex(fm9), Gen3Device.Fm3.Normalise(retargeted).Bytes.ToArray());
		}

		[Fact]
		public void EachDeviceIsKnownByItsModelByte()
		{
			Assert.Same(Gen3Device.AxeFx3, Gen3Device.FromModel(0x10));
			Assert.Same(Gen3Device.Fm3, Gen3Device.FromModel(0x11));
			Assert.Same(Gen3Device.Fm9, Gen3Device.FromModel(0x12));
			Assert.Null(Gen3Device.FromModel(0x07));
			Assert.Equal((8, 8, 16), (Gen3Device.Fm9.DumpDataFrames, Gen3Device.Fm3.DumpDataFrames, Gen3Device.AxeFx3.DumpDataFrames));
			Assert.Equal((4, 12), (Gen3Device.Fm3.GridRows, Gen3Device.Fm3.GridColumns));
		}

		[Fact]
		public void TheDelayColumnIsEachDevicesDelayModelParameter()
		{
			// DELAY_MODEL's id per device from the gen-3 xref (2026-10-13): FM9 10, FM3 6, Axe-Fx III 0. The brief's 7 and 1
			// were DELAY_TYPE, the column after it; the FM3 script's Delay voicing check sets id 6.
			Assert.Equal((10, 6, 0), (Gen3Device.Fm9.DelayModelColumn, Gen3Device.Fm3.DelayModelColumn, Gen3Device.AxeFx3.DelayModelColumn));
		}

		[Fact]
		public void AnotherModelsFrameIsNotPassedOffAsTheFm9s()
		{
			FractalSysExFrame axe = Gen3Device.AxeFx3.Retarget(FractalSysExCodec.CreateFirmwareRequest());

			Assert.False(FractalSysExCodec.IsValidFm9Frame(Gen3Device.Fm3.Normalise(axe).Bytes.Span));
			Assert.True(FractalSysExCodec.IsValidFm9Frame(Gen3Device.AxeFx3.Normalise(axe).Bytes.Span));
		}

		[DictionaryFact]
		public async Task TheFm9ReadersWorkOnAnFm3ThroughTheWrapperAndKeepItsOwnBytes()
		{
			Fm9PresetDump preset = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx")));
			FakeFm9 fm9 = new(preset) { EditBuffer = preset, Preset = 7 };
			using Gen3DeviceTransport transport = new(new SpeaksAs(fm9, Gen3Device.Fm3.Model), Gen3Device.Fm3);

			Fm9ReadOnlyProbeResult identity = await new Fm9ReadOnlyProbe(transport).ProbeAsync(CancellationToken.None);
			Fm9StoredPresetReadResult buffer = await new Fm9StoredPresetReader(transport).ReadAsync(0x3FFF, CancellationToken.None);

			Assert.Equal("12.00", identity.FirmwareVersion);
			Assert.Equal(preset.PresetName, buffer.Dump.PresetName);
			// What the device sent is kept as it sent it: model 0x11, valid checksums.
			Assert.All(transport.Exchanges.SelectMany(exchange => exchange.Received.Append(exchange.Sent)), bytes =>
			{
				Assert.Equal(0x11, bytes[4]);
				Assert.True(FractalSysExCodec.IsValidFm9Frame(Gen3Device.Fm3.Normalise(new FractalSysExFrame(bytes)).Bytes.Span));
			});
			await Assert.ThrowsAsync<NotSupportedException>(() => transport.SelectPresetAsync(5, 1, CancellationToken.None));
		}

		[Theory]
		// FracCompat's Ride templates for the FM3 (8 data frames, a 4 x 12 grid) and the Axe-Fx III (16 data frames,
		// 0x4000 words): read as the wrapper hands them over, decoded with the device's grid, and re-encoded unchanged.
		[InlineData("gen3-fm3-13.00-ride.syx", 0x11, 10)]
		[InlineData("gen3-axe3-32.06-ride.syx", 0x10, 18)]
		public void FracCompatsTemplatesForTheOtherUnitsReadDecodeAndReencode(string file, byte model, int frames)
		{
			byte[] bytes = File.ReadAllBytes(Fixtures.Path(file));
			Gen3Device device = Gen3Device.FromModel(model)!;
			List<byte> normalised = [];
			int start = 0;
			for (int index = 0; index < bytes.Length; index++)
				if (bytes[index] == 0xF7)
				{
					normalised.AddRange(device.Normalise(new FractalSysExFrame(bytes[start..(index + 1)])).Bytes.ToArray());
					start = index + 1;
				}

			Fm9PresetDump dump = Fm9PresetDump.Parse([.. normalised]);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump, device);

			Assert.Equal(frames, dump.Frames.Count);
			Assert.Equal(device.GridRows * device.GridColumns, body.GridCells.Count);
			Assert.Equal(8, body.GridCells.Count(cell => cell.EffectId is not null));
			Assert.True(dump.HasSameRawPatch(Fm9PresetBodyEncoder.Reencode(dump, body.Bytes.ToArray())));
		}

		private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal));

		// The simulated FM9 dressed as another model: it only answers frames carrying that model byte, and answers with it.
		private sealed class SpeaksAs(IFractalMidiTransport fm9, byte model) : IFractalMidiTransport
		{
			private static FractalSysExFrame WithModel(FractalSysExFrame frame, byte to)
			{
				byte[] bytes = frame.Bytes.ToArray();
				byte checksum = 0;
				bytes[4] = to;
				for (int index = 0; index < bytes.Length - 2; index++)
					checksum ^= bytes[index];
				bytes[^2] = (byte)(checksum & 0x7F);
				return new FractalSysExFrame(bytes);
			}

			public Task SendAsync(FractalSysExFrame frame, CancellationToken cancellationToken)
			{
				Assert.Equal(model, frame.Bytes.Span[4]);
				return fm9.SendAsync(WithModel(frame, 0x12), cancellationToken);
			}

			public async Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(FractalSysExFrame request, CancellationToken cancellationToken)
			{
				Assert.Equal(model, request.Bytes.Span[4]);
				return [.. (await fm9.RequestAsync(WithModel(request, 0x12), cancellationToken)).Select(reply => WithModel(reply, model))];
			}
		}
	}
}
