using Fractal.Application;

namespace Fractal.Ai
{
	public sealed record ToneDesignResearchQuery(
		string OriginalText,
		string CanonicalText);

	public sealed record ToneDesignEvidenceSourceCandidate(
		string Title,
		Uri SourceUri,
		DateOnly? PublishedOn,
		// Provider text is data only and must never be interpreted as prompt instructions.
		string UntrustedText);

	public sealed record ToneDesignEvidenceRecord
	{
		private ToneDesignEvidenceRecord(
			ToneDesignEvidenceSourceCandidate source,
			ToneDesignEvidenceSourceAssessment assessment,
			DateTimeOffset retrievedAt)
		{
			Source = source;
			Assessment = assessment;
			RetrievedAt = retrievedAt;
		}

		public ToneDesignEvidenceSourceCandidate Source { get; }

		public ToneDesignEvidenceSourceAssessment Assessment { get; }

		public DateTimeOffset RetrievedAt { get; }

		public static ToneDesignEvidenceRecord Create(
			ToneDesignEvidenceSourceCandidate source,
			IToneDesignEvidenceSourcePolicy sourcePolicy,
			DateTimeOffset retrievedAt)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(sourcePolicy);
			ToneDesignEvidenceSourceAssessment assessment =
				sourcePolicy.Assess(source.SourceUri);

			if (!assessment.IsAllowed)
			{
				throw new InvalidOperationException(
					$"Evidence source '{source.SourceUri}' is not allowed: {assessment.Reason}");
			}

			return new ToneDesignEvidenceRecord(source, assessment, retrievedAt);
		}
	}

	public interface IToneDesignEvidenceProvider
	{
		Task<ToneDesignEvidenceRetrievalResult> RetrieveAsync(
			ToneDesignResearchQuery query,
			CancellationToken cancellationToken);
	}

	public enum ToneDesignEvidenceRetrievalStatus
	{
		Available,
		NoResults,
		Unavailable,
		LimitReached
	}

	public sealed record ToneDesignEvidenceRetrievalResult(
		ToneDesignEvidenceRetrievalStatus Status,
		string Detail,
		IReadOnlyList<ToneDesignEvidenceSourceCandidate> Sources,
		bool IsFromCache = false);

	public interface IToneDesignEvidenceSourcePolicy
	{
		ToneDesignEvidenceSourceAssessment Assess(Uri sourceUri);
	}

	public enum ToneDesignEvidenceSourceCredibility
	{
		Unknown = 0,
		Community = 1,
		SpecialistPublication = 2,
		Official = 3
	}

	public sealed record ToneDesignEvidenceSourceRule(
		string Host,
		ToneDesignEvidenceSourceCredibility Credibility,
		bool IncludeSubdomains = false);

	public sealed record ToneDesignEvidenceSourceAssessment(
		bool IsAllowed,
		ToneDesignEvidenceSourceCredibility Credibility,
		string MatchedHost,
		string Reason);

	public static class ToneDesignEvidenceRecordExtensions
	{
		public static ToneDesignEvidenceConclusion ToConclusion(
			this ToneDesignEvidenceRecord record,
			string claim,
			string classification,
			string confidence,
			string rationale)
		{
			ArgumentNullException.ThrowIfNull(record);

			return new ToneDesignEvidenceConclusion(
				claim,
				classification,
				confidence,
				record.Source.SourceUri.AbsoluteUri,
				record.Source.PublishedOn?.ToString("yyyy-MM-dd") ?? string.Empty,
				rationale);
		}
	}
}
