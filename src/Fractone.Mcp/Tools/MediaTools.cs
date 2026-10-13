using System.ComponentModel;
using System.Globalization;
using Fractal.Core;
using Fractal.Fm9;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	public sealed record MediaCaptureResult(MediaCapture Capture, AudioMeasurement? Measurement, IReadOnlyList<string> Hints);

	[McpServerToolType]
	public sealed class MediaTools(Fm9Context context)
	{
		[McpServerTool(Name = "capture_media", ReadOnly = true, OpenWorld = true)]
		[Description("Records the audio of a web page's own main video or audio element, from start to end seconds, in a headless browser on this PC (nothing plays aloud; it takes real time), as a tone-match target. Only capture media you have the right to use; you are responsible for following each site's terms. It never gets past bot checks, CAPTCHAs, sign-in walls or DRM, and never records ads: it waits ads out, declines consent dialogs with the most private option (or stops), and otherwise stops and reports why with a screenshot. First use downloads a headless browser (about 100 MB) into the browserFolder setting. Reports loudness, band balance and whether it looks like a full mix.")]
		public Task<MediaCaptureResult> CaptureMedia(
			[Description("The page's address (http or https).")] string url,
			[Description("Seconds into the media where the part starts.")] double start,
			[Description("Seconds into the media where it ends (at most 600 s after start).")] double end,
			[Description("Output .wav; default targets/media <time>.wav.")] string? file = null,
			IProgress<ProgressNotificationValue>? progress = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				Fm9Settings settings = context.Settings;
				string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				string output = file is null ? Path.Combine(settings.WorkFolder, "targets", $"media {stamp}.wav") : settings.Resolve(file);
				int step = 0;
				MediaCapture capture = await new MediaCapturer(settings.BrowserFolder).CaptureAsync(url, start, end, output,
					Path.Combine(settings.WorkFolder, "targets"),
					message => progress?.Report(new ProgressNotificationValue { Progress = ++step, Message = message }), cancellationToken);
				if (capture.File is null) return new MediaCaptureResult(capture, null, []);
				MultichannelAudioBuffer audio;
				using (FileStream stream = File.OpenRead(capture.File)) audio = WaveFileReader.ReadChannels(stream);
				try
				{
					AudioMeasurement measured = AudioMeasurer.Measure(audio);
					return new MediaCaptureResult(capture, measured, ToneTools.Hints(measured));
				}
				catch (InvalidOperationException)
				{
					return new MediaCaptureResult(capture, null, ["Too quiet to measure."]);
				}
			});
	}
}
