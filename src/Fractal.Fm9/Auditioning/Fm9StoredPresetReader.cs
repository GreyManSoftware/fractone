using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Companion.Auditioning
{
	public sealed record Fm9StoredPresetReadResult(
		int VisiblePresetNumber,
		Fm9PresetDump Dump,
		Fm9PresetBody Body);

	public sealed class Fm9StoredPresetReader
	{
		private const int DefaultMaximumAttempts = 3;
		private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);

		private readonly IFractalMidiTransport _transport;
		private readonly int _maximumAttempts;
		private readonly TimeSpan _retryDelay;
		private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

		public Fm9StoredPresetReader(
			IFractalMidiTransport transport,
			int maximumAttempts = DefaultMaximumAttempts,
			TimeSpan? retryDelay = null,
			Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
		{
			_transport = transport ?? throw new ArgumentNullException(nameof(transport));

			if (maximumAttempts <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
			}

			_maximumAttempts = maximumAttempts;
			_retryDelay = retryDelay ?? DefaultRetryDelay;
			_delayAsync = delayAsync ?? Task.Delay;
		}

		public async Task<Fm9StoredPresetReadResult> ReadAsync(
			int visiblePresetNumber,
			CancellationToken cancellationToken)
		{
			if (visiblePresetNumber is < 0 or > 16_383)
			{
				throw new ArgumentOutOfRangeException(
					nameof(visiblePresetNumber),
					"FM9 visible preset numbers must be between 0 and 16383.");
			}

			InvalidDataException? lastFailure = null;

			for (int attempt = 1; attempt <= _maximumAttempts; attempt++)
			{
				IReadOnlyList<FractalSysExFrame> responses = await _transport.RequestAsync(
					FractalSysExCodec.CreateStoredPresetRequest(visiblePresetNumber),
					cancellationToken);
				int responseLength = responses.Sum(frame => frame.Bytes.Length);
				byte[] responseBytes = new byte[responseLength];
				int offset = 0;

				foreach (FractalSysExFrame response in responses)
				{
					response.Bytes.Span.CopyTo(responseBytes.AsSpan(offset));
					offset += response.Bytes.Length;
				}

				try
				{
					Fm9PresetDump dump = Fm9PresetDump.Parse(responseBytes);
					Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump);
					return new Fm9StoredPresetReadResult(visiblePresetNumber, dump, body);
				}
				catch (InvalidDataException exception)
				{
					lastFailure = exception;

					if (attempt == _maximumAttempts)
					{
						break;
					}

					await _delayAsync(_retryDelay, cancellationToken);
				}
			}

			throw new InvalidDataException(
				$"The FM9 did not return a complete valid preset dump after " +
				$"{_maximumAttempts} attempts.",
				lastFailure);
		}
	}
}
