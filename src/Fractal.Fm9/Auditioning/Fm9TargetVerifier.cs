using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Companion.Auditioning
{
	public sealed record Fm9TargetVerificationResult(
		int VisiblePresetNumber,
		string PresetName,
		int SceneNumber);

	public sealed class Fm9TargetVerifier
	{
		private readonly IFractalMidiTransport _transport;

		public Fm9TargetVerifier(IFractalMidiTransport transport)
		{
			_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		}

		public async Task<Fm9TargetVerificationResult> VerifyAsync(
			int expectedVisiblePresetNumber,
			int expectedSceneNumber,
			CancellationToken cancellationToken)
		{
			if (expectedVisiblePresetNumber is < 0 or > 511)
			{
				throw new ArgumentOutOfRangeException(
					nameof(expectedVisiblePresetNumber),
					"FM9 visible preset numbers are 0 through 511.");
			}

			if (expectedSceneNumber is < 1 or > 8)
			{
				throw new ArgumentOutOfRangeException(
					nameof(expectedSceneNumber),
					"FM9 scene numbers are 1 through 8.");
			}

			IReadOnlyList<FractalSysExFrame> presetResponses = await _transport.RequestAsync(
				FractalSysExCodec.CreateCurrentPresetNameRequest(),
				cancellationToken);
			FractalSysExFrame? presetResponse = presetResponses.FirstOrDefault(frame =>
				FractalSysExCodec.TryGetCurrentPreset(frame.Bytes.Span, out _, out _));

			if (presetResponse is null ||
				!FractalSysExCodec.TryGetCurrentPreset(
					presetResponse.Value.Bytes.Span,
					out int visiblePresetNumber,
					out string presetName))
			{
				throw new InvalidOperationException(
					"The FM9 did not return a valid current-preset response.");
			}

			if (visiblePresetNumber != expectedVisiblePresetNumber)
			{
				ReadOnlySpan<byte> responseBytes = presetResponse.Value.Bytes.Span;
				throw new InvalidOperationException(
					$"The FM9 reports preset #{visiblePresetNumber} ('{presetName}'; " +
					$"number bytes 0x{responseBytes[6]:X2} 0x{responseBytes[7]:X2}), " +
					$"not the requested preset #{expectedVisiblePresetNumber}.");
			}

			IReadOnlyList<FractalSysExFrame> sceneResponses = await _transport.RequestAsync(
				FractalSysExCodec.CreateCurrentSceneRequest(),
				cancellationToken);
			FractalSysExFrame? sceneResponse = sceneResponses.FirstOrDefault(frame =>
				FractalSysExCodec.TryGetCurrentScene(frame.Bytes.Span, out _));

			if (sceneResponse is null ||
				!FractalSysExCodec.TryGetCurrentScene(
					sceneResponse.Value.Bytes.Span,
					out int sceneNumber))
			{
				throw new InvalidOperationException(
					"The FM9 did not return a valid current-scene response.");
			}

			if (sceneNumber != expectedSceneNumber)
			{
				throw new InvalidOperationException(
					$"The FM9 reports Scene {sceneNumber}, not the requested Scene {expectedSceneNumber}.");
			}

			return new Fm9TargetVerificationResult(
				visiblePresetNumber,
				presetName,
				sceneNumber);
		}
	}
}
