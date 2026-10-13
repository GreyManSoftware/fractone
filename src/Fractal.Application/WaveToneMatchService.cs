using Fractal.Core;

namespace Fractal.Application
{
	public sealed record WaveToneMatchingResult(
		WetReferenceAnalysisResult Analysis,
		WetToneMatchResult Matching,
		byte[]? PreviewWav);

	// The stretch of a recording to analyse: StartSeconds in, for up to LengthSeconds (cut short at the end).
	public sealed record ToneMatchWindow(double StartSeconds, double LengthSeconds)
	{
		public static ToneMatchWindow FromStart { get; } = new(0, WetToneMatcher.MaximumDurationSeconds);
	}

	public sealed class WaveToneMatchService
	{
		private readonly WetReferenceAnalysisService _wetReferenceAnalysisService;
		private readonly WetToneMatcher _matcher;

		public WaveToneMatchService(WetReferenceAnalysisService wetReferenceAnalysisService, WetToneMatcher matcher)
		{
			_wetReferenceAnalysisService = wetReferenceAnalysisService ??
				throw new ArgumentNullException(nameof(wetReferenceAnalysisService));
			_matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
		}

		public WaveToneMatchingResult MatchToneRefinement(Stream userStream, Stream targetStream) =>
			MatchToneRefinement(userStream, ToneMatchWindow.FromStart, targetStream, ToneMatchWindow.FromStart);

		public WaveToneMatchingResult MatchToneRefinement(
			Stream userStream, ToneMatchWindow userWindow, Stream targetStream, ToneMatchWindow targetWindow)
		{
			ArgumentNullException.ThrowIfNull(userWindow);
			ArgumentNullException.ThrowIfNull(targetWindow);
			WetToneMatchResult matching = _matcher.Match(
				WaveFileReader.ReadChannelWindow(userStream, userWindow.StartSeconds, userWindow.LengthSeconds),
				WaveFileReader.ReadChannelWindow(targetStream, targetWindow.StartSeconds, targetWindow.LengthSeconds));
			return Complete(matching);
		}

		// Fits the FM9 Parametric EQ, starting from the EQ the wet take was recorded through (see Fm9ParametricEq.Recorded).
		public WaveToneMatchingResult MatchParametricToneRefinement(
			Stream userStream, ToneMatchWindow userWindow, Stream targetStream, ToneMatchWindow targetWindow,
			IReadOnlyList<ParametricEqBand> recordedEq)
		{
			ArgumentNullException.ThrowIfNull(userWindow);
			ArgumentNullException.ThrowIfNull(targetWindow);
			ArgumentNullException.ThrowIfNull(recordedEq);
			WetToneMatchResult matching = _matcher.MatchParametric(
				WaveFileReader.ReadChannelWindow(userStream, userWindow.StartSeconds, userWindow.LengthSeconds),
				WaveFileReader.ReadChannelWindow(targetStream, targetWindow.StartSeconds, targetWindow.LengthSeconds),
				new ParametricEqMatchSettings(recordedEq, Fm9ParametricEq.Limits, Fm9ParametricEq.MaximumFitGainDecibels));
			return Complete(matching);
		}

		private static WaveToneMatchingResult Complete(WetToneMatchResult matching)
		{
			List<WetReferenceCorrectionRecommendation> recommendations = [];
			for (int index = 0; index < matching.ParametricEq.Count; index++)
			{
				ParametricEqBand band = matching.ParametricEq[index];
				recommendations.Add(new(WetReferenceCorrectionArea.Equalization,
					$"Parametric EQ band {index + 1} ({Describe(band.Shape)}): {band.FrequencyHz:F0} Hz, Q {band.Q:F2}, {band.GainDecibels:+0.0;-0.0;0} dB.",
					"The fitted settings were re-measured against the uploaded reference after rendering.",
					"Statistical musical-clip comparison cannot distinguish gear from playing or production differences."));
			}
			foreach (ToneMatchEqBand band in matching.EqBands.Where(band => Math.Abs(band.GainDecibels) >= 0.25))
			{
				recommendations.Add(new(WetReferenceCorrectionArea.Equalization,
					$"Post-Cab peaking EQ: {band.FrequencyHz:F0} Hz, Q {band.Q:F2}, {band.GainDecibels:+0.00;-0.00;0} dB.",
					"The fitted settings were re-measured against the uploaded reference after rendering.",
					"Statistical musical-clip comparison cannot distinguish gear from playing or production differences."));
			}
			if (matching.HasPreview)
			{
				recommendations.Add(new(WetReferenceCorrectionArea.GainStaging,
					$"Preview output gain: {matching.AppliedLevelGainDecibels:+0.00;-0.00;0} dB.",
					$"Active RMS: wet {matching.UserActiveRmsDecibels:F2} dBFS; reference {matching.TargetActiveRmsDecibels:F2} dBFS; preview {matching.PreviewActiveRmsDecibels:F2} dBFS.",
					"Output gain is bounded and reduced where necessary for sample-peak headroom; this is not an amp-gain recommendation."));
			}
			WetReferenceAnalysisResult analysis = new(
				matching.HasPreview ? WetReferenceAnalysisStatus.RecommendationsAvailable :
					WetReferenceAnalysisStatus.InsufficientEvidence,
				matching.Summary, recommendations, matching.Warnings);
			byte[]? preview = null;
			if (matching.HasPreview)
			{
				using MemoryStream output = new();
				WaveFileWriter.WritePcm24(matching.Preview!, output);
				preview = output.ToArray();
			}
			return new WaveToneMatchingResult(analysis, matching, preview);
		}

		public static string Describe(ParametricEqShape shape) => shape switch
		{
			ParametricEqShape.LowShelf => "low shelf",
			ParametricEqShape.HighShelf => "high shelf",
			_ => "peaking"
		};

		public WetReferenceAnalysisResult AnalyzeToneRefinement(
			Stream presetWetRecordingStream,
			Stream targetIsolatedGuitarRecordingStream)
		{
			ArgumentNullException.ThrowIfNull(presetWetRecordingStream);
			ArgumentNullException.ThrowIfNull(targetIsolatedGuitarRecordingStream);

			AudioBuffer presetWetRecording = WaveFileReader.ReadMono(presetWetRecordingStream);
			AudioBuffer targetIsolatedGuitarRecording = WaveFileReader.ReadMono(
				targetIsolatedGuitarRecordingStream);

			return _wetReferenceAnalysisService.Analyze(new WetReferenceAnalysisRequest(
				presetWetRecording,
				targetIsolatedGuitarRecording));
		}
	}
}
