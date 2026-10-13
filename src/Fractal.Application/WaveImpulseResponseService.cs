using Fractal.Core;

namespace Fractal.Application
{
	public enum KnownExcitationReference
	{
		FractalLogarithmicSweepV1
	}

	public sealed record ControlledIrCaptureMetadata(
		string SystemUnderTest,
		string CapturePath,
		int ResponseStartOffsetMilliseconds,
		string? CaptureNotes)
	{
		public void Validate()
		{
			if (string.IsNullOrWhiteSpace(SystemUnderTest))
			{
				throw new ArgumentException(
					"Identify the speaker, cabinet, or hardware system being captured.",
					nameof(SystemUnderTest));
			}

			if (string.IsNullOrWhiteSpace(CapturePath))
			{
				throw new ArgumentException(
					"Describe the playback-to-recording capture path.",
					nameof(CapturePath));
			}

			if (ResponseStartOffsetMilliseconds is < 0 or > 10_000)
			{
				throw new ArgumentOutOfRangeException(
					nameof(ResponseStartOffsetMilliseconds),
					"The response start offset must be between 0 and 10,000 milliseconds.");
			}
		}
	}

	public sealed record ControlledIrCaptureRequest(
		KnownExcitationReference ExcitationReference,
		Stream ExcitationSweep,
		Stream CapturedResponse,
		ControlledIrCaptureMetadata Metadata);

	public sealed record DeconvolutionReadiness(
		bool IsReady,
		string Reason,
		double? ResponseRmsLevel = null,
		double? ResponsePeakLevel = null);

	public sealed record ControlledIrDeconvolutionResult(
		DeconvolutionReadiness Readiness,
		byte[]? Fm9ImpulseResponseWav)
	{
		public bool WasDeconvolved => Readiness.IsReady && Fm9ImpulseResponseWav is not null;
	}

	public sealed class WaveImpulseResponseService
	{
		private const double SweepDurationSeconds = 10;
		private const double MaximumImpulseResponseDurationSeconds = 0.2;
		private const double MinimumResponseRmsLevel = 0.0001;
		private const double MaximumPermittedPeakLevel = 0.9995;
		private const double MaximumKnownSweepMeanAbsoluteError = 0.0002;

		private readonly ImpulseResponseCapture _capture;
		private readonly Fm9ImpulseResponsePreparer _preparer;

		public WaveImpulseResponseService(
			ImpulseResponseCapture capture,
			Fm9ImpulseResponsePreparer preparer)
		{
			_capture = capture ?? throw new ArgumentNullException(nameof(capture));
			_preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
		}

		public byte[] CreateExcitationSweepWav()
		{
			AudioBuffer sweep = CreateReferenceSweep();
			return WriteWav(sweep, isFm9ImpulseResponse: false);
		}

		public ControlledIrDeconvolutionResult CreateControlledFm9ImpulseResponse(
			ControlledIrCaptureRequest request)
		{
			(WaveAudioFile excitationFile, WaveAudioFile responseFile) = ReadCaptureFiles(request);
			DeconvolutionReadiness readiness = AssessReadiness(request, excitationFile, responseFile);

			if (!readiness.IsReady)
			{
				return new ControlledIrDeconvolutionResult(readiness, null);
			}

			int responseStartOffsetSamples = checked(
				request.Metadata.ResponseStartOffsetMilliseconds * responseFile.Audio.SampleRate / 1_000);
			int outputSampleCount = checked((int)Math.Ceiling(
				responseFile.Audio.SampleRate * MaximumImpulseResponseDurationSeconds));
			int deconvolutionSampleCount = checked(responseStartOffsetSamples + outputSampleCount);
			AudioBuffer deconvolved = _capture.Deconvolve(
				excitationFile.Audio,
				responseFile.Audio,
				deconvolutionSampleCount);
			AudioBuffer alignedImpulseResponse = new(
				deconvolved.SampleRate,
				deconvolved.Samples
					.Skip(responseStartOffsetSamples)
					.Take(outputSampleCount)
					.ToArray());
			AudioBuffer prepared = _preparer.PrepareForExport(alignedImpulseResponse);

			return new ControlledIrDeconvolutionResult(
				readiness,
				WriteWav(prepared, isFm9ImpulseResponse: true));
		}

		public DeconvolutionReadiness AssessControlledFm9ImpulseResponse(
			ControlledIrCaptureRequest request)
		{
			(WaveAudioFile excitationFile, WaveAudioFile responseFile) = ReadCaptureFiles(request);
			return AssessReadiness(request, excitationFile, responseFile);
		}

		private static (WaveAudioFile Excitation, WaveAudioFile Response) ReadCaptureFiles(
			ControlledIrCaptureRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);
			ArgumentNullException.ThrowIfNull(request.ExcitationSweep);
			ArgumentNullException.ThrowIfNull(request.CapturedResponse);
			ArgumentNullException.ThrowIfNull(request.Metadata);
			request.Metadata.Validate();

			return (
				WaveFileReader.Read(request.ExcitationSweep),
				WaveFileReader.Read(request.CapturedResponse));
		}

		private DeconvolutionReadiness AssessReadiness(
			ControlledIrCaptureRequest request,
			WaveAudioFile excitation,
			WaveAudioFile response)
		{
			if (request.ExcitationReference != KnownExcitationReference.FractalLogarithmicSweepV1)
			{
				return Refuse("Only this application's identified 10-second reference logarithmic sweep is accepted as excitation.");
			}

			if (excitation.Channels != 1 || response.Channels != 1)
			{
				return Refuse("Controlled capture requires mono excitation and mono captured-response WAV files; stereo music stems are refused.");
			}

			if (excitation.Audio.SampleRate != response.Audio.SampleRate)
			{
				return Refuse("The known excitation and captured response must have the same sample rate.");
			}

			if (!MatchesReferenceSweep(excitation.Audio))
			{
				return Refuse("The excitation WAV does not match this application's reference sweep. Arbitrary audio and isolated song stems cannot be used as IR excitation.");
			}

			if (response.Audio.Samples.Any(static sample => !float.IsFinite(sample)))
			{
				return Refuse("The captured response contains non-finite audio samples and cannot be deconvolved.");
			}

			int responseStartOffsetSamples = checked(
				request.Metadata.ResponseStartOffsetMilliseconds * response.Audio.SampleRate / 1_000);
			int requiredResponseSamples = checked(
				responseStartOffsetSamples +
				excitation.Audio.Samples.Count +
				(int)Math.Ceiling(response.Audio.SampleRate * MaximumImpulseResponseDurationSeconds));

			if (response.Audio.Samples.Count < requiredResponseSamples)
			{
				return Refuse("The captured response is too short for the declared response offset and 200 ms IR window.");
			}

			(double rms, double peak) = MeasureSignal(response.Audio.Samples);

			if (rms < MinimumResponseRmsLevel)
			{
				return Refuse("The captured response is effectively silent; record the known sweep through the system before deconvolution.", rms, peak);
			}

			if (peak >= MaximumPermittedPeakLevel)
			{
				return Refuse("The captured response is clipped or too close to full scale; reduce capture gain and record it again.", rms, peak);
			}

			return new DeconvolutionReadiness(
				true,
				"Ready: the exact known excitation, explicit capture metadata, compatible mono WAV files, and a non-silent unclipped response were supplied. This is readiness for deconvolution, not proof that the capture path was physically valid.",
				rms,
				peak);
		}

		private static DeconvolutionReadiness Refuse(string reason, double? rms = null, double? peak = null)
		{
			return new DeconvolutionReadiness(false, reason, rms, peak);
		}

		private static bool MatchesReferenceSweep(AudioBuffer candidate)
		{
			AudioBuffer reference = CreateReferenceSweep();

			if (candidate.SampleRate != reference.SampleRate ||
				candidate.Samples.Count != reference.Samples.Count)
			{
				return false;
			}

			double absoluteError = 0;

			for (int index = 0; index < candidate.Samples.Count; index++)
			{
				absoluteError += Math.Abs(candidate.Samples[index] - reference.Samples[index]);
			}

			return absoluteError / candidate.Samples.Count <= MaximumKnownSweepMeanAbsoluteError;
		}

		private static AudioBuffer CreateReferenceSweep()
		{
			LogarithmicSweepSettings settings = new(
				WaveFileWriter.Fm9SampleRate,
				SweepDurationSeconds,
				20,
				20_000);

			return new ImpulseResponseCapture().GenerateLogarithmicSweep(settings);
		}

		private static (double Rms, double Peak) MeasureSignal(IReadOnlyList<float> samples)
		{
			double squareSum = 0;
			double peak = 0;

			foreach (float sample in samples)
			{
				double magnitude = Math.Abs(sample);
				squareSum += sample * sample;
				peak = Math.Max(peak, magnitude);
			}

			return (Math.Sqrt(squareSum / samples.Count), peak);
		}

		private static byte[] WriteWav(AudioBuffer audio, bool isFm9ImpulseResponse)
		{
			using MemoryStream stream = new();

			if (isFm9ImpulseResponse)
			{
				WaveFileWriter.WriteFm9CompatibleImpulseResponse(audio, stream);
			}
			else
			{
				WaveFileWriter.WritePcm24Mono(audio, stream);
			}

			return stream.ToArray();
		}
	}
}
