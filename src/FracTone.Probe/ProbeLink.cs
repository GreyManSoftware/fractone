using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace FracTone.Probe
{
	public interface IProbeClock
	{
		DateTimeOffset Now { get; }

		Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
	}

	public sealed class SystemProbeClock : IProbeClock
	{
		public static SystemProbeClock Instance { get; } = new();

		public DateTimeOffset Now => DateTimeOffset.UtcNow;

		public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
			delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
	}

	// Whose bytes an exchange carries: the user's presets stay out of an anonymised report; our templates' readbacks don't.
	public enum ProbeContent
	{
		Protocol,
		User,
		Ours
	}

	public sealed record ProbeExchange(
		string Step,
		DateTimeOffset At,
		byte[] Sent,
		IReadOnlyList<byte[]> Received,
		string Outcome,
		ProbeContent Content);

	public sealed class ProbeTimeoutException(string message) : TimeoutException(message);

	// Two silent timeouts in a row: an unthrottled III has wedged its MIDI before, and retrying in a loop doesn't help.
	public sealed class ProbeWedgedException(string message) : Exception(message);

	// The one path to the device: every frame is checked against the allow-list, paced 80-100 ms apart, given a timeout,
	// and kept verbatim (as sent and as received) for backups and the report.
	public sealed class ProbeLink(IFractalMidiTransport port, IProbeClock clock, TimeSpan pacing)
	{
		private readonly List<ProbeExchange> _exchanges = [];
		private DateTimeOffset? _lastSent;
		private int _silentTimeouts;

		public ProbeCommandPolicy? Policy { get; set; }
		public string Step { get; set; } = "";
		public ProbeContent Content { get; set; } = ProbeContent.Protocol;
		public bool ChangedState { get; private set; }
		public IReadOnlyList<ProbeExchange> Exchanges => _exchanges;

		public async Task<IReadOnlyList<byte[]>> RequestAsync(byte[] frame, TimeSpan timeout, CancellationToken cancellationToken, bool countsTowardsWedge = true)
		{
			Check(frame);
			await PaceAsync(cancellationToken);
			ChangedState |= Policy is not null && ProbeCommandPolicy.ChangesState(frame);
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			limit.CancelAfter(timeout);
			IReadOnlyList<FractalSysExFrame> replies;
			try
			{
				replies = await port.RequestAsync(new FractalSysExFrame(frame), limit.Token);
			}
			catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
				(exception is TimeoutException || (exception is OperationCanceledException && limit.IsCancellationRequested)))
			{
				Record(frame, [], "timeout");
				if (countsTowardsWedge && ++_silentTimeouts >= 2)
					throw new ProbeWedgedException(
						"The device stopped answering (two timeouts in a row). Power-cycle it before trying again.");
				throw new ProbeTimeoutException($"No reply within {timeout.TotalMilliseconds:0} ms.");
			}
			finally
			{
				_lastSent = clock.Now;
			}
			byte[][] received = [.. replies.Select(reply => reply.Bytes.ToArray())];
			if (received.Length == 0)
			{
				Record(frame, received, "no reply");
				throw new ProbeTimeoutException("The device sent nothing back.");
			}
			_silentTimeouts = 0;
			Record(frame, received, "ok");
			return received;
		}

		public async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
		{
			Check(frame);
			await PaceAsync(cancellationToken);
			ChangedState |= Policy is not null && ProbeCommandPolicy.ChangesState(frame);
			await port.SendAsync(new FractalSysExFrame(frame), cancellationToken);
			_lastSent = clock.Now;
			Record(frame, [], "sent");
		}

		public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => clock.DelayAsync(delay, cancellationToken);

		private void Check(byte[] frame)
		{
			if (Policy is null)
				ProbeCommandPolicy.CheckIdentity(frame);
			else
				Policy.Check(frame);
		}

		private async Task PaceAsync(CancellationToken cancellationToken)
		{
			if (_lastSent is DateTimeOffset last)
				await clock.DelayAsync(last + pacing - clock.Now, cancellationToken);
		}

		private void Record(byte[] sent, IReadOnlyList<byte[]> received, string outcome) =>
			_exchanges.Add(new ProbeExchange(Step, clock.Now, sent, received, outcome, Content));
	}
}
