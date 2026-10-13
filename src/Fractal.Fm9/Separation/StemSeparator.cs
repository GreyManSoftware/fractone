using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fractal.Core;

namespace Fractal.Fm9
{
	// Where separation runs: "cuda" (an NVIDIA GPU), "mps" (an Apple silicon GPU) or "cpu".
	public sealed record SeparatorDevice(string Kind, string Name);

	public sealed record SeparationPlan(bool Installed, string Folder, SeparatorDevice Device, double Seconds, double EstimatedSeconds,
		string? Install, IReadOnlyList<string> Notes);

	public sealed record StemSeparation(string Stem, string Rest, int SampleRate, double Start, double Seconds, string Device,
		double ElapsedSeconds, double? StemLufs, double? MixLufs, IReadOnlyList<AudioBandLevel> StemBands, IReadOnlyList<string> Notes);

	// Separates an instrument from a full mix with Demucs (htdemucs_6s, the Demucs model with a guitar stem). An optional
	// add-on, installed on first use into one folder: uv (a pinned, checksummed download) fetches its own Python, then a
	// venv gets the PyTorch build for the hardware found (CUDA, Apple's MPS or CPU) and Demucs; uv's cache, temp files and
	// the model live there too, so nothing lands elsewhere and deleting the folder removes it all.
	public sealed class StemSeparator(string folder)
	{
		public const string Model = "htdemucs_6s";
		public const int ModelRate = 44_100;
		public const double LongestSeconds = 180;
		public static IReadOnlyList<string> Stems { get; } = ["guitar", "bass", "drums", "vocals", "piano", "other"];
		private const string PythonVersion = "3.12";
		private const string TorchVersion = "2.5.1";
		private const string DemucsVersion = "4.0.1";
		private const string UvVersion = "0.12.23";

		// uv's release archives and their SHA-256 digests, as GitHub publishes them.
		private static readonly Dictionary<string, string> UvDigests = new()
		{
			["uv-x86_64-pc-windows-msvc.zip"] = "75d05de6762778c31ee183398de7dd15093fad0ed90b1f236d8205ea5ec00c90",
			["uv-aarch64-pc-windows-msvc.zip"] = "13294e232ececbe709c06b74e6ced06f2a225ea5591476685362f22be56a50d5",
			["uv-aarch64-apple-darwin.tar.gz"] = "50487ae565ccd96e499056b4674d438f4c53170202617b4c759defe0c6a1b544",
			["uv-x86_64-apple-darwin.tar.gz"] = "960da44cb4b73685206ddd250b19e0a117fa41095710c1038f081f5cb613efb4",
			["uv-x86_64-unknown-linux-gnu.tar.gz"] = "9167d72b3319674b6303c4cbe071854bba13ebdf3d76b1a7cbdc175471fb66d6"
		};

		public string Folder { get; } = Path.GetFullPath(folder);
		private string Uv => Path.Combine(Folder, "uv", OperatingSystem.IsWindows() ? "uv.exe" : "uv");
		public string Python => OperatingSystem.IsWindows()
			? Path.Combine(Folder, "venv", "Scripts", "python.exe")
			: Path.Combine(Folder, "venv", "bin", "python");
		private string Marker => Path.Combine(Folder, "installed.json");
		public bool Installed => File.Exists(Python) && File.Exists(Marker);

		// Half the cores on a CPU run, so the PC stays usable.
		public static int CpuThreads => Math.Max(1, System.Environment.ProcessorCount / 2);

		// The installed device if there is one, otherwise what this machine has.
		public async Task<SeparatorDevice> DeviceAsync(CancellationToken cancellationToken)
		{
			if (Installed)
			{
				using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(Marker));
				if (marker.RootElement.TryGetProperty("kind", out JsonElement kind) && marker.RootElement.TryGetProperty("device", out JsonElement name))
					return new(kind.GetString()!, name.GetString()!);
			}
			return await DetectAsync(cancellationToken);
		}

		public static async Task<SeparatorDevice> DetectAsync(CancellationToken cancellationToken)
		{
			if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64) return new("mps", "Apple silicon GPU");
			if (!OperatingSystem.IsMacOS())
			{
				try
				{
					(int code, string output, _) = await RunAsync("nvidia-smi", ["-L"], null, cancellationToken);
					// "GPU 0: NVIDIA GeForce RTX 4090 (UUID: ...)"
					string? first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
					if (code == 0 && first is not null)
						return new("cuda", first.Split(':', 2)[^1].Split(" (UUID", 2)[0].Trim());
				}
				catch (System.ComponentModel.Win32Exception) { }
			}
			return new("cpu", $"CPU ({CpuThreads} of {System.Environment.ProcessorCount} threads)");
		}

		// Start-up and model load, then seconds per second of audio. Measured 2026-10-08 on 60 s of a mix: 10-17 s in all on
		// an RTX 4090; 10 s on a 32-thread desktop CPU at 16 threads (Demucs itself 4.6 s), scaled by threads and padded
		// 1.5x for slower laptop cores. Apple's MPS is a guess until someone measures it.
		internal static double Estimate(string kind, double seconds, int shifts) => Estimate(kind, seconds, shifts, CpuThreads);

		internal static double Estimate(string kind, double seconds, int shifts, int threads) => kind switch
		{
			"cuda" => 10 + (0.05 * seconds * shifts),
			"mps" => 10 + (0.15 * seconds * shifts),
			_ => 8 + (0.12 * seconds * shifts * 16 / threads)
		};

		public async Task<SeparationPlan> PlanAsync(double seconds, int shifts, bool cpu, CancellationToken cancellationToken)
		{
			SeparatorDevice device = cpu ? await CpuDeviceAsync(cancellationToken) : await DeviceAsync(cancellationToken);
			List<string> notes = [];
			if (device.Kind == "cpu") notes.Add("Runs on the CPU: slower than a GPU, but it works.");
			string? install = Installed ? null : device.Kind switch
			{
				"cuda" => "First use installs Python, PyTorch with CUDA and Demucs: about 2.7 GB to download, 5 GB on disk, roughly 5-10 minutes.",
				"mps" => "First use installs Python, PyTorch and Demucs: about 250 MB to download, roughly 2 minutes.",
				_ => "First use installs Python, PyTorch (CPU build) and Demucs: about 400 MB to download, roughly 2-3 minutes."
			};
			if (install is not null) notes.Add($"Installs into {Folder} (the separatorFolder setting).");
			return new(Installed, Folder, device, seconds, Math.Round(Estimate(device.Kind, seconds, shifts)), install, notes);
		}

		private async Task<SeparatorDevice> CpuDeviceAsync(CancellationToken cancellationToken) =>
			(await DeviceAsync(cancellationToken)) is { Kind: "cpu" } device ? device : new("cpu", $"CPU ({CpuThreads} of {System.Environment.ProcessorCount} threads)");

		// Installs uv, Python, PyTorch for the device found and Demucs, then fetches the model. Safe to run again.
		public async Task InstallAsync(Action<string> log, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(log);
			Directory.CreateDirectory(Folder);
			SeparatorDevice device = await DetectAsync(cancellationToken);
			if (!File.Exists(Uv)) await InstallUvAsync(log, cancellationToken);
			if (!File.Exists(Python))
			{
				log($"Creating a Python {PythonVersion} environment in {Path.Combine(Folder, "venv")}.");
				await CheckAsync(Uv, ["venv", Path.Combine(Folder, "venv"), "--python", PythonVersion, "--python-preference", "only-managed"], cancellationToken);
			}
			// CUDA wheels only for an NVIDIA GPU; the CPU build elsewhere (macOS's default build carries MPS).
			string? index = device.Kind switch
			{
				"cuda" => "https://download.pytorch.org/whl/cu124",
				"cpu" when !OperatingSystem.IsMacOS() => "https://download.pytorch.org/whl/cpu",
				_ => null
			};
			log($"Installing PyTorch {TorchVersion} for {device.Name} ({device.Kind}).");
			// uv searches an extra index before the default one, so PyTorch's index goes there; otherwise PyPI's Windows
			// torch (CPU only) wins. The Demucs step names it too, so torch is never re-resolved.
			string[] indexes = index is null ? [] : ["--extra-index-url", index];
			// Replace a torch build from an earlier install for other hardware: "2.5.1+cpu" would satisfy "==2.5.1".
			await CheckAsync(Uv, ["pip", "install", "--python", Python, $"torch=={TorchVersion}", $"torchaudio=={TorchVersion}",
				"--reinstall-package", "torch", "--reinstall-package", "torchaudio", .. indexes], cancellationToken);
			if (device.Kind == "cuda")
			{
				(_, string cuda, _) = await RunAsync(Python, ["-c", "import torch; print(torch.version.cuda or '')"], Environment(), cancellationToken);
				if (string.IsNullOrWhiteSpace(cuda)) throw new InvalidOperationException("PyTorch was installed without CUDA support, so the NVIDIA GPU would go unused.");
			}
			log($"Installing Demucs {DemucsVersion}.");
			await CheckAsync(Uv, ["pip", "install", "--python", Python, $"demucs=={DemucsVersion}", .. indexes], cancellationToken);
			log($"Fetching the {Model} model.");
			await CheckAsync(Python, ["-c", $"from demucs.pretrained import get_model; get_model('{Model}')"], cancellationToken);
			(_, string found, _) = await RunAsync(Python, ["-c",
				"import torch; print('cuda|' + torch.cuda.get_device_name(0) if torch.cuda.is_available() else ('mps|Apple silicon GPU' if torch.backends.mps.is_available() else 'cpu|CPU'))"],
				Environment(), cancellationToken);
			string[] parts = found.Trim().Split('|', 2);
			SeparatorDevice installed = parts[0] == "cpu" ? new("cpu", $"CPU ({CpuThreads} of {System.Environment.ProcessorCount} threads)") : new(parts[0], parts[1]);
			File.WriteAllText(Marker, JsonSerializer.Serialize(new
			{
				kind = installed.Kind, device = installed.Name, python = PythonVersion, torch = TorchVersion, demucs = DemucsVersion, model = Model,
				uv = UvVersion, installed = DateTime.UtcNow
			}));
			log($"Ready: separation runs on {installed.Name}.");
		}

		private async Task InstallUvAsync(Action<string> log, CancellationToken cancellationToken)
		{
			string target = (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture) switch
			{
				(true, _, Architecture.Arm64) => "uv-aarch64-pc-windows-msvc.zip",
				(true, _, _) => "uv-x86_64-pc-windows-msvc.zip",
				(_, true, Architecture.Arm64) => "uv-aarch64-apple-darwin.tar.gz",
				(_, true, _) => "uv-x86_64-apple-darwin.tar.gz",
				_ when RuntimeInformation.OSArchitecture == Architecture.X64 => "uv-x86_64-unknown-linux-gnu.tar.gz",
				_ => throw new PlatformNotSupportedException("No uv build is pinned for this platform.")
			};
			string url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{target}";
			log($"Downloading uv {UvVersion} (Python and package installer) from {url}.");
			using HttpClient http = new();
			byte[] archive = await http.GetByteArrayAsync(url, cancellationToken);
			string digest = Convert.ToHexStringLower(SHA256.HashData(archive));
			if (digest != UvDigests[target]) throw new InvalidDataException($"The uv download's checksum is wrong ({digest}); not installing it.");
			string folder = Path.Combine(Folder, "uv");
			Directory.CreateDirectory(folder);
			string name = OperatingSystem.IsWindows() ? "uv.exe" : "uv";
			if (target.EndsWith(".zip", StringComparison.Ordinal))
			{
				using ZipArchive zip = new(new MemoryStream(archive));
				ZipArchiveEntry entry = zip.Entries.First(candidate => candidate.Name == name);
				entry.ExtractToFile(Uv, overwrite: true);
			}
			else
			{
				using GZipStream gzip = new(new MemoryStream(archive), CompressionMode.Decompress);
				using TarReader tar = new(gzip);
				while (tar.GetNextEntry() is { } entry)
					if (Path.GetFileName(entry.Name) == name && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
					{
						entry.ExtractToFile(Uv, overwrite: true);
						break;
					}
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Uv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}

		// Separates the window [start, start + length) of the recording, installing first if need be.
		public async Task<StemSeparation> SeparateAsync(string input, double start, double length, string stemPath, string restPath,
			string stem, int shifts, bool cpu, Action<string> log, CancellationToken cancellationToken)
		{
			if (!Stems.Contains(stem)) throw new ArgumentException($"Stems are {string.Join(", ", Stems)}.", nameof(stem));
			if (shifts is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(shifts), "Shifts are 1 to 10.");
			if (length is <= 0 or > LongestSeconds)
				throw new ArgumentOutOfRangeException(nameof(length), $"Separate up to {LongestSeconds} s: the part to match, not the whole song.");
			MultichannelAudioBuffer mix;
			mix = AudioFiles.Read(input, start, length, out _);
			float[][] stereo = mix.Channels.Count switch
			{
				0 => throw new InvalidDataException("The recording has no channels."),
				1 => [[.. mix.Channels[0]], [.. mix.Channels[0]]],
				_ => [[.. mix.Channels[0]], [.. mix.Channels[1]]]
			};
			if (stereo[0].Length == 0) throw new ArgumentOutOfRangeException(nameof(start), "The window starts after the recording ends.");
			if (mix.SampleRate != ModelRate) stereo = Fm9Reamper.Resample(stereo, mix.SampleRate, ModelRate);
			int frames = stereo[0].Length;
			if (!Installed) await InstallAsync(log, cancellationToken);

			string work = Path.Combine(Folder, "jobs", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(work);
			try
			{
				string raw = Path.Combine(work, "mix.f32"), stemRaw = Path.Combine(work, "stem.f32"), restRaw = Path.Combine(work, "rest.f32");
				WriteRaw(raw, stereo);
				string job = Path.Combine(work, "job.json");
				File.WriteAllText(job, JsonSerializer.Serialize(new
				{
					model = Model, stem, shifts, device = cpu ? "cpu" : "auto", threads = CpuThreads, channels = 2, frames,
					input = raw, stemOutput = stemRaw, restOutput = restRaw
				}));
				string script = Path.Combine(work, "separate.py");
				await using (Stream resource = typeof(StemSeparator).Assembly.GetManifestResourceStream("Fractal.Fm9.Separation.separate.py")!)
				await using (FileStream file = File.Create(script)) await resource.CopyToAsync(file, cancellationToken);
				(int code, string output, string error) = await RunAsync(Python, [script, job], Environment(), cancellationToken);
				if (code != 0) throw new InvalidOperationException($"Demucs failed: {Tail(error)}");
				using JsonDocument report = JsonDocument.Parse(output.Trim().Split('\n')[^1]);

				MultichannelAudioBuffer stemAudio = new(ModelRate, ReadRaw(stemRaw, frames)), restAudio = new(ModelRate, ReadRaw(restRaw, frames));
				Write(stemPath, stemAudio);
				Write(restPath, restAudio);
				AudioMeasurement? stemMeasured = TryMeasure(stemAudio), mixMeasured = TryMeasure(new MultichannelAudioBuffer(ModelRate, stereo));
				return new StemSeparation(Path.GetFullPath(stemPath), Path.GetFullPath(restPath), ModelRate, start, frames / (double)ModelRate,
					report.RootElement.GetProperty("device").GetString() ?? "?", Math.Round(report.RootElement.GetProperty("seconds").GetDouble(), 1),
					stemMeasured?.IntegratedLufs, mixMeasured?.IntegratedLufs, stemMeasured?.Bands ?? [], Notes(stem, stemMeasured, mixMeasured));
			}
			finally
			{
				try { Directory.Delete(work, recursive: true); } catch (IOException) { }
			}
		}

		// How much of the mix the stem carries, and what bled into it.
		internal static IReadOnlyList<string> Notes(string stem, AudioMeasurement? stemMeasured, AudioMeasurement? mixMeasured)
		{
			if (stemMeasured is null) return [$"The {stem} stem is silent: Demucs found no {stem} in this part."];
			List<string> notes = [];
			if (mixMeasured is not null)
			{
				double below = mixMeasured.IntegratedLufs - stemMeasured.IntegratedLufs;
				// A guitar-free track left a stem 50 dB down; a guitar at the backing's level, 5 dB.
				if (below > 30)
					return [string.Create(CultureInfo.InvariantCulture, $"The {stem} stem is {below:0.0} dB below the mix: there is little or no {stem} in this part, so it is no use as a target.")];
				notes.Add(string.Create(CultureInfo.InvariantCulture, $"The {stem} stem plays {below:0.0} dB below the mix."));
			}
			if (stem == "guitar")
			{
				// Isolated guitar measured -26 to -33 dB below 60 Hz and -34 dB or less above 8 kHz.
				if (Band(stemMeasured, "sub") is double sub and > -20)
					notes.Add(string.Create(CultureInfo.InvariantCulture, $"Bass or kick bleed: {sub:0.0} dB below 60 Hz; trust the match less in the lows."));
				if (Band(stemMeasured, "air") is double air and > -25)
					notes.Add(string.Create(CultureInfo.InvariantCulture, $"Cymbal bleed: {air:0.0} dB above 8 kHz; a match will brighten the preset to chase it."));
			}
			if (stemMeasured.Clipped) notes.Add("The stem clips.");
			return notes;
		}

		private static double? Band(AudioMeasurement measured, string name) =>
			measured.Bands.FirstOrDefault(band => band.Name == name)?.RelativeDecibels;

		private static AudioMeasurement? TryMeasure(MultichannelAudioBuffer audio)
		{
			try { return AudioMeasurer.Measure(audio); }
			catch (InvalidOperationException) { return null; }
		}

		private static void Write(string path, MultichannelAudioBuffer audio)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			using FileStream stream = File.Create(path);
			WaveFileWriter.WritePcm24(audio, stream);
		}

		internal static void WriteRaw(string path, float[][] channels)
		{
			using FileStream stream = File.Create(path);
			foreach (float[] channel in channels) stream.Write(MemoryMarshal.AsBytes(channel.AsSpan()));
		}

		internal static float[][] ReadRaw(string path, int frames)
		{
			byte[] bytes = File.ReadAllBytes(path);
			int channels = bytes.Length / (frames * 4);
			return Enumerable.Range(0, channels)
				.Select(channel => MemoryMarshal.Cast<byte, float>(bytes.AsSpan(channel * frames * 4, frames * 4)).ToArray())
				.ToArray();
		}

		// Every download, cache and temp file stays in the folder; CPU math libraries use half the cores.
		private Dictionary<string, string> Environment() => new()
		{
			["UV_CACHE_DIR"] = Path.Combine(Folder, "cache", "uv"),
			["UV_PYTHON_INSTALL_DIR"] = Path.Combine(Folder, "python"),
			["PIP_CACHE_DIR"] = Path.Combine(Folder, "cache", "pip"),
			["TORCH_HOME"] = Path.Combine(Folder, "models"),
			["XDG_CACHE_HOME"] = Path.Combine(Folder, "cache"),
			["TMP"] = Path.Combine(Folder, "tmp"),
			["TEMP"] = Path.Combine(Folder, "tmp"),
			["TMPDIR"] = Path.Combine(Folder, "tmp"),
			["OMP_NUM_THREADS"] = CpuThreads.ToString(CultureInfo.InvariantCulture),
			["MKL_NUM_THREADS"] = CpuThreads.ToString(CultureInfo.InvariantCulture),
			["PYTHONIOENCODING"] = "utf-8",
			["PYTHONNOUSERSITE"] = "1"
		};

		private async Task CheckAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
		{
			(int code, _, string error) = await RunAsync(command, arguments, Environment(), cancellationToken);
			if (code != 0) throw new InvalidOperationException($"'{Path.GetFileName(command)} {string.Join(' ', arguments)}' failed: {Tail(error)}");
		}

		private static string Tail(string text) =>
			string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(6));

		private static async Task<(int Code, string Output, string Error)> RunAsync(string command, IReadOnlyList<string> arguments,
			IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
		{
			ProcessStartInfo start = new(command)
			{
				// Its own (closed) stdin: under the MCP server, an inherited stdin is the protocol pipe, and Python blocked at
				// start-up on that pipe while the server held a read on it.
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			foreach (string argument in arguments) start.ArgumentList.Add(argument);
			if (environment is not null)
				foreach ((string key, string value) in environment)
				{
					start.Environment[key] = value;
					if (key.EndsWith("_DIR", StringComparison.Ordinal) || key is "TMP" or "TORCH_HOME" or "XDG_CACHE_HOME") Directory.CreateDirectory(value);
				}
			using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
			process.StandardInput.Close();
			Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken), error = process.StandardError.ReadToEndAsync(cancellationToken);
			try { await process.WaitForExitAsync(cancellationToken); }
			catch (OperationCanceledException)
			{
				process.Kill(entireProcessTree: true);
				throw;
			}
			return (process.ExitCode, await output, await error);
		}
	}
}
