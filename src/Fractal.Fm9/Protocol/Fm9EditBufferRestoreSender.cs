using Fractal.Companion.Transport;

namespace Fractal.Companion.Protocol
{
	public sealed class Fm9EditBufferRestoreSender
	{
		public static readonly TimeSpan InterFrameDelay = TimeSpan.FromMilliseconds(150);
		public static readonly TimeSpan AcknowledgedImportSettleDelay = TimeSpan.FromMilliseconds(300);
		private const int DefaultAcknowledgementAttempts = 3;

		private readonly IFractalMidiTransport _transport;
		private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
		private readonly int _acknowledgementAttempts;

		public Fm9EditBufferRestoreSender(
			IFractalMidiTransport transport,
			Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
			int acknowledgementAttempts = DefaultAcknowledgementAttempts)
		{
			_transport = transport ?? throw new ArgumentNullException(nameof(transport));
			_delayAsync = delayAsync ?? Task.Delay;

			if (acknowledgementAttempts <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(acknowledgementAttempts));
			}

			_acknowledgementAttempts = acknowledgementAttempts;
		}

		public async Task RestoreAsync(
			Fm9PresetDump dump,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(dump);
			// A dump captured from a stored slot carries that slot number; retarget it so it can never write a stored preset.
			dump = dump.ForEditBufferImport();

			for (int frameIndex = 0; frameIndex < dump.Frames.Count; frameIndex++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await _transport.SendAsync(dump.Frames[frameIndex], cancellationToken);

				if (frameIndex < dump.Frames.Count - 1)
				{
					await _delayAsync(InterFrameDelay, cancellationToken);
				}
			}
		}

		public Task RestoreAsync(
			ReadOnlyMemory<byte> dumpBytes,
			CancellationToken cancellationToken)
		{
			// Parsing before the first transport call makes malformed dumps atomic at the sender boundary.
			return RestoreAsync(Fm9PresetDump.Parse(dumpBytes.Span), cancellationToken);
		}

		public async Task RestoreAcknowledgedAsync(
			Fm9PresetDump dump,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(dump);
			dump = dump.ForEditBufferImport();

			byte? unacknowledgedFunction = null;

			for (int attempt = 1; attempt <= _acknowledgementAttempts; attempt++)
			{
				unacknowledgedFunction = null;

				foreach (FractalSysExFrame frame in dump.Frames)
				{
					byte function = frame.Bytes.Span[5];
					IReadOnlyList<FractalSysExFrame> responses =
						await _transport.RequestAsync(frame, cancellationToken);
					bool acknowledged = responses.Any(response =>
						FractalSysExCodec.IsSuccessfulAcknowledgement(
							response.Bytes.Span,
							function));

					if (!acknowledged)
					{
						unacknowledgedFunction = function;
						break;
					}
				}

				if (unacknowledgedFunction is null)
				{
					await _delayAsync(AcknowledgedImportSettleDelay, cancellationToken);
					return;
				}

				if (attempt < _acknowledgementAttempts)
				{
					await _delayAsync(InterFrameDelay, cancellationToken);
				}
			}

			throw new InvalidOperationException(
				$"The FM9 did not successfully acknowledge preset frame function " +
				$"0x{unacknowledgedFunction!.Value:X2} after {_acknowledgementAttempts} " +
				"complete transfer attempts.");
		}
	}
}
