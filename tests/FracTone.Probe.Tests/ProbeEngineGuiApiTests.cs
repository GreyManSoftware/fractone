using Fractal.Companion.Protocol;

namespace FracTone.Probe.Tests
{
	// The engine surface the GUI wizard uses: progress events, the read-only identity check and slot-name read, and the
	// safe-slot rule it checks up front.
	public sealed class ProbeEngineGuiApiTests
	{
		[Fact]
		public async Task ProgressReportsEveryStepStartAndFinishEndingWithTheRestore()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			List<ProbeProgress> events = [];

			ProbeRun run = await new ProbeEngine(fm9, new FakeClock()).RunAsync(
				new ProbeOptions(503, 1) { QueryTimeout = TimeSpan.FromMilliseconds(150), Progress = new Recorder(events) }, CancellationToken.None);

			Assert.Equal(ProbeOutcome.Completed, run.Outcome);
			List<ProbeProgress> finished = [.. events.Where(progress => progress.Status is not null)];
			Assert.Equal(run.Steps.Count + 1, finished.Count);
			Assert.Equal([.. Enumerable.Range(1, finished.Count)], finished.Select(progress => progress.Completed));
			ProbeProgress last = events[^1];
			Assert.Equal(("restore", ProbeStepStatus.Passed), (last.Step, last.Status));
			Assert.Equal(last.Total, last.Completed);
			Assert.Equal(("identify", (ProbeStepStatus?)null), (events[0].Step, events[0].Status));
		}

		[Fact]
		public async Task IdentifyingSendsOnlyTheFirmwareRequestAndSaysWhetherAScriptCoversIt()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (13, 0));

			ProbeIdentity identity = await new ProbeEngine(fm9, new FakeClock()).IdentifyAsync(null, CancellationToken.None);

			Assert.Equal(("FM9", "13.00", false), (identity.Device.Name, identity.Firmware, identity.Scripted));
			Assert.All(fm9.Received, frame => Assert.Equal(0x08, frame[5]));
		}

		[Fact]
		public async Task ReadingASlotsNameChangesNothingOnTheDevice()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			ProbeEngine engine = new(fm9, new FakeClock());
			ProbeIdentity identity = await engine.IdentifyAsync(Gen3Device.Fm9.Model, CancellationToken.None);

			string? name = await engine.ReadPresetNameAsync(identity, 503, CancellationToken.None);

			Assert.Equal("Safe slot preset", name);
			Assert.DoesNotContain(fm9.Received, frame => ProbeCommandPolicy.ChangesState(frame));
		}

		[Theory]
		[InlineData(-1, true)]
		[InlineData(0, false)]
		[InlineData(511, false)]
		[InlineData(512, true)]
		public void TheSafeSlotRuleAcceptsOnlyPresetsTheDeviceHasBelow512(int slot, bool refused)
		{
			Assert.Equal(refused, ProbeEngine.SafeSlotProblem(Gen3Device.Fm9, slot) is not null);
		}

		private sealed class Recorder(List<ProbeProgress> events) : IProgress<ProbeProgress>
		{
			public void Report(ProbeProgress value) => events.Add(value);
		}
	}
}
