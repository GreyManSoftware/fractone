using Fractal.Companion.Transport;

namespace Fractal.Fm9
{
	public sealed record Fm9MidiPorts(IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);

	// The one way the tools reach the FM9. Calls run one at a time, in this process and across processes (Claude Code and
	// another agent may each run a bridge), and the MIDI ports are open only while a call runs, so FM9-Edit can use them
	// in between.
	public sealed class Fm9Connection : IDisposable
	{
		private readonly Func<IFractalMidiTransport> _open;
		private readonly SemaphoreSlim _gate = new(1, 1);
		private readonly string? _lockFile;
		private readonly TimeSpan _wait;

		// The cross-process lock is a file held open exclusively while a call runs: every FM9 tools process for this user
		// shares it, and the system releases it if a process dies mid-call, which a named semaphore would not.
		public Fm9Connection(Func<IFractalMidiTransport> open, string? lockFile = null, TimeSpan? wait = null)
		{
			_open = open ?? throw new ArgumentNullException(nameof(open));
			_wait = wait ?? TimeSpan.FromSeconds(60);
			_lockFile = lockFile;
		}

		public static string DefaultLockFile =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fractone", "midi.lock");

		// The FM9's USB MIDI ports on Windows, found by name each time a call starts (so a replugged FM9 is found again).
		public static Fm9Connection ForSettings(Func<Fm9MidiSettings> midi)
		{
			ArgumentNullException.ThrowIfNull(midi);
			return new(() => OpenTransport(midi()), DefaultLockFile);
		}

		public static IFractalMidiTransport OpenTransport(Fm9MidiSettings midi)
		{
			ArgumentNullException.ThrowIfNull(midi);
			Fm9MidiPorts ports = ListPorts();
			int input = Find(ports.Inputs, midi.Input, "input");
			int output = Find(ports.Outputs, midi.Output, "output");
			try
			{
				return new WindowsNaudioMidiTransport(input, output);
			}
			catch (Exception exception) when (exception is not PlatformNotSupportedException)
			{
				throw new InvalidOperationException(
					$"The FM9's MIDI ports could not be opened ({exception.Message}). FM9-Edit or another program may be holding them; " +
					"disconnect FM9-Edit (or close it) and try again.", exception);
			}
		}

		public static Fm9MidiPorts ListPorts()
		{
			if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The FM9 tools reach the FM9 through Windows MIDI.");
			return new(
				Enumerable.Range(0, NAudio.Midi.MidiIn.NumberOfDevices).Select(index => NAudio.Midi.MidiIn.DeviceInfo(index).ProductName).ToArray(),
				Enumerable.Range(0, NAudio.Midi.MidiOut.NumberOfDevices).Select(index => NAudio.Midi.MidiOut.DeviceInfo(index).ProductName).ToArray());
		}

		private static int Find(IReadOnlyList<string> ports, string name, string kind)
		{
			for (int index = 0; index < ports.Count; index++)
				if (ports[index].Contains(name, StringComparison.OrdinalIgnoreCase)) return index;
			throw new InvalidOperationException(ports.Count == 0
				? $"No MIDI {kind} ports were found. Is the FM9 connected by USB and switched on?"
				: $"No MIDI {kind} port matching '{name}'. Ports: {string.Join(", ", ports)}. Set midi.{kind} in the settings file.");
		}

		public async Task<T> RunAsync<T>(Func<IFractalMidiTransport, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(work);
			if (!await _gate.WaitAsync(_wait, cancellationToken))
				throw new TimeoutException("Another FM9 call in this session is still running.");
			FileStream? held = null;
			try
			{
				if (_lockFile is not null)
					held = await AcquireAsync(_lockFile, cancellationToken);
				IFractalMidiTransport transport = _open();
				try
				{
					return await work(transport, cancellationToken);
				}
				finally
				{
					(transport as IDisposable)?.Dispose();
				}
			}
			finally
			{
				held?.Dispose();
				_gate.Release();
			}
		}

		private async Task<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			DateTime giveUp = DateTime.UtcNow + _wait;
			while (true)
			{
				try
				{
					return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				}
				catch (IOException) when (DateTime.UtcNow < giveUp)
				{
					await Task.Delay(250, cancellationToken);
				}
				catch (IOException exception)
				{
					throw new TimeoutException("Another FM9 tools process has been using the FM9 for over a minute; try again when it finishes.", exception);
				}
			}
		}

		public void Dispose() => _gate.Dispose();
	}
}
