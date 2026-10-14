using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	// One progress event: a step starting (Status null) or finishing. Total is 0 until the device is identified, since
	// the script (and so the step count) depends on its firmware; the restore counts as the last step.
	public sealed record ProbeProgress(int Completed, int Total, string Step, string Kind, ProbeStepStatus? Status, string? Message);

	// What the read-only identity check found: the device, its firmware, and whether a probe script covers that firmware
	// (without one, a run does the read-only steps only).
	public sealed record ProbeIdentity(Gen3Device Device, string Firmware, bool Scripted);
}
