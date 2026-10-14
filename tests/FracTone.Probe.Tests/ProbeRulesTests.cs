using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fractal.Companion.Protocol;
using FracTone.Probe.Cli;

namespace FracTone.Probe.Tests
{
	public sealed class ProbeRulesTests
	{
		private static readonly ProbeCommandPolicy Fm9Policy = new(Gen3Device.Fm9, 503, 7, allowStore: false);

		public static TheoryData<string, byte[]> Refused => new()
		{
			{ "01 0A steps a value on every read", Fm9Frame(0x01, [0x0A, 0, 0, 0]) },
			{ "01 0E marks the preset edited", Fm9Frame(0x01, [0x0E, 0, 0, 0]) },
			{ "01 0D corrupted a live amp", Fm9Frame(0x01, [0x0D, 0, 0, 0]) },
			{ "firmware frames", Fm9Frame(0x7D, [0, 0]) },
			{ "a store without level 3", ProbeFrames.StorePreset(Gen3Device.Fm9, 503) },
			{ "a switch to another slot", ProbeFrames.SwitchPreset(Gen3Device.Fm9, 12) },
			{ "a preset sent to a stored slot", Fm9Frame(0x77, [0x03, 0x77, 0, 0x40, 0]) },
			{ "a live write to the Controllers", FractalSysExCodec.CreateDiscreteParameterChange(2, 1, 1).Bytes.ToArray() },
			{ "another model's byte", ProbeFrames.CurrentPreset(Gen3Device.Fm3) },
			{ "a bad checksum", [0xF0, 0x00, 0x01, 0x74, 0x12, 0x08, 0x00, 0xF7] }
		};

		[Theory]
		[MemberData(nameof(Refused))]
		public void TheAllowListRefusesWhatTheBriefForbids(string why, byte[] frame)
		{
			Assert.False(string.IsNullOrEmpty(why));
			Assert.Throws<ProbeRefusedException>(() => Fm9Policy.Check(frame));
		}

		[Fact]
		public void TheAllowListLetsThroughTheProbesOwnFrames()
		{
			ProbeCommandPolicy policy = new(Gen3Device.AxeFx3, 503, 7, allowStore: true);
			Gen3Device axe = Gen3Device.AxeFx3;

			foreach (byte[] frame in new[]
			{
				ProbeFrames.Firmware(axe.Model), ProbeFrames.StatusDump(axe), ProbeFrames.CurrentPreset(axe), ProbeFrames.CurrentScene(axe),
				ProbeFrames.SelectScene(axe, 3), ProbeFrames.ReadPreset(axe, 503), ProbeFrames.ReadPreset(axe, ProbeFrames.EditBuffer),
				ProbeFrames.SwitchPreset(axe, 503), ProbeFrames.SwitchPreset(axe, 7), ProbeFrames.StorePreset(axe, 503)
			})
				policy.Check(frame);
			foreach (byte[] frame in ProbeFrames.ForEditBuffer(SimulatedGen3.Load("gen3-axe3-32.06-ride.syx")))
				policy.Check(frame);
		}

		[Fact]
		public void TheSwitchFrameIsTheFm9CodecsFrameInTheDevicesByte()
		{
			Assert.Equal(FractalSysExCodec.CreateSwitchPreset(503).Bytes.ToArray(), ProbeFrames.SwitchPreset(Gen3Device.Fm9, 503));
			Assert.Equal(FractalSysExCodec.CreateStorePreset(503).Bytes.ToArray(), ProbeFrames.StorePreset(Gen3Device.Fm9, 503));
			Assert.Equal(Gen3Device.Fm3.Retarget(FractalSysExCodec.CreateSwitchPreset(438)).Bytes.ToArray(), ProbeFrames.SwitchPreset(Gen3Device.Fm3, 438));
		}

		[Fact]
		public void AFrameAimedAtTheEditBufferChangesOnlyItsHeaderAddress()
		{
			IReadOnlyList<byte[]> stored = SimulatedGen3.Load("gen3-fm3-13.00-ride.syx");

			IReadOnlyList<byte[]> aimed = ProbeFrames.ForEditBuffer(stored);

			Assert.Equal("F0 00 01 74 11 77 7F 7F 00 40 00 23 F7", ProbeFrames.Hex(aimed[0]));
			Assert.All(stored.Skip(1).Zip(aimed.Skip(1)), pair => Assert.Same(pair.First, pair.Second));
		}

		[Fact]
		public void TheReadbackRuleAllowsOnlyTheAmpWordsTheDeviceRecomputesAndWordZero()
		{
			Fm9PresetDump sent = Fm9PresetDump.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Presets", "fm9-synthetic-ride.syx")));
			Fm9PresetDump readBack = Fm9PresetDump.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Presets", "fm9-synthetic-ride-fm9-readback.syx")));
			ProbeScript fm9 = ProbeScripts.Find(Gen3Device.Fm9, "12.00")!;

			ReadbackComparison comparison = ReadbackComparer.Compare(Gen3Device.Fm9, fm9, sent, readBack);
			ReadbackComparison wrongWords = ReadbackComparer.Compare(Gen3Device.Fm9, fm9 with { RecomputedAmpParameters = [83, 105, 115] }, sent, readBack);
			byte[] body = Fm9PresetBodyDecoder.Decode(readBack).Bytes.ToArray();
			body[0] = 5;
			body[^1] ^= 1;
			ReadbackComparison edited = ReadbackComparer.Compare(Gen3Device.Fm9, fm9, sent, Fm9PresetBodyEncoder.Reencode(readBack, body));

			Assert.True(comparison.Matches);
			Assert.Equal([(58, 0, 87, 43711), (58, 0, 109, 64078), (58, 0, 119, 53738)],
				comparison.Recomputed.Select(word => (word.EffectId, word.Channel, word.ParameterId, (int)word.ReadBack)));
			Assert.False(wrongWords.Matches);
			Assert.Equal([body.Length - 1], edited.UnexpectedOffsets);
		}

		[Fact]
		public void EachDeviceHasAScriptForItsFirstFirmwareWithATemplateInItsOwnFormat()
		{
			foreach (Gen3Device device in Gen3Device.All)
			{
				ProbeScript script = ProbeScripts.Find(device, device.ScriptedFirmware[0])!;
				IReadOnlyList<byte[]> template = script.LoadTemplate();

				Assert.Equal(device.Model, script.Model);
				Assert.Equal(device.DumpDataFrames + 2, template.Count);
				Assert.All(template, frame => Assert.Equal(device.Model, frame[4]));
				Assert.Equal(device.GridRows * device.GridColumns, Fm9PresetBodyDecoder.Decode(ProbeFrames.Parse(device, template), device).GridCells.Count);
				Assert.Equal([1, 2, 3], script.Steps.Select(step => step.Level).Distinct().Order());
				IReadOnlyList<byte[]> empty = script.LoadTemplate("empty");
				Assert.All(empty, frame => Assert.Equal(device.Model, frame[4]));
				Assert.DoesNotContain(Fm9PresetBodyDecoder.Decode(ProbeFrames.Parse(device, empty), device).GridCells, cell => cell.EffectId is not null);
				// Every check's cells fit the device's grid (the FM3's is 4 x 12), and anything not run on it is flagged.
				foreach (ProbeStep step in script.Steps.Where(step => step.Kind is ProbeStepKind.VoicingCheck or ProbeStepKind.FreshInserts or ProbeStepKind.CableCheck))
				{
					Assert.All(step.Inserts ?? [], insert => new GridPosition(insert.Row, insert.Column).Validate(device));
					Assert.All(step.Routes ?? [], route => ProbeFrames.Route(device, route.FromRow, route.FromColumn, route.ToRow));
					Assert.Equal(device != Gen3Device.Fm9, step.Unverified == true);
				}
				Assert.All(ProbeScripts.ReadOnly(device, "99.99").Steps, step => Assert.Equal(1, step.Level));
			}
		}

		[Fact]
		public async Task TheReportIsOneAnonymisedZipWithTheBackupsKeptBesideIt()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			IReadOnlyList<byte[]> userBuffer = fm9.EditBuffer;
			ProbeRun run = await ProbeEngineTests.Run(fm9, 2);
			string folder = Directory.CreateTempSubdirectory("fractone-probe-tests-").FullName;
			try
			{
				ProbeReportFiles files = ProbeReportWriter.Write(run, folder);

				using ZipArchive zip = ZipFile.OpenRead(files.Zip);
				Assert.Equal(["exchanges.txt", "log.txt", "readbacks/cable-codes-readback.syx", "readbacks/fresh-inserts-readback.syx",
					"readbacks/template-readback.syx", "readbacks/template-sent.syx", "run.json"],
					zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
				foreach (ZipArchiveEntry entry in zip.Entries)
				{
					using MemoryStream bytes = new();
					using (Stream stream = entry.Open()) stream.CopyTo(bytes);
					string text = Encoding.Latin1.GetString(bytes.ToArray());
					Assert.DoesNotContain(UserNameFragment, text, StringComparison.Ordinal);
					Assert.DoesNotContain(folder, text, StringComparison.OrdinalIgnoreCase);
					Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
				}
				string json = new StreamReader(zip.GetEntry("run.json")!.Open()).ReadToEnd();
				Assert.Contains("\"outcome\": \"Completed\"", json, StringComparison.Ordinal);
				Assert.Contains("\"presetFoundName\": \"[preset found]\"", json, StringComparison.Ordinal);
				Assert.Contains("\"leftAsFound\": true", json, StringComparison.Ordinal);
				// The user's backup is the device's frames, byte for byte, and stays on this PC.
				Assert.Equal(userBuffer.SelectMany(frame => frame).ToArray(), File.ReadAllBytes(Path.Combine(files.Backups!, "backup-edit-buffer.syx")));
			}
			finally
			{
				Directory.Delete(folder, recursive: true);
			}
		}

		[Fact]
		public async Task RunJsonCarriesTheFieldsTheSiteUploadListsAtItsTopLevel()
		{
			ProbeRun run = await ProbeEngineTests.Run(ProbeEngineTests.Unit(Gen3Device.Fm3, "gen3-fm3-13.00-ride.syx", (13, 0)), 1);
			string folder = Directory.CreateTempSubdirectory("fractone-probe-tests-").FullName;
			try
			{
				using ZipArchive zip = ZipFile.OpenRead(ProbeReportWriter.Write(run, folder).Zip);
				using JsonDocument json = JsonDocument.Parse(zip.GetEntry("run.json")!.Open());
				JsonElement root = json.RootElement;

				// The site reads exactly these names (ProbeReportUpload): device, firmware, outcome and toolVersion.
				Assert.Equal(run.Device, root.GetProperty("device").GetString());
				Assert.Equal(run.Firmware, root.GetProperty("firmware").GetString());
				Assert.Equal(run.Outcome.ToString(), root.GetProperty("outcome").GetString());
				Assert.Equal(run.ToolVersion, root.GetProperty("toolVersion").GetString());
				Assert.False(root.TryGetProperty("version", out _));
				Assert.False(string.IsNullOrWhiteSpace(run.Device));
				Assert.False(string.IsNullOrWhiteSpace(run.Firmware));
			}
			finally
			{
				Directory.Delete(folder, recursive: true);
			}
		}

		[Fact]
		public void TheDryRunPrintsEveryFrameWithoutOpeningAPort()
		{
			StringWriter output = new();

			int code = Program.DryRun(Program.Parse(["--slot", "503", "--level", "2", "--dry-run", "--device", "fm3"]), output);

			string text = output.ToString();
			Assert.Equal(0, code);
			Assert.Contains(ProbeFrames.Hex(ProbeFrames.SwitchPreset(Gen3Device.Fm3, 503)), text, StringComparison.Ordinal);
			Assert.Contains("F0 00 01 74 11 77 7F 7F 00 40 00 23 F7", text, StringComparison.Ordinal);
			Assert.DoesNotContain(ProbeFrames.Hex(ProbeFrames.StorePreset(Gen3Device.Fm3, 503)), text, StringComparison.Ordinal);
		}

		[Fact]
		public void TheCommandLineNeedsASafeSlot()
		{
			Assert.Throws<ArgumentException>(() => Program.Parse(["--level", "2"]));
			Assert.Throws<ArgumentException>(() => Program.Parse(["--slot", "3", "--level", "4"]));
			CliOptions options = Program.Parse(["--slot", "503", "--level", "3", "--out", "x", "--device-port", "Axe-Fx III"]);
			Assert.Equal((503, 3, "x", "Axe-Fx III"), (options.Slot, options.Level, options.Out, options.DevicePort));
		}

		private const string UserNameFragment = "Secret";

		private static byte[] Fm9Frame(byte function, byte[] payload)
		{
			byte[] frame = [0xF0, 0x00, 0x01, 0x74, 0x12, function, .. payload, 0, 0xF7];
			frame[^2] = ProbeFrames.Checksum(frame);
			return frame;
		}
	}
}
