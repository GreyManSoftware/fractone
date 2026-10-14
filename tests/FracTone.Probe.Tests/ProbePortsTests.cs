using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;
using FracTone.Probe.Cli;

namespace FracTone.Probe.Tests
{
	// The CLI and the GUI open ports through ProbePorts; these drive it with the CLI's options, as the CLI does.
	public sealed class ProbePortsTests
	{
		private static readonly SerialPortInfo[] Ports = [new("COM3", "Communications Port (COM3)"), new("COM7", "FM3 Communications Port (COM7)")];

		[Fact]
		public void Naming_the_FM3_opens_its_serial_port_with_the_FM3_hint()
		{
			List<string> opened = [];
			StubTransport serial = new();

			ProbePort port = Open(["--slot", "503", "--device", "fm3"], NoMidi, new Catalog(Ports), Recording(opened, serial));

			Assert.Same(serial, port.Transport);
			Assert.Equal(Gen3Device.Fm3.Model, port.ModelHint);
			Assert.Equal(["COM7"], opened);
			Assert.Contains("COM7", port.Description);
		}

		[Fact]
		public void A_COM_port_given_as_the_device_port_is_opened_as_serial()
		{
			List<string> opened = [];

			Open(["--slot", "503", "--device-port", "COM3"], NoMidi, new Catalog(Ports), Recording(opened, new StubTransport()));

			Assert.Equal(["COM3"], opened);
		}

		[Fact]
		public void A_missing_COM_port_is_reported_with_the_ports_there_are()
		{
			InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Open(
				["--slot", "503", "--device-port", "COM9"], NoMidi, new Catalog(Ports), _ => throw new InvalidOperationException("opened")));

			Assert.Contains("COM9", error.Message);
			Assert.Contains("COM7 (FM3 Communications Port (COM7))", error.Message);
		}

		[Fact]
		public void A_MIDI_port_name_still_opens_MIDI()
		{
			StubTransport midi = new();
			string? asked = null;

			ProbePort port = Open(["--slot", "503", "--device-port", "Axe-Fx III"],
				name => { asked = name; return new ProbePort(midi, Gen3Device.AxeFx3.Model, "MIDI"); },
				new Catalog(Ports), _ => throw new InvalidOperationException("serial opened"));

			Assert.Same(midi, port.Transport);
			Assert.Equal("Axe-Fx III", asked);
		}

		[Fact]
		public void With_no_MIDI_unit_an_FM3_serial_port_is_used()
		{
			List<string> opened = [];

			ProbePort port = Open(["--slot", "503"], NoMidi, new Catalog(Ports), Recording(opened, new StubTransport()));

			Assert.Equal(["COM7"], opened);
			Assert.Equal(Gen3Device.Fm3.Model, port.ModelHint);
		}

		[Fact]
		public void With_no_unit_at_all_the_MIDI_error_is_kept()
		{
			InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Open(
				["--slot", "503"], NoMidi, new Catalog([]), _ => throw new InvalidOperationException("serial opened")));

			Assert.Equal("no MIDI unit", error.Message);
		}

		[Fact]
		public void The_serial_transport_is_handed_over_unwrapped()
		{
			StubTransport serial = new();

			ProbePort port = Open(["--slot", "503", "--device", "fm3"], NoMidi, new Catalog(Ports), _ => serial);

			// The engine identifies the unit from raw replies; a Gen3DeviceTransport would make an FM3 look like an FM9.
			Assert.IsType<StubTransport>(port.Transport);
		}

		private static ProbePort Open(string[] args, Func<string?, ProbePort> openMidi, ISerialPortCatalog catalog, Func<string, IFractalMidiTransport> openSerial)
		{
			CliOptions options = Program.Parse(args);
			return ProbePorts.Open(options.DevicePort, options.DryRunDevice == "fm3", openMidi, catalog, openSerial);
		}

		private static ProbePort NoMidi(string? name) => throw new InvalidOperationException("no MIDI unit");

		private static Func<string, IFractalMidiTransport> Recording(List<string> opened, IFractalMidiTransport transport) => port =>
		{
			opened.Add(port);
			return transport;
		};

		private sealed class Catalog(IReadOnlyList<SerialPortInfo> ports) : ISerialPortCatalog
		{
			public IReadOnlyList<SerialPortInfo> List() => ports;
		}

		private sealed class StubTransport : IFractalMidiTransport
		{
			public Task SendAsync(FractalSysExFrame frame, CancellationToken cancellationToken) => throw new NotSupportedException();

			public Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(FractalSysExFrame request, CancellationToken cancellationToken) =>
				throw new NotSupportedException();
		}
	}
}
