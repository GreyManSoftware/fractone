using Fractal.Companion.Auditioning;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Fm9.Tests
{
	// The FM9 code made device-aware: grid shape, record chain order, the Axe-Fx III's prefix words and the Amp words each
	// unit recalculates, checked on FracCompat's FM3 and Axe-Fx III Ride templates. FM9 results must not change.
	public sealed class Gen3DeviceEditingTests
	{
		public static TheoryData<string, byte> Templates => new()
		{
			{ "gen3-fm3-13.00-ride.syx", 0x11 },
			{ "gen3-axe3-32.06-ride.syx", 0x10 }
		};

		[Fact]
		public void AGridPositionIsCheckedAgainstTheDevicesGrid()
		{
			new GridPosition(6, 14).Validate();
			new GridPosition(6, 14).Validate(Gen3Device.AxeFx3);
			new GridPosition(4, 12).Validate(Gen3Device.Fm3);

			ArgumentOutOfRangeException row = Assert.Throws<ArgumentOutOfRangeException>(() => new GridPosition(5, 1).Validate(Gen3Device.Fm3));
			ArgumentOutOfRangeException column = Assert.Throws<ArgumentOutOfRangeException>(() => new GridPosition(1, 13).Validate(Gen3Device.Fm3));
			ArgumentOutOfRangeException fm9 = Assert.Throws<ArgumentOutOfRangeException>(() => new GridPosition(7, 1).Validate());

			Assert.StartsWith("FM3 grid rows are 1 through 4.", row.Message, StringComparison.Ordinal);
			Assert.StartsWith("FM3 grid columns are 1 through 12.", column.Message, StringComparison.Ordinal);
			Assert.StartsWith("FM9 grid rows are 1 through 6.", fm9.Message, StringComparison.Ordinal);
		}

		[Fact]
		public void LiveGridCellsNumberByTheDevicesRows()
		{
			// Row 2, column 3: cell 13 with the FM9's six rows, 9 with the FM3's four (bytes 12-16 hold the cell, low septet first).
			FractalSysExFrame fm9 = FractalSysExCodec.CreateBlockPlacementCommands(66, new GridPosition(2, 3))[0];
			FractalSysExFrame fm3 = FractalSysExCodec.CreateBlockPlacementCommands(66, new GridPosition(2, 3), Gen3Device.Fm3)[0];

			Assert.Equal(13, fm9.Bytes.Span[12]);
			Assert.Equal(9, fm3.Bytes.Span[12]);
			Assert.Equal(fm9.Bytes.ToArray(), FractalSysExCodec.CreateBlockPlacementCommands(66, new GridPosition(2, 3), Gen3Device.Fm9)[0].Bytes.ToArray());
			Assert.Equal(FractalSysExCodec.CreateGridRoute(3, 2, 4).Bytes.ToArray(), FractalSysExCodec.CreateGridRoute(3, 2, 4, Gen3Device.AxeFx3).Bytes.ToArray());
			Assert.Throws<ArgumentOutOfRangeException>(() => FractalSysExCodec.CreateGridRoute(1, 12, 1, Gen3Device.Fm3));
			Assert.Throws<ArgumentOutOfRangeException>(() => FractalSysExCodec.CreateGridRoute(1, 1, 5, Gen3Device.Fm3));
			Assert.Throws<ArgumentOutOfRangeException>(() => FractalSysExCodec.CreateBlockClearCommands(new GridPosition(5, 1), Gen3Device.Fm3));
		}

		[Fact]
		public void PresetNumbersFollowTheDevicesPresetCount()
		{
			FractalSysExFrame axe = FractalSysExCodec.CreateSwitchPreset(1023, Gen3Device.AxeFx3);

			Assert.Equal((0x7F, 0x07), (axe.Bytes.Span[12], axe.Bytes.Span[13]));
			Assert.Equal("FM9 presets are 0 through 511.",
				Assert.Throws<ArgumentOutOfRangeException>(() => FractalSysExCodec.CreateSwitchPreset(512)).Message.Split(" (")[0]);
			Assert.Throws<ArgumentOutOfRangeException>(() => FractalSysExCodec.CreateStorePreset(512, Gen3Device.Fm3));
			_ = FractalSysExCodec.CreateStorePreset(1000, Gen3Device.AxeFx3);
		}

		[Fact]
		public void TheReplacerTakesItsSlotRangeFromTheWrappedDevice()
		{
			using FakeFm9 fm9 = new(Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx"))));
			using Gen3DeviceTransport axe = new(fm9, Gen3Device.AxeFx3);

			Assert.Equal(1000, new Fm9StoredPresetReplacer(axe, displayedPresetNumber: 1000).DisplayedPresetNumber);
			Assert.Throws<ArgumentOutOfRangeException>(() => new Fm9StoredPresetReplacer(fm9, displayedPresetNumber: 1000));
			Assert.Same(Gen3Device.Fm9, Gen3DeviceTransport.DeviceOf(fm9));
		}

		[Fact]
		public void TheRecordChainFollowsEachDevicesOrder()
		{
			Fm9PresetBody fm9 = Body("metallica-eight-scenes.syx", Gen3Device.Fm9);
			Fm9PresetBody fm3 = Body("gen3-fm3-13.00-ride.syx", Gen3Device.Fm3);
			Fm9PresetBody axe = Body("gen3-axe3-32.06-ride.syx", Gen3Device.AxeFx3);

			// The FM9 writes record 200 first; the FM3 and Axe-Fx III write it last, after 201.
			Assert.Equal([200, 2, 3], Fm9RecordOrder.Units(fm9).Take(3).Select(unit => unit.EffectId));
			Assert.Equal(201, Fm9RecordOrder.Units(fm9)[^1].EffectId);
			foreach (Fm9PresetBody other in new[] { fm3, axe })
			{
				IReadOnlyList<Fm9RecordUnit> units = Fm9RecordOrder.Units(other);
				Assert.Equal([2, 3], units.Take(2).Select(unit => unit.EffectId));
				Assert.Equal([201, 200], units.TakeLast(2).Select(unit => unit.EffectId));
				Assert.Equal(32, units.Count(unit => unit.EffectId is >= 3 and <= 34));
			}
			Assert.Empty(Fm9RecordOrder.Check(fm9));
			Assert.Empty(Fm9RecordOrder.Check(fm3));
			Assert.Empty(Fm9RecordOrder.Check(axe));
			Assert.Empty(Fm9RecordOrder.Check(Body("fm9-uploaded-shunts.syx", Gen3Device.Fm9)));
			Assert.Empty(Fm9RecordOrder.Check(Body("fm9-uploaded-usb-input.syx", Gen3Device.Fm9)));
		}

		[Fact]
		public void TheAxeFx3MarksNonBlockRecordsAndSomeFamiliesInPrefixWord1()
		{
			Fm9PresetBody axe = Body("gen3-axe3-32.06-ride.syx", Gen3Device.AxeFx3);
			Dictionary<int, ushort> words = Fm9RecordOrder.Units(axe).ToDictionary(unit => unit.EffectId, unit => unit.PrefixWord1);

			Assert.Equal(0xFFFF, words[2]);
			Assert.Equal(0xFFFF, words[3]);
			Assert.Equal(0xFFFF, words[200]);
			Assert.Equal(0xFFFF, words[42]);
			Assert.Equal(0, words[37]);
			Assert.Equal(0, words[58]);
			Assert.Equal(0xFFFF, Fm9RecordOrder.PrefixWord1(Gen3Device.AxeFx3, 38));
			Assert.Equal(0xFFFF, Fm9RecordOrder.PrefixWord1(Gen3Device.AxeFx3, 182));
			Assert.Equal(0, Fm9RecordOrder.PrefixWord1(Gen3Device.Fm3, 42));
			// The same chain claimed for an FM9 (same grid) breaks its order and its prefix words.
			IReadOnlyList<string> problems = Fm9RecordOrder.Check(axe with { Device = Gen3Device.Fm9 });
			Assert.Contains(problems, problem => problem.StartsWith("The FM9 chain starts with 200, 2", StringComparison.Ordinal));
			Assert.Contains(problems, problem => problem.StartsWith("Record 42 has prefix word 1 = 0xFFFF", StringComparison.Ordinal));
		}

		[Theory]
		[MemberData(nameof(Templates))]
		public void EachTemplateBlockComesBackExactlyWhenRemovedAndPutBackInItsShunt(string file, byte model)
		{
			Gen3Device device = Gen3Device.FromModel(model)!;
			Fm9PresetDump dump = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path(file)), device);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump, device);

			foreach (Fm9PresetGridCell cell in body.GridCells.Where(cell => cell.EffectId is not null))
			{
				int effectId = cell.EffectId!.Value;
				Fm9PresetBlockRecord record = body.BlockRecords.Single(candidate => candidate.Offset >= 12 &&
					BitConverter.ToUInt16(body.Bytes.Span[(candidate.Offset - 12)..]) == effectId);
				byte[] recordBytes = body.Bytes.Slice(record.Offset, record.Length).ToArray();
				Fm9PresetBody removed = Fm9PresetBodyDecoder.Decode(
					Fm9PresetBodyEncoder.Reencode(dump, Fm9PresetBodyBlockInserter.WithoutBlock(body, effectId)), device);

				byte[] restored = Fm9PresetBodyBlockInserter.WithBlockRecordInShunt(removed, effectId, recordBytes, new(cell.Row, cell.Column));

				Assert.True(body.Bytes.Span.SequenceEqual(restored), $"{device.Name} block {effectId} did not come back exactly.");
			}
		}

		[Theory]
		[MemberData(nameof(Templates))]
		public void AnotherUnitsBlockGoesInItsChainPlaceWithItsPrefixWord(string file, byte model)
		{
			Gen3Device device = Gen3Device.FromModel(model)!;
			Fm9PresetDump dump = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path(file)), device);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump, device);
			// Output 2 (43), from the template's Output 1 record, in the bottom-right cell of the device's grid.
			Fm9PresetBlockRecord output = body.BlockRecords.Single(candidate => candidate.Offset >= 12 &&
				BitConverter.ToUInt16(body.Bytes.Span[(candidate.Offset - 12)..]) == 42);
			GridPosition corner = new(device.GridRows, device.GridColumns);

			Fm9PresetBody inserted = Fm9PresetBodyDecoder.Decode(Fm9PresetBodyEncoder.Reencode(dump,
				Fm9PresetBodyBlockInserter.WithBlockRecord(body, 43, body.Bytes.Slice(output.Offset, output.Length).Span, corner)), device);

			Assert.Equal(43, inserted.GridCells.Single(cell => cell.Row == corner.Row && cell.Column == corner.Column).EffectId);
			Assert.Empty(Fm9RecordOrder.Check(inserted));
			Assert.Equal(Fm9RecordOrder.PrefixWord1(device, 43), Fm9RecordOrder.Units(inserted).Single(unit => unit.EffectId == 43).PrefixWord1);
			Assert.Throws<NotSupportedException>(() => Fm9PresetBodyBlockInserter.WithBlock(body, "Delay", 1, new(1, 1)));
			Assert.Throws<ArgumentOutOfRangeException>(() => Fm9PresetBodyBlockInserter.WithBlockRecord(body, 43,
				body.Bytes.Slice(output.Offset, output.Length).Span, new(device.GridRows + 1, 1)));
		}

		[Fact]
		public void Fm3GridEditsStayInsideItsFourRowsAndTwelveColumns()
		{
			Fm9PresetDump dump = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("gen3-fm3-13.00-ride.syx")), Gen3Device.Fm3);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump, Gen3Device.Fm3);
			Fm9PresetGridCell amp = body.GridCells.Single(cell => cell.EffectId == 58);
			GridPosition from = new(amp.Row, amp.Column);

			Fm9PresetBody moved = Fm9PresetBodyDecoder.Decode(
				Fm9PresetBodyEncoder.Reencode(dump, Fm9PresetBodyGridEditor.MovePlacedBlock(body, from, new(4, 12))), Gen3Device.Fm3);

			Assert.Equal(58, moved.GridCells.Single(cell => cell.Row == 4 && cell.Column == 12).EffectId);
			Assert.Throws<ArgumentOutOfRangeException>(() => Fm9PresetBodyGridEditor.MovePlacedBlock(body, from, new(5, 12)));
			Assert.Throws<ArgumentOutOfRangeException>(() => Fm9PresetBodyGridEditor.WithRoute(body, from, 0x0010));
			_ = Fm9PresetBodyGridEditor.WithRoute(body, from, 0x000F);
			Assert.Throws<NotSupportedException>(() => Fm9PresetBodyGridEditor.WithInputOutputInstances(body, 1, 1));
		}

		[Fact]
		public void TheLiveGridReadTakesTheDevicesCellCount()
		{
			// An FM3-shaped 01 2E reply (48 cells): Reverb 1 at row 4, column 12, cabled from rows 1 and 4.
			byte[] frame = GridReply(Gen3Device.Fm3, 4, 12, 66, 0b1001);

			Fm9GridCell cell = Assert.Single(Fm9GridLayout.TryParse(frame, Gen3Device.Fm3)!);

			Assert.Equal((4, 12, 66, (ushort)0b1001), (cell.Row, cell.Column, cell.EffectId!.Value, cell.RouteMask));
			Assert.True(Fm9GridLayout.HasIncomingCable(cell, 4, Gen3Device.Fm3));
			Assert.Throws<ArgumentOutOfRangeException>(() => Fm9GridLayout.HasIncomingCable(cell, 5, Gen3Device.Fm3));
			Fm9GridCell fm9 = Assert.Single(Fm9GridLayout.TryParse(GridReply(Gen3Device.Fm9, 6, 14, 66, 1))!);
			Assert.Equal((6, 14), (fm9.Row, fm9.Column));
		}

		[Fact]
		public void EachUnitRecalculatesItsOwnAmpIds()
		{
			Assert.Equal([27, 38, 43, 44, 70, 71, 87, 109, 113, 114, 119], Fm9PresetWriteVerifier.AmpNormalizations(Gen3Device.Fm9).Keys.Order());
			foreach (Gen3Device device in Gen3Device.All)
			{
				IReadOnlyDictionary<int, string> ids = Fm9PresetWriteVerifier.AmpNormalizations(device);
				Assert.Equal(11, ids.Count);
				Assert.Equal(["DISTORT_CFGRID", "DISTORT_GRIDHARDNESS", "DISTORT_CFHARDNESS"],
					device.RecomputedAmpParameters.Select(id => ids[id]));
			}
		}

		[Theory]
		[InlineData("gen3-fm3-13.00-ride.syx", 0x11, 83, 87)]
		[InlineData("gen3-axe3-32.06-ride.syx", 0x10, 81, 87)]
		public void AnImportedAmpIsAllowedOnlyTheDevicesRecalculatedWords(string file, byte model, int cfGrid, int fm9CfGrid)
		{
			Gen3Device device = Gen3Device.FromModel(model)!;
			Fm9PresetDump sent = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path(file)), device);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(sent, device);
			Fm9PresetBlockRecord amp = body.BlockRecords.Single(candidate => candidate.Offset >= 12 &&
				BitConverter.ToUInt16(body.Bytes.Span[(candidate.Offset - 12)..]) == 58);

			IReadOnlyList<Fm9NormalizedFieldExpectation> fields =
				Fm9PresetWriteVerifier.VerifyImportedBody(sent, Changed(sent, body, amp, cfGrid), device);

			Fm9NormalizedFieldExpectation field = Assert.Single(fields);
			Assert.Equal((cfGrid, "DISTORT_CFGRID"), (field.ParameterId, field.CatalogName));
			// The FM9's CFGRID id is another setting on this unit, so a change there is the import altering the preset.
			Assert.Throws<InvalidDataException>(() => Fm9PresetWriteVerifier.VerifyImportedBody(sent, Changed(sent, body, amp, fm9CfGrid), device));
		}

		private static Fm9PresetDump Changed(Fm9PresetDump dump, Fm9PresetBody body, Fm9PresetBlockRecord amp, int parameterId)
		{
			byte[] bytes = body.Bytes.ToArray();
			int offset = Fm9ChannelParameterStorage.GetOffset(amp, 0, parameterId);
			bytes[offset] ^= 0x55;
			return Fm9PresetBodyEncoder.Reencode(dump, bytes);
		}

		private static Fm9PresetBody Body(string file, Gen3Device device) =>
			Fm9PresetBodyDecoder.Decode(Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path(file)), device), device);

		// A 01 2E reply as the FM9 sends it: 400 bytes of other state, then the grid bitstream (from bit 45, each cell's id and
		// route mask as little-endian words, MSB first in 7-bit bytes), the checksum and F7.
		private static byte[] GridReply(Gen3Device device, int row, int column, int effectId, int routeMask)
		{
			const int baseBit = 45;
			int regionBytes = (baseBit + (device.GridRows * device.GridColumns * 32) + 6) / 7;
			byte[] region = new byte[regionBytes];
			int cellBit = baseBit + ((((column - 1) * device.GridRows) + (row - 1)) * 32);
			WriteBits(region, cellBit, effectId & 0xFF);
			WriteBits(region, cellBit + 8, effectId >> 8);
			WriteBits(region, cellBit + 16, routeMask & 0xFF);
			WriteBits(region, cellBit + 24, routeMask >> 8);
			List<byte> frame = [0xF0, 0x00, 0x01, 0x74, 0x12, 0x01, 0x2E, .. new byte[400], .. region];
			byte checksum = 0;
			foreach (byte value in frame)
				checksum ^= value;
			frame.Add((byte)(checksum & 0x7F));
			frame.Add(0xF7);
			return [.. frame];
		}

		private static void WriteBits(byte[] data, int bitOffset, int value)
		{
			for (int index = 0; index < 8; index++)
				if (((value >> (7 - index)) & 1) != 0)
				{
					int bit = bitOffset + index;
					data[bit / 7] |= (byte)(1 << (6 - (bit % 7)));
				}
		}
	}
}
