using System.Numerics;

namespace Fractal.Core
{
	public sealed record LogarithmicSweepSettings(
		int SampleRate,
		double DurationSeconds,
		double StartFrequencyHz,
		double EndFrequencyHz)
	{
		public void Validate()
		{
			if (SampleRate <= 0 || DurationSeconds <= 0 || StartFrequencyHz <= 0 ||
				EndFrequencyHz <= StartFrequencyHz || EndFrequencyHz >= SampleRate / 2d)
			{
				throw new ArgumentOutOfRangeException(
					nameof(SampleRate),
					"The sweep must have a positive duration, increasing frequencies, and an end frequency below Nyquist.");
			}
		}
	}

	public sealed class ImpulseResponseCapture
	{
		public AudioBuffer GenerateLogarithmicSweep(LogarithmicSweepSettings settings)
		{
			ArgumentNullException.ThrowIfNull(settings);
			settings.Validate();

			int sampleCount = checked((int)Math.Round(settings.SampleRate * settings.DurationSeconds));
			float[] samples = new float[sampleCount];
			double logarithmicRange = Math.Log(settings.EndFrequencyHz / settings.StartFrequencyHz);
			double scale = 2 * Math.PI * settings.StartFrequencyHz * settings.DurationSeconds / logarithmicRange;

			for (int index = 0; index < samples.Length; index++)
			{
				double time = (double)index / settings.SampleRate;
				double phase = scale * (Math.Exp(time * logarithmicRange / settings.DurationSeconds) - 1);
				samples[index] = (float)Math.Sin(phase);
			}

			return new AudioBuffer(settings.SampleRate, samples);
		}

		public AudioBuffer Deconvolve(
			AudioBuffer excitation,
			AudioBuffer recordedResponse,
			int impulseResponseSampleCount,
			double regularization = 1e-10)
		{
			ArgumentNullException.ThrowIfNull(excitation);
			ArgumentNullException.ThrowIfNull(recordedResponse);
			excitation.Validate();
			recordedResponse.Validate();

			if (excitation.SampleRate != recordedResponse.SampleRate)
			{
				throw new ArgumentException("The excitation and response must use the same sample rate.", nameof(recordedResponse));
			}

			if (impulseResponseSampleCount <= 0 || impulseResponseSampleCount > recordedResponse.Samples.Count)
			{
				throw new ArgumentOutOfRangeException(
					nameof(impulseResponseSampleCount),
					"The requested impulse-response length must fit in the recorded response.");
			}

			if (regularization <= 0 || !double.IsFinite(regularization))
			{
				throw new ArgumentOutOfRangeException(nameof(regularization));
			}

			int transformLength = GetTransformLength(excitation.Samples.Count, recordedResponse.Samples.Count);
			Complex[] excitationSpectrum = CopyToComplex(excitation.Samples, transformLength);
			Complex[] responseSpectrum = CopyToComplex(recordedResponse.Samples, transformLength);
			FastFourierTransform.TransformInPlace(excitationSpectrum);
			FastFourierTransform.TransformInPlace(responseSpectrum);

			for (int index = 0; index < transformLength; index++)
			{
				Complex excitationValue = excitationSpectrum[index];
				double denominator = (excitationValue.Magnitude * excitationValue.Magnitude) + regularization;
				responseSpectrum[index] = responseSpectrum[index] * Complex.Conjugate(excitationValue) / denominator;
			}

			FastFourierTransform.TransformInPlace(responseSpectrum, inverse: true);

			float[] impulseResponse = new float[impulseResponseSampleCount];

			for (int index = 0; index < impulseResponse.Length; index++)
			{
				impulseResponse[index] = (float)responseSpectrum[index].Real;
			}

			return new AudioBuffer(excitation.SampleRate, impulseResponse);
		}

		private static Complex[] CopyToComplex(IReadOnlyList<float> samples, int transformLength)
		{
			Complex[] values = new Complex[transformLength];

			for (int index = 0; index < samples.Count; index++)
			{
				values[index] = new Complex(samples[index], 0);
			}

			return values;
		}

		private static int GetTransformLength(int excitationLength, int responseLength)
		{
			long requiredLength = (long)excitationLength + responseLength - 1;
			int transformLength = 1;

			while (transformLength < requiredLength)
			{
				if (transformLength > (1 << 29))
				{
					throw new ArgumentOutOfRangeException(
						nameof(responseLength),
						"The recordings are too long to deconvolve in memory.");
				}

				transformLength <<= 1;
			}

			return transformLength;
		}
	}
}
