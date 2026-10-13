using NAudio.Midi;
using Fractal.Companion.Protocol;

namespace Fractal.Companion.Transport
{
	public sealed class WindowsNaudioMidiTransport : IFractalMidiTransport, IDisposable
	{
		private readonly WinMmMidiInput _input;
		private readonly MidiOut _output;
		private readonly SemaphoreSlim _requestGate = new(1, 1);
		private readonly object _syncRoot = new();
		private readonly TimeSpan _responseTimeout;
		private TaskCompletionSource? _pendingResponseSignal;
		private List<FractalSysExFrame>? _pendingResponses;
		private bool _expectsBulkReadEnd;
		private bool _expectsPresetDumpEnd;
		private bool _disposed;

		public WindowsNaudioMidiTransport(
			int inputDeviceNumber,
			int outputDeviceNumber,
			TimeSpan? responseTimeout = null)
		{
			if (!OperatingSystem.IsWindows())
			{
				throw new PlatformNotSupportedException(
					"NAudio MIDI transport is supported only on Windows.");
			}

			_responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(2);

			if (_responseTimeout <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(
					nameof(responseTimeout),
					"The MIDI response timeout must be positive.");
			}

			_output = new MidiOut(outputDeviceNumber);
			// Input is WinMM directly: NAudio's MidiIn overflows in 64-bit processes (see WinMmMidiInput).
			_input = new WinMmMidiInput(inputDeviceNumber, 4_096, 4);
			_input.SysexReceived += OnSysexMessageReceived;
		}

		public Task SendAsync(
			FractalSysExFrame frame,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ThrowIfDisposed();
			_output.SendBuffer(frame.Bytes.ToArray());

			return Task.CompletedTask;
		}

		public Task SelectPresetAsync(int presetNumber, int midiChannel, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ThrowIfDisposed();
			if (presetNumber is < 0 or > 511) throw new ArgumentOutOfRangeException(nameof(presetNumber));
			if (midiChannel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(midiChannel));
			_output.Send(MidiMessage.ChangeControl(0, presetNumber / 128, midiChannel).RawData);
			_output.Send(MidiMessage.ChangePatch(presetNumber % 128, midiChannel).RawData);
			return Task.CompletedTask;
		}

		public async Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(
			FractalSysExFrame request,
			CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			await _requestGate.WaitAsync(cancellationToken);

			try
			{
				TaskCompletionSource completion = new(
					TaskCreationOptions.RunContinuationsAsynchronously);
				bool expectsBulkReadEnd = request.Bytes.Span[5] == 0x1F;
				bool expectsPresetDumpEnd = request.Bytes.Span[5] == 0x03;

				lock (_syncRoot)
				{
					_pendingResponseSignal = completion;
					_pendingResponses = [];
					_expectsBulkReadEnd = expectsBulkReadEnd;
					_expectsPresetDumpEnd = expectsPresetDumpEnd;
				}

				try
				{
					_output.SendBuffer(request.Bytes.ToArray());
					TimeSpan responseTimeout = expectsPresetDumpEnd
						? TimeSpan.FromSeconds(Math.Max(_responseTimeout.TotalSeconds, 10))
						: _responseTimeout;
					using CancellationTokenSource timeout = new(responseTimeout);
					using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
						cancellationToken,
						timeout.Token);
					await completion.Task.WaitAsync(linked.Token);

					if (!expectsBulkReadEnd && !expectsPresetDumpEnd)
					{
						await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token);
					}

					IReadOnlyList<FractalSysExFrame> responses;

					lock (_syncRoot)
					{
						responses = _pendingResponses?.ToArray()
							?? throw new InvalidOperationException(
								"MIDI response collection ended unexpectedly.");
					}

					return responses;
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					throw new TimeoutException(
						"The FM9 did not return a SysEx response before the request timeout.");
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
			{
				return;
			}

			_disposed = true;
			_input.SysexReceived -= OnSysexMessageReceived;
			_input.Dispose();
			_output.Dispose();
			_requestGate.Dispose();
		}

		private void OnSysexMessageReceived(byte[] bytes)
		{
			FractalSysExFrame frame = new(bytes);
			TaskCompletionSource? completion;
			bool completesResponse;

			lock (_syncRoot)
			{
				_pendingResponses?.Add(frame);
				completion = _pendingResponseSignal;
				completesResponse =
					(_expectsBulkReadEnd &&
						FractalSysExCodec.IsValidFm9Frame(frame.Bytes.Span) &&
						frame.Bytes.Span[5] == 0x76) ||
					(_expectsPresetDumpEnd &&
						FractalSysExCodec.IsValidFm9Frame(frame.Bytes.Span) &&
						frame.Bytes.Span[5] == 0x79);
			}

			if ((!_expectsBulkReadEnd && !_expectsPresetDumpEnd) || completesResponse)
			{
				completion?.TrySetResult();
			}
		}

		private void ThrowIfDisposed()
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
		}
	}
}
