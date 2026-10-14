using Fractal.Companion.Protocol;

namespace FracTone.Probe.Tests
{
	public sealed class ProbeEngineTests
	{
		private const int Found = 7, Safe = 503, Other = 9;
		private const string UserName = "My Secret Tone";

		[Fact]
		public async Task AnFm9LevelTwoRunLoadsTheTemplateChecksTheReadbackAndLeavesTheDeviceAsFound()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.Scene = 3;
			// The FM9's own readback of this template (505, 2026-10-13): three Amp words recomputed, nothing else changed.
			IReadOnlyList<byte[]> readback = SimulatedGen3.Load("fm9-synthetic-ride-fm9-readback.syx");
			fm9.OnLoad = frames => ProbeFrames.Parse(Gen3Device.Fm9, frames).PresetName == ProbeFrames.Parse(Gen3Device.Fm9, readback).PresetName ? readback : frames;

			ProbeRun run = await Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.Equal(("FM9", "12.00", true), (run.Device, run.Firmware, run.FirmwareScripted));
			ProbeStepResult load = run.Steps.Single(step => step.Id == "load-template");
			Assert.Equal(ProbeStepStatus.Passed, load.Status);
			Assert.Contains("id 87: 0 -> 43711", load.Data["recomputed"], StringComparison.Ordinal);
			Assert.Contains("id 119", load.Data["recomputed"], StringComparison.Ordinal);
			Assert.All(run.Steps.Where(step => step.Level == 2), step => Assert.Equal(ProbeStepStatus.Passed, step.Status));
			Assert.True(run.Restore!.LeftAsFound);
			Assert.Equal((Found, 3, 0), (fm9.Preset, fm9.Scene, fm9.Stores));
			Assert.Equal(UserName, ProbeFrames.Parse(Gen3Device.Fm9, fm9.EditBuffer).PresetName);
		}

		[Theory]
		[InlineData(0x11, 13, 0, "gen3-fm3-13.00-ride.syx")]
		[InlineData(0x10, 32, 6, "gen3-axe3-32.06-ride.syx")]
		public async Task AnFm3OrAxeFx3IsIdentifiedByItsModelByteAndProbedInItsOwnFrames(byte model, byte major, byte minor, string preset)
		{
			Gen3Device device = Gen3Device.FromModel(model)!;
			SimulatedGen3 unit = Unit(device, preset, (major, minor));

			ProbeRun run = await Run(unit, 2);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.Equal(device.Name, run.Device);
			Assert.Equal("Read back exactly as sent.", run.Steps.Single(step => step.Id == "load-template").Message);
			Assert.True(run.Restore!.LeftAsFound);
			Assert.Equal(Found, unit.Preset);
			// After the firmware question in the FM9's byte went unanswered, everything carried the device's own.
			Assert.All(unit.Received.SkipWhile(frame => frame[4] != model), frame => Assert.Equal(model, frame[4]));
			Assert.All(run.Readbacks.Where(readback => readback.Name.StartsWith("backup", StringComparison.Ordinal)),
				readback => Assert.Equal(device.DumpDataFrames + 2, readback.Frames.Count));
		}

		[Fact]
		public async Task AnUnknownModelStopsTheRunAfterTheFirmwareRequest()
		{
			SimulatedGen3 unknown = new(0x07, (1, 0)) { AnswersAnyModel = true };

			ProbeRun run = await Run(unknown, 2);

			Assert.Equal(ProbeOutcome.Stopped, run.Outcome);
			Assert.Contains("0x07", run.StopReason, StringComparison.Ordinal);
			Assert.All(unknown.Received, frame => Assert.Equal(0x08, frame[5]));
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task FirmwareWithoutAScriptRunsReadOnlyStepsLabelledUnverified()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (13, 0));

			ProbeRun run = await Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.Equal((false, 1), (run.FirmwareScripted, run.LevelRun));
			Assert.DoesNotContain(fm9.Received, frame => ProbeCommandPolicy.ChangesState(frame));
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task UnsavedEditsStopALevelTwoRunBeforeAnythingChanges()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.EditBuffer = Variant(Gen3Device.Fm9, fm9.Slots[Found], "Edited on the unit");

			ProbeRun run = await Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Stopped, run.Outcome);
			Assert.Contains("unsaved edits", run.StopReason, StringComparison.Ordinal);
			Assert.DoesNotContain(fm9.Received, frame => ProbeCommandPolicy.ChangesState(frame));
			Assert.Equal("Edited on the unit", ProbeFrames.Parse(Gen3Device.Fm9, fm9.EditBuffer).PresetName);
		}

		[Fact]
		public async Task UnsavedEditsAreOnlyNotedOnALevelOneRun()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.EditBuffer = Variant(Gen3Device.Fm9, fm9.Slots[Found], "Edited on the unit");

			ProbeRun run = await Run(fm9, 1);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.True(run.UnsavedEdits);
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task ADroppedLoadIsSentOnceMore()
		{
			SimulatedGen3 fm3 = Unit(Gen3Device.Fm3, "gen3-fm3-13.00-ride.syx", (13, 0));
			fm3.DropLoads = 1;

			ProbeRun run = await Run(fm3, 2);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.Equal("2", run.Steps.Single(step => step.Id == "load-template").Data["loadAttempts"]);
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task ALoadDroppedTwiceStopsTheRunAndTheDeviceIsStillRestored()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.DropLoads = 2;

			ProbeRun run = await Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Stopped, run.Outcome);
			Assert.Equal(ProbeStepStatus.Stopped, run.Steps.Single(step => step.Id == "load-template").Status);
			Assert.True(run.Restore!.LeftAsFound);
			Assert.Equal(Found, fm9.Preset);
		}

		[Fact]
		public async Task AQueryThatTimesOutOnceIsAskedAgain()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.Silent = frame => frame[5] == 0x13;
			fm9.SilentCount = 1;

			ProbeRun run = await Run(fm9, 1);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.Equal(2, fm9.Received.Count(frame => frame[5] == 0x13));
			Assert.Contains(run.Exchanges, exchange => exchange.Outcome == "timeout");
		}

		[Fact]
		public async Task TwoTimeoutsInARowStopTheRunAskForAPowerCycleAndStillRestore()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			fm9.Silent = frame => frame[5] == 0x03 && frame[6] == 0x7F && fm9.Loads > 0;
			fm9.SilentCount = 2;

			ProbeRun run = await Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Stopped, run.Outcome);
			Assert.Contains("Power-cycle", run.StopReason, StringComparison.Ordinal);
			Assert.True(run.Restore!.LeftAsFound);
			Assert.Equal(Found, fm9.Preset);
		}

		[Fact]
		public async Task CancellingMidRunStillRestoresAndVerifies()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			using CancellationTokenSource cancel = new();
			fm9.OnFrame = frame =>
			{
				if (frame[5] == 0x79) cancel.Cancel();
			};

			ProbeRun run = await Run(fm9, 2, cancel.Token);

			Assert.Equal(ProbeOutcome.Cancelled, run.Outcome);
			Assert.True(run.Restore!.LeftAsFound);
			Assert.Equal(true, run.Restore.SafeSlotVerified);
			Assert.Equal(Found, fm9.Preset);
			Assert.Equal(UserName, ProbeFrames.Parse(Gen3Device.Fm9, fm9.EditBuffer).PresetName);
		}

		[Fact]
		public async Task ChangingPresetOnTheUnitDuringTheRunStopsItBeforeTheNextWrite()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			int switches = 0;
			fm9.OnFrame = frame =>
			{
				if (frame[5] == 0x0D && fm9.Switches == 1 && switches++ == 1)
				{
					fm9.Preset = Other;
					fm9.EditBuffer = fm9.Slots[Other];
				}
			};

			ProbeRun run = await Run(fm9, 2);

			Assert.Equal(ProbeOutcome.Stopped, run.Outcome);
			Assert.Contains("changed during the run", run.StopReason, StringComparison.Ordinal);
			Assert.Equal(0, fm9.Loads);
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task LevelThreeStoresTheTemplateThenPutsTheBackupBackVerified()
		{
			SimulatedGen3 axe = Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6));
			IReadOnlyList<byte[]> before = axe.Slots[Safe];

			ProbeRun run = await Run(axe, 3, allowStore: true);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			Assert.Equal(2, axe.Stores);
			Assert.Equal("true", run.Steps.Single(step => step.Id == "store-check").Data["slotRestored"]);
			Assert.True(ReadbackComparer.SameContent(Gen3Device.AxeFx3, ProbeFrames.Parse(Gen3Device.AxeFx3, before), ProbeFrames.Parse(Gen3Device.AxeFx3, axe.Slots[Safe])));
			Assert.True(run.Restore!.LeftAsFound);
		}

		[Fact]
		public async Task NothingIsStoredBelowLevelThree()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));

			await Run(fm9, 2, allowStore: true);

			Assert.Equal(0, fm9.Stores);
			Assert.DoesNotContain(fm9.Received, frame => frame[5] == 0x01 && frame[6] == 0x26);
		}

		[Fact]
		public async Task FramesArePacedAtLeastEightyMillisecondsApart()
		{
			FakeClock clock = new();
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			List<DateTimeOffset> times = [];
			fm9.OnFrame = _ => times.Add(clock.Now);

			await new ProbeEngine(fm9, clock).RunAsync(new ProbeOptions(Safe, 2) { QueryTimeout = TimeSpan.FromMilliseconds(500) }, CancellationToken.None);

			Assert.True(times.Count > 20);
			Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(80)));
		}

		[Fact]
		public async Task TheSafeSlotMustExistOnTheDevice()
		{
			SimulatedGen3 fm9 = Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));

			ProbeRun run = await new ProbeEngine(fm9, new FakeClock()).RunAsync(new ProbeOptions(512, 2), CancellationToken.None);

			Assert.Equal(ProbeOutcome.Stopped, run.Outcome);
			Assert.All(fm9.Received, frame => Assert.Equal(0x08, frame[5]));
		}

		internal static SimulatedGen3 Unit(Gen3Device device, string preset, (byte, byte) firmware)
		{
			IReadOnlyList<byte[]> frames = SimulatedGen3.Load(preset);
			SimulatedGen3 unit = new(device.Model, firmware) { Preset = Found };
			unit.Slots[Found] = Variant(device, frames, UserName);
			unit.Slots[Safe] = Variant(device, frames, "Safe slot preset");
			unit.Slots[Other] = Variant(device, frames, "Another preset");
			unit.EditBuffer = unit.Slots[Found];
			// The unit's own voicing (the FM9's captures; the other units share the Drive's ids) and its blocks' records.
			unit.Voicing = (effect, ordinal) => BlockOf(effect) is string block && (device == Gen3Device.Fm9 || block == "Drive")
				? Fractal.Companion.Catalog.Fm9ModelDefaults.For(block, ordinal)
				: null;
			unit.Donor = SimulatedGen3.Load(device == Gen3Device.Fm9 ? "fm9-synthetic-ride.syx" : device == Gen3Device.Fm3 ? "gen3-fm3-13.00-ride.syx" : "gen3-axe3-32.06-ride.syx");
			return unit;
		}

		internal static Task<ProbeRun> Run(SimulatedGen3 unit, int level, CancellationToken cancellationToken = default, bool allowStore = false) =>
			new ProbeEngine(unit, new FakeClock()).RunAsync(
				new ProbeOptions(Safe, level) { AllowStore = allowStore || level == 3, QueryTimeout = TimeSpan.FromMilliseconds(150) },
				cancellationToken);

		private static string? BlockOf(int effect) => effect switch
		{
			58 or 59 => "Amp",
			>= 66 and <= 69 => "Reverb",
			>= 70 and <= 73 => "Delay",
			>= 118 and <= 121 => "Drive",
			_ => null
		};

		// The same preset under another name, in the device's own frames.
		internal static IReadOnlyList<byte[]> Variant(Gen3Device device, IReadOnlyList<byte[]> frames, string name) =>
			[.. ProbeFrames.Parse(device, frames).WithPresetName(name).Frames.Select(frame => device.Retarget(frame).Bytes.ToArray())];
	}
}
