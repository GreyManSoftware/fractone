using System.ComponentModel;
using System.Globalization;
using Fractal.Core;
using Fractal.Fm9;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	// Measurement is null when the capture was too quiet to measure.
	public sealed record Fm9PlaybackCapture(Fm9Recording Recording, AudioMeasurement? Measurement, IReadOnlyList<string> Hints);

	[McpServerToolType]
	public sealed class CaptureTools(Fm9Context context)
	{
		[McpServerTool(Name = "capture_playback", ReadOnly = true, OpenWorld = false)]
		[Description("Records what the PC is playing (the default playback device, as Audacity's loopback does) to a WAV in the work folder, to use as a tone-match target: a track or video the user plays in a browser or player. Waits for sound, so start it, then play (in the same turn, or ask the user to press play). Reports length, peak, loudness and band balance, and flags a recording that looks like a full mix rather than an isolated guitar. Other sounds the PC makes (notifications, ads) are recorded too.")]
		public Task<Fm9PlaybackCapture> CapturePlayback(
			[Description("Seconds to record (1-600): the length of the part, e.g. end minus start.")] double seconds,
			[Description("Wait for sound before starting, default true; false starts straight away.")] bool? waitForSound = null,
			[Description("Output .wav; default targets/playback <time>.wav.")] string? file = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				string path = file is null ? Path.Combine(context.Settings.WorkFolder, "targets", $"playback {stamp}.wav") : context.Settings.Resolve(file);
				Fm9Recording recording = await Fm9AudioRecorder.RecordAsync(path, "system", TimeSpan.FromSeconds(seconds), waitForSound != false, 1, 2, cancellationToken);
				MultichannelAudioBuffer audio;
				using (FileStream stream = File.OpenRead(recording.Path)) audio = WaveFileReader.ReadChannels(stream);
				try
				{
					AudioMeasurement measured = AudioMeasurer.Measure(audio);
					return new Fm9PlaybackCapture(recording, measured, ToneTools.Hints(measured));
				}
				catch (InvalidOperationException)
				{
					return new Fm9PlaybackCapture(recording, null, ["Too quiet to measure: check the player's volume and that it plays on the default device."]);
				}
			});
	}
}
