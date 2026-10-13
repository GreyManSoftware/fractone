using System.ComponentModel;
using System.Globalization;
using Fractal.Core;
using Fractal.Fm9;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	public sealed record Fm9LevelMeasurement(int Scene, double Lufs, double ActiveRmsDecibels, double PeakDecibels, bool Clipped,
		double ActiveSeconds, string File, IReadOnlyList<string> Hints);

	public sealed record Fm9LevelResult(Fm9PresetLevelPlan Plan, bool Applied, IReadOnlyList<Fm9LiveResult> Changes);

	// Scene levelling while the player plays: measure each scene from the FM9's own USB output, then set the Output block's
	// per-scene trims. A tool call cannot talk to the player mid-call, so the agent says "play scene 3 now" first.
	[McpServerToolType]
	public sealed class LevelTools(Fm9Context context)
	{
		[McpServerTool(Name = "measure_level", ReadOnly = true, OpenWorld = false)]
		[Description("Measures how loud a scene plays while the player plays it: selects the scene (unsaved edits are kept), waits for sound, records the FM9's Output 1 over USB for the given seconds and reports loudness (LUFS), RMS while playing, peak and clipping. Tell the player to play that scene, the way they will on stage, before calling.")]
		public Task<Fm9LevelMeasurement> MeasureLevel(
			[Description("Scene 1-8 to select first; default the scene playing.")] int? scene = null,
			[Description("Seconds of playing to record, default 12.")] double? seconds = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				int playing = scene is int chosen
					? await context.Live().SelectSceneAsync(chosen, cancellationToken)
					: (await context.Device().StatusAsync(cancellationToken)).Scene;
				string file = Path.Combine(context.Settings.WorkFolder, "levels", $"scene {playing} {DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.wav");
				Fm9Recording recording = await Fm9AudioRecorder.RecordAsync(file, "fm9", TimeSpan.FromSeconds(seconds ?? 12), true, 1, 2, cancellationToken);
				MultichannelAudioBuffer audio;
				using (FileStream stream = File.OpenRead(recording.Path)) audio = WaveFileReader.ReadChannels(stream);
				AudioMeasurement measured = AudioMeasurer.Measure(audio);
				return new Fm9LevelMeasurement(playing, measured.IntegratedLufs, measured.ActiveRmsDecibels, Math.Round(measured.PeakDecibels, 2),
					measured.Clipped, measured.ActiveSeconds, recording.Path, ToneTools.Hints(measured));
			});

		[McpServerTool(Name = "level_scenes", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Levels the preset from measure_level results (or reamp takes' levels), with the Output block, which comes after every other block so one pass lands. The scenes are balanced by loudness with their \"Scene N Level\" trims (target: \"match 1\", the default being the first scene measured, or a loudness such as \"-18\"; offsets add per-scene dB, e.g. {\"5\": 2} for a louder lead). The preset as a whole is set with the master Level so its loudest scene peaks at peak dBFS, by default -9 (about a quarter into the red of the FM9's output meters); keepPresetLevel leaves the master alone. The master takes the part every scene shares, so trims stay near 0. Warns when a trim hits +/-20 dB or a peak would clip, and applies live (undo takes it back; store_edit_buffer keeps it).")]
		public Task<Fm9LevelResult> LevelScenes(
			[Description("The scenes measured: [{scene, lufs, peakDecibels}], from measure_level or reamp.")] Fm9SceneLevel[] levels,
			[Description("\"match N\" (default: the first scene measured) or a loudness such as \"-18\" LUFS.")] string? target = null,
			[Description("Per-scene dB on top of the target, by scene number, e.g. {\"5\": 2}.")] Dictionary<string, double>? offsets = null,
			[Description("Where the loudest scene's peaks land, in dBFS; default -9 (a quarter into the meters' red).")] double? peak = null,
			[Description("true to leave the preset's master Level as it is and only balance the scenes.")] bool? keepPresetLevel = null,
			[Description("false to only show the plan.")] bool? apply = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				Fm9Live live = context.Live();
				Fm9Device device = context.Device();
				Fm9ReadResult buffer = await device.ReadEditBufferAsync(Path.Combine("reads", $"edit buffer {DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.syx"), cancellationToken);
				IReadOnlyDictionary<int, string> channels = Fm9PresetLevelling.OutputChannels(buffer.Path, levels.Select(level => level.Scene));
				IReadOnlyDictionary<int, (string Channel, double Trim, double Master)> current = await Fm9PresetLevelling.CurrentAsync(live, channels, cancellationToken);
				Dictionary<int, double>? perScene = offsets?.ToDictionary(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture), pair => pair.Value);
				Fm9PresetLevelPlan plan = Fm9PresetLevelling.Plan(
					[.. levels.Select(level => new Fm9SceneLevelState(level.Scene, level.Lufs, level.PeakDecibels, current[level.Scene].Trim, current[level.Scene].Master))],
					target, perScene, keepPresetLevel == true ? null : peak ?? Fm9PresetLevelling.DefaultPeakTarget);
				if (apply == false) return new Fm9LevelResult(plan, false, []);
				int showing = (await device.StatusAsync(cancellationToken)).Scene;
				IReadOnlyList<Fm9LiveResult> changes = await Fm9PresetLevelling.ApplyAsync(live, plan, current, showing, cancellationToken);
				return new Fm9LevelResult(plan, changes.Count > 0, changes);
			});
	}
}
