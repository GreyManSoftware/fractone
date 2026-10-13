using System.ComponentModel;
using System.Globalization;
using Fractal.Fm9;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	[McpServerToolType]
	public sealed class ReampTools(Fm9Context context)
	{
		[McpServerTool(Name = "reamp", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Re-amps a DI through the FM9: for each scene, selects it, plays the DI into the preset's USB input (Input 4, USB channels 7/8) and records Output 1 (USB 1/2) in one ASIO stream, so every take lines up with the DI. Each take is saved and measured (loudness, peak, band balance; against a stem if given). With level it first levels live from a few short probes of the DI's loudest seconds (scenes balanced by loudness, the loudest peaking at about a quarter into the output meters' red; the master Level moves only when all eight scenes are levelled, otherwise each scene's own trim), so the full takes come out levelled; otherwise the takes' levels feed level_scenes. With match and a stem, each scene also gets its own fitted Parametric EQ channel in a new .syx. The FM9 plays through the speakers while it runs: tell the user first. Needs the FM9's ASIO driver free (close Reaper; FM9-Edit is fine).")]
		public Task<Fm9ReampResult> Reamp(
			[Description("The DI recording (any common audio file: WAV, AIFF, FLAC, MP3, M4A, OGG, Opus); resampled to the rate the FM9's USB audio runs at if it differs.")] string di,
			[Description("Scenes to re-amp, e.g. [1, 3, 5].")] int[] scenes,
			[Description("A preset file to audition first; default the preset playing.")] string? preset = null,
			[Description("With preset: the slot to audition it on (its unsaved edits are lost); default the preset showing.")] int? slot = null,
			[Description("An isolated stem of the target to compare each take with (any common audio file).")] string? stem = null,
			[Description("With a stem: fit the Parametric EQ per scene into a new .syx.")] bool? match = null,
			[Description("Seconds to cut from the start of the DI; default: up to the first note.")] double? trim = null,
			[Description("Seconds recorded after the DI ends, default 1.5.")] double? tail = null,
			[Description("Folder for the takes (relative to the work folder); default reamp/<preset name> <time>.")] string? folder = null,
			[Description("Level the preset from the takes, live: scenes balanced by loudness, and the whole preset set so its loudest scene peaks at peak dBFS.")] bool? level = null,
			[Description("With level: \"match N\" (default the first scene) or a loudness such as \"-18\".")] string? levelTarget = null,
			[Description("With level: where the loudest scene peaks, dBFS; default -9 (a quarter into the meters' red).")] double? peak = null,
			[Description("With level: true leaves the preset's master Level alone and only balances the scenes.")] bool? keepPresetLevel = null,
			[Description("With level: per-scene dB on top of the target, e.g. {\"5\": 2}.")] Dictionary<string, double>? offsets = null,
			[Description("With level: stop after levelling, without the full takes.")] bool? levelOnly = null,
			IProgress<ProgressNotificationValue>? progress = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				Fm9Device device = context.Device();
				if (preset is not null)
				{
					await device.AuditionAsync(context.Input(preset), slot, cancellationToken);
					context.Live().ResetHistory();
				}
				Fm9Status status = await device.StatusAsync(cancellationToken);
				string target = folder is null
					? Path.Combine(context.Settings.WorkFolder, "reamp", $"{Fm9Device.FileName(status.PresetName)} {DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}")
					: context.Settings.Resolve(folder);
				int step = 0;
				Action<string> log = message => progress?.Report(new ProgressNotificationValue { Progress = ++step, Message = message });
				Fm9Reamper reamper = context.Wait is null
					? new Fm9Reamper(context.Live(), device) { Log = log }
					: new Fm9Reamper(context.Live(), device) { Wait = context.Wait, Log = log };
				return await reamper.RunAsync(context.Input(di), scenes, target, new Fm9ReampOptions
				{
					Stem = stem is null ? null : context.Input(stem),
					Match = match == true,
					TrimSeconds = trim,
					TailSeconds = tail ?? 1.5,
					Level = level == true,
					LevelTarget = levelTarget,
					PeakTarget = peak,
					KeepPresetLevel = keepPresetLevel == true,
					LevelOffsets = offsets?.ToDictionary(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture), pair => pair.Value),
					LevelOnly = levelOnly == true
				}, cancellationToken);
			});
	}
}
