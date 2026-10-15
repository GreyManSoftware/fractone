using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fractal.Companion.Protocol;

namespace FracTone.Probe.Tests
{
	// Reports are written by ProbeReportWriter from runs on the simulated units, then read back as the site reads uploads.
	public sealed class ProbeReportAnalysisTests : IDisposable
	{
		private readonly string _folder = Directory.CreateTempSubdirectory("probe-analysis-").FullName;

		public void Dispose()
		{
			try { Directory.Delete(_folder, true); }
			catch (IOException) { }
		}

		private byte[] Zip(ProbeRun run, bool includeUserPresets = false)
		{
			string folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
			return File.ReadAllBytes(ProbeReportWriter.Write(run, folder, includeUserPresets).Zip);
		}

		private static async Task<ProbeRun> Fm9Run() => (await SharedProbeRuns.Fm9).Run;

		private static async Task<ProbeRun> Fm9WithDrive46DriftedRun() => (await SharedProbeRuns.Fm9WithDrive46Drifted).Run;

		[Fact]
		public async Task AnFm9LevelTwoReportIsSummarisedAndEveryEvidenceStepMatchesItsScript()
		{
			ProbeReportAnalysis analysis = ProbeReportAnalyzer.Analyse(Zip(await Fm9Run()));

			Assert.Equal(("FM9", "12.00", "Completed", true, 2), (analysis.Device, analysis.Firmware, analysis.Outcome, analysis.LeftAsFound, analysis.LevelRun));
			Assert.Equal("FM9 12.00", analysis.ExpectationsFrom);
			Assert.Empty(analysis.Problems);
			Assert.All(analysis.Steps.Where(step => step.Level == 2), step => Assert.Equal("Passed", step.Status));
			Assert.Equal(ProbeVerdict.Match, analysis.TemplateLoad!.Verdict);
			Assert.True(analysis.TemplateLoad.CheckedFromReadbacks);
			Assert.Contains(analysis.RecomputedAmpWords, word => word is { EffectId: 58, ParameterId: 87, Sent: 0, ReadBack: 43711 });
			ProbeStepAnalysis drive = analysis.Steps.Single(step => step.Id == "voicing-drive");
			Assert.Equal(3, drive.Evidence.Count);
			Assert.All(drive.Evidence, evidence => Assert.Equal(ProbeVerdict.Match, evidence.Verdict));
			Assert.Equal(43987, analysis.Voicings.Single(voicing => voicing is { Block: "Drive", Ordinal: 6 }).Words[6]);
			Assert.All(analysis.Steps.Single(step => step.Id == "voicing-amp").Evidence, evidence => Assert.Equal(ProbeVerdict.Match, evidence.Verdict));
			Assert.Equal(11, analysis.Inserts.Count);
			Assert.All(analysis.Inserts, insert => Assert.Equal(ProbeVerdict.Match, insert.Verdict));
			Assert.Equal("147x4", analysis.Inserts.Single(insert => insert.Block == "Amp 1").Shape);
			Assert.Equal(6, analysis.Cables.Count);
			Assert.All(analysis.Cables, cable => Assert.Equal((ProbeVerdict.Match, cable.ExpectedFrame), (cable.Verdict, cable.Frame)));
			Assert.True(analysis.Exchanges.FramesSent > 0 && analysis.Exchanges.FramesReceived > analysis.Exchanges.FramesSent);
			Assert.True(analysis.Exchanges.FramesWithheld > 0);
			Assert.Contains("template-readback.syx", analysis.Readbacks);
		}

		[Fact]
		public async Task AVoicingThatDiffersIsShownWithTheWordsAndWhatTheScriptWanted()
		{
			ProbeRun run = await Fm9WithDrive46DriftedRun();

			ProbeReportAnalysis analysis = ProbeReportAnalyzer.Analyse(Zip(run));

			Assert.Equal("CompletedWithFailures", analysis.Outcome);
			ProbeEvidence crunch = analysis.Steps.Single(step => step.Id == "voicing-drive").Evidence.Single(evidence => evidence.Subject.Contains("46", StringComparison.Ordinal));
			Assert.Equal(ProbeVerdict.Differs, crunch.Verdict);
			Assert.Contains("6: 45675 want 60458", crunch.Detail, StringComparison.Ordinal);
		}

		[Fact]
		public async Task AnFm3ReportMarksItsCarriedOverExpectationsAndConfirmsThem()
		{
			ProbeReportAnalysis analysis = ProbeReportAnalyzer.Analyse(Zip((await SharedProbeRuns.Fm3).Run));

			Assert.Equal(("FM3", "FM3 13.00"), (analysis.Device, analysis.ExpectationsFrom));
			ProbeCableObservation cable = analysis.Cables.Single(observation => observation.Route == "r2c2>r1c3");
			Assert.Equal((ProbeVerdict.Match, true, "0x02", "02 42 00"), (cable.Verdict, cable.CarriedOver, cable.Mask, cable.Frame));
		}

		[Fact]
		public async Task ACableThatLandsInTheWrongPlaceDiffers()
		{
			// This test edits its run, so it runs its own rather than the shared one.
			ProbeRun run = await ProbeEngineTests.Run(SharedProbeRuns.Fm9WithItsReadback(), 2);
			ProbeStepResult cables = run.Steps.Single(step => step.Id == "cable-codes");
			Dictionary<string, string> data = new(cables.Data)
			{
				["r1c1>r1c2.mask"] = "0x00",
				["r1c1>r1c2.changed"] = "r3c2 0x00->0x01"
			};
			run.Steps[run.Steps.IndexOf(cables)] = cables with { Data = data };

			ProbeReportAnalysis analysis = ProbeReportAnalyzer.Analyse(Zip(run));

			ProbeCableObservation cable = analysis.Cables.Single(observation => observation.Route == "r1c1>r1c2");
			Assert.Equal(ProbeVerdict.Differs, cable.Verdict);
			Assert.Contains("mask 0x00, expected 0x01", cable.Detail, StringComparison.Ordinal);
			Assert.Contains("other cells changed: r3c2 0x00->0x01", cable.Detail, StringComparison.Ordinal);
		}

		[Fact]
		public async Task AFirmwareWithoutAScriptIsSummarisedButNothingIsCompared()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (13, 0));

			ProbeReportAnalysis analysis = ProbeReportAnalyzer.Analyse(Zip(await ProbeEngineTests.Run(fm9, 2)));

			Assert.Equal(("13.00", false, 1, (string?)null), (analysis.Firmware, analysis.FirmwareScripted, analysis.LevelRun, analysis.ExpectationsFrom));
			Assert.Contains(analysis.Problems, problem => problem.Contains("No probe script existed for firmware 13.00", StringComparison.Ordinal));
			Assert.Null(analysis.TemplateLoad);
			Assert.Empty(analysis.Voicings);
		}

		[Fact]
		public void AReportWithoutRunJsonOrWithBrokenJsonCannotBeAnalysed()
		{
			Assert.Throws<InvalidDataException>(() => ProbeReportAnalyzer.Analyse(RawZip(("log.txt", "hello"))));
			Assert.Throws<InvalidDataException>(() => ProbeReportAnalyzer.Analyse(RawZip(("run.json", "{ not json"))));
			Assert.Throws<InvalidDataException>(() => ProbeReportAnalyzer.Analyse([1, 2, 3]));
		}

		[Fact]
		public void AMinimalRunFromAnUnknownDeviceListsItsStepsAndSaysWhyNothingWasCompared()
		{
			ProbeReportAnalysis analysis = ProbeReportAnalyzer.Analyse(RawZip(("run.json",
				"""{"device":"Mystery","firmware":"1.0","outcome":"Stopped","steps":[{"id":"identify","kind":"identify","level":1,"status":"Stopped","message":"No answer.","data":{}}]}""")));

			Assert.Equal(("Mystery", "Stopped"), (analysis.Device, analysis.Outcome));
			Assert.Equal("Stopped", analysis.Steps.Single().Status);
			Assert.Contains(analysis.Problems, problem => problem.Contains("isn't a gen-3 unit", StringComparison.Ordinal));
			Assert.Contains(analysis.Problems, problem => problem.Contains("no exchanges.txt", StringComparison.Ordinal));
		}

		[Fact]
		public async Task TheAggregateGroupsByDeviceAndFirmwareAndShowsDisagreeingUnitsAsVariants()
		{
			ProbeReportAnalysis first = ProbeReportAnalyzer.Analyse(Zip(await Fm9Run()));
			ProbeReportAnalysis second = ProbeReportAnalyzer.Analyse(Zip(await Fm9WithDrive46DriftedRun()));
			ProbeReportAnalysis fm3 = ProbeReportAnalyzer.Analyse(Zip((await SharedProbeRuns.Fm3).Run));
			DateTimeOffset now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

			ProbeAggregate aggregate = ProbeReportAggregator.Build(
				[new("a", now, first), new("b", now.AddMinutes(1), second), new("c", now, fm3)], now, ["d: unreadable"]);

			Assert.Equal((ProbeReportAggregator.Format, 3), (aggregate.Format, aggregate.Reports));
			Assert.Equal(["FM3 13.00", "FM9 12.00"], aggregate.Groups.Select(group => $"{group.Device} {group.Firmware}"));
			ProbeAggregateGroup fm9 = aggregate.Groups.Single(group => group.Device == "FM9");
			Assert.Equal(["a", "b"], fm9.Reports.Select(report => report.Id));
			Assert.Equal(new ProbeAggregateTemplateLoads(2, 2, 0, 0), fm9.TemplateLoads);
			ProbeAggregateRecomputed word = fm9.RecomputedAmpWords.Single(candidate => candidate is { EffectId: 58, ParameterId: 87 });
			Assert.Equal((true, 2), (word.InScript, word.Reports));
			ProbeAggregateVoicing crunch = fm9.Voicing.Single(voicing => voicing is { Block: "Drive", Ordinal: 46 });
			Assert.False(crunch.Consistent);
			Assert.Equal(new ProbeVerdictCounts(1, 1, 0), crunch.Verdicts);
			Assert.True(fm9.Voicing.Single(voicing => voicing is { Block: "Drive", Ordinal: 6 }).Consistent);
			Assert.All(fm9.InsertDefaults, insert => Assert.True(insert.Consistent));
			Assert.All(fm9.Cables, cable => Assert.Single(cable.Variants));
			Assert.Equal(2, fm9.Steps.Single(step => step.Step == "voicing-drive").Passed + fm9.Steps.Single(step => step.Step == "voicing-drive").Failed);

			using JsonDocument json = JsonDocument.Parse(ProbeReportAggregator.Serialise(aggregate));
			JsonElement group = json.RootElement.GetProperty("groups")[1];
			Assert.Equal("FM9 12.00", group.GetProperty("script").GetString());
			Assert.Equal(43987, group.GetProperty("voicing").EnumerateArray()
				.First(voicing => voicing.GetProperty("ordinal").GetInt32() == 6 && voicing.GetProperty("block").GetString() == "Drive")
				.GetProperty("variants")[0].GetProperty("words").GetProperty("6").GetInt32());
			Assert.Equal("d: unreadable", json.RootElement.GetProperty("unreadable")[0].GetString());
		}

		[Fact]
		public async Task AnAxeFx3ReportCarriesTheVariantThePresetSlotsAndWhetherTheCheckIsVerified()
		{
			SimulatedGen3 mkI = ProbeEngineTests.Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6));
			mkI.PresetCount = 512;

			ProbeReportAnalysis mkII = ProbeReportAnalyzer.Analyse(Zip(await ProbeEngineTests.Run(
				ProbeEngineTests.Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6)), 1)));
			ProbeReportAnalysis first = ProbeReportAnalyzer.Analyse(Zip(await ProbeEngineTests.Run(mkI, 1)));

			Assert.Equal(("0x10", (ProbeDeviceVariant?)ProbeDeviceVariant.MkIIOrTurbo, (bool?)false, (int?)1024),
				(mkII.Model, mkII.DeviceVariant, mkII.DeviceVariantVerified, mkII.PresetSlots));
			Assert.Equal(((ProbeDeviceVariant?)ProbeDeviceVariant.MkI, (int?)512), (first.DeviceVariant, first.PresetSlots));
			Assert.Equal("Mk II or Turbo (unverified)", ProbeReportAnalyzer.DescribeVariant(mkII.DeviceVariant, mkII.DeviceVariantVerified));
			Assert.DoesNotContain(mkII.Problems, problem => problem.Contains("Mk I or a Mk II", StringComparison.Ordinal));
		}

		[Fact]
		public async Task AnFm9ReportHasNoVariantAndAnOlderAxeFx3ReportSaysItCannotTell()
		{
			ProbeReportAnalysis fm9 = ProbeReportAnalyzer.Analyse(Zip(await Fm9Run()));
			ProbeReportAnalysis older = ProbeReportAnalyzer.Analyse(RawZip(("run.json",
				"""{"device":"Axe-Fx III","model":"0x10","firmware":"32.06","outcome":"Completed","steps":[]}""")));
			ProbeReportAnalysis odd = ProbeReportAnalyzer.Analyse(RawZip(("run.json",
				"""{"device":"Axe-Fx III","model":"0x10","firmware":"32.06","deviceVariant":"MkIV","outcome":"Completed","steps":[]}""")));

			Assert.Equal(((ProbeDeviceVariant?)null, (int?)512), (fm9.DeviceVariant, fm9.PresetSlots));
			Assert.Equal("none reported", ProbeReportAnalyzer.DescribeVariant(fm9.DeviceVariant, fm9.DeviceVariantVerified));
			Assert.Null(older.DeviceVariant);
			Assert.Contains(older.Problems, problem => problem.Contains("Mk I or a Mk II", StringComparison.Ordinal));
			Assert.Equal(ProbeDeviceVariant.Unknown, odd.DeviceVariant);
			Assert.Contains(odd.Problems, problem => problem.Contains("'MkIV'", StringComparison.Ordinal));
		}

		[Fact]
		public async Task TheAggregateKeepsAnAxeFx3MkIApartFromAMkII()
		{
			SimulatedGen3 mkI = ProbeEngineTests.Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6));
			mkI.PresetCount = 512;
			ProbeReportAnalysis first = ProbeReportAnalyzer.Analyse(Zip(await ProbeEngineTests.Run(mkI, 1)));
			ProbeReportAnalysis second = ProbeReportAnalyzer.Analyse(Zip(await ProbeEngineTests.Run(
				ProbeEngineTests.Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6)), 1)));
			DateTimeOffset now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

			ProbeAggregate aggregate = ProbeReportAggregator.Build([new("a", now, first), new("b", now, second)], now);

			Assert.Equal(new ProbeDeviceVariant?[] { ProbeDeviceVariant.MkI, ProbeDeviceVariant.MkIIOrTurbo }, aggregate.Groups.Select(group => group.DeviceVariant));
			Assert.Equal((int?)1024, aggregate.Groups[1].Reports.Single().PresetSlots);
			using JsonDocument json = JsonDocument.Parse(ProbeReportAggregator.Serialise(aggregate));
			Assert.Equal("MkI", json.RootElement.GetProperty("groups")[0].GetProperty("deviceVariant").GetString());
			Assert.False(json.RootElement.GetProperty("groups")[1].GetProperty("reports")[0].GetProperty("deviceVariantVerified").GetBoolean());
		}

		private static byte[] RawZip(params (string Name, string Contents)[] entries)
		{
			using MemoryStream stream = new();
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
				foreach ((string name, string contents) in entries)
				{
					using Stream entry = archive.CreateEntry(name).Open();
					entry.Write(Encoding.UTF8.GetBytes(contents));
				}
			return stream.ToArray();
		}
	}
}
