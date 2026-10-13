using NWaves.Operations;
using NWaves.Signals;

namespace Fractal.Core
{
	public sealed class Fm9ImpulseResponsePreparer
	{
		public AudioBuffer PrepareForExport(
			AudioBuffer impulseResponse,
			float peakLevel = 0.99f)
		{
			ArgumentNullException.ThrowIfNull(impulseResponse);
			impulseResponse.Validate();

			if (!float.IsFinite(peakLevel) || peakLevel is <= 0 or > 1)
			{
				throw new ArgumentOutOfRangeException(
					nameof(peakLevel),
					"The target peak level must be finite and between 0 and 1.");
			}

			AudioBuffer resampled = ResampleToFm9Rate(impulseResponse);
			float[] normalizedSamples = resampled.Samples.ToArray();
			float currentPeak = normalizedSamples.Max(static sample => Math.Abs(sample));

			if (currentPeak == 0)
			{
				return new AudioBuffer(WaveFileWriter.Fm9SampleRate, normalizedSamples);
			}

			float scale = peakLevel / currentPeak;

			for (int index = 0; index < normalizedSamples.Length; index++)
			{
				normalizedSamples[index] *= scale;
			}

			return new AudioBuffer(WaveFileWriter.Fm9SampleRate, normalizedSamples);
		}

		private static AudioBuffer ResampleToFm9Rate(AudioBuffer source)
		{
			if (source.SampleRate == WaveFileWriter.Fm9SampleRate)
			{
				return new AudioBuffer(source.SampleRate, source.Samples.ToArray());
			}

			DiscreteSignal signal = new(source.SampleRate, source.Samples.ToArray());
			DiscreteSignal resampled = Operation.Resample(signal, WaveFileWriter.Fm9SampleRate);

			return new AudioBuffer(resampled.SamplingRate, resampled.Samples);
		}
	}
}
