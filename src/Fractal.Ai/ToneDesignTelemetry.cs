using System.Diagnostics;
using System.Diagnostics.Metrics;
using Fractal.Application;

namespace Fractal.Ai
{
	internal static class ToneDesignTelemetry
	{
		public const string MeterName = "Fractal.Ai";
		public const string ActivitySourceName = "Fractal.Ai";

		private static readonly ActivitySource Activities = new(ActivitySourceName);
		private static readonly Meter Meter = new(MeterName);
		private static readonly Counter<long> ProviderRequests =
			Meter.CreateCounter<long>("fractal.ai.provider.requests");
		private static readonly Histogram<double> ProviderDuration =
			Meter.CreateHistogram<double>("fractal.ai.provider.duration", "s");
		private static readonly Counter<long> ProviderErrors =
			Meter.CreateCounter<long>("fractal.ai.provider.errors");
		private static readonly Counter<long> ProviderRateLimits =
			Meter.CreateCounter<long>("fractal.ai.provider.rate_limits");
		private static readonly Counter<long> EvidenceRetrievals =
			Meter.CreateCounter<long>("fractal.ai.evidence.retrievals");
		private static readonly Histogram<double> EvidenceDuration =
			Meter.CreateHistogram<double>("fractal.ai.evidence.duration", "s");

		public static Activity? StartProviderActivity(string provider)
		{
			Activity? activity = Activities.StartActivity("tone-design.provider");
			activity?.SetTag("provider.name", NormalizeProvider(provider));
			return activity;
		}

		public static void RecordProvider(
			string provider,
			string outcome,
			TimeSpan duration,
			ToneDesignProviderErrorKind? errorKind = null)
		{
			TagList tags = new()
			{
				{ "provider.name", NormalizeProvider(provider) },
				{ "outcome", outcome }
			};
			ProviderRequests.Add(1, tags);
			ProviderDuration.Record(duration.TotalSeconds, tags);
			if (errorKind is not null)
			{
				TagList errorTags = new()
				{
					{ "provider.name", NormalizeProvider(provider) },
					{ "error.type", ErrorClass(errorKind.Value) }
				};
				ProviderErrors.Add(1, errorTags);
				if (errorKind == ToneDesignProviderErrorKind.RateLimited)
					ProviderRateLimits.Add(1, errorTags);
			}
		}

		public static void RecordEvidence(
			ToneDesignEvidenceRetrievalResult result,
			TimeSpan duration)
		{
			TagList tags = new()
			{
				{ "outcome", result.Status.ToString().ToLowerInvariant() },
				{ "cache.outcome", result.IsFromCache ? "hit" : "miss" }
			};
			EvidenceRetrievals.Add(1, tags);
			EvidenceDuration.Record(duration.TotalSeconds, tags);
		}

		public static string ErrorClass(ToneDesignProviderErrorKind kind)
		{
			return kind switch
			{
				ToneDesignProviderErrorKind.RateLimited => "rate-limited",
				ToneDesignProviderErrorKind.InvalidResponse => "invalid-response",
				_ => kind.ToString().ToLowerInvariant()
			};
		}

		private static string NormalizeProvider(string provider)
		{
			return provider switch
			{
				"Azure OpenAI" => "azure-openai",
				"Ollama" => "ollama",
				_ => "unknown"
			};
		}
	}
}
