using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;
using Fractal.Fm9;

namespace FracTone.Probe
{
	public sealed record ProbePort(IFractalMidiTransport Transport, byte? ModelHint, string Description);

	// Finds and opens the unit's port for the CLI and the GUI alike: an FM9 or Axe-Fx III over USB MIDI (Windows), an FM3
	// over its USB serial port ("FM3 Communications Port", COMn). Opening a port sends nothing; the engine's first frame
	// is the firmware request.
	public static class ProbePorts
	{
		private static readonly string[] DeviceNames = ["FM9", "Axe-Fx III", "AXE-FX III", "Axe-Fx"];

		// name: a MIDI port name or a COMn port; fm3: the user said the unit is an FM3, so go straight to its serial port.
		public static ProbePort Open(string? name = null, bool fm3 = false) =>
			Open(name, fm3, OpenMidi, new SystemSerialPortCatalog(), port => SerialSysExTransport.Open(port, TimeSpan.FromSeconds(2)));

		internal static ProbePort Open(string? name, bool fm3, Func<string?, ProbePort> openMidi, ISerialPortCatalog catalog,
			Func<string, IFractalMidiTransport> openSerial)
		{
			bool serialName = name is not null && SerialPortDiscovery.LooksLikeSerialPort(name);
			if (fm3 || serialName)
				return OpenSerial(catalog, openSerial, serialName ? name : null) ?? throw new InvalidOperationException(NoSerialPort(catalog, name));
			try
			{
				return openMidi(name);
			}
			catch (InvalidOperationException) when (name is null)
			{
				// No FM9 or Axe-Fx III over MIDI: an FM3 on its serial port is the other unit a user may have plugged in.
				if (OpenSerial(catalog, openSerial, null) is { } port)
					return port;
				throw;
			}
		}

		// The transport stays plain, not a Gen3DeviceTransport: the engine identifies the unit from the model byte of its
		// replies and keeps them verbatim, and that wrapper would rewrite an FM3's replies to look like an FM9's.
		private static ProbePort? OpenSerial(ISerialPortCatalog catalog, Func<string, IFractalMidiTransport> openSerial, string? explicitPort)
		{
			SerialPortInfo? port = SerialPortDiscovery.Find(catalog.List(), Gen3Device.Fm3.Name, explicitPort);
			return port is null ? null : new ProbePort(openSerial(port.Port), Gen3Device.Fm3.Model, $"serial port {port.Port} ('{port.Description}')");
		}

		private static ProbePort OpenMidi(string? name)
		{
			Fm9MidiPorts ports = Fm9Connection.ListPorts();
			string[] wanted = name is null ? DeviceNames : [name];
			foreach (string candidate in wanted)
			{
				int input = Index(ports.Inputs, candidate), output = Index(ports.Outputs, candidate);
				if (input < 0 || output < 0) continue;
				string portName = ports.Inputs[input];
				byte? hint = portName.Contains("FM9", StringComparison.OrdinalIgnoreCase) ? Gen3Device.Fm9.Model
					: portName.Contains("Axe", StringComparison.OrdinalIgnoreCase) ? Gen3Device.AxeFx3.Model
					: portName.Contains("FM3", StringComparison.OrdinalIgnoreCase) ? Gen3Device.Fm3.Model : null;
				return new ProbePort(new WindowsNaudioMidiTransport(input, output, TimeSpan.FromSeconds(2)), hint,
					$"MIDI ports '{ports.Inputs[input]}' / '{ports.Outputs[output]}'");
			}
			throw new InvalidOperationException(
				$"No MIDI port {(name is null ? "naming an FM9 or Axe-Fx III" : $"matching '{name}'")}. Inputs: {string.Join(", ", ports.Inputs)}. " +
				"An FM3 connects as a USB serial port: choose the FM3, or name its COMn port.");
		}

		private static string NoSerialPort(ISerialPortCatalog catalog, string? explicitPort)
		{
			IReadOnlyList<SerialPortInfo> ports = catalog.List();
			string listed = ports.Count == 0 ? "none" : string.Join(", ", ports.Select(port => $"{port.Port} ({port.Description})"));
			return (explicitPort is not null && SerialPortDiscovery.LooksLikeSerialPort(explicitPort)
				? $"No serial port {explicitPort}."
				: "No serial port names an FM3 (\"FM3 Communications Port\"); install Fractal's FM3 USB Serial Driver, or name its COMn port.") +
				$" Serial ports: {listed}.";
		}

		private static int Index(IReadOnlyList<string> ports, string name)
		{
			for (int index = 0; index < ports.Count; index++)
				if (ports[index].Contains(name, StringComparison.OrdinalIgnoreCase)) return index;
			return -1;
		}
	}
}
