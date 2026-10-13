using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9DeviceTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();
		private readonly Fm9PresetDump _metallica = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx")));
		private readonly Fm9PresetDump _usb = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("fm9-uploaded-usb-input.syx")));
		private readonly FakeFm9 _fm9;

		public Fm9DeviceTests()
		{
			_fm9 = new(_metallica);
			_fm9.Slots[500] = _metallica;
			_fm9.EditBuffer = _metallica;
		}

		public void Dispose() => _folder.Dispose();

		private Fm9Device Device(Fm9Safeguards? safeguards = null) =>
			new(new Fm9Connection(() => { _fm9.Opens++; return _fm9; }, _folder.File("midi.lock")),
				new Fm9Settings { WorkFolder = _folder.Root, Safeguards = safeguards ?? new() })
			{
				Wait = (_, _) => Task.CompletedTask
			};

		[DictionaryFact]
		public async Task StatusReportsFirmwarePresetAndScene()
		{
			Fm9Status status = await Device().StatusAsync(CancellationToken.None);

			Assert.Equal(new Fm9Status("12.00", 500, _metallica.PresetName, 1), status);
		}

		[DictionaryFact]
		public async Task ReadingAPresetSavesItAndDescribesIt()
		{
			Fm9ReadResult read = await Device().ReadPresetAsync(500, "reads/500.syx", CancellationToken.None);

			Assert.Equal(_metallica.ToArray(), File.ReadAllBytes(read.Path));
			Assert.Equal(Path.Combine(_folder.Root, "reads", "500.syx"), read.Path);
			Assert.Equal(_metallica.PresetName, read.Summary.Name);
		}

		[DictionaryFact]
		public async Task AStoreReportsWhatItReplacedAndKeepsNoBackupByDefault()
		{
			string file = Write("usb.syx", _usb);

			Fm9StoreResult stored = await Device().StoreAsync(file, 500, CancellationToken.None);

			Assert.Equal(_metallica.PresetName, stored.Replaced);
			Assert.Equal(_usb.PresetName, stored.Stored);
			Assert.True(_usb.HasSameRawPatch(_fm9.Slots[500]));
			Assert.Null(stored.Backup);
			Assert.False(stored.Verified);
			Assert.Empty(Directory.Exists(Path.Combine(_folder.Root, "backups")) ? Directory.GetFiles(Path.Combine(_folder.Root, "backups")) : []);
		}

		[DictionaryFact]
		public async Task TheCarefulProfileKeepsABackupAndVerifiesTheStoredCopy()
		{
			string file = Write("usb.syx", _usb);

			Fm9StoreResult stored = await Device(Fm9Safeguards.Careful).StoreAsync(file, 500, CancellationToken.None);

			Assert.True(stored.Verified);
			Assert.NotNull(stored.Backup);
			Assert.True(_metallica.HasSameRawPatch(Fm9PresetDump.Parse(File.ReadAllBytes(stored.Backup!))));
		}

		[DictionaryFact]
		public async Task AnEmptySlotCanBeStoredInto()
		{
			string file = Write("usb.syx", _usb);

			Fm9StoreResult stored = await Device(Fm9Safeguards.Careful).StoreAsync(file, 3, CancellationToken.None);

			Assert.Equal("(empty slot)", stored.Replaced);
			Assert.True(_usb.HasSameRawPatch(_fm9.Slots[3]));
		}

		[DictionaryFact]
		public async Task ASlotOutsideTheWritableSlotsIsRefusedBeforeTheFm9IsTouched()
		{
			string file = Write("usb.syx", _usb);
			Fm9Device device = Device(new Fm9Safeguards { WritableSlots = ["500-505"] });

			await Assert.ThrowsAsync<InvalidOperationException>(() => device.StoreAsync(file, 3, CancellationToken.None));

			Assert.Equal(0, _fm9.Opens);
			Assert.False(_fm9.Slots.ContainsKey(3));
		}

		[DictionaryFact]
		public async Task AmpValuesTheFm9RecalculatesOnImportAreAcceptedAndStoredAsTheFm9HoldsThem()
		{
			string file = Write("usb.syx", _usb);
			_fm9.OnImport = Recalculate;

			Fm9StoreResult stored = await Device(Fm9Safeguards.Careful).StoreAsync(file, 500, CancellationToken.None);

			Assert.True(stored.Verified);
			Assert.Equal(["DISTORT_OFFSET1"], stored.RecalculatedByFm9);
			Assert.True(Recalculate(_usb).HasSameRawPatch(_fm9.Slots[500]));
		}

		[DictionaryFact]
		public async Task AnImportThatChangesAnythingElseIsUndoneAndTheSlotKeepsItsPreset()
		{
			string file = Write("usb.syx", _usb);
			_fm9.OnImport = dump => dump.WithPresetName("Corrupted");

			await Assert.ThrowsAsync<InvalidOperationException>(() => Device().StoreAsync(file, 500, CancellationToken.None));

			Assert.Equal(0, _fm9.Stores);
			Assert.True(_metallica.HasSameRawPatch(_fm9.Slots[500]));
		}

		[DictionaryFact]
		public async Task FirmwareOtherThan12IsRefusedForWritesButStillRead()
		{
			string file = Write("usb.syx", _usb);
			_fm9.Firmware = (13, 0);

			await Assert.ThrowsAsync<NotSupportedException>(() => Device().StoreAsync(file, 500, CancellationToken.None));
			await Assert.ThrowsAsync<NotSupportedException>(() => Device().AuditionAsync(file, null, CancellationToken.None));
			Assert.Equal(_metallica.PresetName, (await Device().ReadPresetAsync(500, null, CancellationToken.None)).Summary.Name);
		}

		[DictionaryFact]
		public async Task AnAuditionLoadsTheEditBufferAndStoresNothing()
		{
			string file = Write("usb.syx", _usb);

			Fm9AuditionResult result = await Device().AuditionAsync(file, null, CancellationToken.None);

			Assert.Equal(500, result.Preset);
			Assert.True(_usb.HasSameRawPatch(_fm9.EditBuffer!));
			Assert.Equal(0, _fm9.Stores);
			Assert.True(_metallica.HasSameRawPatch(_fm9.Slots[500]));
		}

		[DictionaryFact]
		public async Task UnsavedEditsInTheBufferBlockALoadOnlyWhenTheSafeguardIsOn()
		{
			string file = Write("usb.syx", _usb);
			// Someone tweaked the preset on the FM9 and has not stored it.
			_fm9.EditBuffer = _metallica.WithPresetName("Tweaked on the FM9");

			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				Device(new Fm9Safeguards { RefuseLoadIfBufferChanged = true }).AuditionAsync(file, null, CancellationToken.None));
			Assert.Equal(0, _fm9.Imports);

			await Device().AuditionAsync(file, null, CancellationToken.None);
			Assert.Equal(1, _fm9.Imports);
		}

		[DictionaryFact]
		public async Task WhatTheToolsLoadedLastDoesNotCountAsUnsavedEdits()
		{
			string first = Write("usb.syx", _usb);
			string second = Write("metallica.syx", _metallica.WithPresetName("Second"));
			Fm9Device device = Device(new Fm9Safeguards { RefuseLoadIfBufferChanged = true });

			await device.AuditionAsync(first, null, CancellationToken.None);
			await device.AuditionAsync(second, null, CancellationToken.None);

			Assert.Equal("Second", _fm9.EditBuffer!.PresetName);
		}

		[DictionaryFact]
		public async Task SwitchingScenesDoesNotCountAsAnUnsavedEdit()
		{
			string file = Write("usb.syx", _usb);
			Fm9Device device = Device(new Fm9Safeguards { RefuseLoadIfBufferChanged = true });
			await device.AuditionAsync(file, null, CancellationToken.None);

			await _fm9.SendAsync(FractalSysExCodec.CreateSceneSelection(3), CancellationToken.None);
			await device.SelectAsync(_fm9.Preset, 1, CancellationToken.None);

			Assert.Equal(1, _fm9.Scene);
		}

		[DictionaryFact]
		public async Task ABackupWritesEachPresetAndListsEmptySlots()
		{
			_fm9.Slots[501] = _usb;

			Fm9BackupResult backup = await Device().BackupAsync(500, 502, "backup", null, CancellationToken.None);

			Assert.Equal(2, backup.Files.Count);
			Assert.Equal([502], backup.EmptySlots);
			Assert.True(_usb.HasSameRawPatch(Fm9PresetDump.Parse(File.ReadAllBytes(backup.Files[1]))));
		}

		[DictionaryFact]
		public async Task AnotherProcessHoldingTheFm9MakesACallWaitThenGiveUp()
		{
			string lockFile = _folder.File("held.lock");
			using FileStream held = new(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			Fm9Device device = new(new Fm9Connection(() => _fm9, lockFile, TimeSpan.FromMilliseconds(600)), new Fm9Settings { WorkFolder = _folder.Root });

			await Assert.ThrowsAsync<TimeoutException>(() => device.StatusAsync(CancellationToken.None));
		}

		private string Write(string name, Fm9PresetDump dump)
		{
			string path = _folder.File(name);
			File.WriteAllBytes(path, dump.ToArray());
			return path;
		}

		// Changes channel A's DISTORT_OFFSET1 (column 27) of the preset's Amp, as the FM9 does for some models.
		private static Fm9PresetDump Recalculate(Fm9PresetDump dump)
		{
			Fm9UploadedPreset preset = Fm9UploadedPreset.Read(dump);
			Fm9PresetBlockRecord amp = preset.Find("Amp", 1)!.Record!;
			byte[] bytes = preset.Body.Bytes.ToArray();
			int offset = Fm9ChannelParameterStorage.GetOffset(amp, 0, 27);
			BitConverter.TryWriteBytes(bytes.AsSpan(offset), (ushort)(BitConverter.ToUInt16(bytes, offset) ^ 0x0101));
			return Fm9PresetBodyEncoder.Reencode(dump, bytes);
		}
	}
}
