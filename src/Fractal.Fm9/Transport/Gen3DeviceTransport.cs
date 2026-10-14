using Fractal.Companion.Protocol;

namespace Fractal.Companion.Transport
{
	// Lets the FM9 code talk to another gen-3 unit: each message is retargeted to the device's model byte on the way out,
	// and each reply is mapped back to the FM9's so the existing readers and checks apply unchanged. Every frame is also
	// kept exactly as the device sent or received it (Exchanges), because backups and reports must be the device's own
	// bytes, never re-encoded (the encoder drops the Axe-Fx III's Tone Match area).
	public sealed class Gen3DeviceTransport(IFractalMidiTransport inner, Gen3Device device) : IFractalMidiTransport, IDisposable
	{
		private readonly List<Gen3Exchange> _exchanges = [];
		private readonly Lock _gate = new();

		public Gen3Device Device { get; } = device ?? throw new ArgumentNullException(nameof(device));

		// The unit behind a transport: the wrapped device, or the FM9 for a plain transport.
		public static Gen3Device DeviceOf(IFractalMidiTransport transport) => transport is Gen3DeviceTransport gen3 ? gen3.Device : Gen3Device.Fm9;

		public IReadOnlyList<Gen3Exchange> Exchanges
		{
			get
			{
				lock (_gate)
					return [.. _exchanges];
			}
		}

		public async Task SendAsync(FractalSysExFrame frame, CancellationToken cancellationToken)
		{
			FractalSysExFrame sent = Device.Retarget(frame);
			Record(sent, []);
			await inner.SendAsync(sent, cancellationToken);
		}

		public async Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(FractalSysExFrame request, CancellationToken cancellationToken)
		{
			FractalSysExFrame sent = Device.Retarget(request);
			IReadOnlyList<FractalSysExFrame> replies = await inner.RequestAsync(sent, cancellationToken);
			Record(sent, replies);
			return replies.Select(Device.Normalise).ToArray();
		}

		// Program Change is never sent to a gen-3 unit through this wrapper: the FM3 12.00 ignores CC32, so a switch to
		// preset 438 landed on 54 (FracCompat). Switch with 01 27 (FractalSysExCodec.CreateSwitchPreset) instead.
		public Task SelectPresetAsync(int presetNumber, int midiChannel, CancellationToken cancellationToken) =>
			throw new NotSupportedException($"Program Change is not used with the {Device.Name}; switch presets with 01 27.");

		public void Dispose() => (inner as IDisposable)?.Dispose();

		private void Record(FractalSysExFrame sent, IReadOnlyList<FractalSysExFrame> replies)
		{
			lock (_gate)
				_exchanges.Add(new Gen3Exchange(DateTimeOffset.UtcNow, sent.Bytes.ToArray(), [.. replies.Select(reply => reply.Bytes.ToArray())]));
		}
	}

	public sealed record Gen3Exchange(DateTimeOffset At, byte[] Sent, IReadOnlyList<byte[]> Received);
}
