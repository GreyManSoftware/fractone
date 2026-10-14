namespace FracTone.Probe.Gui.Services
{
	// What the wizard needs from the probe engine, so its flow is tested with a fake and no ports.
	public interface IProbeService
	{
		// Opens the device's port and identifies it; read-only.
		Task<ProbeIdentity> ConnectAsync(CancellationToken cancellationToken);

		// The name of the preset stored in a slot, or null for an empty slot; read-only.
		Task<string?> ReadPresetNameAsync(ProbeIdentity identity, int slot, CancellationToken cancellationToken);

		// A full run; the engine restores and verifies whatever happens, cancel included.
		Task<ProbeRun> RunAsync(ProbeOptions options, CancellationToken cancellationToken);

		ProbeReportFiles WriteReport(ProbeRun run);
	}

	public interface IFolderOpener
	{
		void Open(string folder);
	}
}
