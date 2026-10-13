using System.Globalization;
using Fractal.Companion.Protocol;
using Fractal.Core;
using NAudio.Wave;
using NWaves.Operations;
using NWaves.Signals;

namespace Fractal.Fm9
{
	public sealed record Fm9ReampOptions
	{
		// The FM9's ASIO driver as Windows names it.
		public string Driver { get; init; } = Fm9Reamper.DefaultDriver;
		// The first of the two USB channels the DI plays on (7/8 feed the preset's Input 4, the USB input).
		public int OutputChannel { get; init; } = 7;
		// The first of the two USB channels recorded (1/2 carry Output 1).
		public int InputChannel { get; init; } = 1;
		// Seconds cut from the start of the DI; null finds the first note and keeps 50 ms before it.
		public double? TrimSeconds { get; init; }
		// Seconds recorded after the DI ends, for decays, delays and reverb tails.
		public double TailSeconds { get; init; } = 1.5;
		// Seconds to let the FM9 settle after a scene change.
		public double SettleSeconds { get; init; } = 0.5;
		// An isolated stem of the target, to compare each take with and, with Match, to fit an EQ per scene.
		public string? Stem { get; init; }
		public bool Match { get; init; }
		// Level before the takes, from short probes of the DI's loudest few seconds: scenes balanced by loudness
		// (LevelTarget, default "match" the first scene) and the loudest peaking at PeakTarget (default about a quarter into
		// the meters' red), probing again until within 0.3 dB. The master moves only when all eight scenes are levelled;
		// KeepPresetLevel only balances. Applied live. LevelOnly stops after levelling, without full takes.
		public bool Level { get; init; }
		public string? LevelTarget { get; init; }
		public double? PeakTarget { get; init; }
		public bool KeepPresetLevel { get; init; }
		public IReadOnlyDictionary<int, double>? LevelOffsets { get; init; }
		public bool LevelOnly { get; init; }
		public double ProbeSeconds { get; init; } = 4;
	}

	// One probe of one scene: what it measured, and the trim and master it played with.
	public sealed record Fm9LevelProbe(int Round, int Scene, double Lufs, double PeakDecibels, bool Clipped, double Trim, double Master);

	// Measurement and Level are null when the take came back silent (the DI did not reach the preset).
	public sealed record Fm9ReampTake(int Scene, string File, AudioMeasurement? Measurement, AudioComparison? Comparison, Fm9SceneLevel? Level);

	// SampleRate is the FM9's: the takes are recorded at the rate its USB audio runs at, the DI resampled to it if need be.
	public sealed record Fm9ReampResult(string Folder, int SampleRate, double TrimmedSeconds, IReadOnlyList<Fm9ReampTake> Takes,
		Fm9PresetLevelPlan? Levelling, IReadOnlyList<Fm9LevelProbe>? Probes, Fm9EditResult? Matched, IReadOnlyList<string> Notes);

	// Re-amping through the FM9: for each scene, select it over MIDI, then play the DI into the preset's USB input and record
	// Output 1 in one ASIO stream, so play and record share one clock and the take lines up with the DI sample for sample.
	public sealed class Fm9Reamper
	{
		public const string DefaultDriver = "FM9 USB Audio Device";
		private readonly Fm9Live _live;
		private readonly Fm9Device _device;
		private readonly Func<float[][], int, Fm9ReampOptions, CancellationToken, Task<MultichannelAudioBuffer>> _stream;

		// stream plays the DI and returns what was recorded, at whatever rate the device ran (the ASIO stream by default;
		// tests pass their own).
		public Fm9Reamper(Fm9Live live, Fm9Device device, Func<float[][], int, Fm9ReampOptions, CancellationToken, Task<MultichannelAudioBuffer>>? stream = null)
		{
			_live = live ?? throw new ArgumentNullException(nameof(live));
			_device = device ?? throw new ArgumentNullException(nameof(device));
			_stream = stream ?? AsioStreamAsync;
		}

		public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

		// Each step as it happens, for progress reports.
		public Action<string>? Log { get; init; }

		public static IReadOnlyList<string> Drivers() => OperatingSystem.IsWindows() ? AsioOut.GetDriverNames() : [];

		public async Task<Fm9ReampResult> RunAsync(string di, IReadOnlyList<int> scenes, string folder, Fm9ReampOptions options, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(options);
			if (scenes is not { Count: > 0 }) throw new ArgumentException("Name at least one scene.", nameof(scenes));
			if (scenes.Any(scene => scene is < 1 or > 8)) throw new ArgumentOutOfRangeException(nameof(scenes), "Scenes are 1 to 8.");
			List<string> notes = [];

			// The preset playing must listen to USB: Input 4 is the FM9's USB input block.
			try { _ = await _live.GetAsync(new("Input", 4), null, ["Level"], cancellationToken); }
			catch (ArgumentException)
			{
				throw new InvalidOperationException("The preset playing has no Input 4 (the USB input), so the DI would not reach it. Add Input 4 on the FM9 or in FM9-Edit (or use a preset that has one), then re-amp.");
			}

			MultichannelAudioBuffer source;
			source = AudioFiles.Read(di);
			float[][] dry = Stereo(source);
			int start = options.TrimSeconds is double seconds ? (int)Math.Round(seconds * source.SampleRate) : FirstNote(dry, source.SampleRate);
			start = Math.Clamp(start, 0, dry[0].Length - 1);
			dry = dry.Select(channel => channel[start..]).ToArray();
			if (start > 0) notes.Add(string.Create(CultureInfo.InvariantCulture, $"Trimmed {start / (double)source.SampleRate:0.00} s of silence from the start of the DI."));

			MultichannelAudioBuffer? stem = null;
			if (options.Stem is { } stemPath)
				stem = AudioFiles.Read(stemPath);

			Directory.CreateDirectory(folder);
			Fm9ReadResult before = await _device.ReadEditBufferAsync(Path.Combine(folder, "preset before re-amp.syx"), cancellationToken);
			int showing = (await _live.GetAsync(new("Output", 1), null, ["Level"], cancellationToken)).Scene;
			string matchBase = before.Path;
			Fm9PresetLevelPlan? levelling = null;
			List<Fm9LevelProbe>? probes = null;
			if (options.Level)
			{
				(levelling, probes) = await LevelAsync(dry, source.SampleRate, scenes, before.Path, showing, options, notes, cancellationToken);
				matchBase = (await _device.ReadEditBufferAsync(Path.Combine(folder, "preset levelled.syx"), cancellationToken)).Path;
				notes.Add("Levelled live: undo takes it back, store_edit_buffer keeps it.");
				if (options.LevelOnly)
				{
					await _live.SelectSceneAsync(showing, cancellationToken);
					return new(Path.GetFullPath(folder), source.SampleRate, start / (double)source.SampleRate, [], levelling, probes, null, notes);
				}
			}
			Log?.Invoke($"Recording the full take{(scenes.Count == 1 ? "" : "s")}.");
			(IReadOnlyList<Fm9ReampTake> takes, int rate) = await TakesAsync(dry, source.SampleRate, scenes, folder, "", stem, options, notes, cancellationToken);

			Fm9EditResult? matched = null;
			if (options.Match)
			{
				if (options.Stem is null) throw new ArgumentException("Matching needs a stem to match against.");
				string current = matchBase;
				foreach (Fm9ReampTake take in takes.Where(take => take.Measurement is not null))
				{
					string output = Path.Combine(folder, $"matched after scene {take.Scene}.syx");
					Fm9ToneMatchResult result = Fm9ToneMatch.MatchScene(new(current, output, take.Scene, take.File, options.Stem)
					{
						NewScene = take.Scene,
						NewSceneName = Fm9Presets.Load(current).GetSceneName(take.Scene) is { Length: > 0 } name ? name : $"Scene {take.Scene}",
						Preview = Path.Combine(folder, $"scene {take.Scene} matched preview.wav")
					});
					if (result.Edit is null)
					{
						notes.Add($"Scene {take.Scene} was not matched: {result.Summary}");
						continue;
					}
					matched = result.Edit;
					current = result.Edit.Output;
				}
				if (matched is not null)
				{
					string final = Path.Combine(folder, "matched.syx");
					File.Copy(matched.Output, final, overwrite: true);
					matched = matched with { Output = final };
					notes.Add("Each matched scene plays its own Parametric EQ 1 channel in matched.syx; audition it to compare.");
				}
			}
			await _live.SelectSceneAsync(showing, cancellationToken);
			return new(Path.GetFullPath(folder), rate, start / (double)source.SampleRate, takes, levelling, probes, matched, notes);
		}

		// Levels from short probes of the DI's loudest seconds, a few rounds at most: probe every scene, plan, apply, probe
		// again, until no change of 0.3 dB or more is left. A clipped probe hides how loud the scene really is, so it counts
		// as 6 dB louder than measured, which gets the next round out of clipping.
		private async Task<(Fm9PresetLevelPlan Plan, List<Fm9LevelProbe> Probes)> LevelAsync(float[][] dry, int rate, IReadOnlyList<int> scenes,
			string presetPath, int showing, Fm9ReampOptions options, List<string> notes, CancellationToken cancellationToken)
		{
			float[][] probe = LoudestWindow(dry, rate, options.ProbeSeconds);
			Fm9ReampOptions probing = options with { TailSeconds = 0.3 };
			IReadOnlyDictionary<int, string> channels = Fm9PresetLevelling.OutputChannels(presetPath, scenes);
			double? peakTarget = options.KeepPresetLevel ? null : options.PeakTarget ?? Fm9PresetLevelling.DefaultPeakTarget;
			List<Fm9LevelProbe> probes = [];
			Fm9PresetLevelPlan? plan = null;
			for (int round = 1; round <= 5; round++)
			{
				IReadOnlyDictionary<int, (string Channel, double Trim, double Master)> current = await Fm9PresetLevelling.CurrentAsync(_live, channels, cancellationToken);
				List<Fm9SceneLevelState> states = [];
				foreach (int scene in scenes)
				{
					await _live.SelectSceneAsync(scene, cancellationToken);
					await Wait(TimeSpan.FromSeconds(options.SettleSeconds), cancellationToken);
					MultichannelAudioBuffer take = await _stream(probe, rate, probing, cancellationToken);
					AudioMeasurement measured;
					try { measured = AudioMeasurer.Measure(take); }
					catch (InvalidOperationException)
					{
						throw new InvalidOperationException($"Scene {scene} is silent: check that Input 4 is cabled into its signal path and the Output is not muted.");
					}
					probes.Add(new(round, scene, Math.Round(measured.IntegratedLufs, 1), Math.Round(measured.PeakDecibels, 1), measured.Clipped, current[scene].Trim, current[scene].Master));
					Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
						$"Probe {round}, scene {scene}: {measured.IntegratedLufs:0.0} LUFS, peak {measured.PeakDecibels:0.0} dBFS{(measured.Clipped ? ", clipping" : "")} (trim {current[scene].Trim:+0.0;-0.0}, master {current[scene].Master:+0.0;-0.0})."));
					double hidden = measured.Clipped ? 6 : 0;
					states.Add(new(scene, measured.IntegratedLufs + hidden, measured.PeakDecibels + hidden, current[scene].Trim, current[scene].Master));
				}
				Fm9PresetLevelPlan next = Fm9PresetLevelling.Plan(states, options.LevelTarget, options.LevelOffsets, peakTarget);
				bool settled = Math.Abs(next.MasterChange) < 0.3 && next.Scenes.All(step => Math.Abs(step.NewTrim - step.CurrentTrim) < 0.3);
				if (plan is null || !settled) plan = next;
				if (settled)
				{
					Log?.Invoke($"Levelled after {round} probe{(round == 1 ? "" : "s")} per scene.");
					break;
				}
				string trims = string.Join(", ", next.Scenes.Where(step => step.NewTrim != step.CurrentTrim)
					.Select(step => string.Create(CultureInfo.InvariantCulture, $"scene {step.Scene} trim {step.CurrentTrim:+0.0;-0.0} -> {step.NewTrim:+0.0;-0.0}")));
				string master = next.MasterChange != 0 ? string.Create(CultureInfo.InvariantCulture, $" master {next.MasterChange:+0.0;-0.0} dB") : "";
				Log?.Invoke($"Setting {trims}{master}.");
				await Fm9PresetLevelling.ApplyAsync(_live, next, current, showing, cancellationToken);
				if (round == 5) notes.Add("Levelling stopped after five probes per scene without settling within 0.3 dB.");
			}
			return (plan!, probes);
		}

		// The probe: the stretch of the DI around its highest peak, which sets where the levelled take peaks. (The loudest
		// stretch by RMS missed it on hardware: the full take then peaked 0.9 dB over the target.)
		internal static float[][] LoudestWindow(float[][] dry, int rate, double seconds)
		{
			int length = Math.Min(dry[0].Length, (int)(seconds * rate));
			if (length >= dry[0].Length) return dry;
			int peak = 0;
			float highest = -1;
			for (int index = 0; index < dry[0].Length; index++)
			{
				float value = dry.Max(channel => Math.Abs(channel[index]));
				if (value > highest) (highest, peak) = (value, index);
			}
			int start = Math.Clamp(peak - (length / 2), 0, dry[0].Length - length);
			return dry.Select(channel => channel[start..(start + length)]).ToArray();
		}

		// Each scene in turn: select it, let it settle, play the DI and record, then save and measure the take.
		private async Task<(IReadOnlyList<Fm9ReampTake> Takes, int Rate)> TakesAsync(float[][] dry, int sourceRate, IReadOnlyList<int> scenes,
			string folder, string suffix, MultichannelAudioBuffer? stem, Fm9ReampOptions options, List<string> notes, CancellationToken cancellationToken)
		{
			List<Fm9ReampTake> takes = [];
			int rate = sourceRate;
			foreach (int scene in scenes)
			{
				await _live.SelectSceneAsync(scene, cancellationToken);
				await Wait(TimeSpan.FromSeconds(options.SettleSeconds), cancellationToken);
				MultichannelAudioBuffer take = await _stream(dry, sourceRate, options, cancellationToken);
				if (take.SampleRate != sourceRate && takes.Count == 0 && suffix.Length == 0)
					notes.Add($"The FM9's USB audio runs at {take.SampleRate} Hz, so the {sourceRate} Hz DI was resampled to it; the takes are at {take.SampleRate} Hz.");
				rate = take.SampleRate;
				string file = Path.Combine(folder, $"scene {scene}{suffix}.wav");
				using (FileStream stream = File.Create(file)) Fractal.Core.WaveFileWriter.WritePcm24(take, stream);
				AudioMeasurement measured;
				try { measured = AudioMeasurer.Measure(take); }
				catch (InvalidOperationException)
				{
					takes.Add(new(scene, file, null, null, null));
					notes.Add($"Scene {scene}'s take is silent: check that Input 4 is cabled into the scene's signal path and the Output is not muted.");
					continue;
				}
				takes.Add(new(scene, file, measured, stem is null ? null : AudioMeasurer.Compare(take, stem), new(scene, measured.IntegratedLufs, measured.PeakDecibels)));
				if (measured.Clipped) notes.Add($"Scene {scene}'s take{suffix} clips: lower the Output level or the DI before trusting it.");
			}
			return (takes, rate);
		}

		// The first moment the DI rises above about -50 dBFS, less 50 ms.
		internal static int FirstNote(float[][] channels, int rate)
		{
			const float Threshold = 0.003f;
			for (int index = 0; index < channels[0].Length; index++)
				if (channels.Any(channel => Math.Abs(channel[index]) > Threshold))
					return Math.Max(0, index - (rate / 20));
			throw new InvalidOperationException("The DI is silent.");
		}

		private static float[][] Stereo(MultichannelAudioBuffer audio) => audio.Channels.Count switch
		{
			0 => throw new InvalidDataException("The DI has no channels."),
			1 => [[.. audio.Channels[0]], [.. audio.Channels[0]]],
			_ => [[.. audio.Channels[0]], [.. audio.Channels[1]]]
		};

		// One ASIO stream: the DI (and a tail of silence) out of the chosen output pair, the chosen input pair recorded, on a
		// single-threaded apartment as ASIO's COM drivers expect.
		private static Task<MultichannelAudioBuffer> AsioStreamAsync(float[][] dry, int rate, Fm9ReampOptions options, CancellationToken cancellationToken)
		{
			if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Re-amping uses the FM9's ASIO driver on Windows.");
			TaskCompletionSource<MultichannelAudioBuffer> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Thread thread = new(() =>
			{
				try { result.TrySetResult(RunAsio(dry, rate, options, cancellationToken)); }
				catch (Exception exception) { result.TrySetException(exception); }
			});
			thread.SetApartmentState(ApartmentState.STA);
			thread.IsBackground = true;
			thread.Start();
			return result.Task;
		}

		private static MultichannelAudioBuffer RunAsio(float[][] dry, int rate, Fm9ReampOptions options, CancellationToken cancellationToken)
		{
			AsioOut asio;
			try { asio = new AsioOut(options.Driver); }
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
			{
				throw new InvalidOperationException($"The ASIO driver '{options.Driver}' could not be opened ({exception.Message}). Close Reaper or any other program using it. ASIO drivers here: {string.Join(", ", Drivers())}.", exception);
			}
			using (asio)
			{
				// The FM9's USB audio runs at one rate at a time (48 kHz here, even with Reaper set to 44.1 earlier): play at
				// whichever the driver takes, resampling the DI when it differs.
				if (!asio.IsSampleRateSupported(rate))
				{
					int device = new[] { 48_000, 44_100 }.FirstOrDefault(asio.IsSampleRateSupported);
					if (device == 0) throw new NotSupportedException($"The ASIO driver '{options.Driver}' runs at neither 44.1 nor 48 kHz.");
					dry = Resample(dry, rate, device);
					rate = device;
				}
				int total = dry[0].Length + (int)(options.TailSeconds * rate);
				if (options.OutputChannel + 1 > asio.DriverOutputChannelCount || options.InputChannel + 1 > asio.DriverInputChannelCount)
					throw new ArgumentException($"The driver has {asio.DriverOutputChannelCount} outputs and {asio.DriverInputChannelCount} inputs.");
				asio.ChannelOffset = options.OutputChannel - 1;
				asio.InputChannelOffset = options.InputChannel - 1;
				DiProvider provider = new(dry, rate, total);
				float[] left = new float[total], right = new float[total];
				int recorded = 0;
				using ManualResetEventSlim done = new(false);
				float[] samples = [];
				asio.AudioAvailable += (_, e) =>
				{
					// One buffer reused across callbacks; the driver's block size is fixed while it runs.
					int needed = e.SamplesPerBuffer * e.InputBuffers.Length;
					if (samples.Length < needed) samples = new float[needed];
					int frames = e.GetAsInterleavedSamples(samples) / 2;
					for (int frame = 0; frame < frames && recorded < total; frame++, recorded++)
					{
						left[recorded] = samples[frame * 2];
						right[recorded] = samples[(frame * 2) + 1];
					}
					if (recorded >= total) done.Set();
				};
				asio.InitRecordAndPlayback(provider, 2, rate);
				asio.Play();
				TimeSpan limit = TimeSpan.FromSeconds((double)total / rate + 10);
				if (!done.Wait(limit, cancellationToken))
					throw new TimeoutException("The FM9's ASIO stream stopped delivering audio.");
				asio.Stop();
				return new MultichannelAudioBuffer(rate, [left, right]);
			}
		}

		internal static float[][] Resample(float[][] channels, int from, int to) =>
			channels.Select(channel => Operation.Resample(new DiscreteSignal(from, channel), to).Samples).ToArray();

		// The DI as 32-bit float stereo, then silence until the tail ends.
		private sealed class DiProvider(float[][] dry, int rate, int total) : IWaveProvider
		{
			private int _position;

			public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);

			public int Read(byte[] buffer, int offset, int count)
			{
				int frames = count / 8;
				for (int frame = 0; frame < frames; frame++, _position++)
				{
					float l = _position < dry[0].Length ? dry[0][_position] : 0, r = _position < dry[1].Length ? dry[1][_position] : 0;
					BitConverter.TryWriteBytes(buffer.AsSpan(offset + (frame * 8)), _position < total ? l : 0);
					BitConverter.TryWriteBytes(buffer.AsSpan(offset + (frame * 8) + 4), _position < total ? r : 0);
				}
				return frames * 8;
			}
		}
	}
}
