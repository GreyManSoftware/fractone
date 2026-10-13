namespace Fractal.Core
{
	public sealed record AudioBandLevel(string Name, double LowHz, double HighHz, double RelativeDecibels);

	public sealed record AudioMeasurement(
		int SampleRate,
		int Channels,
		double Seconds,
		double ActiveSeconds,
		double IntegratedLufs,
		double ActiveRmsDecibels,
		double PeakDecibels,
		int ClippedSamples,
		IReadOnlyList<AudioBandLevel> Bands)
	{
		public bool Clipped => ClippedSamples > 0;
	}

	public sealed record AudioComparison(
		double LoudnessDifferenceDecibels,
		IReadOnlyList<AudioBandLevel> BandDifferences,
		double? SpectralDeviationDecibels);

	// Measurements of a guitar take while it is playing: integrated loudness (ITU-R BS.1770 K-weighting with its absolute
	// and relative gates), RMS over active 50 ms frames, sample peak and clipping, and the balance of tonal bands relative
	// to the whole while playing. Silence before, between and after phrases does not count.
	public static class AudioMeasurer
	{
		public static IReadOnlyList<(string Name, double Low, double High)> BandsOfInterest { get; } =
		[
			("sub", 20, 60), ("lows", 60, 250), ("mud", 250, 1000), ("1-2k", 1000, 2000),
			("bite", 2000, 4000), ("fizz", 4000, 8000), ("air", 8000, 12000)
		];

		// A sample at or above this (about -0.01 dBFS) counts as clipped.
		public const float ClipLevel = 0.999f;
		private const double FrameSeconds = 0.05;
		// Frames within this of the loud end of the take count as playing.
		private const double ActiveRangeDecibels = 30;
		private const double SilenceDecibels = -60;

		public static AudioMeasurement Measure(MultichannelAudioBuffer audio)
		{
			ArgumentNullException.ThrowIfNull(audio);
			if (audio.Channels.Count == 0 || audio.Channels[0].Count == 0) throw new ArgumentException("The recording is empty.", nameof(audio));
			int rate = audio.SampleRate, length = audio.Channels[0].Count;
			float peak = 0;
			int clipped = 0;
			foreach (IReadOnlyList<float> channel in audio.Channels)
				foreach (float sample in channel)
				{
					float magnitude = Math.Abs(sample);
					if (magnitude > peak) peak = magnitude;
					if (magnitude >= ClipLevel) clipped++;
				}

			float[] mono = new float[length];
			foreach (IReadOnlyList<float> channel in audio.Channels)
				for (int index = 0; index < length; index++) mono[index] += channel[index] / audio.Channels.Count;

			int hop = Math.Max(1, (int)(FrameSeconds * rate));
			int frames = length / hop;
			if (frames == 0) throw new ArgumentException("The recording is shorter than 50 ms.", nameof(audio));
			double[] energy = FrameEnergy(mono, hop, frames);
			bool[] active = Active(energy);
			int activeFrames = active.Count(flag => flag);
			if (activeFrames == 0) throw new InvalidOperationException("The recording is silent.");
			double activeEnergy = Enumerable.Range(0, frames).Where(frame => active[frame]).Sum(frame => energy[frame]) / activeFrames;

			IReadOnlyList<AudioBandLevel> bands = BandBalance(mono, rate);

			return new(rate, audio.Channels.Count, (double)length / rate, activeFrames * FrameSeconds, IntegratedLoudness(audio),
				Decibels(activeEnergy), peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity, clipped, bands);
		}

		// Take minus reference: loudness, each band's share, and the spectral deviation the tone matcher sees.
		public static AudioComparison Compare(MultichannelAudioBuffer take, MultichannelAudioBuffer reference)
		{
			AudioMeasurement a = Measure(take), b = Measure(reference);
			AudioBandLevel[] differences = a.Bands.Join(b.Bands, band => band.Name, band => band.Name,
				(x, y) => x with { RelativeDecibels = Math.Round(x.RelativeDecibels - y.RelativeDecibels, 2) }).ToArray();
			double? deviation = null;
			try { deviation = new WetToneMatcher().Match(take, reference).BeforeSpectralErrorDecibels; }
			catch (ArgumentException) { }
			return new(Math.Round(a.IntegratedLufs - b.IntegratedLufs, 2), differences, deviation);
		}

		// ITU-R BS.1770-4: K-weighted mean square in 400 ms blocks every 100 ms, absolute gate -70 LUFS, relative gate -10 LU.
		public static double IntegratedLoudness(MultichannelAudioBuffer audio)
		{
			int rate = audio.SampleRate, length = audio.Channels[0].Count;
			double[][] weighted = audio.Channels.Select(channel =>
			{
				float[] samples = channel as float[] ?? channel.ToArray();
				return Array.ConvertAll(Filter(samples, Biquad.KWeightingShelf(rate), Biquad.KWeightingHighPass(rate)), value => (double)value);
			}).ToArray();
			int block = (int)(0.4 * rate), step = (int)(0.1 * rate);
			List<double> powers = [];
			for (int start = 0; start + block <= length; start += step)
			{
				double power = 0;
				foreach (double[] channel in weighted)
				{
					double sum = 0;
					for (int index = start; index < start + block; index++) sum += channel[index] * channel[index];
					power += sum / block;
				}
				powers.Add(power);
			}
			if (powers.Count == 0) return double.NegativeInfinity;
			static double Lufs(double power) => -0.691 + (10 * Math.Log10(power));
			double[] absolute = powers.Where(power => power > 0 && Lufs(power) > -70).ToArray();
			if (absolute.Length == 0) return double.NegativeInfinity;
			double relative = Lufs(absolute.Average()) - 10;
			double[] gated = absolute.Where(power => Lufs(power) > relative).ToArray();
			return Math.Round(Lufs(gated.Average()), 2);
		}

		// Each band's share of the whole while playing, from the spectrum of every playing frame (exact band edges).
		private static IReadOnlyList<AudioBandLevel> BandBalance(float[] mono, int rate)
		{
			int size = rate >= 32_000 ? 2048 : 1024;
			int frames = mono.Length / size;
			if (frames == 0) return [];
			bool[] active = Active(FrameEnergy(mono, size, frames));
			double[] window = Enumerable.Range(0, size).Select(index => 0.5 - (0.5 * Math.Cos(2 * Math.PI * index / size))).ToArray();
			double total = 0;
			double[] sums = new double[BandsOfInterest.Count];
			System.Numerics.Complex[] bins = new System.Numerics.Complex[size];
			for (int frame = 0; frame < frames; frame++)
			{
				if (!active[frame]) continue;
				for (int index = 0; index < size; index++) bins[index] = new(mono[(frame * size) + index] * window[index], 0);
				FastFourierTransform.TransformInPlace(bins);
				for (int bin = 1; bin < size / 2; bin++)
				{
					double frequency = (double)bin * rate / size, power = bins[bin].Magnitude * bins[bin].Magnitude;
					total += power;
					for (int band = 0; band < BandsOfInterest.Count; band++)
						if (frequency >= BandsOfInterest[band].Low && frequency < BandsOfInterest[band].High) sums[band] += power;
				}
			}
			return Enumerable.Range(0, BandsOfInterest.Count).Where(band => BandsOfInterest[band].High < rate / 2.0)
				.Select(band => new AudioBandLevel(BandsOfInterest[band].Name, BandsOfInterest[band].Low, BandsOfInterest[band].High, Decibels(sums[band] / total)))
				.ToArray();
		}

		private static double[] FrameEnergy(float[] samples, int hop, int frames)
		{
			double[] energy = new double[frames];
			for (int frame = 0; frame < frames; frame++)
			{
				double sum = 0;
				for (int index = frame * hop; index < (frame + 1) * hop; index++) sum += (double)samples[index] * samples[index];
				energy[frame] = sum / hop;
			}
			return energy;
		}

		// Playing frames: within ActiveRangeDecibels of the 95th-percentile frame, and above silence.
		private static bool[] Active(double[] energy)
		{
			double[] sorted = energy.Order().ToArray();
			double loud = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))];
			double floor = Math.Max(loud * Math.Pow(10, -ActiveRangeDecibels / 10), Math.Pow(10, SilenceDecibels / 10));
			return energy.Select(value => value > floor && value > 0).ToArray();
		}

		private static float[] Filter(float[] samples, params Biquad[] stages)
		{
			float[] result = (float[])samples.Clone();
			foreach (Biquad stage in stages) stage.Process(result);
			return result;
		}

		private static double Decibels(double energy) => energy > 0 ? Math.Round(10 * Math.Log10(energy), 2) : double.NegativeInfinity;

		private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
		{
			public void Process(float[] samples)
			{
				double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
				for (int index = 0; index < samples.Length; index++)
				{
					double x = samples[index];
					double y = (b0 * x) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
					x2 = x1; x1 = x; y2 = y1; y1 = y;
					samples[index] = (float)y;
				}
			}

			// BS.1770 K-weighting at any sample rate (the 48 kHz coefficients re-derived from their analogue prototypes).
			public static Biquad KWeightingShelf(int rate)
			{
				const double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
				double k = Math.Tan(Math.PI * f0 / rate), vh = Math.Pow(10, gain / 20), vb = Math.Pow(vh, 0.4996667741545416);
				double a0 = 1 + (k / q) + (k * k);
				return new((vh + (vb * k / q) + (k * k)) / a0, 2 * ((k * k) - vh) / a0, (vh - (vb * k / q) + (k * k)) / a0,
					2 * ((k * k) - 1) / a0, (1 - (k / q) + (k * k)) / a0);
			}

			public static Biquad KWeightingHighPass(int rate)
			{
				const double f0 = 38.13547087602444, q = 0.5003270373238773;
				double k = Math.Tan(Math.PI * f0 / rate), a0 = 1 + (k / q) + (k * k);
				return new(1, -2, 1, 2 * ((k * k) - 1) / a0, (1 - (k / q) + (k * k)) / a0);
			}
		}
	}
}
