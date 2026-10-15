using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fractal.Companion.Catalog;
using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	[JsonConverter(typeof(JsonStringEnumConverter<ProbeVerdict>))]
	public enum ProbeVerdict
	{
		// Observed as the script expects.
		Match,
		// Observed, and not as the script expects (the detail says how).
		Differs,
		// Recorded, but the script holds nothing to compare it with.
		Unverified,
		// The step didn't run or recorded nothing for this.
		NotRun
	}

	// One observation and its comparison. CarriedOver marks expectations taken from the FM9 by name or id and not yet seen
	// on this device: a match there is the first confirmation.
	public sealed record ProbeEvidence(string Subject, ProbeVerdict Verdict, string Detail, bool CarriedOver = false);

	public sealed record ProbeStepAnalysis(string Id, string Kind, int Level, string Status, string Message, double DurationMs,
		IReadOnlyList<ProbeEvidence> Evidence);

	public sealed record ProbeRecomputedAmpWord(int EffectId, string Channel, int ParameterId, int Sent, int ReadBack);

	public sealed record ProbeVoicingObservation(string Block, int EffectId, int ParameterId, int Ordinal, string? Name,
		IReadOnlyDictionary<int, int> Words, ProbeVerdict Verdict, string Detail, bool CarriedOver);

	// A block inserted alone on the empty preset: its record as the device made it. Only the first insert after a power
	// cycle gives true defaults, and the probe can't tell, so these are recorded, never judged.
	public sealed record ProbeInsertObservation(string Block, int EffectId, string ExpectedCell, string? Cell, string? Shape,
		string? Header, string? Words, ProbeVerdict Verdict, string Detail, bool CarriedOver);

	public sealed record ProbeCableObservation(string Route, string? ExpectedFrame, string? Frame, string? ExpectedMask, string? Mask,
		ProbeVerdict Verdict, string Detail, bool CarriedOver);

	public sealed record ProbeExchangeSummary(int Exchanges, int FramesSent, int FramesReceived, int FramesWithheld, bool Truncated);

	public sealed record ProbeTemplateLoad(ProbeVerdict Verdict, bool CheckedFromReadbacks, bool Identical, IReadOnlyList<int> UnexpectedOffsets,
		string Detail);

	// What an uploaded probe report says, compared with the bundled script for its device and firmware. Pure parsing: the
	// zip's files are only ever read as data, and nothing in them is run or sent anywhere.
	public sealed record ProbeReportAnalysis(
		string Device,
		string? Model,
		// From run.json (Axe-Fx III only): the model byte can't tell a Mk I from a Mk II, so the probe asks and says so here.
		ProbeDeviceVariant? DeviceVariant,
		bool? DeviceVariantVerified,
		int? PresetSlots,
		string Firmware,
		string? Script,
		bool? FirmwareScripted,
		string? ExpectationsFrom,
		string ToolVersion,
		string Outcome,
		string? StopReason,
		int LevelRequested,
		int LevelRun,
		bool? LeftAsFound,
		IReadOnlyList<string> RestoreNotes,
		bool UserPresetsIncluded,
		IReadOnlyList<ProbeStepAnalysis> Steps,
		ProbeTemplateLoad? TemplateLoad,
		IReadOnlyList<ProbeRecomputedAmpWord> RecomputedAmpWords,
		IReadOnlyList<ProbeVoicingObservation> Voicings,
		IReadOnlyList<ProbeInsertObservation> Inserts,
		IReadOnlyList<ProbeCableObservation> Cables,
		ProbeExchangeSummary Exchanges,
		IReadOnlyList<string> Readbacks,
		IReadOnlyList<string> Problems);

	public static partial class ProbeReportAnalyzer
	{
		private const int MaximumRunJsonBytes = 1024 * 1024;
		private const int MaximumReadbackBytes = 1024 * 1024;
		private const long MaximumExchangeBytes = 64L * 1024 * 1024;
		private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

		// Throws InvalidDataException when the zip or its run.json can't be read at all; anything less is a listed problem.
		public static ProbeReportAnalysis Analyse(byte[] zip)
		{
			ArgumentNullException.ThrowIfNull(zip);
			try
			{
				using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
				return Analyse(archive);
			}
			catch (InvalidDataException)
			{
				throw;
			}
			catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
			{
				throw new InvalidDataException($"The report can't be read: {exception.Message}", exception);
			}
		}

		private static ProbeReportAnalysis Analyse(ZipArchive archive)
		{
			List<string> problems = [];
			ZipArchiveEntry run = archive.GetEntry("run.json") ?? throw new InvalidDataException("The report has no run.json.");
			RunFile file = JsonSerializer.Deserialize<RunFile>(ReadBounded(run, MaximumRunJsonBytes, "run.json"), Json)
				?? throw new InvalidDataException("run.json is empty.");

			Gen3Device? device = DeviceOf(file);
			string firmware = file.Firmware ?? "unknown";
			ProbeScript? script = device is null || file.Firmware is null || file.FirmwareScripted == false
				? null
				: ProbeScripts.All.FirstOrDefault(candidate => candidate.Model == device.Model && candidate.Firmware == file.Firmware);
			if (device is null)
				problems.Add($"The device '{file.Device ?? file.Model ?? "unknown"}' isn't a gen-3 unit this site knows; nothing was compared.");
			else if (file.FirmwareScripted == false)
				problems.Add($"No probe script existed for firmware {firmware}: only read-only steps ran, and nothing was compared.");
			else if (script is null)
				problems.Add($"This site has no script for {device.Name} firmware {firmware} to compare with.");
			if (script is not null && file.Script is not null && file.Script != script.Id)
				problems.Add($"The run used the script '{file.Script}'; compared with '{script.Id}'.");

			ProbeDeviceVariant? variant = null;
			if (file.DeviceVariant is string variantText)
			{
				if (Enum.TryParse(variantText, ignoreCase: false, out ProbeDeviceVariant parsed) && Enum.IsDefined(parsed)) variant = parsed;
				else problems.Add($"The run names a device variant '{variantText}' this site doesn't know; it's shown as unknown.");
			}
			else if (device == Gen3Device.AxeFx3)
				problems.Add("The run doesn't say whether the Axe-Fx III is a Mk I or a Mk II (a probe older than 0.16).");

			string[] readbacks = [.. archive.Entries.Where(entry => entry.FullName.StartsWith("readbacks/", StringComparison.Ordinal) && !entry.FullName.EndsWith('/'))
				.Select(entry => entry.FullName["readbacks/".Length..]).Order(StringComparer.Ordinal)];

			Context context = new(device, script, problems);
			List<ProbeStepAnalysis> steps = [];
			foreach (RunStep step in file.Steps ?? [])
			{
				IReadOnlyDictionary<string, string> data = step.Data ?? new Dictionary<string, string>();
				ProbeStep? scripted = script?.Steps.FirstOrDefault(candidate => candidate.Id == step.Id);
				bool ran = step.Status is "Passed" or "Failed";
				List<ProbeEvidence> evidence = step.Kind switch
				{
					"loadTemplate" => TemplateEvidence(context, archive, data, ran),
					"voicingCheck" => VoicingEvidence(context, scripted, data, ran),
					"freshInserts" => InsertEvidence(context, scripted, data, ran),
					"cableCheck" => CableEvidence(context, scripted, data, ran),
					"storeCheck" => StoreEvidence(data, ran),
					_ => []
				};
				// A check that loads a template first (fresh inserts, cables) says whether that load read back as sent.
				if (step.Kind != "loadTemplate" && data.TryGetValue("template", out string? template))
					evidence.Insert(0, (step.Message ?? "").StartsWith($"The {template} template read back differently", StringComparison.Ordinal)
						? new ProbeEvidence($"{template} template load", ProbeVerdict.Differs, step.Message!)
						: new ProbeEvidence($"{template} template load", ProbeVerdict.Match, "Read back as sent (apart from recomputed Amp words)."));
				steps.Add(new ProbeStepAnalysis(step.Id ?? "?", step.Kind ?? "?", step.Level, step.Status ?? "unknown", step.Message ?? "",
					step.DurationMs, evidence));
			}

			return new ProbeReportAnalysis(
				device?.Name ?? file.Device ?? "unknown",
				file.Model,
				variant ?? (file.DeviceVariant is null ? null : ProbeDeviceVariant.Unknown),
				file.DeviceVariantVerified,
				file.PresetSlots,
				firmware,
				file.Script,
				file.FirmwareScripted,
				script?.Id,
				file.ToolVersion ?? "unknown",
				file.Outcome ?? "unknown",
				file.StopReason,
				file.LevelRequested,
				file.LevelRun,
				file.Restore?.LeftAsFound,
				file.Restore?.Notes ?? [],
				file.UserPresetsIncluded == true,
				steps,
				context.TemplateLoad,
				context.Recomputed,
				context.Voicings,
				context.Inserts,
				context.Cables,
				Exchanges(archive, problems),
				readbacks,
				problems);
		}

		// The template load: re-compared from the readbacks when both are in the zip (the authority), else run.json's words.
		private static List<ProbeEvidence> TemplateEvidence(Context context, ZipArchive archive, IReadOnlyDictionary<string, string> data, bool ran)
		{
			if (!ran)
				return [new ProbeEvidence("Template readback", ProbeVerdict.NotRun, "The load didn't run.")];
			ProbeScript? script = context.Script;
			ReadbackComparison? comparison = null;
			if (context.Device is Gen3Device device && script is not null &&
				archive.GetEntry("readbacks/template-sent.syx") is { } sentEntry && archive.GetEntry("readbacks/template-readback.syx") is { } readEntry)
			{
				try
				{
					Fm9PresetDump sent = ProbeFrames.Parse(device, ProbeFrames.Split(ReadBounded(sentEntry, MaximumReadbackBytes, "template-sent.syx")));
					Fm9PresetDump readBack = ProbeFrames.Parse(device, ProbeFrames.Split(ReadBounded(readEntry, MaximumReadbackBytes, "template-readback.syx")));
					comparison = ReadbackComparer.Compare(device, script, sent, readBack);
				}
				catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
				{
					context.Problems.Add($"The template readbacks couldn't be compared ({exception.Message}); run.json's record is used.");
				}
			}

			List<ProbeEvidence> evidence = [];
			if (comparison is not null)
			{
				context.Recomputed.AddRange(comparison.Recomputed.Select(word =>
					new ProbeRecomputedAmpWord(word.EffectId, ((char)('A' + word.Channel)).ToString(), word.ParameterId, word.Sent, word.ReadBack)));
				context.TemplateLoad = new ProbeTemplateLoad(comparison.Matches ? ProbeVerdict.Match : ProbeVerdict.Differs, true, comparison.Identical,
					comparison.UnexpectedOffsets, comparison.Summary);
				evidence.Add(new ProbeEvidence("Template readback (re-checked from the readbacks)", context.TemplateLoad.Verdict, comparison.Summary));
			}
			else
			{
				foreach (Match match in RecomputedPattern().Matches(data.GetValueOrDefault("recomputed") ?? ""))
					context.Recomputed.Add(new ProbeRecomputedAmpWord(Int(match.Groups["effect"].Value), match.Groups["channel"].Value,
						Int(match.Groups["id"].Value), Int(match.Groups["sent"].Value), Int(match.Groups["read"].Value)));
				int[] offsets = [.. (data.GetValueOrDefault("unexpectedOffsets") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
					.Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) ? offset : -1).Where(offset => offset >= 0)];
				bool identical = data.GetValueOrDefault("identical") == "true";
				ProbeVerdict verdict = script is null ? ProbeVerdict.Unverified : offsets.Length == 0 ? ProbeVerdict.Match : ProbeVerdict.Differs;
				string detail = offsets.Length > 0
					? $"{offsets.Length} byte(s) differ outside the recomputed Amp words (body offsets {string.Join(", ", offsets.Take(8))})."
					: identical ? "Read back exactly as sent." : $"Read back as sent apart from {context.Recomputed.Count} recomputed Amp word(s).";
				context.TemplateLoad = new ProbeTemplateLoad(verdict, false, identical, offsets, detail);
				evidence.Add(new ProbeEvidence("Template readback (from run.json)", verdict, detail));
			}

			if (script is not null)
			{
				int[] seen = [.. context.Recomputed.Select(word => word.ParameterId).Distinct().Order()];
				int[] outside = [.. context.Recomputed.Where(word => !script.AmpEffectIds.Contains(word.EffectId) || !script.RecomputedAmpParameters.Contains(word.ParameterId))
					.Select(word => word.ParameterId).Distinct().Order()];
				int[] quiet = [.. script.RecomputedAmpParameters.Except(seen).Order()];
				evidence.Add(new ProbeEvidence("Recomputed Amp words",
					outside.Length > 0 ? ProbeVerdict.Differs : seen.Length == 0 ? ProbeVerdict.Unverified : ProbeVerdict.Match,
					outside.Length > 0
						? $"Changed outside the script's list: ids {string.Join(", ", outside)}."
						: seen.Length == 0
							? "None changed on load, so the script's list was neither confirmed nor contradicted."
							: $"Changed: ids {string.Join(", ", seen)} ({context.Recomputed.Count} word(s)); listed but unchanged: {(quiet.Length == 0 ? "none" : string.Join(", ", quiet))}."));
			}
			return evidence;
		}

		private static List<ProbeEvidence> VoicingEvidence(Context context, ProbeStep? scripted, IReadOnlyDictionary<string, string> data, bool ran)
		{
			if (scripted is null || scripted.Types is not { Count: > 0 } || scripted.EffectId is not int effect || scripted.ParameterId is not int parameter)
				return [new ProbeEvidence("Voicing", ran ? ProbeVerdict.Unverified : ProbeVerdict.NotRun,
					scripted is null ? "The script has no such step to compare with." : "The script lists no types for this block yet.")];
			bool carried = scripted.Unverified == true || data.GetValueOrDefault("unverified") == "true";
			int tolerance = scripted.Tolerance ?? 1;
			string block = scripted.Block ?? $"effect {effect}";
			List<ProbeEvidence> evidence = [];
			foreach (ProbeVoicing type in scripted.Types)
			{
				string key = type.Ordinal.ToString(CultureInfo.InvariantCulture);
				string name = data.GetValueOrDefault($"{key}.name") ?? type.Name ?? $"type {key}";
				string subject = $"{block} {key} ({name})";
				Dictionary<int, int> words = Words(data.GetValueOrDefault($"{key}.words"));
				(ProbeVerdict verdict, string detail) = data.GetValueOrDefault($"{key}.taken") == "false"
					? (ProbeVerdict.Differs, "The type wasn't selected after two tries.")
					: words.Count == 0
						? (ProbeVerdict.NotRun, "No words recorded.")
						: Compare(words, ExpectedVoicing(context, scripted, type), tolerance);
				evidence.Add(new ProbeEvidence(subject, verdict, detail, carried));
				if (words.Count > 0)
					context.Voicings.Add(new ProbeVoicingObservation(block, effect, parameter, type.Ordinal, type.Name, words, verdict, detail, carried));
			}
			return evidence;
		}

		private static (ProbeVerdict, string) Compare(IReadOnlyDictionary<int, int> words, IReadOnlyDictionary<int, int>? expected, int tolerance)
		{
			if (expected is null || expected.Count == 0)
				return (ProbeVerdict.Unverified, $"{words.Count} word(s) recorded; the script expects nothing for this type.");
			KeyValuePair<int, int>[] compared = [.. expected.Where(pair => words.ContainsKey(pair.Key)).OrderBy(pair => pair.Key)];
			if (compared.Length == 0)
				return (ProbeVerdict.Unverified, "None of the expected words were recorded.");
			string[] mismatches = [.. compared.Where(pair => Math.Abs(words[pair.Key] - pair.Value) > tolerance)
				.Select(pair => $"{pair.Key}: {words[pair.Key]} want {pair.Value}")];
			string unrecorded = compared.Length < expected.Count ? $" ({expected.Count - compared.Length} expected word(s) not recorded)" : "";
			return mismatches.Length == 0
				? (ProbeVerdict.Match, $"{compared.Length} expected word(s) match{unrecorded}.")
				: (ProbeVerdict.Differs, $"{mismatches.Length} of {compared.Length} differ: {string.Join("; ", mismatches)}{unrecorded}.");
		}

		private static IReadOnlyDictionary<int, int>? ExpectedVoicing(Context context, ProbeStep step, ProbeVoicing type)
		{
			if (type.Expect is not null) return type.Expect;
			if (step.ExpectFrom != "fm9ModelDefaults" || context.Device != Gen3Device.Fm9 || step.Block is null) return null;
			try
			{
				return Fm9ModelDefaults.For(step.Block, type.Ordinal);
			}
			catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException)
			{
				context.Problems.Add($"The FM9 model defaults couldn't be read: {exception.Message}");
				return null;
			}
		}

		private static List<ProbeEvidence> InsertEvidence(Context context, ProbeStep? scripted, IReadOnlyDictionary<string, string> data, bool ran)
		{
			if (scripted?.Inserts is not { Count: > 0 } inserts)
				return [new ProbeEvidence("Fresh inserts", ran ? ProbeVerdict.Unverified : ProbeVerdict.NotRun, "The script lists no inserts to compare with.")];
			bool carried = scripted.Unverified == true || data.GetValueOrDefault("unverified") == "true";
			string sincePowerCycle = data.GetValueOrDefault("sincePowerCycle") ?? "unknown";
			List<ProbeEvidence> evidence = [];
			foreach (ProbeInsert insert in inserts)
			{
				string expectedCell = $"r{insert.Row}c{insert.Column}";
				string? cell = data.GetValueOrDefault($"{insert.Block}.cell"), shape = data.GetValueOrDefault($"{insert.Block}.shape");
				(ProbeVerdict verdict, string detail) = shape is not null
					? cell is null || cell == expectedCell
						? (ProbeVerdict.Match, $"Inserted at {expectedCell}, record {shape} recorded (first insert since a power cycle: {sincePowerCycle}).")
						: (ProbeVerdict.Differs, $"Recorded at {cell}, the script inserts it at {expectedCell}.")
					: data.GetValueOrDefault($"{insert.Block}.attempts") == "2"
						? (ProbeVerdict.Differs, "Not inserted after two tries.")
						: (ProbeVerdict.NotRun, "Not reached.");
				evidence.Add(new ProbeEvidence($"{insert.Block} at {expectedCell}", verdict, detail, carried));
				if (shape is not null)
					context.Inserts.Add(new ProbeInsertObservation(insert.Block, insert.EffectId, expectedCell, cell, shape,
						data.GetValueOrDefault($"{insert.Block}.header"), data.GetValueOrDefault($"{insert.Block}.words"), verdict, detail, carried));
			}
			return evidence;
		}

		private static List<ProbeEvidence> CableEvidence(Context context, ProbeStep? scripted, IReadOnlyDictionary<string, string> data, bool ran)
		{
			if (scripted?.Routes is not { Count: > 0 } routes)
				return [new ProbeEvidence("Cables", ran ? ProbeVerdict.Unverified : ProbeVerdict.NotRun, "The script lists no cables to compare with.")];
			bool carried = scripted.Unverified == true || data.GetValueOrDefault("unverified") == "true";
			List<ProbeEvidence> evidence = [];
			foreach (ProbeRoute route in routes)
			{
				string destination = $"r{route.ToRow}c{route.FromColumn + 1}";
				string? mask = data.GetValueOrDefault($"{route.Id}.mask"), frame = data.GetValueOrDefault($"{route.Id}.frame");
				string? changed = data.GetValueOrDefault($"{route.Id}.changed");
				string? expectedFrame = ExpectedRouteFrame(context.Device, route);
				string? expectedMask = null;
				ProbeVerdict verdict;
				string detail;
				if (mask is null || !TryHex(mask, out int observed))
				{
					(verdict, detail) = (ProbeVerdict.NotRun, "No mask recorded.");
				}
				else
				{
					// The mask before the cable, from the cells it changed ("r1c2 0x00->0x01"); unchanged means it was already there.
					(string Cell, int Before, int After)[] changes = [.. ChangePattern().Matches(changed ?? "")
						.Select(match => (match.Groups["cell"].Value, Hex(match.Groups["before"].Value), Hex(match.Groups["after"].Value)))];
					int before = observed;
					foreach ((string cell, int was, _) in changes)
						if (cell == destination) before = was;
					int expected = route.ExpectMask ?? (before | (1 << (route.FromRow - 1)));
					expectedMask = $"0x{expected:X2}";
					string[] others = [.. changes.Where(change => change.Cell != destination).Select(change => $"{change.Cell} 0x{change.Before:X2}->0x{change.After:X2}")];
					List<string> problems = [];
					if (observed != expected) problems.Add($"mask {mask}, expected {expectedMask}");
					if (others.Length > 0) problems.Add($"other cells changed: {string.Join(", ", others)}");
					if (expectedFrame is not null && frame is not null && !string.Equals(frame, expectedFrame, StringComparison.OrdinalIgnoreCase))
						problems.Add($"sent {frame}, this site encodes {expectedFrame}");
					(verdict, detail) = problems.Count == 0
						? (ProbeVerdict.Match, $"{destination} mask {mask} (frame {frame ?? "?"}).")
						: (ProbeVerdict.Differs, string.Join("; ", problems) + ".");
				}
				evidence.Add(new ProbeEvidence($"Cable {route.Id}", verdict, detail, carried));
				if (mask is not null)
					context.Cables.Add(new ProbeCableObservation(route.Id, expectedFrame, frame, expectedMask, mask, verdict, detail, carried));
			}
			return evidence;
		}

		private static List<ProbeEvidence> StoreEvidence(IReadOnlyDictionary<string, string> data, bool ran) =>
			data.TryGetValue("storedMatches", out string? matches)
				? [new ProbeEvidence("Stored template readback", matches == "true" ? ProbeVerdict.Match : ProbeVerdict.Differs,
					data.GetValueOrDefault("storedSummary") ?? "")]
				: [new ProbeEvidence("Stored template readback", ProbeVerdict.NotRun, ran ? "Nothing recorded." : "The store didn't run.")];

		// The cable frame's three code bytes as this build encodes them for the device, to catch a numbering disagreement.
		private static string? ExpectedRouteFrame(Gen3Device? device, ProbeRoute route)
		{
			if (device is null) return null;
			try
			{
				return ProbeFrames.Hex(ProbeFrames.Route(device, route.FromRow, route.FromColumn, route.ToRow).AsSpan(21, 3));
			}
			catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException)
			{
				return null;
			}
		}

		// exchanges.txt is counted, never kept: it can be large and the frames are already summarised in run.json.
		private static ProbeExchangeSummary Exchanges(ZipArchive archive, List<string> problems)
		{
			ZipArchiveEntry? entry = archive.GetEntry("exchanges.txt");
			if (entry is null)
			{
				problems.Add("The report has no exchanges.txt.");
				return new ProbeExchangeSummary(0, 0, 0, 0, false);
			}
			int exchanges = 0, sent = 0, received = 0, withheld = 0;
			long read = 0;
			bool truncated = false;
			using StreamReader reader = new(entry.Open(), Encoding.UTF8);
			while (reader.ReadLine() is string line)
			{
				read += line.Length + 1;
				if (read > MaximumExchangeBytes)
				{
					truncated = true;
					problems.Add("exchanges.txt is over 64 MB; only its start was counted.");
					break;
				}
				if (line.StartsWith(">> ", StringComparison.Ordinal)) sent++;
				else if (line.StartsWith("<< ", StringComparison.Ordinal)) received++;
				else if (line.Length > 0 && line[0] != '#') exchanges++;
				if (line.Contains("withheld]", StringComparison.Ordinal)) withheld++;
			}
			return new ProbeExchangeSummary(exchanges, sent, received, withheld, truncated);
		}

		// For people reading a report: the model byte alone names the family, not the Axe-Fx III's mark.
		public static string DescribeVariant(ProbeDeviceVariant? variant, bool? verified) => variant switch
		{
			null => "none reported",
			ProbeDeviceVariant.MkI => "Mk I",
			ProbeDeviceVariant.MkIIOrTurbo => "Mk II or Turbo",
			_ => "unknown (the Mk II check had no clear answer)"
		} + (variant is null or ProbeDeviceVariant.Unknown ? "" : verified == true ? " (verified)" : " (unverified)");

		private static Gen3Device? DeviceOf(RunFile file)
		{
			if (file.Model is string model && model.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
				byte.TryParse(model.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value) && Gen3Device.FromModel(value) is { } byModel)
				return byModel;
			return Gen3Device.All.FirstOrDefault(device => string.Equals(device.Name, file.Device, StringComparison.OrdinalIgnoreCase));
		}

		private static byte[] ReadBounded(ZipArchiveEntry entry, int maximum, string name)
		{
			if (entry.Length > maximum) throw new InvalidDataException($"{name} is larger than {maximum / 1024} KB.");
			byte[] buffer = new byte[maximum + 1];
			int length;
			using (Stream stream = entry.Open()) length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
			if (length > maximum) throw new InvalidDataException($"{name} is larger than {maximum / 1024} KB.");
			return buffer[..length];
		}

		// "6=43987 9=65534" as recorded by the voicing check.
		private static Dictionary<int, int> Words(string? text)
		{
			Dictionary<int, int> words = [];
			foreach (string pair in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = pair.Split('=');
				if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
					int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int word))
					words[id] = word;
			}
			return words;
		}

		private static int Int(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

		private static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

		private static bool TryHex(string text, out int value) =>
			int.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

		// ProbeEngine's "effect 58 A id 27: 100 -> 200".
		[GeneratedRegex(@"effect (?<effect>\d+) (?<channel>[A-D]) id (?<id>\d+): (?<sent>\d+) -> (?<read>\d+)")]
		private static partial Regex RecomputedPattern();

		// The cable check's "r1c2 0x00->0x01".
		[GeneratedRegex(@"(?<cell>r\d+c\d+) 0x(?<before>[0-9A-Fa-f]+)->0x(?<after>[0-9A-Fa-f]+)")]
		private static partial Regex ChangePattern();

		private sealed class Context(Gen3Device? device, ProbeScript? script, List<string> problems)
		{
			public Gen3Device? Device { get; } = device;
			public ProbeScript? Script { get; } = script;
			public List<string> Problems { get; } = problems;
			public ProbeTemplateLoad? TemplateLoad { get; set; }
			public List<ProbeRecomputedAmpWord> Recomputed { get; } = [];
			public List<ProbeVoicingObservation> Voicings { get; } = [];
			public List<ProbeInsertObservation> Inserts { get; } = [];
			public List<ProbeCableObservation> Cables { get; } = [];
		}

		// run.json as ProbeReportWriter writes it; every field optional, so an older or partial report still reads.
		private sealed record RunFile(
			string? Tool,
			string? ToolVersion,
			string? Device,
			string? Model,
			string? DeviceVariant,
			bool? DeviceVariantVerified,
			int? PresetSlots,
			string? Firmware,
			bool? FirmwareScripted,
			string? Script,
			int LevelRequested,
			int LevelRun,
			string? Outcome,
			string? StopReason,
			IReadOnlyList<RunStep>? Steps,
			RunRestore? Restore,
			bool? UserPresetsIncluded);

		private sealed record RunStep(string? Id, string? Kind, int Level, string? Status, string? Message, double DurationMs,
			IReadOnlyDictionary<string, string>? Data);

		private sealed record RunRestore(bool? LeftAsFound, IReadOnlyList<string>? Notes);
	}
}
