using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Companion.Auditioning
{
	public sealed record Fm9ReadOnlyProbeResult(
		string FirmwareVersion,
		bool StatusDumpReceived);

	public sealed class Fm9ReadOnlyProbe
	{
		private readonly IFractalMidiTransport _transport;

		public Fm9ReadOnlyProbe(IFractalMidiTransport transport)
		{
			_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		}

		public async Task<Fm9ReadOnlyProbeResult> ProbeAsync(
			CancellationToken cancellationToken)
		{
			IReadOnlyList<FractalSysExFrame> firmwareResponses = await _transport.RequestAsync(
				FractalSysExCodec.CreateFirmwareRequest(),
				cancellationToken);
			FractalSysExFrame? firmwareResponse = firmwareResponses.FirstOrDefault(frame =>
				FractalSysExCodec.TryGetFirmwareVersion(
					frame.Bytes.Span,
					out _));

			if (firmwareResponse is null ||
				!FractalSysExCodec.TryGetFirmwareVersion(
					firmwareResponse.Value.Bytes.Span,
					out string firmwareVersion))
			{
				throw new InvalidOperationException(
					"The FM9 did not return a valid firmware response.");
			}

			IReadOnlyList<FractalSysExFrame> statusResponses = await _transport.RequestAsync(
				FractalSysExCodec.CreateStatusDumpRequest(),
				cancellationToken);
			bool statusReceived = statusResponses.Any(frame =>
				FractalSysExCodec.IsStatusDumpResponse(frame.Bytes.Span));

			if (!statusReceived)
			{
				throw new InvalidOperationException(
					"The FM9 did not return a valid status-dump response.");
			}

			return new Fm9ReadOnlyProbeResult(firmwareVersion, true);
		}
	}
}
