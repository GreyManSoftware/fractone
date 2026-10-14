using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Fractal.Core;

namespace Fractal.Fm9
{
	// Outcome: "captured", or why nothing was: "bot-check", "sign-in", "consent", "drm", "no-media", "stalled", "silent".
	public sealed record MediaCapture(string Outcome, string? File, string Url, string Title, double Start, double End, double Seconds,
		int SampleRate, double? PeakDecibels, bool AdsWaitedOut, string? Screenshot, IReadOnlyList<string> Notes);

	// Records the audio of a web page's own main video or audio element in a headless browser, for a tone-match target.
	// It never gets past bot checks, CAPTCHAs, sign-in walls or DRM, and never records an ad: it stops (or waits an ad out)
	// and reports, with a screenshot. Consent dialogs get their decline option or the capture stops. The browser keeps its
	// own headless identity and a fresh profile each time, deleted afterwards.
	public sealed class MediaCapturer(string browserFolder)
	{
		public const double LongestSeconds = 600;
		// Chrome for Testing's headless shell, pinned; installed on first use.
		private const string BrowserVersion = "155.0.8059.39";

		public string Folder { get; } = Path.GetFullPath(browserFolder);
		public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

		private static string Platform => (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture) switch
		{
			(true, _, Architecture.X64 or Architecture.Arm64) => "win64",
			(true, _, _) => "win32",
			(_, true, Architecture.Arm64) => "mac-arm64",
			(_, true, _) => "mac-x64",
			_ when RuntimeInformation.OSArchitecture == Architecture.Arm64 => "linux-arm64",
			_ => "linux64"
		};

		public string Executable => Path.Combine(Folder, BrowserVersion, $"chrome-headless-shell-{Platform}",
			OperatingSystem.IsWindows() ? "chrome-headless-shell.exe" : "chrome-headless-shell");

		public bool Installed => File.Exists(Executable);

		// Downloads the headless shell (about 100 MB) and checks it against the MD5 Google's storage reports for it.
		public async Task InstallAsync(Action<string> log, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(log);
			string url = $"https://storage.googleapis.com/chrome-for-testing-public/{BrowserVersion}/{Platform}/chrome-headless-shell-{Platform}.zip";
			log($"Downloading the headless browser (Chrome for Testing {BrowserVersion}, about 100 MB) into {Folder}.");
			Directory.CreateDirectory(Folder);
			string zip = Path.Combine(Folder, $"download-{Guid.NewGuid():N}.zip");
			try
			{
				using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
				using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
				response.EnsureSuccessStatusCode();
				string? expected = response.Headers.TryGetValues("x-goog-hash", out IEnumerable<string>? hashes)
					? hashes.SelectMany(value => value.Split(',')).Select(part => part.Trim())
						.FirstOrDefault(part => part.StartsWith("md5=", StringComparison.Ordinal))?[4..]
					: null;
				if (expected is null) throw new InvalidDataException("The browser download carried no checksum; not installing it.");
				using (MD5 md5 = MD5.Create())
				await using (FileStream file = File.Create(zip))
				await using (CryptoStream hashing = new(file, md5, CryptoStreamMode.Write))
				{
					await (await response.Content.ReadAsStreamAsync(cancellationToken)).CopyToAsync(hashing, cancellationToken);
					await hashing.FlushFinalBlockAsync(cancellationToken);
					if (Convert.ToBase64String(md5.Hash!) != expected) throw new InvalidDataException("The browser download's checksum is wrong; not installing it.");
				}
				ZipFile.ExtractToDirectory(zip, Path.Combine(Folder, BrowserVersion), overwriteFiles: true);
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				log($"Installed the headless browser at {Executable}.");
			}
			finally
			{
				File.Delete(zip);
			}
		}

		public async Task<MediaCapture> CaptureAsync(string url, double start, double end, string output, string screenshots,
			Action<string> log, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(log);
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme is not ("http" or "https"))
				throw new ArgumentException("Give the page's full http or https address.", nameof(url));
			if (start < 0 || end <= start) throw new ArgumentOutOfRangeException(nameof(end), "The end must come after the start.");
			if (end - start > LongestSeconds) throw new ArgumentOutOfRangeException(nameof(end), $"Capture up to {LongestSeconds} s at a time.");
			if (!Installed) await InstallAsync(log, cancellationToken);

			string profile = Path.Combine(Folder, "profiles", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(profile);
			using Process browser = Launch(profile);
			try
			{
				Uri endpoint = await EndpointAsync(browser, cancellationToken);
				await using DevToolsClient devTools = new();
				await devTools.ConnectAsync(endpoint, cancellationToken);
				return await new Session(this, devTools, url, start, end, output, screenshots, log).RunAsync(cancellationToken);
			}
			finally
			{
				try { browser.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
				await browser.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
				try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
		}

		private Process Launch(string profile)
		{
			ProcessStartInfo start = new(Executable)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				RedirectStandardInput = true
			};
			foreach (string argument in new[]
			{
				"--remote-debugging-port=0", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check",
				"--autoplay-policy=no-user-gesture-required", "--mute-audio", "--lang=en-US", "--window-size=1280,800",
				"--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows",
				"about:blank"
			})
				start.ArgumentList.Add(argument);
			Process process = Process.Start(start) ?? throw new InvalidOperationException("The headless browser did not start.");
			process.StandardInput.Close();
			return process;
		}

		// The shell prints "DevTools listening on ws://127.0.0.1:<port>/devtools/browser/<id>" on stderr.
		private static async Task<Uri> EndpointAsync(Process browser, CancellationToken cancellationToken)
		{
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			limit.CancelAfter(TimeSpan.FromSeconds(30));
			while (await browser.StandardError.ReadLineAsync(limit.Token) is { } line)
			{
				int at = line.IndexOf("ws://", StringComparison.Ordinal);
				if (line.Contains("DevTools listening", StringComparison.Ordinal) && at >= 0)
				{
					_ = browser.StandardError.ReadToEndAsync(CancellationToken.None);
					_ = browser.StandardOutput.ReadToEndAsync(CancellationToken.None);
					return new Uri(line[at..].Trim());
				}
			}
			throw new InvalidOperationException("The headless browser did not open its DevTools port.");
		}

		private sealed class Session(MediaCapturer owner, DevToolsClient devTools, string url, double start, double end, string output,
			string screenshots, Action<string> log)
		{
			private static readonly string Script = LoadScript();
			private string _session = "";
			private int? _status;

			private static string LoadScript()
			{
				using Stream stream = typeof(MediaCapturer).Assembly.GetManifestResourceStream("Fractal.Fm9.Capture.capture.js")!;
				using StreamReader reader = new(stream);
				return reader.ReadToEnd();
			}

			public async Task<MediaCapture> RunAsync(CancellationToken cancellationToken)
			{
				devTools.OnEvent = (method, parameters, _) =>
				{
					// The main document's HTTP status: 403 and 429 are how sites turn automated visitors away.
					if (method == "Network.responseReceived" && _status is null && parameters.GetProperty("type").GetString() == "Document")
						_status = parameters.GetProperty("response").GetProperty("status").GetInt32();
				};
				string target = (await devTools.SendAsync("Target.createTarget", new { url = "about:blank" }, null, cancellationToken)).GetProperty("targetId").GetString()!;
				_session = (await devTools.SendAsync("Target.attachToTarget", new { targetId = target, flatten = true }, null, cancellationToken)).GetProperty("sessionId").GetString()!;
				await Send("Page.enable", null, cancellationToken);
				await Send("Network.enable", null, cancellationToken);
				log($"Opening {url}");
				JsonElement navigation = await Send("Page.navigate", new { url }, cancellationToken);
				if (navigation.TryGetProperty("errorText", out JsonElement failure) && failure.GetString() is { Length: > 0 } reason)
					throw new InvalidOperationException($"The page did not load: {reason}.");

				// Up to 45 s for the page, any consent dialog and the media element to settle.
				List<string> notes = [];
				JsonElement page = default;
				int ready = 0;
				for (int attempt = 0; attempt < 45; attempt++)
				{
					await owner.Wait(TimeSpan.FromSeconds(1), cancellationToken);
					page = await Inspect(cancellationToken);
					if (_status is 403 or 429 || page.GetProperty("botCheck").GetBoolean())
						return await Stopped("bot-check", page, $"The site is checking for a human or refused the visit{(_status is int code and >= 400 ? $" (HTTP {code})" : "")}. FracTone doesn't get past these checks. Play it in your own browser and use capture_playback instead, if you have the right to.", cancellationToken);
					if (page.GetProperty("consent").GetBoolean())
					{
						if (await Evaluate<string?>("window.__fractone.decline()", cancellationToken) is { } clicked)
						{
							notes.Add($"Declined a consent dialog (\"{clicked}\").");
							await owner.Wait(TimeSpan.FromSeconds(2), cancellationToken);
							continue;
						}
						if (attempt >= 5)
							return await Stopped("consent", page, "A consent dialog has no decline option FracTone can find, and it won't accept one. Play it in your own browser and use capture_playback instead.", cancellationToken);
						continue;
					}
					// Ready twice running: consent dialogs often appear a moment after the player.
					if (page.GetProperty("media").ValueKind == JsonValueKind.Object && page.GetProperty("media").GetProperty("readyState").GetInt32() >= 1)
					{
						if (++ready >= 2) break;
						continue;
					}
					ready = 0;
					if (page.GetProperty("signIn").GetBoolean() && attempt >= 8) break;
					// Some players create their media element only when their play button is pressed: press it, once or twice.
					if (page.GetProperty("media").ValueKind != JsonValueKind.Object && attempt is 3 or 10
						&& await Evaluate<string?>("window.__fractone.play()", cancellationToken) is { } pressed)
						notes.Add($"Pressed the page's \"{pressed}\" button to load its player.");
				}
				if (page.GetProperty("media").ValueKind != JsonValueKind.Object)
					return page.GetProperty("signIn").GetBoolean()
						? await Stopped("sign-in", page, "The page wants a sign-in, a subscription or an age check before it plays. FracTone doesn't sign in. Play it in your own browser and use capture_playback instead.", cancellationToken)
						: await Stopped("no-media", page, "No playable video or audio element appeared on the page.", cancellationToken);
				if (page.GetProperty("media").GetProperty("encrypted").GetBoolean())
					return await Stopped("drm", page, "The media is DRM-protected, so it can't be captured from the page, and FracTone doesn't get round DRM. If you have the right to use it, play it in your own browser and use capture_playback.", cancellationToken);
				double duration = page.GetProperty("media").GetProperty("duration").ValueKind == JsonValueKind.Number ? page.GetProperty("media").GetProperty("duration").GetDouble() : double.NaN;
				if (double.IsFinite(duration) && start >= duration)
					throw new ArgumentOutOfRangeException(nameof(start), $"The media is {duration:0.#} s long; the start is past its end.");
				double stop = double.IsFinite(duration) ? Math.Min(end, duration) : end;

				// Ads play once playback starts; the page script records nothing while one shows (it never skips one) and
				// goes to the part when the programme returns.
				bool adsWaited = page.GetProperty("ad").GetBoolean();
				JsonElement started = await Evaluate<JsonElement>(
					string.Create(CultureInfo.InvariantCulture, $"window.__fractone.start({start}, {stop})"), cancellationToken, awaitPromise: true);
				int rate = started.GetProperty("sampleRate").GetInt32();
				foreach (JsonElement note in started.GetProperty("notes").EnumerateArray())
					notes.Add(note.GetString() switch
					{
						"cors-refused" => "The media's server refused a cross-origin (CORS) request, so the page can't read its audio; expect silence.",
						"live" => "This is a live stream; it was captured as it played.",
						var other => other ?? ""
					});
				log(string.Create(CultureInfo.InvariantCulture, $"Capturing {start:0.#}-{stop:0.#} s in real time."));

				List<float> left = [], right = [];
				int wanted = (int)((stop - start) * rate);
				double lastTime = -1, peak = 0;
				DateTime lastProgress = DateTime.UtcNow;
				DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds((stop - start) + 240);
				while (true)
				{
					await owner.Wait(TimeSpan.FromMilliseconds(500), cancellationToken);
					JsonElement chunk = await Evaluate<JsonElement>("window.__fractone.drain()", cancellationToken);
					byte[] data = Convert.FromBase64String(chunk.GetProperty("data").GetString()!);
					float[] samples = MemoryMarshal.Cast<byte, float>(data).ToArray();
					for (int index = 0; index + 1 < samples.Length; index += 2)
					{
						left.Add(samples[index]);
						right.Add(samples[index + 1]);
					}
					peak = chunk.GetProperty("peak").GetDouble();
					adsWaited |= chunk.GetProperty("adSeen").GetBoolean();
					double time = chunk.GetProperty("time").GetDouble();
					if (Math.Abs(time - lastTime) > 0.01 || chunk.GetProperty("ad").GetBoolean())
					{
						lastTime = time;
						lastProgress = DateTime.UtcNow;
					}
					if (chunk.GetProperty("ended").GetBoolean() || left.Count >= wanted) break;
					if (chunk.GetProperty("error").ValueKind == JsonValueKind.Number)
						return await Stopped("stalled", await Inspect(cancellationToken), "The media failed while playing.", cancellationToken);
					// A late consent dialog can pause the player: decline it and play on.
					if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(3) && DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(4))
					{
						JsonElement now = await Inspect(cancellationToken);
						if (now.GetProperty("consent").GetBoolean() && await Evaluate<string?>("window.__fractone.decline()", cancellationToken) is { } clicked)
						{
							notes.Add($"Declined a consent dialog (\"{clicked}\").");
						}
						await Evaluate<JsonElement>("window.__fractone.resume()", cancellationToken);
					}
					if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(20) || DateTime.UtcNow > deadline)
					{
						JsonElement now = await Inspect(cancellationToken);
						string why = now.GetProperty("botCheck").GetBoolean() ? "bot-check" : now.GetProperty("signIn").GetBoolean() ? "sign-in" : "stalled";
						return await Stopped(why, now, "Playback stopped before the end of the part and didn't resume.", cancellationToken);
					}
				}
				await Evaluate<JsonElement>("window.__fractone.stop()", cancellationToken);
				if (left.Count == 0 || peak == 0)
					return await Stopped("silent", await Inspect(cancellationToken), "The page's media played but its audio reached the capture as silence (typically media on another site that doesn't allow cross-origin reads). Use capture_playback while it plays in your own browser instead, if you have the right to.", cancellationToken);

				if (adsWaited) notes.Add("Waited out an ad; nothing was recorded while it showed.");
				int frames = Math.Min(left.Count, Math.Max(wanted, 1));
				MultichannelAudioBuffer audio = new(rate, [left.Take(frames).ToArray(), right.Take(frames).ToArray()]);
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
				await using (FileStream file = File.Create(output)) WaveFileWriter.WritePcm24(audio, file);
				return new MediaCapture("captured", Path.GetFullPath(output), url, page.GetProperty("title").GetString() ?? "", start, stop,
					frames / (double)rate, rate, 20 * Math.Log10(peak), adsWaited, null, notes);
			}

			private async Task<MediaCapture> Stopped(string outcome, JsonElement page, string why, CancellationToken cancellationToken)
			{
				string? shot = null;
				try
				{
					JsonElement image = await Send("Page.captureScreenshot", new { format = "png" }, cancellationToken);
					Directory.CreateDirectory(screenshots);
					shot = Path.Combine(screenshots, $"capture stopped {DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.png");
					await File.WriteAllBytesAsync(shot, Convert.FromBase64String(image.GetProperty("data").GetString()!), cancellationToken);
				}
				catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException) { }
				string title = page.ValueKind == JsonValueKind.Object && page.TryGetProperty("title", out JsonElement value) ? value.GetString() ?? "" : "";
				return new MediaCapture(outcome, null, url, title, start, end, 0, 0, null, false, shot, [why]);
			}

			private async Task<JsonElement> Inspect(CancellationToken cancellationToken)
			{
				await Evaluate<string>(Script, cancellationToken);
				return await Evaluate<JsonElement>("window.__fractone.inspect()", cancellationToken);
			}

			private Task<JsonElement> Send(string method, object? parameters, CancellationToken cancellationToken) =>
				devTools.SendAsync(method, parameters, _session, cancellationToken);

			private async Task<T> Evaluate<T>(string expression, CancellationToken cancellationToken, bool awaitPromise = false)
			{
				JsonElement result = await devTools.SendAsync("Runtime.evaluate",
					new { expression, returnByValue = true, awaitPromise }, _session, cancellationToken, TimeSpan.FromSeconds(90));
				if (result.TryGetProperty("exceptionDetails", out JsonElement exception))
				{
					string message = exception.TryGetProperty("exception", out JsonElement thrown) && thrown.TryGetProperty("description", out JsonElement description)
						? description.GetString() ?? "" : exception.GetProperty("text").GetString() ?? "";
					throw new InvalidOperationException($"The page script failed: {message.Split('\n')[0]}");
				}
				JsonElement value = result.GetProperty("result");
				if (!value.TryGetProperty("value", out JsonElement inner)) return default!;
				return typeof(T) == typeof(JsonElement) ? (T)(object)inner : inner.Deserialize<T>()!;
			}
		}
	}
}
