namespace Fractal.Core
{
	public enum ToneAnalysisKind
	{
		SpectralToneMatch,
		ImpulseResponseCapture
	}

	public sealed record ToneAnalysisRequest(
		ToneAnalysisKind Kind,
		Uri CurrentToneRecording,
		Uri ReferenceRecording,
		bool IsKnownExcitationSignal)
	{
		public void Validate()
		{
			ArgumentNullException.ThrowIfNull(CurrentToneRecording);
			ArgumentNullException.ThrowIfNull(ReferenceRecording);

			if (Kind == ToneAnalysisKind.ImpulseResponseCapture && !IsKnownExcitationSignal)
			{
				throw new InvalidOperationException(
					"Physical impulse-response capture requires a known excitation signal and a recorded response.");
			}
		}
	}

	public sealed record ToneAnalysisResult(
		ToneAnalysisKind Kind,
		string Summary,
		IReadOnlyList<ToneRecommendation> Recommendations);

	public sealed record ToneRecommendation(
		string Target,
		string SuggestedAdjustment,
		string Rationale);
}
