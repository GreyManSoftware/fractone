using System.Diagnostics;
using System.Globalization;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Companion.Auditioning
{
	public sealed record Fm9StoredPresetReadResult(
		int VisiblePresetNumber,
		Fm9PresetDump Dump,
		Fm9PresetBody Body);

	// Reads a preset (03) and retries a dump that comes back incomplete or garbled, backing off between attempts.
	// Two things spoil a dump on a busy FM9 (2026-10-15: level_scenes straight after measure_level, the player playing):
	// unsolicited frames arriving mid-dump (realtime SysEx such as tempo, a late reply to a scene change), which the
	// transport collects with the dump's frames, and a dump cut short while the unit is still busy. The first is filtered
	// out here; the second is waited out with growing pauses, during which the transport collects nothing, so anything
	// the FM9 is still sending is dropped before the next request.
	public sealed class Fm9StoredPresetReader
	{
		private const int DefaultMaximumAttempts = 5;
		private const int MaximumTimeouts = 2;
		private const byte HeaderFunction = 0x77, DataFunction = 0x78, FooterFunction = 0x79;
		private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(2);

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

		// The pause before attempt n + 1: doubling from the first delay, capped (250 ms, 0.5 s, 1 s, 2 s, 2 s...).
		public TimeSpan RetryDelay(int attempt) =>
			TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * (1L << Math.Min(attempt - 1, 16)), Math.Max(MaximumRetryDelay.Ticks, _retryDelay.Ticks)));

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

			Exception? lastFailure = null;
			string lastProblem = "";
			int timeouts = 0, attempt;
			Stopwatch elapsed = Stopwatch.StartNew();

			for (attempt = 1; attempt <= _maximumAttempts; attempt++)
			{
				try
				{
					IReadOnlyList<FractalSysExFrame> responses = await _transport.RequestAsync(
						FractalSysExCodec.CreateStoredPresetRequest(visiblePresetNumber),
						cancellationToken);
					FractalSysExFrame[] dumpFrames = DumpFrames(responses);
					lastProblem = Describe(responses, dumpFrames);
					Fm9PresetDump dump = Fm9PresetDump.Parse(Concatenate(dumpFrames));
					Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(dump, Gen3DeviceTransport.DeviceOf(_transport));
					return new Fm9StoredPresetReadResult(visiblePresetNumber, dump, body);
				}
				catch (InvalidDataException exception)
				{
					lastFailure = exception;
				}
				catch (TimeoutException exception) when (++timeouts < MaximumTimeouts)
				{
					// A dump that never finishes costs the transport's whole timeout, so only one is retried.
					lastFailure = exception;
					lastProblem = exception.Message;
				}

				if (attempt == _maximumAttempts)
				{
					break;
				}

				await _delayAsync(RetryDelay(attempt), cancellationToken);
			}

			string preset = visiblePresetNumber == 0x3FFF ? "edit buffer" : $"preset {visiblePresetNumber}";
			throw new InvalidDataException(
				$"The FM9 did not return a complete valid preset dump after {Math.Min(attempt, _maximumAttempts)} attempts " +
				$"over {elapsed.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s reading the {preset} " +
				$"(last attempt: {lastProblem}; {lastFailure?.Message}). It may still have been busy, e.g. straight after a scene " +
				"change or a recording, or another program (FM9-Edit) was talking to it: wait a few seconds and try again.",
				lastFailure);
		}

		// The dump's own frames, from the last header on: unsolicited frames are dropped, and so is the tail of an earlier
		// dump that was still arriving when this request went out.
		private static FractalSysExFrame[] DumpFrames(IReadOnlyList<FractalSysExFrame> responses)
		{
			FractalSysExFrame[] frames = [.. responses.Where(frame => Function(frame) is HeaderFunction or DataFunction or FooterFunction)];
			int header = Array.FindLastIndex(frames, frame => Function(frame) == HeaderFunction);
			return header > 0 ? frames[header..] : frames;
		}

		private static byte? Function(FractalSysExFrame frame)
		{
			ReadOnlySpan<byte> bytes = frame.Bytes.Span;
			return bytes.Length >= 8 && bytes[0] == 0xF0 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x74 ? bytes[5] : null;
		}

		private static byte[] Concatenate(IReadOnlyList<FractalSysExFrame> frames)
		{
			byte[] bytes = new byte[frames.Sum(frame => frame.Bytes.Length)];
			int offset = 0;
			foreach (FractalSysExFrame frame in frames)
			{
				frame.Bytes.Span.CopyTo(bytes.AsSpan(offset));
				offset += frame.Bytes.Length;
			}
			return bytes;
		}

		// What came back, for the error: e.g. "header, 3 data frames, no footer; 2 other frames (0x10) dropped".
		private static string Describe(IReadOnlyList<FractalSysExFrame> responses, IReadOnlyList<FractalSysExFrame> dump)
		{
			if (responses.Count == 0) return "no reply";
			int headers = dump.Count(frame => Function(frame) == HeaderFunction);
			int data = dump.Count(frame => Function(frame) == DataFunction);
			int footers = dump.Count(frame => Function(frame) == FooterFunction);
			string[] others = [.. responses.Where(frame => Function(frame) is not (HeaderFunction or DataFunction or FooterFunction))
				.Select(frame => Function(frame) is byte function ? $"0x{function:X2}" : "non-Fractal").Distinct()];
			int otherCount = responses.Count(frame => Function(frame) is not (HeaderFunction or DataFunction or FooterFunction));
			string text = $"{(headers == 0 ? "no header" : "header")}, {data} data frame(s), {(footers == 0 ? "no footer" : "footer")}";
			return otherCount == 0 ? text : $"{text}; {otherCount} other frame(s) ({string.Join(", ", others)}) dropped";
		}
	}
}
