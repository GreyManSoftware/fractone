using System.ComponentModel;
using System.Globalization;
using Fractal.Core;
using Fractal.Fm9;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	public sealed record Fm9MeasureResult(AudioMeasurement Measurement, AudioComparison? Comparison, IReadOnlyList<string> Hints);

	[McpServerToolType]
	public sealed class ToneTools(Fm9Context context)
	{
		[McpServerTool(Name = "tone_design_guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("The tone designer's rules: the proposal JSON contract and FM9 ranges, every amp, drive and cab choice, the effect blocks and modifiers, and notes on how models behave on the FM9. Read it before writing a proposal for build_preset.")]
		public Task<string> Guide() => Fm9Context.RunSync(Fm9ToneDesigner.Guide);

		[McpServerTool(Name = "build_preset", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Builds an FM9 preset from a tone-design proposal you wrote: runs FracTone's repairs and checks (no AI service or key involved) and exports a .syx. If corrections come back, fix the proposal and build again; adjustments are what the checks changed.")]
		public Task<Fm9BuildResult> Build(
			[Description("The proposal JSON, or the path of a .json file holding it.")] string proposal,
			[Description("Preset name; default the proposal's presetName.")] string? name = null,
			[Description("Output .syx; default designs/<name> <time>.syx.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string json = proposal.TrimStart().StartsWith('{') ? proposal : File.ReadAllText(context.Input(proposal));
				string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				string path = output is null
					? Path.Combine(context.Settings.WorkFolder, "designs", $"{Fm9Device.FileName(name ?? "design")} {stamp}.syx")
					: context.Settings.Resolve(output);
				return Fm9ToneDesigner.Build(json, path, name);
			});

		[McpServerTool(Name = "record", ReadOnly = true, OpenWorld = false)]
		[Description("Records a take to a WAV in the work folder: source \"fm9\" records the FM9's USB audio (channels 1-2 carry Output 1), \"system\" records what the PC is playing (e.g. the target track). Waits for sound before starting unless told not to.")]
		public Task<Fm9Recording> Record(
			[Description("Seconds to record (1-600). 20-60 s of the part to match works well.")] double seconds,
			[Description("\"fm9\" (default), \"system\", or part of a recording device's name.")] string? source = null,
			[Description("Output .wav; default recordings/<source> <time>.wav.")] string? file = null,
			[Description("Start straight away instead of waiting for sound.")] bool? startNow = null,
			[Description("First channel to record, default 1.")] int? firstChannel = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() =>
			{
				string from = source ?? "fm9";
				string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				string path = file is null ? Path.Combine(context.Settings.WorkFolder, "recordings", $"{Fm9Device.FileName(from)} {stamp}.wav") : context.Settings.Resolve(file);
				return Fm9AudioRecorder.RecordAsync(path, from, TimeSpan.FromSeconds(seconds), startNow != true, firstChannel ?? 1, 2, cancellationToken);
			});

		[McpServerTool(Name = "measure", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Measures a recording while it plays (silence ignored): integrated loudness (LUFS, ITU-R BS.1770), RMS over playing frames, peak in dBFS and clipping, and the balance of sub, lows (60-250 Hz), mud (250-1k), 1-2k, bite (2-4k), fizz (4-8k) and air (8-12k) relative to the whole. With a stem or other reference, also the take-minus-reference differences and the tone matcher's spectral deviation.")]
		public Task<Fm9MeasureResult> Measure(
			[Description("The recording (any common audio file: WAV, AIFF, FLAC, MP3, M4A, OGG, Opus).")] string file,
			[Description("A reference to compare with, e.g. an isolated stem or another take (any common audio file).")] string? reference = null,
			[Description("Seconds into the recording to start.")] double? start = null,
			[Description("Seconds to measure; default all.")] double? length = null) =>
			Fm9Context.RunSync(() =>
			{
				MultichannelAudioBuffer take = AudioFiles.Read(context.Input(file), start ?? 0, length, out AudioSource source);
				AudioMeasurement measured = AudioMeasurer.Measure(take);
				List<string> hints = [.. Hints(measured), Describe("The recording", source)];
				AudioComparison? comparison = null;
				if (reference is not null)
				{
					comparison = AudioMeasurer.Compare(take, AudioFiles.Read(context.Input(reference), start ?? 0, length, out AudioSource other));
					hints.Add(Describe("The reference", other));
				}
				return new Fm9MeasureResult(measured, comparison, hints);
			});

		// Lossy files are fine for EQ matching: the fitter trusts little above 4.5 kHz, where lossy codecs cut first.
		private static string Describe(string what, AudioSource source)
		{
			string layout = source.Channels switch { 1 => "mono", 2 => "stereo", _ => $"{source.Channels} channels" };
			string quality = source.Lossy
				? "lossy: fine for EQ matching, which trusts little above 4.5 kHz, but its loudness and top end are approximate."
				: "lossless.";
			return string.Create(CultureInfo.InvariantCulture, $"{what} is {source.Format}, {source.SampleRate / 1000.0:0.#} kHz, {layout}, {quality}");
		}

		// Plain-language flags a person would want before using a recording as a take or a target.
		internal static IReadOnlyList<string> Hints(AudioMeasurement measured)
		{
			List<string> hints = [];
			if (measured.Clipped) hints.Add($"{measured.ClippedSamples} samples are clipped (at or near 0 dBFS).");
			if (measured.ActiveSeconds < 5) hints.Add($"Only {measured.ActiveSeconds:0.#} s of playing: 20 s or more measures more reliably.");
			// Isolated guitar measured -26 to -33 dB below 60 Hz and -34 dB or less above 8 kHz (six Metallica stems and two
			// FM9 takes); bass and kick lift the first, cymbals the second.
			if (measured.Bands.FirstOrDefault(band => band.Name == "sub") is { RelativeDecibels: > -20 } sub)
				hints.Add($"Strong content below 60 Hz ({sub.RelativeDecibels:0.#} dB): this looks like a full mix (bass, kick), not an isolated guitar.");
			else if (measured.Bands.FirstOrDefault(band => band.Name == "air") is { RelativeDecibels: > -25 } air)
				hints.Add($"Strong content above 8 kHz ({air.RelativeDecibels:0.#} dB): this looks like a full mix (cymbals), not an isolated guitar.");
			return hints;
		}

		[McpServerTool(Name = "tone_match", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("FracTone's Parametric EQ match: compares a take of the preset played in one scene with the target recording, fits the EQ, writes the bands into a Parametric EQ 1 channel no scene plays (levelled to keep the take's loudness) and, if asked, a new scene that copies the recorded one but plays it. Writes a new .syx and optionally a preview WAV of the matched take.")]
		public Task<Fm9ToneMatchResult> Match(
			[Description("The preset the take was played through.")] string file,
			[Description("Scene the take was recorded in (1-8).")] int recordedScene,
			[Description("The take: the preset played through the FM9 (any common audio file).")] string wet,
			[Description("The target recording (any common audio file: WAV, AIFF, FLAC, MP3, M4A, OGG, Opus).")] string target,
			[Description("Parametric EQ 1 channel for the fit (A-D); default the first channel no scene plays.")] string? eqChannel = null,
			[Description("Scene to add that plays the fit (1-8).")] int? newScene = null,
			[Description("Name for that scene, default \"Matched\".")] string? newSceneName = null,
			[Description("Seconds into the take to start.")] double? wetStart = null,
			[Description("Seconds into the target to start.")] double? targetStart = null,
			[Description("Seconds to compare, up to 60.")] double? length = null,
			[Description("Write a preview WAV of the matched take.")] bool? preview = null,
			[Description("Output .syx.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				string outputPath = context.Output(output, input, "matched");
				return Fm9ToneMatch.MatchScene(new(input, outputPath, recordedScene, context.Input(wet), context.Input(target))
				{
					EqChannel = eqChannel,
					NewScene = newScene,
					NewSceneName = newSceneName ?? "Matched",
					WetStartSeconds = wetStart ?? 0,
					TargetStartSeconds = targetStart ?? 0,
					LengthSeconds = length ?? 60,
					Preview = preview == true ? Path.ChangeExtension(outputPath, ".preview.wav") : null
				});
			});

		[McpServerTool(Name = "rank_cabs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Finds the factory cabs that, each with a gentle Parametric EQ after it (broad bands, at most 3 dB), come closest to an isolated stem: one cab and an EQ instead of an IR blend. Needs one re-amp of the scene with its Cab block and any EQ after the cab bypassed (set_block, then reamp); every cab is then predicted from its measured response, so nothing else plays. Gap is the remaining weighted difference from the stem in dB (smaller is closer; predictions matched real re-amps within 0.04 dB). Many cabs land within a few tenths of each other: prefer one that suits the player's gear and era (filter such as \"4x12\") over a slightly smaller gap. Apply the winner with set_channel (cab and Parametric EQ settings as returned, on channels no scene plays) and compose_scene for an A/B scene, then reamp to confirm. Curves are from FM9 firmware 12; the FM3 and Axe-Fx III list the same cabs.")]
		public Task<Fm9CabRanking> RankCabs(
			[Description("The re-amp of the scene with the Cab and any EQ after it bypassed (any common audio file).")] string take,
			[Description("The isolated stem to match (any common audio file).")] string stem,
			[Description("Seconds into the take to start.")] double? takeStart = null,
			[Description("Seconds into the stem to start.")] double? stemStart = null,
			[Description("Seconds to compare, up to 60.")] double? length = null,
			[Description("Only cabs whose name contains this, e.g. \"4x12\", \"V30\", \"Brit\".")] string? filter = null,
			[Description("How many to return, default 10.")] int? count = null) =>
			Fm9Context.RunSync(() =>
			{
				double seconds = Math.Min(length ?? 60, WetToneMatcher.MaximumDurationSeconds);
				IReadOnlyList<Fm9CabCandidate> ranked = Fm9CabRanker.Rank(
					AudioFiles.Read(context.Input(take), takeStart ?? 0, seconds, out _),
					AudioFiles.Read(context.Input(stem), stemStart ?? 0, seconds, out _), Math.Clamp(count ?? 10, 1, 50), filter);
				return new Fm9CabRanking(ranked.Select(candidate => new Fm9CabRankingEntry(candidate.Name, candidate.GapDecibels,
					candidate.GapWithoutEqDecibels,
					new Dictionary<string, string>
					{
						["Bank"] = candidate.Bank.ToString(CultureInfo.InvariantCulture),
						["Cab Type"] = candidate.Type.ToString(CultureInfo.InvariantCulture)
					},
					new Dictionary<string, string>(Fractal.Application.Fm9ParametricEq.ToParameters(candidate.Eq))
					{
						["Type 1"] = "Shelving", ["Type 2"] = "Peaking", ["Type 3"] = "Peaking", ["Type 4"] = "Peaking", ["Type 5"] = "Shelving"
					})).ToArray());
			});
	}

	public sealed record Fm9CabRankingEntry(string Cab, double GapDecibels, double GapWithoutEqDecibels,
		IReadOnlyDictionary<string, string> CabSettings, IReadOnlyDictionary<string, string> ParametricEqSettings);

	public sealed record Fm9CabRanking(IReadOnlyList<Fm9CabRankingEntry> Cabs);
}
