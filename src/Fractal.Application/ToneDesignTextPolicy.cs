using System.Text;
using System.Text.RegularExpressions;

namespace Fractal.Application
{
	public static class ToneDesignTextPolicy
	{
		private static readonly string[] OffTopicTasks =
		[
			"write code", "write a program", "write a function", "generate code", "debug code",
			"python script", "python code", "python program", "javascript code", "c# code", "sql query",
			"solve this equation", "solve this math", "calculate this math",
			"write an essay", "write a story", "write a poem", "write an email", "write my resume",
			"translate this", "tell me a joke", "weather forecast", "medical advice", "legal advice",
			"concert tickets", "write to hardware", "connect to hardware", "send midi",
			"delete files", "delete account", "execute code", "execute command",
			"execute a network request", "run command", "upload files", "download files"
		];
		private static readonly Regex OffTopicRequest = new(
			@"\b(?:(?:want|need|would like|i d like)\s+(?:(?:a|an|the|my)\s+)?(?:essay|story|poem|email|resume|joke|program|code)\b|(?:want|would like|i d like)\s+to\s+(?:know|learn|understand)\b)",
			RegexOptions.CultureInvariant);
		private static readonly Regex ArithmeticExpression = new(
			@"^\s*\d+(?:\.\d+)?\s*[+*/^=]\s*\d+(?:\.\d+)?(?:\s*[+*/^=]\s*\d+(?:\.\d+)?)*\s*[?.]?\s*$",
			RegexOptions.CultureInvariant);
		private static readonly Regex QuotedTargetName = new(
			"\\b(?:song|track|album|record|artist|band|title)\\s+(?:(?:named|called|titled)\\s+)?(?<name>\"[^\"\\r\\n]{1,160}\"|'[^'\\r\\n]{1,160}'|“[^”\\r\\n]{1,160}”|‘[^’\\r\\n]{1,160}’)",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		private static readonly IReadOnlySet<string> OverrideVerbs =
			new HashSet<string>(
				["ignore", "disregard", "forget", "override", "bypass", "change",
					"replace", "rewrite", "modify", "edit", "remove", "delete"],
				StringComparer.Ordinal);
		private static readonly IReadOnlySet<string> InstructionTargets =
			new HashSet<string>(
				["instruction", "instructions", "prompt", "message", "messages", "rules",
					"guardrail", "guardrails", "domain", "scope", "guidance",
					"constraint", "constraints", "restriction", "restrictions", "policy", "policies"],
				StringComparer.Ordinal);
		private static readonly IReadOnlySet<string> RevealVerbs =
			new HashSet<string>(["reveal", "show", "print", "expose", "repeat"], StringComparer.Ordinal);
		private static readonly IReadOnlySet<string> AssistantTargets =
			new HashSet<string>(["model", "assistant", "chatbot", "system"], StringComparer.Ordinal);
		private static readonly IReadOnlySet<string> SensitiveDataTargets =
			new HashSet<string>(["credential", "credentials", "password", "passwords", "secret", "secrets"], StringComparer.Ordinal);
		private static readonly IReadOnlySet<string> SensitiveDataVerbs =
			new HashSet<string>(["reveal", "show", "print", "expose", "return", "dump"], StringComparer.Ordinal);
		private static readonly IReadOnlySet<string> JailbreakVerbs =
			new HashSet<string>(["attempt", "perform"], StringComparer.Ordinal);

		public static string[] Tokenize(string text)
		{
			string normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
			string tokenized = new(normalized
				.Select(character => char.IsLetterOrDigit(character) ? character : ' ')
				.ToArray());
			return tokenized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		}

		public static string NormalizeForMatching(string text) =>
			string.Join(" ", Tokenize(text));

		public static bool ContainsAnyPhrase(string text, IEnumerable<string> phrases)
		{
			string normalized = $" {NormalizeForMatching(text)} ";
			return phrases.Any(phrase =>
				normalized.Contains($" {NormalizeForMatching(phrase)} ", StringComparison.Ordinal));
		}

		public static bool ContainsOffTopicTask(string text)
		{
			string checkedText = MaskQuotedTargetNames(text);
			return ContainsAnyPhrase(checkedText, OffTopicTasks) ||
				OffTopicRequest.IsMatch(NormalizeForMatching(checkedText)) ||
				ArithmeticExpression.IsMatch(checkedText);
		}

		// Only explicitly labelled, quoted target names are data; arbitrary quoted instructions remain checked.
		public static string MaskQuotedTargetNames(string text, bool includeLabel = false)
		{
			return QuotedTargetName.Replace(text.Normalize(NormalizationForm.FormKC), match =>
			{
				if (includeLabel)
				{
					return new string(' ', match.Length);
				}

				Group name = match.Groups["name"];
				int offset = name.Index - match.Index;
				return match.Value[..offset] + new string(' ', name.Length);
			});
		}

		public static bool ContainsInstructionOverride(string text)
		{
			string[] terms = Tokenize(MaskQuotedTargetNames(text));
			for (int index = 0; index < terms.Length; index++)
			{
				if ((OverrideVerbs.Contains(terms[index]) &&
					ContainsWithin(terms, index + 1, 6, InstructionTargets)) ||
					(RevealVerbs.Contains(terms[index]) &&
					ContainsWithin(terms, index + 1, 5, InstructionTargets)) ||
					(SensitiveDataVerbs.Contains(terms[index]) &&
						(ContainsWithin(terms, index + 1, 5, SensitiveDataTargets) ||
						ContainsAnyPhrase(string.Join(" ", terms.Skip(index + 1).Take(5)), ["api key", "api keys"]))) ||
					(terms[index] == "jailbreak" &&
						((index == 0 && terms.Length > 1) ||
						(index > 0 && terms[index - 1] == "and") ||
						ContainsWithin(terms, Math.Max(0, index - 3), 7, AssistantTargets) ||
						ContainsWithin(terms, Math.Max(0, index - 3), Math.Min(3, index), JailbreakVerbs))))
				{
					return true;
				}
			}

			return false;
		}

		private static bool ContainsWithin(
			IReadOnlyList<string> terms,
			int start,
			int count,
			IReadOnlySet<string> targets)
		{
			int end = Math.Min(terms.Count, start + count);
			for (int index = start; index < end; index++)
			{
				if (targets.Contains(terms[index]))
				{
					return true;
				}
			}

			return false;
		}
	}
}
