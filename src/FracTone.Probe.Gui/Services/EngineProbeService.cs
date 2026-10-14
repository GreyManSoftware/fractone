namespace FracTone.Probe.Gui.Services
{
	// The real engine over the device's port (USB MIDI, or the FM3's USB serial port). The port stays open from Connect to exit, so the editors can't
	// grab it between the slot check and the run.
	public sealed class EngineProbeService(string reportFolder) : IProbeService, IDisposable
	{
		private ProbePort? _port;

		public async Task<ProbeIdentity> ConnectAsync(CancellationToken cancellationToken)
		{
			if (!OperatingSystem.IsWindows())
				throw new PlatformNotSupportedException("This version runs on Windows only.");
			Close();
			_port = ProbePorts.Open();
			return await new ProbeEngine(_port.Transport).IdentifyAsync(_port.ModelHint, cancellationToken);
		}

		public Task<string?> ReadPresetNameAsync(ProbeIdentity identity, int slot, CancellationToken cancellationToken) =>
			new ProbeEngine(Port.Transport).ReadPresetNameAsync(identity, slot, cancellationToken);

		public Task<ProbeRun> RunAsync(ProbeOptions options, CancellationToken cancellationToken) =>
			new ProbeEngine(Port.Transport).RunAsync(options with { ModelHint = Port.ModelHint }, cancellationToken);

		public ProbeReportFiles WriteReport(ProbeRun run) => ProbeReportWriter.Write(run, reportFolder);

		public void Dispose() => Close();

		private ProbePort Port => _port ?? throw new InvalidOperationException("Connect to the device first.");

		private void Close()
		{
			(_port?.Transport as IDisposable)?.Dispose();
			_port = null;
		}
	}
}
