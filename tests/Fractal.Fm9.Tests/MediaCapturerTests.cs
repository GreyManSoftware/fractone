using System.Net;
using System.Net.Sockets;
using System.Text;
using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	// A test that drives the real headless browser. It never downloads one: set FRACTONE_BROWSER_FOLDER to a folder where
	// 'fractone-mcp setup-browser' has installed it, or the test is skipped.
	public sealed class BrowserFactAttribute : FactAttribute
	{
		public static string? Folder => Environment.GetEnvironmentVariable("FRACTONE_BROWSER_FOLDER");

		public BrowserFactAttribute()
		{
			if (Folder is null || !new MediaCapturer(Folder).Installed)
				Skip = "Needs the headless browser: run 'fractone-mcp setup-browser' and set FRACTONE_BROWSER_FOLDER to its folder.";
		}
	}

	public sealed class MediaCapturerTests : IDisposable
	{
		private const int Rate = 48_000;
		private readonly TemporaryFolder _folder = new();
		private readonly TestSite _site = new();

		public void Dispose()
		{
			_site.Dispose();
			_folder.Dispose();
		}

		private Task<MediaCapture> Capture(string page, double start, double end) =>
			new MediaCapturer(BrowserFactAttribute.Folder!).CaptureAsync(_site.Url(page), start, end, _folder.File("capture.wav"),
				_folder.File("shots"), _ => { }, CancellationToken.None);

		private static MultichannelAudioBuffer Read(string path)
		{
			using FileStream stream = File.OpenRead(path);
			return WaveFileReader.ReadChannels(stream);
		}

		// Zero crossings per second, halved: the tone's frequency.
		private static double Frequency(MultichannelAudioBuffer audio)
		{
			IReadOnlyList<float> left = audio.Channels[0];
			int crossings = 0;
			for (int index = 1; index < left.Count; index++) if ((left[index - 1] < 0) != (left[index] < 0)) crossings++;
			return crossings / 2.0 / (left.Count / (double)audio.SampleRate);
		}

		[BrowserFact]
		public async Task ThePagesOwnAudioIsRecordedForTheRequestedWindow()
		{
			MediaCapture capture = await Capture("plain", 1, 4);

			Assert.Equal("captured", capture.Outcome);
			MultichannelAudioBuffer audio = Read(capture.File!);
			Assert.InRange(audio.Channels[0].Count / (double)audio.SampleRate, 2.9, 3.05);
			Assert.InRange(Frequency(audio), 435, 445);
			Assert.InRange(capture.PeakDecibels!.Value, -7, -5);
		}

		[BrowserFact]
		public async Task AConsentDialogIsDeclinedNotAccepted()
		{
			MediaCapture capture = await Capture("consent", 0.5, 2.5);

			Assert.Equal("captured", capture.Outcome);
			Assert.Contains(capture.Notes, note => note.Contains("\"Reject all\"", StringComparison.Ordinal));
			Assert.Equal("rejected", _site.ConsentChoice);
		}

		[BrowserFact]
		public async Task ABotCheckStopsTheCaptureWithAScreenshot()
		{
			MediaCapture capture = await Capture("captcha", 0, 2);

			Assert.Equal("bot-check", capture.Outcome);
			Assert.Null(capture.File);
			Assert.True(File.Exists(capture.Screenshot));
		}

		[BrowserFact]
		public async Task ASignInWallStopsTheCapture() =>
			Assert.Equal("sign-in", (await Capture("signin", 0, 2)).Outcome);

		[BrowserFact]
		public async Task DrmProtectedMediaIsReportedNotCaptured() =>
			Assert.Equal("drm", (await Capture("drm", 0, 2)).Outcome);

		[BrowserFact]
		public async Task AnAdIsWaitedOutAndNotRecorded()
		{
			MediaCapture capture = await Capture("ad", 0.5, 2.5);

			Assert.Equal("captured", capture.Outcome);
			Assert.True(capture.AdsWaitedOut);
			// The ad is a 1 kHz tone; only the 440 Hz programme made it into the take.
			Assert.InRange(Frequency(Read(capture.File!)), 435, 445);
		}

		// A tiny HTTP server for the test pages and their media, with byte ranges as browsers expect for media.
		private sealed class TestSite : IDisposable
		{
			private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
			private readonly CancellationTokenSource _stop = new();
			private readonly byte[] _tone = Wav(440), _ad = Wav(1000);

			public TestSite()
			{
				_listener.Start();
				_ = Task.Run(AcceptAsync);
			}

			public string? ConsentChoice { get; private set; }

			public string Url(string page) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{page}";

			private static byte[] Wav(double frequency)
			{
				int frames = Rate * 6;
				using MemoryStream stream = new();
				using BinaryWriter writer = new(stream);
				writer.Write("RIFF"u8); writer.Write(36 + (frames * 4)); writer.Write("WAVE"u8);
				writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(Rate);
				writer.Write(Rate * 4); writer.Write((short)4); writer.Write((short)16);
				writer.Write("data"u8); writer.Write(frames * 4);
				for (int frame = 0; frame < frames; frame++)
				{
					short sample = (short)(Math.Sin(2 * Math.PI * frequency * frame / Rate) * 0.5 * short.MaxValue);
					writer.Write(sample); writer.Write(sample);
				}
				return stream.ToArray();
			}

			private const string Media = "<audio id=media src=\"/tone.wav\" preload=auto></audio>";

			private string Page(string name) => name switch
			{
				"plain" => $"<title>Plain</title><p>A tone.</p>{Media}",
				"consent" => "<title>Consent</title><div id=consent style=\"position:fixed;inset:0;background:#fff\"><p>We and our partners use cookies to store and access personal data.</p>" +
					"<button onclick=\"go('accepted')\">Accept all</button> <button onclick=\"go('rejected')\">Reject all</button></div><script>" +
					"function go(choice){fetch('/choice?'+choice);document.getElementById('consent').remove();" +
					"var a=document.createElement('audio');a.src='/tone.wav';a.preload='auto';document.body.appendChild(a);}</script>",
				"captcha" => "<title>Just a moment</title><h1>Verify you are human</h1><iframe src=\"/frame?captcha\" width=300 height=80></iframe>",
				"signin" => "<title>Sign in</title><h1>Sign in to confirm your age</h1><p>This video may be inappropriate for some users.</p>",
				"drm" => "<title>Protected</title><video id=media src=\"/tone.wav\" width=320 height=180></video><script>" +
					"navigator.requestMediaKeySystemAccess('org.w3.clearkey',[{initDataTypes:['keyids'],audioCapabilities:[{contentType:'audio/mp4; codecs=\"mp4a.40.2\"'}]}])" +
					".then(a=>a.createMediaKeys()).then(k=>document.getElementById('media').setMediaKeys(k));</script>",
				"ad" => "<title>Ad first</title><div id=player class=\"player ad-showing\"><video id=media src=\"/ad.wav\" width=320 height=180></video>" +
					"<button>Skip ad</button></div><script>var v=document.getElementById('media');v.addEventListener('playing',function once(){v.removeEventListener('playing',once);" +
					"setTimeout(function(){var p=document.getElementById('player');p.className='player';p.querySelector('button').remove();var t=v.currentTime;" +
					"v.src='/tone.wav';v.play();},1500);});</script>",
				_ => "<title>Frame</title>"
			};

			private async Task AcceptAsync()
			{
				while (!_stop.IsCancellationRequested)
				{
					TcpClient client;
					try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
					catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
					_ = Task.Run(() => ServeAsync(client));
				}
			}

			private async Task ServeAsync(TcpClient client)
			{
				using (client)
				{
					NetworkStream stream = client.GetStream();
					using StreamReader reader = new(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
					string? request = await reader.ReadLineAsync();
					if (request is null) return;
					string? range = null;
					for (string? line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
						if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
					string path = request.Split(' ')[1];
					if (path.StartsWith("/choice?", StringComparison.Ordinal)) ConsentChoice = path[8..];
					(byte[] body, string type) = path switch
					{
						"/tone.wav" => (_tone, "audio/wav"),
						"/ad.wav" => (_ad, "audio/wav"),
						_ => (Encoding.UTF8.GetBytes($"<!doctype html><html><body>{Page(path.TrimStart('/').Split('?')[0])}</body></html>"), "text/html; charset=utf-8")
					};
					int from = 0, to = body.Length - 1;
					bool partial = range is not null && range.StartsWith("bytes=", StringComparison.Ordinal);
					if (partial)
					{
						string[] bounds = range![6..].Split('-');
						from = int.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
						if (bounds[1].Length > 0) to = Math.Min(to, int.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture));
					}
					string head = (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
						$"Content-Type: {type}\r\nContent-Length: {to - from + 1}\r\nAccept-Ranges: bytes\r\n" +
						(partial ? $"Content-Range: bytes {from}-{to}/{body.Length}\r\n" : "") + "Connection: close\r\n\r\n";
					try
					{
						await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
						await stream.WriteAsync(body.AsMemory(from, to - from + 1));
					}
					catch (IOException) { }
				}
			}

			public void Dispose()
			{
				_stop.Cancel();
				_listener.Stop();
				_stop.Dispose();
			}
		}
	}
}
