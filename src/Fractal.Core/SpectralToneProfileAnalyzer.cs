using System.Numerics;

namespace Fractal.Core
{
	public sealed record AudioBuffer(
		int SampleRate,
		IReadOnlyList<float> Samples)
	{
		public void Validate()
		{
			if (SampleRate <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(SampleRate), "The sample rate must be positive.");
			}

			ArgumentNullException.ThrowIfNull(Samples);

			if (Samples.Count == 0)
			{
				throw new ArgumentException(
					"At least one audio sample is required.",
					nameof(Samples));
			}

			foreach (float sample in Samples)
			{
				if (!float.IsFinite(sample))
				{
					throw new ArgumentException(
						"Audio samples must be finite values.",
						nameof(Samples));
				}
			}
		}
	}

	public sealed record ToneFrequencyBand(
		string Name,
		double LowerFrequencyHz,
		double UpperFrequencyHz);

	public sealed record ToneBandLevel(
		ToneFrequencyBand Band,
		double RelativeLevelDecibels);

	public sealed record ToneProfile(
		double RmsLevelDecibels,
		IReadOnlyList<ToneBandLevel> Bands);

	public sealed record ToneBandDelta(
		ToneFrequencyBand Band,
		double ReferenceMinusCurrentDecibels);

	public sealed class SpectralToneProfileAnalyzer
	{
		private const int MaximumWindowSize = 2_048;

		private static readonly ToneFrequencyBand[] FrequencyBands =
		[
			new("Sub bass", 50, 120),
			new("Bass", 120, 250),
			new("Low mids", 250, 500),
			new("Mids", 500, 1_000),
			new("Upper mids", 1_000, 2_000),
			new("Presence", 2_000, 4_000),
			new("Treble", 4_000, 8_000)
		];

		public ToneProfile Analyze(AudioBuffer audio)
		{
			ArgumentNullException.ThrowIfNull(audio);
			audio.Validate();

			if (audio.Samples.Count < 64)
			{
				throw new ArgumentException(
					"At least 64 audio samples are required for spectral analysis.",
					nameof(audio));
			}

			int windowSize = GetWindowSize(audio.Samples.Count);
			int hopSize = windowSize / 2;
			double[] accumulatedBandPowers = new double[FrequencyBands.Length];
			int frameCount = 0;

			for (int start = 0; start <= audio.Samples.Count - windowSize; start += hopSize)
			{
				Complex[] spectrum = CreateWindowedSpectrum(audio.Samples, start, windowSize);
				FastFourierTransform.TransformInPlace(spectrum);
				AccumulateBandPowers(
					spectrum,
					audio.SampleRate,
					accumulatedBandPowers);
				frameCount++;
			}

			double[] averageBandDecibels = new double[FrequencyBands.Length];

			for (int index = 0; index < accumulatedBandPowers.Length; index++)
			{
				averageBandDecibels[index] = ToDecibels(
					accumulatedBandPowers[index] / frameCount);
			}

			double meanBandDecibels = averageBandDecibels.Average();
			List<ToneBandLevel> levels = [];

			for (int index = 0; index < FrequencyBands.Length; index++)
			{
				levels.Add(new ToneBandLevel(
					FrequencyBands[index],
					averageBandDecibels[index] - meanBandDecibels));
			}

			return new ToneProfile(
				CalculateRmsDecibels(audio.Samples),
				levels);
		}

		public IReadOnlyList<ToneBandDelta> Compare(
			ToneProfile current,
			ToneProfile reference)
		{
			ArgumentNullException.ThrowIfNull(current);
			ArgumentNullException.ThrowIfNull(reference);

			if (current.Bands.Count != reference.Bands.Count)
			{
				throw new ArgumentException("Tone profiles must use the same frequency bands.", nameof(reference));
			}

			List<ToneBandDelta> deltas = [];

			for (int index = 0; index < current.Bands.Count; index++)
			{
				ToneBandLevel currentBand = current.Bands[index];
				ToneBandLevel referenceBand = reference.Bands[index];

				if (currentBand.Band != referenceBand.Band)
				{
					throw new ArgumentException("Tone profiles must use the same frequency bands.", nameof(reference));
				}

				deltas.Add(new ToneBandDelta(
					currentBand.Band,
					referenceBand.RelativeLevelDecibels - currentBand.RelativeLevelDecibels));
			}

			return deltas;
		}

		private static Complex[] CreateWindowedSpectrum(
			IReadOnlyList<float> samples,
			int start,
			int windowSize)
		{
			Complex[] values = new Complex[windowSize];

			for (int index = 0; index < windowSize; index++)
			{
				double window = 0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / (windowSize - 1)));
				values[index] = new Complex(samples[start + index] * window, 0);
			}

			return values;
		}

		private static void AccumulateBandPowers(
			IReadOnlyList<Complex> spectrum,
			int sampleRate,
			double[] accumulatedBandPowers)
		{
			double[] frameBandPowers = new double[FrequencyBands.Length];
			int[] binCounts = new int[FrequencyBands.Length];

			for (int bin = 1; bin < spectrum.Count / 2; bin++)
			{
				double frequency = (double)bin * sampleRate / spectrum.Count;
				double power = spectrum[bin].Magnitude * spectrum[bin].Magnitude;

				for (int bandIndex = 0; bandIndex < FrequencyBands.Length; bandIndex++)
				{
					ToneFrequencyBand band = FrequencyBands[bandIndex];

					if (frequency >= band.LowerFrequencyHz && frequency < band.UpperFrequencyHz)
					{
						frameBandPowers[bandIndex] += power;
						binCounts[bandIndex]++;
						break;
					}
				}
			}

			for (int bandIndex = 0; bandIndex < accumulatedBandPowers.Length; bandIndex++)
			{
				if (binCounts[bandIndex] > 0)
				{
					accumulatedBandPowers[bandIndex] += frameBandPowers[bandIndex] / binCounts[bandIndex];
				}
			}
		}

		private static int GetWindowSize(int sampleCount)
		{
			int windowSize = 1;
			int maximum = Math.Min(sampleCount, MaximumWindowSize);

			while (windowSize * 2 <= maximum)
			{
				windowSize *= 2;
			}

			return windowSize;
		}

		private static double CalculateRmsDecibels(IReadOnlyList<float> samples)
		{
			double sumOfSquares = 0;

			foreach (float sample in samples)
			{
				sumOfSquares += sample * sample;
			}

			double rms = Math.Sqrt(sumOfSquares / samples.Count);
			return ToDecibels(rms * rms) / 2;
		}

		private static double ToDecibels(double power)
		{
			return 10 * Math.Log10(Math.Max(power, double.Epsilon));
		}
	}
}
