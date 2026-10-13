using System.Globalization;
using Fractal.Application;
using Fractal.Companion.Protocol;
using Fractal.Core;

namespace Fractal.Fm9
{
	public sealed record Fm9ToneMatchRequest(
		string Input,
		string Output,
		int RecordedScene,
		string WetRecording,
		string TargetRecording)
	{
		// Where to put the fitted bands: a Parametric EQ 1 channel. Defaults to the first channel no scene plays.
		public string? EqChannel { get; init; }
		// A scene that copies the recorded one but plays the fitted channel; none leaves the scenes as they were.
		public int? NewScene { get; init; }
		public string NewSceneName { get; init; } = "Matched";
		// The matched take, at the take's own loudness, so it can be compared with the recording directly.
		public string? Preview { get; init; }
		public double WetStartSeconds { get; init; }
		public double TargetStartSeconds { get; init; }
		public double LengthSeconds { get; init; } = WetToneMatcher.MaximumDurationSeconds;
	}

	public sealed record Fm9ToneMatchResult(
		string Status,
		string Summary,
		double? DeviationBeforeDecibels,
		double? DeviationAfterDecibels,
		IReadOnlyList<string> RecordedThrough,
		IReadOnlyList<string> Fitted,
		IReadOnlyList<string> Warnings,
		Fm9EditResult? Edit,
		string? Preview);

	// FracAI's Parametric EQ match for one take: a recording of the preset played in a scene, against the target recording.
	// The fit starts from the EQ that scene played, the bands go into a Parametric EQ channel with a level that keeps the
	// take's loudness, and optionally a new scene plays it.
	public static class Fm9ToneMatch
	{
		public static Fm9ToneMatchResult MatchScene(Fm9ToneMatchRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);
			CultureInfo inv = CultureInfo.InvariantCulture;
			Fm9UploadedPreset preset = Fm9Presets.Load(request.Input);
			ToneDesignProposal proposal = preset.ToProposal();
			if (request.RecordedScene is < 1 or > Fm9Presets.SceneCount)
				throw new ArgumentOutOfRangeException(nameof(request), "The recorded scene is 1 to 8.");
			ToneDesignBlockProposal peq = Fm9ParametricEq.Find(proposal) ?? throw new InvalidOperationException(
				"The preset has no Parametric EQ 1 to hold the match. Add one into a shunt or empty cell first.");
			Fm9UploadedBlock peqBlock = preset.Find(Fm9ParametricEq.BlockType, 1)!;
			string channel = request.EqChannel is { } asked ? Fm9Names.Letter(Fm9Names.Channel(asked)) : FreeChannel(preset, peqBlock);
			IReadOnlyList<ParametricEqBand> recorded = Fm9ParametricEq.Recorded(proposal, request.RecordedScene);
			ToneDesignSceneProposal source = proposal.Scenes[request.RecordedScene - 1];
			ToneDesignSceneBlockSetting sourcePeq = source.BlockSettings.Single(setting =>
				setting.BlockType == Fm9ParametricEq.BlockType && setting.Instance == 1);
			double recordedLevel = sourcePeq.IsBypassed ? 0 :
				double.Parse(peq.GetParameters(sourcePeq.Channel).GetValueOrDefault("Level", "0"), inv);

			MultichannelAudioBuffer wet = Read(request.WetRecording, request.WetStartSeconds, request.LengthSeconds);
			MultichannelAudioBuffer target = Read(request.TargetRecording, request.TargetStartSeconds, request.LengthSeconds);
			WetToneMatchResult match = new WetToneMatcher().MatchParametric(wet, target,
				new ParametricEqMatchSettings(recorded, Fm9ParametricEq.Limits, Fm9ParametricEq.MaximumFitGainDecibels));
			string[] recordedText = recorded.Select(Describe).ToArray();
			if (!match.HasPreview)
				return new(match.Status.ToString(), match.Summary, match.BeforeSpectralErrorDecibels, match.AfterSpectralErrorDecibels,
					recordedText, [], match.Warnings, null, null);

			// Makeup that keeps the take's level: the filtered take is the preview without the matcher's level move.
			double makeup = match.UserActiveRmsDecibels - (match.PreviewActiveRmsDecibels!.Value - match.AppliedLevelGainDecibels);
			string? previewPath = null;
			if (request.Preview is { } preview)
			{
				double keep = match.UserActiveRmsDecibels - match.PreviewActiveRmsDecibels.Value;
				float peak = match.Preview!.Channels.SelectMany(samples => samples).Max(sample => Math.Abs(sample));
				double gain = Math.Min(keep, -1 - (20 * Math.Log10(Math.Max(peak, 1e-9f))));
				float multiplier = (float)Math.Pow(10, gain / 20);
				MultichannelAudioBuffer levelled = new(match.Preview.SampleRate, match.Preview.Channels
					.Select(samples => (IReadOnlyList<float>)samples.Select(sample => sample * multiplier).ToArray()).ToArray());
				previewPath = Path.GetFullPath(preview);
				Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
				using FileStream stream = File.Create(previewPath);
				WaveFileWriter.WritePcm24(levelled, stream);
			}

			Dictionary<string, string> parameters = new(peq.GetParameters(channel), StringComparer.Ordinal);
			foreach ((string name, string value) in Fm9ParametricEq.ToParameters(match.ParametricEq))
				parameters[name] = value;
			foreach ((string name, string value) in new[] { ("Type 1", "Shelving"), ("Type 2", "Peaking"), ("Type 3", "Peaking"), ("Type 4", "Peaking"), ("Type 5", "Shelving") })
				parameters[name] = value;
			parameters["Level"] = Math.Round(recordedLevel + makeup, 1).ToString(inv);
			peq = peq.WithChannel(channel, parameters, peq.GetModelSelection(channel));
			List<ToneDesignSceneProposal> scenes = proposal.Scenes.ToList();
			List<string> changes = [$"Parametric EQ 1 {channel}: {string.Join("; ", match.ParametricEq.Select(Describe))}, level {parameters["Level"]} dB (keeps the take's loudness)"];
			if (request.NewScene is int newScene)
			{
				if (newScene is < 1 or > Fm9Presets.SceneCount) throw new ArgumentOutOfRangeException(nameof(request), "The new scene is 1 to 8.");
				Fm9PresetBodySceneAuthor.ValidateSceneName(request.NewSceneName);
				scenes[newScene - 1] = source with
				{
					Name = request.NewSceneName,
					BlockSettings = source.BlockSettings.Select(setting => setting.BlockType == Fm9ParametricEq.BlockType && setting.Instance == 1
						? setting with { Channel = channel, IsBypassed = false } : setting).ToArray()
				};
				changes.Add($"Scene {newScene} '{request.NewSceneName}' copies scene {request.RecordedScene} and plays Parametric EQ 1 {channel}");
			}
			proposal = proposal with
			{
				Blocks = proposal.Blocks.Select(block => block.BlockType == Fm9ParametricEq.BlockType && block.Instance == 1 ? peq : block).ToArray(),
				Scenes = scenes
			};
			Fm9EditResult edit = Fm9Presets.Save(Fm9UploadedPreset.Apply(proposal), request.Input, request.Output, changes);
			return new(match.Status.ToString(), match.Summary, match.BeforeSpectralErrorDecibels, match.AfterSpectralErrorDecibels,
				recordedText, match.ParametricEq.Select(Describe).ToArray(), match.Warnings, edit, previewPath);
		}

		// The first Parametric EQ channel no scene plays, so a match never changes what an existing scene sounds like.
		private static string FreeChannel(Fm9UploadedPreset preset, Fm9UploadedBlock peq)
		{
			HashSet<int> played = Enumerable.Range(1, Fm9Presets.SceneCount).Select(scene => (int)preset.GetSceneState(peq, scene).Channel).ToHashSet();
			for (int channel = 0; channel < Fm9UploadedPreset.ChannelsOf(peq); channel++)
				if (!played.Contains(channel)) return Fm9Names.Letter(channel);
			throw new InvalidOperationException("Every Parametric EQ channel is played by a scene; name the channel to overwrite.");
		}

		private static MultichannelAudioBuffer Read(string path, double start, double length)
		{
			return AudioFiles.Read(path, start, Math.Min(length, WetToneMatcher.MaximumDurationSeconds), out _);
		}

		private static string Describe(ParametricEqBand band) => string.Create(CultureInfo.InvariantCulture,
			$"{WaveToneMatchService.Describe(band.Shape)} {band.FrequencyHz:0} Hz Q {band.Q:0.##} {band.GainDecibels:+0.0;-0.0;0}");
	}
}
