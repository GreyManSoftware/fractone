using System.IO.Ports;
using Fractal.Companion.Protocol;

namespace Fractal.Companion.Transport
{
	// The FM3 talks SysEx over a USB serial (CDC) port, not USB MIDI: "FM3 Communications Port" (COMn) through Fractal's
	// FM3 USB Serial Driver on Windows, /dev/cu.usbmodem* on macOS. The bytes are the same SysEx as over MIDI, but arrive
	// as an unframed stream, so frames are cut out here. Requests behave as WindowsNaudioMidiTransport's do: a preset dump
	// (03) ends at its 79 footer and a bulk read (1F) at 76, from any gen-3 model; anything else collects replies until the
	// line goes quiet; no reply before the timeout is a TimeoutException.
	public sealed class SerialSysExTransport : IFractalMidiTransport, IDisposable
	{
		private readonly Stream _stream;
		private readonly IDisposable? _owner;
		private readonly TimeSpan _responseTimeout;
		private readonly TimeSpan _quietGap;
		private readonly SysExFramer _framer = new();
		private readonly SemaphoreSlim _requestGate = new(1, 1);
		private readonly SemaphoreSlim _writeGate = new(1, 1);
		private readonly Lock _syncRoot = new();
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _reader;
		private TaskCompletionSource? _pendingResponseSignal;
		private List<FractalSysExFrame>? _pendingResponses;
		private bool _expectsBulkReadEnd;
		private bool _expectsPresetDumpEnd;
		private Exception? _readFault;
		private bool _disposed;

		// The stream is any duplex byte stream (a serial port's, or a fake in tests); owner, if given, is disposed with it.
		public SerialSysExTransport(Stream stream, TimeSpan? responseTimeout = null, TimeSpan? quietGap = null, IDisposable? owner = null)
		{
			_stream = stream ?? throw new ArgumentNullException(nameof(stream));
			if (!stream.CanRead || !stream.CanWrite)
				throw new ArgumentException("The serial stream must be readable and writable.", nameof(stream));
			_responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(2);
			_quietGap = quietGap ?? TimeSpan.FromMilliseconds(100);
			if (_responseTimeout <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(responseTimeout), "The response timeout must be positive.");
			if (_quietGap <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(quietGap), "The quiet gap must be positive.");
			_owner = owner;
			_reader = Task.Run(ReadLoopAsync);
		}

		public string? PortName { get; private init; }

		// Opens a real port. CDC ignores the baud rate; DTR is raised because many CDC devices stay silent until it is.
		public static SerialSysExTransport Open(string portName, TimeSpan? responseTimeout = null)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(portName);
			SerialPort port = new(portName, 115_200, Parity.None, 8, StopBits.One)
			{
				Handshake = Handshake.None,
				DtrEnable = true,
				RtsEnable = true,
				// A preset dump is about 25 KB; the read loop drains it, but the driver buffer shouldn't overflow meanwhile.
				ReadBufferSize = 65_536,
				WriteBufferSize = 65_536,
				WriteTimeout = 5_000
			};
			try
			{
				port.Open();
				return new SerialSysExTransport(port.BaseStream, responseTimeout, owner: port) { PortName = portName };
			}
			catch
			{
				port.Dispose();
				throw;
			}
		}

		public async Task SendAsync(FractalSysExFrame frame, CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			await WriteAsync(frame, cancellationToken);
		}

		public Task SelectPresetAsync(int presetNumber, int midiChannel, CancellationToken cancellationToken) =>
			throw new NotSupportedException("A serial port has no Program Change; switch presets with 01 27.");

		public async Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(FractalSysExFrame request, CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			if (request.Bytes.Length < 7)
				throw new ArgumentException("Not a Fractal SysEx request.", nameof(request));
			await _requestGate.WaitAsync(cancellationToken);
			try
			{
				TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
				bool expectsBulkReadEnd = request.Bytes.Span[5] == 0x1F;
				bool expectsPresetDumpEnd = request.Bytes.Span[5] == 0x03;
				lock (_syncRoot)
				{
					ThrowIfReadFaulted();
					_pendingResponseSignal = completion;
					_pendingResponses = [];
					_expectsBulkReadEnd = expectsBulkReadEnd;
					_expectsPresetDumpEnd = expectsPresetDumpEnd;
				}
				try
				{
					TimeSpan responseTimeout = expectsPresetDumpEnd
						? TimeSpan.FromSeconds(Math.Max(_responseTimeout.TotalSeconds, 10))
						: _responseTimeout;
					using CancellationTokenSource timeout = new(responseTimeout);
					using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
					await WriteAsync(request, linked.Token);
					await completion.Task.WaitAsync(linked.Token);
					if (!expectsBulkReadEnd && !expectsPresetDumpEnd)
						await WaitForQuietAsync(linked.Token);
					lock (_syncRoot)
						return _pendingResponses?.ToArray() ?? throw new InvalidOperationException("Serial response collection ended unexpectedly.");
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					throw new TimeoutException("The device did not return a SysEx response before the request timeout.");
				}
				finally
				{
					lock (_syncRoot)
					{
						if (ReferenceEquals(_pendingResponseSignal, completion))
						{
							_pendingResponseSignal = null;
							_pendingResponses = null;
							_expectsBulkReadEnd = false;
							_expectsPresetDumpEnd = false;
						}
					}
				}
			}
			finally
			{
				_requestGate.Release();
			}
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			_stop.Cancel();
			// Closing the stream is what ends a pending read on a real port; a read can't always be cancelled.
			_stream.Dispose();
			_owner?.Dispose();
			try
			{
				_reader.Wait(TimeSpan.FromSeconds(2));
			}
			catch (AggregateException)
			{
			}
			_stop.Dispose();
			_requestGate.Dispose();
			_writeGate.Dispose();
		}

		// Replies that aren't a terminated read are complete once no new frame has arrived for the quiet gap.
		private async Task WaitForQuietAsync(CancellationToken cancellationToken)
		{
			while (true)
			{
				int seen;
				lock (_syncRoot)
					seen = _pendingResponses?.Count ?? 0;
				await Task.Delay(_quietGap, cancellationToken);
				lock (_syncRoot)
				{
					ThrowIfReadFaulted();
					if ((_pendingResponses?.Count ?? 0) == seen)
						return;
				}
			}
		}

		private async Task WriteAsync(FractalSysExFrame frame, CancellationToken cancellationToken)
		{
			await _writeGate.WaitAsync(cancellationToken);
			try
			{
				await _stream.WriteAsync(frame.Bytes, cancellationToken);
				await _stream.FlushAsync(cancellationToken);
			}
			finally
			{
				_writeGate.Release();
			}
		}

		private async Task ReadLoopAsync()
		{
			byte[] buffer = new byte[4_096];
			try
			{
				while (!_stop.IsCancellationRequested)
				{
					int count = await _stream.ReadAsync(buffer, _stop.Token);
					if (count == 0)
						throw new IOException("The serial port closed.");
					foreach (byte[] frame in _framer.Push(buffer.AsSpan(0, count)))
						OnFrame(new FractalSysExFrame(frame));
				}
			}
			catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or ObjectDisposedException or IOException)
			{
			}
			catch (Exception exception)
			{
				// The port went away (unplugged, or the editor grabbed it): fail the waiting request now, not at its timeout.
				TaskCompletionSource? completion;
				lock (_syncRoot)
				{
					_readFault = exception;
					completion = _pendingResponseSignal;
				}
				completion?.TrySetException(new IOException("The serial port stopped responding: " + exception.Message, exception));
			}
		}

		private void OnFrame(FractalSysExFrame frame)
		{
			TaskCompletionSource? completion;
			bool completes;
			lock (_syncRoot)
			{
				if (_pendingResponses is null)
					return;
				_pendingResponses.Add(frame);
				completion = _pendingResponseSignal;
				ReadOnlySpan<byte> bytes = frame.Bytes.Span;
				completes = (!_expectsBulkReadEnd && !_expectsPresetDumpEnd) ||
					(_expectsBulkReadEnd && IsGen3Frame(bytes) && bytes[5] == 0x76) ||
					(_expectsPresetDumpEnd && IsGen3Frame(bytes) && bytes[5] == 0x79);
			}
			if (completes)
				completion?.TrySetResult();
		}

		// Any gen-3 model's frame ends a read, as over MIDI.
		private static bool IsGen3Frame(ReadOnlySpan<byte> bytes) =>
			bytes.Length >= 8 && Gen3Device.FromModel(bytes[4]) is { } device &&
			FractalSysExCodec.IsValidFm9Frame(device.Normalise(new FractalSysExFrame(bytes.ToArray())).Bytes.Span);

		private void ThrowIfReadFaulted()
		{
			if (_readFault is not null)
				throw new IOException("The serial port stopped responding: " + _readFault.Message, _readFault);
		}

		private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
	}
}
