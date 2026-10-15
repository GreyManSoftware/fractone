using Fractal.Companion.Protocol;

namespace FracTone.Probe.Tests
{
	// A level-2 run on a simulated unit, with the frames it held before the run.
	internal sealed record SharedProbeRun(SimulatedGen3 Unit, ProbeRun Run, IReadOnlyList<byte[]> BufferBefore);

	// The level-2 runs many tests only read: each is run once per test process and awaited by every test that asks for it,
	// rather than re-run for each test. Tests that change a run or set a unit up differently run their own.
	internal static class SharedProbeRuns
	{
		private static readonly Lazy<Task<SharedProbeRun>> CleanFm9 = new(() => Start(Fm9WithItsReadback(), unit => unit.Scene = 3));
		private static readonly Lazy<Task<SharedProbeRun>> MismatchedFm9 = new(() => Start(Fm9WithItsReadback(), DriftDrive46));
		private static readonly Lazy<Task<SharedProbeRun>> CleanFm3 = new(() =>
			Start(ProbeEngineTests.Unit(Gen3Device.Fm3, "gen3-fm3-13.00-ride.syx", (13, 0)), _ => { }));
		private static readonly Lazy<Task<SharedProbeRun>> CleanAxeFx3 = new(() =>
			Start(ProbeEngineTests.Unit(Gen3Device.AxeFx3, "gen3-axe3-32.06-ride.syx", (32, 6)), _ => { }));

		// An FM9 on firmware 12 that reads the template back as the real one did, showing scene 3.
		public static Task<SharedProbeRun> Fm9 => CleanFm9.Value;

		// The same FM9 whose Drive type 46 loads word 6 as 45675 where the script wants 60458.
		public static Task<SharedProbeRun> Fm9WithDrive46Drifted => MismatchedFm9.Value;

		public static Task<SharedProbeRun> Fm3 => CleanFm3.Value;

		public static Task<SharedProbeRun> Clean(Gen3Device device) =>
			device == Gen3Device.Fm9 ? Fm9 : device == Gen3Device.Fm3 ? Fm3 : CleanAxeFx3.Value;

		// The FM9's own readback of the template (505, 2026-10-13): three Amp words recomputed, nothing else changed.
		public static SimulatedGen3 Fm9WithItsReadback()
		{
			SimulatedGen3 fm9 = ProbeEngineTests.Unit(Gen3Device.Fm9, "metallica-eight-scenes.syx", (12, 0));
			IReadOnlyList<byte[]> readback = SimulatedGen3.Load("fm9-synthetic-ride-fm9-readback.syx");
			fm9.OnLoad = frames => ProbeFrames.Parse(Gen3Device.Fm9, frames).PresetName == ProbeFrames.Parse(Gen3Device.Fm9, readback).PresetName ? readback : frames;
			return fm9;
		}

		public static void DriftDrive46(SimulatedGen3 fm9)
		{
			Func<int, int, IReadOnlyDictionary<int, int>?> voicing = fm9.Voicing;
			fm9.Voicing = (effect, ordinal) => effect == 118 && ordinal == 46
				? new Dictionary<int, int>(voicing(effect, ordinal)!) { [6] = 45675 }
				: voicing(effect, ordinal);
		}

		private static async Task<SharedProbeRun> Start(SimulatedGen3 unit, Action<SimulatedGen3> setUp)
		{
			setUp(unit);
			IReadOnlyList<byte[]> before = unit.EditBuffer;
			return new SharedProbeRun(unit, await ProbeEngineTests.Run(unit, 2), before);
		}
	}
}
