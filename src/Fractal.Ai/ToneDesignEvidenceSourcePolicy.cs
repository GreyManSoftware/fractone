using System.Globalization;
using System.Net;

namespace Fractal.Ai
{
	public sealed class ToneDesignEvidenceSourcePolicy : IToneDesignEvidenceSourcePolicy
	{
		private readonly IReadOnlyList<NormalizedRule> _rules;

		public ToneDesignEvidenceSourcePolicy(IEnumerable<ToneDesignEvidenceSourceRule> rules)
		{
			ArgumentNullException.ThrowIfNull(rules);
			_rules = rules
				.Select(NormalizeRule)
				.OrderByDescending(rule => rule.Host.Length)
				.ToArray();
		}

		public ToneDesignEvidenceSourceAssessment Assess(Uri sourceUri)
		{
			ArgumentNullException.ThrowIfNull(sourceUri);

			if (!sourceUri.IsAbsoluteUri ||
				!string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
				!sourceUri.IsDefaultPort)
			{
				return Deny("Evidence sources must use an absolute HTTPS URI on the default port.");
			}

			if (!string.IsNullOrEmpty(sourceUri.UserInfo))
			{
				return Deny("Evidence source URIs must not contain user information.");
			}

			string host = NormalizeHost(sourceUri.DnsSafeHost);
			if (host.Length == 0 ||
				string.Equals(host, "localhost", StringComparison.Ordinal) ||
				IPAddress.TryParse(host, out IPAddress? _))
			{
				return Deny("Evidence sources must use an allowlisted DNS host.");
			}

			NormalizedRule? rule = _rules.FirstOrDefault(candidate =>
				string.Equals(host, candidate.Host, StringComparison.Ordinal) ||
				(candidate.IncludeSubdomains &&
					host.EndsWith($".{candidate.Host}", StringComparison.Ordinal)));

			if (rule is null)
			{
				return Deny("The evidence source host is not allowlisted.");
			}

			return new ToneDesignEvidenceSourceAssessment(
				true,
				rule.Credibility,
				rule.Host,
				"Allowed by the configured evidence source policy.");
		}

		private static ToneDesignEvidenceSourceAssessment Deny(string reason)
		{
			return new ToneDesignEvidenceSourceAssessment(
				false,
				ToneDesignEvidenceSourceCredibility.Unknown,
				string.Empty,
				reason);
		}

		private static NormalizedRule NormalizeRule(ToneDesignEvidenceSourceRule rule)
		{
			ArgumentNullException.ThrowIfNull(rule);

			if (!Enum.IsDefined(rule.Credibility) ||
				rule.Credibility == ToneDesignEvidenceSourceCredibility.Unknown)
			{
				throw new ArgumentException(
					"Allowlisted evidence sources require a known credibility tier.",
					nameof(rule));
			}

			string host = NormalizeHost(rule.Host);
			if (host.Length == 0 ||
				string.Equals(host, "localhost", StringComparison.Ordinal) ||
				IPAddress.TryParse(host, out IPAddress? _))
			{
				throw new ArgumentException(
					"Allowlisted evidence sources require a DNS host.",
					nameof(rule));
			}

			return new NormalizedRule(host, rule.Credibility, rule.IncludeSubdomains);
		}

		private static string NormalizeHost(string host)
		{
			string trimmed = host.Trim().TrimEnd('.');
			if (trimmed.Length == 0)
			{
				return string.Empty;
			}

			try
			{
				return new IdnMapping()
					.GetAscii(trimmed)
					.ToLowerInvariant();
			}
			catch (ArgumentException)
			{
				return string.Empty;
			}
		}

		private sealed record NormalizedRule(
			string Host,
			ToneDesignEvidenceSourceCredibility Credibility,
			bool IncludeSubdomains);
	}
}
