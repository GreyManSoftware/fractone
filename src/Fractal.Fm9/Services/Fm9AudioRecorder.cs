using Fractal.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Fractal.Fm9
{
	public sealed record Fm9Recording(string Path, string Source, int SampleRate, int Channels, double Seconds, double PeakDecibels);

	// Records a take for tone matching: the FM9's own USB audio (a capture device named like "FM9"; its first two channels
	// carry Output 1, the processed sound), or whatever the PC is playing (to capture a target track). Windows only.
	public static class Fm9AudioRecorder
	{
		// About -50 dBFS: quiet enough to catch a fade-in, loud enough to ignore idle hiss before playing starts.
		public const float SoundThreshold = 0.003f;
		public static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

		public static IReadOnlyList<string> CaptureDevices()
		{
			if (!OperatingSystem.IsWindows()) return [];
			using MMDeviceEnumerator devices = new();
			return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(device => device.FriendlyName).ToArray();
		}

		// source: "fm9" (or part of a capture device's name) or "system" (what the PC plays). firstChannel is 1-based.
		public static async Task<Fm9Recording> RecordAsync(string path, string source, TimeSpan duration, bool waitForSound,
			int firstChannel, int channelCount, CancellationToken cancellationToken)
		{
			if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recording uses Windows audio.");
			if (duration <= TimeSpan.Zero || duration > Longest) throw new ArgumentOutOfRangeException(nameof(duration), "Record 1 second to 10 minutes.");
			if (channelCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channelCount), "Record one or two channels.");
			using MMDeviceEnumerator devices = new();
			bool loopback = source.Equals("system", StringComparison.OrdinalIgnoreCase);
			string name = source.Equals("fm9", StringComparison.OrdinalIgnoreCase) ? "FM9" : source;
			MMDevice device = loopback
				? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
				: devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
					.FirstOrDefault(candidate => candidate.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
					?? throw new InvalidOperationException($"No recording device matching '{name}'. Devices: {string.Join(", ", CaptureDevices())}.");
			using WasapiCapture capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
			WaveFormat format = capture.WaveFormat;
			if (format.BitsPerSample != 32 || format.Encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible))
				throw new NotSupportedException($"Unsupported recording format: {format}.");
			int deviceChannels = format.Channels;
			if (firstChannel < 1 || firstChannel + channelCount - 1 > deviceChannels)
				throw new ArgumentOutOfRangeException(nameof(firstChannel), $"{device.FriendlyName} has {deviceChannels} channels.");
			long wanted = (long)Math.Round(duration.TotalSeconds * format.SampleRate);
			List<float>[] channels = Enumerable.Range(0, channelCount).Select(_ => new List<float>()).ToArray();
			bool started = !waitForSound;
			long captured = 0;
			float peak = 0;
			TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
			object gate = new();
			capture.DataAvailable += (_, data) =>
			{
				lock (gate)
				{
					int frameBytes = 4 * deviceChannels;
					for (int offset = 0; offset + frameBytes <= data.BytesRecorded && captured < wanted; offset += frameBytes)
					{
						if (!started)
						{
							for (int channel = 0; channel < channelCount && !started; channel++)
								started = Math.Abs(BitConverter.ToSingle(data.Buffer, offset + ((firstChannel - 1 + channel) * 4))) > SoundThreshold;
							if (!started) continue;
						}
						for (int channel = 0; channel < channelCount; channel++)
						{
							float sample = BitConverter.ToSingle(data.Buffer, offset + ((firstChannel - 1 + channel) * 4));
							channels[channel].Add(sample);
							peak = Math.Max(peak, Math.Abs(sample));
						}
						captured++;
					}
					if (captured >= wanted) finished.TrySetResult();
				}
			};
			capture.RecordingStopped += (_, stopped) =>
			{
				if (stopped.Exception is not null) finished.TrySetException(stopped.Exception);
				else finished.TrySetResult();
			};
			// Loopback delivers nothing while nothing plays, which would stall the take and squeeze out pauses; playing
			// silence on the same device keeps it running in real time, inaudibly.
			using WasapiOut? keepAlive = loopback ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100) : null;
			if (keepAlive is not null)
			{
				keepAlive.Init(new SilenceProvider(format));
				keepAlive.Play();
			}
			capture.StartRecording();
			// The take's own length plus a minute (to wait for sound) at most, so a silent device cannot hang the call.
			using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				limit.CancelAfter(duration + TimeSpan.FromMinutes(1));
				using (limit.Token.Register(() => finished.TrySetResult()))
					await finished.Task;
			}
			capture.StopRecording();
			keepAlive?.Stop();
			cancellationToken.ThrowIfCancellationRequested();
			if (captured == 0)
				throw new InvalidOperationException($"No sound came from {device.FriendlyName}. Is it playing, and is the right device chosen?");
			string fullPath = System.IO.Path.GetFullPath(path);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
			using (FileStream stream = File.Create(fullPath))
				Fractal.Core.WaveFileWriter.WritePcm24(new MultichannelAudioBuffer(format.SampleRate, channels), stream);
			return new(fullPath, device.FriendlyName, format.SampleRate, channelCount, (double)captured / format.SampleRate,
				peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity);
		}
	}
}
