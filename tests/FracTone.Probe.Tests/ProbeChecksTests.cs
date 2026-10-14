using Fractal.Companion.Protocol;

namespace FracTone.Probe.Tests
{
	public sealed class ProbeChecksTests
	{
		[Fact]
		public async Task TheDriveVoicingCheckComparesTheWordsMeasuredOnTheFm9()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));

			ProbeRun run = await ProbeEngineTests.Run(fm9, 2);

			ProbeStepResult drive = run.Steps.Single(step => step.Id == "voicing-drive");
			Assert.Equal(ProbeStepStatus.Passed, drive.Status);
			Assert.Equal("6=43987 9=65534 11=33095 14=31268", drive.Data["6.words"]);
			Assert.Equal("4", drive.Data["0.expected"]);
			Assert.Equal(ProbeStepStatus.Passed, run.Steps.Single(step => step.Id == "voicing-amp").Status);
			// Every type change is followed by a read of that block before anything else is sent.
			Assert.All(fm9.Received.Select((frame, index) => (frame, index)).Where(pair => pair.frame[5] == 0x01 && pair.frame[6] == 0x09),
				pair => Assert.Equal(0x1F, fm9.Received[pair.index + 1][5]));
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task AVoicingThatDiffersFailsItsStepWhileTheRunGoesOnAndRestores()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			Func<int, int, IReadOnlyDictionary<int, int>?> voicing = fm9.Voicing;
			fm9.Voicing = (effect, ordinal) => effect == 118 && ordinal == 46
				? new Dictionary<int, int>(voicing(effect, ordinal)!) { [6] = 45675 }
				: voicing(effect, ordinal);

			ProbeRun run = await ProbeEngineTests.Run(fm9, 2);

			ProbeStepResult drive = run.Steps.Single(step => step.Id == "voicing-drive");
			Assert.Equal(ProbeStepStatus.Failed, drive.Status);
			Assert.Equal("6: 45675 want 60458", drive.Data["46.mismatches"]);
			Assert.Equal(ProbeOutcome.CompletedWithFailures, run.Outcome);
			Assert.Equal(ProbeStepStatus.Passed, run.Steps.Single(step => step.Id == "cable-codes").Status);
			Assert.True(run.Restore!.LeftAsFound);
			Assert.Equal(7, fm9.Preset);
		}

		[Fact]
		public async Task ATypeChangeThatDidNotTakeIsSentOnceMore()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.OnFrame = frame =>
			{
				if (frame[5] == 0x01 && frame[6] == 0x09 && fm9.Received.Count(sent => sent[5] == 0x01 && sent[6] == 0x09) == 1)
					fm9.DropWrites = 1;
			};

			ProbeRun run = await ProbeEngineTests.Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			byte[] first = fm9.Received.First(frame => frame[5] == 0x01 && frame[6] == 0x09);
			Assert.Equal(2, fm9.Received.Count(frame => frame.AsSpan().SequenceEqual(first)));
		}

		[Fact]
		public async Task FreshInsertsGoOneAtATimeOnTheEmptyPresetAndRecordEachRecord()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));

			ProbeRun run = await ProbeEngineTests.Run(fm9, 2);

			ProbeStepResult fresh = run.Steps.Single(step => step.Id == "fresh-inserts");
			Assert.Equal(ProbeStepStatus.Passed, fresh.Status);
			Assert.Equal(("unknown", "empty", "r1c1", "147x4"), (fresh.Data["sincePowerCycle"], fresh.Data["template"], fresh.Data["Amp 1.cell"], fresh.Data["Amp 1.shape"]));
			Assert.Equal(17, fresh.Data["Amp 1.header"].Split(' ').Length);
			Assert.Equal(147 * 4, fresh.Data["Amp 1.words"].Split(' ').Length);
			Assert.Equal(11, fresh.Data.Keys.Count(key => key.EndsWith(".shape", StringComparison.Ordinal)));
			// Each insert (01 30 + 01 32) is read back before the next cell is selected.
			int[] selects = [.. fm9.Received.Select((frame, index) => (frame, index)).Where(pair => pair.frame[5] == 0x01 && pair.frame[6] == 0x30).Select(pair => pair.index)];
			Assert.All(selects, index => Assert.Equal(0x32, fm9.Received[index + 1][6]));
			Assert.All(selects, index => Assert.Equal([0x03, 0x7F, 0x7F], fm9.Received[index + 2][5..8]));
			Assert.Contains(run.Readbacks, readback => readback.Name == "fresh-inserts-readback.syx" && readback.Content == ProbeContent.Ours);
		}

		[Fact]
		public async Task TheFm3NumbersCellsByItsFourRowsAndFlagsItsChecksUnverified()
		{
			SimulatedGen3 fm3 = ProbeEngineTests.Unit(Gen3Device.Fm3, "gen3-fm3-13.00-ride.syx", (13, 0));

			ProbeRun run = await ProbeEngineTests.Run(fm3, 2);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			ProbeStepResult cables = run.Steps.Single(step => step.Id == "cable-codes");
			Assert.Equal(("true", "0x02"), (cables.Data["unverified"], cables.Data["r2c2>r1c3.mask"]));
			// r2c2 is cell 5 and r1c3 cell 8 on a 4-row grid (7 and 12 on the FM9's 6 rows).
			Assert.Contains(fm3.Received, frame => frame[5] == 0x01 && frame[6] == 0x35 && frame.AsSpan(21, 3).SequenceEqual(new byte[] { 0x02, 0x42, 0x00 }));
			Assert.Equal(3, run.Steps.Single(step => step.Id == "fresh-inserts").Data.Keys.Count(key => key.EndsWith(".shape", StringComparison.Ordinal)));
			ProbeStepResult delay = run.Steps.Single(step => step.Id == "voicing-delay-21-26");
			Assert.Equal(("r1c1", "Worn Tape"), (delay.Data["Delay 1.cell"], delay.Data["21.name"]));
			Assert.Contains(" 6=21 ", delay.Data["21.words"], StringComparison.Ordinal);
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task ACableTheUnitIgnoresIsSentOnceMoreThenRecordedAsAFailure()
		{
			SimulatedGen3 axe = ProbeEngineTests.Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6));
			axe.OnFrame = frame =>
			{
				if (frame[5] == 0x01 && frame[6] == 0x35) axe.DropWrites = 1;
			};

			ProbeRun run = await ProbeEngineTests.Run(axe, 2);

			ProbeStepResult cables = run.Steps.Single(step => step.Id == "cable-codes");
			Assert.Equal(ProbeStepStatus.Failed, cables.Status);
			Assert.Equal(12, axe.Received.Count(frame => frame[5] == 0x01 && frame[6] == 0x35));
			Assert.Equal("none", cables.Data["r1c1>r1c2.changed"]);
			Assert.Equal(ProbeOutcome.CompletedWithFailures, run.Outcome);
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public void TheAllowListLetsThroughLevelTwoEditsOnlyInsideTheDevicesGrid()
		{
			ProbeCommandPolicy fm3 = new(Gen3Device.Fm3, 503, 7, allowStore: false);
			byte[][] allowed =
			[
				ProbeFrames.BulkRead(Gen3Device.Fm3, 58), ProbeFrames.SelectType(Gen3Device.Fm3, 70, 6, 21),
				ProbeFrames.Route(Gen3Device.Fm3, 4, 11, 1), .. ProbeFrames.InsertBlock(Gen3Device.Fm3, 58, 4, 12)
			];
			foreach (byte[] frame in allowed)
				fm3.Check(frame);
			Assert.False(ProbeCommandPolicy.ChangesState(ProbeFrames.BulkRead(Gen3Device.Fm3, 58)));

			// The FM9's numbering of r6c14 (cell 83) is past the FM3's 48 cells; Global (2) is never inserted.
			IReadOnlyList<byte[]> fm9Cell = ProbeFrames.InsertBlock(Gen3Device.Fm9, 58, 6, 14);
			Assert.Throws<ProbeRefusedException>(() => fm3.Check(Gen3Device.Fm3.Retarget(new FractalSysExFrame(fm9Cell[0])).Bytes.ToArray()));
			Assert.Throws<ProbeRefusedException>(() => fm3.Check(Gen3Device.Fm3.Retarget(new FractalSysExFrame(fm9Cell[1])).Bytes.ToArray()));
			Assert.Throws<ProbeRefusedException>(() => fm3.Check(Gen3Device.Fm3.Retarget(new FractalSysExFrame(ProbeFrames.Route(Gen3Device.Fm9, 6, 13, 6))).Bytes.ToArray()));
			Assert.Throws<ProbeRefusedException>(() => fm3.Check(ProbeFrames.InsertBlock(Gen3Device.Fm3, 2, 1, 1)[1]));
		}

		[Fact]
		public void TheDryRunListsEveryLevelTwoCheckFrame()
		{
			IReadOnlyList<PlannedFrame> plan = ProbePlanner.Plan(Gen3Device.Fm3, "13.00", 503, 2, allowStore: false);

			Assert.Contains(plan, frame => frame.Step == "voicing-delay-21-26" && frame.Frame == ProbeFrames.Hex(ProbeFrames.SelectType(Gen3Device.Fm3, 70, 6, 26)));
			Assert.Contains(plan, frame => frame.Step == "cable-codes" && frame.Frame == ProbeFrames.Hex(ProbeFrames.Route(Gen3Device.Fm3, 2, 2, 1)));
			Assert.Contains(plan, frame => frame.Step == "fresh-inserts" && frame.Frame == ProbeFrames.Hex(ProbeFrames.InsertBlock(Gen3Device.Fm3, 66, 1, 3)[1]));
		}
	}
}
