using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracTone.Probe
{
	public sealed record AnalysedProbeReport(string Id, DateTimeOffset UploadedUtc, ProbeReportAnalysis Analysis);

	public sealed record ProbeAggregateReport(string Id, DateTimeOffset UploadedUtc, string Outcome, int LevelRun, bool? LeftAsFound, string ToolVersion,
		bool? DeviceVariantVerified, int? PresetSlots);

	public sealed record ProbeAggregateStep(string Step, string Kind, int Passed, int Failed, int Skipped, int Stopped);

	public sealed record ProbeVerdictCounts(int Match, int Differs, int Unverified);

	public sealed record ProbeAggregateTemplateLoads(int Compared, int Matched, int Differed, int Identical);

	// One Amp parameter the device changed on load, across reports: what FracCompat lists as RecomputedAmpParameters.
	public sealed record ProbeAggregateRecomputed(int EffectId, int ParameterId, bool InScript, int Reports, IReadOnlyList<string> Channels,
		IReadOnlyList<ProbeRecomputedAmpWord> Examples);

	public sealed record ProbeWordsVariant(IReadOnlyDictionary<int, int> Words, int Reports);

	public sealed record ProbeAggregateVoicing(string Block, int EffectId, int ParameterId, int Ordinal, string? Name, bool CarriedOver,
		ProbeVerdictCounts Verdicts, bool Consistent, IReadOnlyList<ProbeWordsVariant> Variants);

	public sealed record ProbeInsertVariant(string? Shape, string? Header, string? Words, int Reports);

	public sealed record ProbeAggregateInsert(string Block, int EffectId, string Cell, bool CarriedOver, ProbeVerdictCounts Verdicts, bool Consistent,
		IReadOnlyList<ProbeInsertVariant> Variants);

	public sealed record ProbeMaskVariant(string? Frame, string? Mask, int Reports);

	public sealed record ProbeAggregateCable(string Route, string? ExpectedFrame, string? ExpectedMask, bool CarriedOver, ProbeVerdictCounts Verdicts,
		bool Consistent, IReadOnlyList<ProbeMaskVariant> Variants);

	public sealed record ProbeAggregateGroup(
		string Device,
		// Axe-Fx III only: a Mk I and a Mk II share a model byte and may not share evidence, so each is its own group.
		ProbeDeviceVariant? DeviceVariant,
		string Firmware,
		string? Script,
		IReadOnlyList<ProbeAggregateReport> Reports,
		IReadOnlyList<ProbeAggregateStep> Steps,
		ProbeAggregateTemplateLoads TemplateLoads,
		IReadOnlyList<ProbeAggregateRecomputed> RecomputedAmpWords,
		// Listed in the script but never seen changing in a compared load: candidates to drop once enough reports agree.
		IReadOnlyList<int> ScriptRecomputedNeverSeen,
		IReadOnlyList<ProbeAggregateVoicing> Voicing,
		IReadOnlyList<ProbeAggregateInsert> InsertDefaults,
		IReadOnlyList<ProbeAggregateCable> Cables);

	public sealed record ProbeAggregate(string Format, DateTimeOffset GeneratedUtc, int Reports, IReadOnlyList<string> Unreadable,
		IReadOnlyList<ProbeAggregateGroup> Groups);

	// Every analysed report folded by device and firmware, for FracCompat to turn into catalogue updates: words that agree
	// across reports are listed once with a count, disagreements as variants, so one odd unit stands out.
	public static class ProbeReportAggregator
	{
		public const string Format = "fractone-probe-aggregate/1";

		public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		public static ProbeAggregate Build(IEnumerable<AnalysedProbeReport> reports, DateTimeOffset generatedUtc, IReadOnlyList<string>? unreadable = null)
		{
			ArgumentNullException.ThrowIfNull(reports);
			AnalysedProbeReport[] all = [.. reports];
			ProbeAggregateGroup[] groups = [.. all
				.GroupBy(report => (report.Analysis.Device, report.Analysis.Firmware, report.Analysis.DeviceVariant))
				.OrderBy(group => group.Key.Device, StringComparer.Ordinal).ThenBy(group => group.Key.Firmware, StringComparer.Ordinal)
				.ThenBy(group => group.Key.DeviceVariant)
				.Select(group => Group(group.Key.Device, group.Key.DeviceVariant, group.Key.Firmware, [.. group.OrderBy(report => report.UploadedUtc)]))];
			return new ProbeAggregate(Format, generatedUtc, all.Length, unreadable ?? [], groups);
		}

		public static byte[] Serialise(ProbeAggregate aggregate) => JsonSerializer.SerializeToUtf8Bytes(aggregate, Json);

		private static ProbeAggregateGroup Group(string device, ProbeDeviceVariant? variant, string firmware, IReadOnlyList<AnalysedProbeReport> reports)
		{
			ProbeReportAnalysis[] analyses = [.. reports.Select(report => report.Analysis)];
			ProbeScript? script = ProbeScripts.All.FirstOrDefault(candidate => analyses.Any(analysis => analysis.ExpectationsFrom == candidate.Id));

			ProbeAggregateStep[] steps = [.. analyses.SelectMany(analysis => analysis.Steps)
				.GroupBy(step => (step.Id, step.Kind))
				.Select(group => new ProbeAggregateStep(group.Key.Id, group.Key.Kind,
					group.Count(step => step.Status == "Passed"), group.Count(step => step.Status == "Failed"),
					group.Count(step => step.Status == "Skipped"), group.Count(step => step.Status == "Stopped")))];

			ProbeTemplateLoad[] loads = [.. analyses.Select(analysis => analysis.TemplateLoad).OfType<ProbeTemplateLoad>()];
			ProbeAggregateTemplateLoads templateLoads = new(loads.Length, loads.Count(load => load.Verdict == ProbeVerdict.Match),
				loads.Count(load => load.Verdict == ProbeVerdict.Differs), loads.Count(load => load.Identical));

			ProbeAggregateRecomputed[] recomputed = [.. analyses
				.SelectMany((analysis, index) => analysis.RecomputedAmpWords.Select(word => (Report: index, Word: word)))
				.GroupBy(pair => (pair.Word.EffectId, pair.Word.ParameterId))
				.OrderBy(group => group.Key.EffectId).ThenBy(group => group.Key.ParameterId)
				.Select(group => new ProbeAggregateRecomputed(group.Key.EffectId, group.Key.ParameterId,
					script is not null && script.AmpEffectIds.Contains(group.Key.EffectId) && script.RecomputedAmpParameters.Contains(group.Key.ParameterId),
					group.Select(pair => pair.Report).Distinct().Count(),
					[.. group.Select(pair => pair.Word.Channel).Distinct().Order(StringComparer.Ordinal)],
					[.. group.Select(pair => pair.Word).Take(3)]))];
			int[] neverSeen = script is null || loads.Length == 0
				? []
				: [.. script.RecomputedAmpParameters.Except(recomputed.Select(word => word.ParameterId)).Order()];

			ProbeAggregateVoicing[] voicing = [.. analyses.SelectMany(analysis => analysis.Voicings)
				.GroupBy(observation => (observation.Block, observation.EffectId, observation.ParameterId, observation.Ordinal))
				.OrderBy(group => group.Key.EffectId).ThenBy(group => group.Key.Ordinal)
				.Select(group =>
				{
					ProbeWordsVariant[] variants = [.. group.GroupBy(observation => WordsKey(observation.Words))
						.OrderByDescending(variant => variant.Count())
						.Select(variant => new ProbeWordsVariant(new SortedDictionary<int, int>(variant.First().Words.ToDictionary()), variant.Count()))];
					return new ProbeAggregateVoicing(group.Key.Block, group.Key.EffectId, group.Key.ParameterId, group.Key.Ordinal,
						group.Select(observation => observation.Name).FirstOrDefault(name => name is not null), group.Any(observation => observation.CarriedOver),
						Counts(group.Select(observation => observation.Verdict)), variants.Length == 1, variants);
				})];

			ProbeAggregateInsert[] inserts = [.. analyses.SelectMany(analysis => analysis.Inserts)
				.GroupBy(observation => (observation.Block, observation.EffectId, observation.ExpectedCell))
				.Select(group =>
				{
					ProbeInsertVariant[] variants = [.. group.GroupBy(observation => (observation.Shape, observation.Header, observation.Words))
						.OrderByDescending(variant => variant.Count())
						.Select(variant => new ProbeInsertVariant(variant.Key.Shape, variant.Key.Header, variant.Key.Words, variant.Count()))];
					return new ProbeAggregateInsert(group.Key.Block, group.Key.EffectId, group.Key.ExpectedCell, group.Any(observation => observation.CarriedOver),
						Counts(group.Select(observation => observation.Verdict)), variants.Length == 1, variants);
				})];

			ProbeAggregateCable[] cables = [.. analyses.SelectMany(analysis => analysis.Cables)
				.GroupBy(observation => observation.Route)
				.Select(group =>
				{
					ProbeMaskVariant[] variants = [.. group.GroupBy(observation => (observation.Frame, observation.Mask))
						.OrderByDescending(variant => variant.Count())
						.Select(variant => new ProbeMaskVariant(variant.Key.Frame, variant.Key.Mask, variant.Count()))];
					return new ProbeAggregateCable(group.Key, group.Select(observation => observation.ExpectedFrame).FirstOrDefault(frame => frame is not null),
						group.Select(observation => observation.ExpectedMask).FirstOrDefault(mask => mask is not null), group.Any(observation => observation.CarriedOver),
						Counts(group.Select(observation => observation.Verdict)), variants.Length == 1, variants);
				})];

			return new ProbeAggregateGroup(device, variant, firmware, script?.Id,
				[.. reports.Select(report => new ProbeAggregateReport(report.Id, report.UploadedUtc, report.Analysis.Outcome, report.Analysis.LevelRun,
					report.Analysis.LeftAsFound, report.Analysis.ToolVersion, report.Analysis.DeviceVariantVerified, report.Analysis.PresetSlots))],
				steps, templateLoads, recomputed, neverSeen, voicing, inserts, cables);
		}

		private static ProbeVerdictCounts Counts(IEnumerable<ProbeVerdict> verdicts)
		{
			ProbeVerdict[] all = [.. verdicts];
			return new ProbeVerdictCounts(all.Count(verdict => verdict == ProbeVerdict.Match), all.Count(verdict => verdict == ProbeVerdict.Differs),
				all.Count(verdict => verdict == ProbeVerdict.Unverified));
		}

		private static string WordsKey(IReadOnlyDictionary<int, int> words) =>
			string.Join(' ', words.OrderBy(pair => pair.Key).Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}")));
	}
}
