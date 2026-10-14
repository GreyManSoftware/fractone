using System.IO.Compression;
using System.Net;
using System.Text;
using FracTone.Probe.Cli;

namespace FracTone.Probe.Tests
{
	// Every test talks to a fake handler; nothing here reaches the real site.
	public sealed class ReportUploadTests : IDisposable
	{
		private const string Code = "ftu_TestCode0123456789abcdefABCDEF";
		private readonly string folder = Path.Combine(Path.GetTempPath(), "fractone-upload-tests-" + Guid.NewGuid().ToString("N"));
		private readonly List<TimeSpan> waits = [];

		public ReportUploadTests() => Directory.CreateDirectory(folder);

		public void Dispose()
		{
			if (Directory.Exists(folder))
				Directory.Delete(folder, recursive: true);
		}

		private sealed class FakeHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] replies) : HttpMessageHandler
		{
			public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
				Requests.Add((request, body));
				int index = Math.Min(Requests.Count - 1, replies.Length - 1);
				return await replies[index](request, cancellationToken);
			}
		}

		private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply(HttpStatusCode status, string json = "{}", TimeSpan? retryAfter = null) =>
			(_, _) =>
			{
				HttpResponseMessage response = new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
				if (retryAfter is TimeSpan delta)
					response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
				return Task.FromResult(response);
			};

		private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> NetworkFailure() =>
			(_, _) => throw new HttpRequestException("No such host is known.");

		private const string CreatedJson = "{\"id\":\"r42\",\"uploadedUtc\":\"2026-10-14T10:00:00Z\",\"device\":\"FM9\",\"firmware\":\"12.00\",\"outcome\":\"Completed\",\"sizeBytes\":1234}";

		private HttpReportUploader Uploader(FakeHandler handler, Uri? site = null, TimeSpan? timeout = null) =>
			new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, site, timeout,
				delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

		private string Report(bool includeUserPresets = false)
		{
			ProbeRun run = new()
			{
				StartedAt = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero),
				FinishedAt = new DateTimeOffset(2026, 10, 14, 9, 5, 0, TimeSpan.Zero),
				Device = "FM9",
				Firmware = "12.00",
				Outcome = ProbeOutcome.Completed
			};
			run.Log.Add("identified FM9 12.00");
			return ProbeReportWriter.Write(run, folder, includeUserPresets).Zip;
		}

		[Fact]
		public async Task A_created_report_is_accepted_with_its_id_and_sent_as_a_bearer_authorised_zip()
		{
			string zip = Report();
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));

			ReportUploadResult result = await Uploader(handler).UploadAsync(zip, Code, CancellationToken.None);

			Assert.True(result.Accepted);
			Assert.Equal("r42", result.ReportId);
			Assert.Contains("FM9", result.Message);
			(HttpRequestMessage request, byte[] body) = Assert.Single(handler.Requests);
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.Equal("https://fractone.greymansoftware.com/api/probe-reports", request.RequestUri!.AbsoluteUri);
			Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
			Assert.Equal(Code, request.Headers.Authorization.Parameter);
			Assert.Equal("application/zip", request.Content!.Headers.ContentType!.MediaType);
			Assert.Equal(File.ReadAllBytes(zip), body);
		}

		[Fact]
		public async Task A_configured_site_receives_the_upload_at_its_own_api_path()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));

			await Uploader(handler, new Uri("http://localhost:8080")).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.Equal("http://localhost:8080/api/probe-reports", Assert.Single(handler.Requests).Request.RequestUri!.AbsoluteUri);
		}

		[Theory]
		[InlineData(HttpStatusCode.Unauthorized, "revoked")]
		[InlineData(HttpStatusCode.BadRequest, "not a valid probe report")]
		[InlineData(HttpStatusCode.RequestEntityTooLarge, "20 MB")]
		[InlineData(HttpStatusCode.UnsupportedMediaType, "file type")]
		[InlineData(HttpStatusCode.Conflict, "200")]
		[InlineData(HttpStatusCode.NotFound, "unexpected HTTP 404")]
		public async Task A_refusal_is_final_and_explained_without_the_code(HttpStatusCode status, string expected)
		{
			FakeHandler handler = new(Reply(status, "{\"error\":\"refused by the site\"}"));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Contains(expected, result.Message);
			Assert.DoesNotContain(Code, result.Message);
			Assert.Single(handler.Requests);
			Assert.Empty(waits);
		}

		[Fact]
		public async Task An_invalid_report_shows_the_sites_reason()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.BadRequest, "{\"error\":\"run.json is missing\"}"));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.Contains("run.json is missing", result.Message);
		}

		[Fact]
		public async Task A_server_error_is_retried_once_and_the_retry_can_succeed()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.InternalServerError), Reply(HttpStatusCode.Created, CreatedJson));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.True(result.Accepted);
			Assert.Equal(2, handler.Requests.Count);
			Assert.Single(waits);
			// The retry sends the whole zip again.
			Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
		}

		[Fact]
		public async Task Two_server_errors_give_up_after_one_retry()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.ServiceUnavailable));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Contains("HTTP 503", result.Message);
			Assert.Equal(2, handler.Requests.Count);
		}

		[Fact]
		public async Task A_network_failure_is_retried_once()
		{
			FakeHandler handler = new(NetworkFailure(), Reply(HttpStatusCode.Created, CreatedJson));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.True(result.Accepted);
			Assert.Equal(2, handler.Requests.Count);
		}

		[Fact]
		public async Task Two_network_failures_report_that_the_site_could_not_be_reached()
		{
			FakeHandler handler = new(NetworkFailure());

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Contains("Couldn't reach fractone.greymansoftware.com", result.Message);
			Assert.Equal(2, handler.Requests.Count);
		}

		[Fact]
		public async Task A_site_that_never_answers_times_out_and_is_retried_once()
		{
			FakeHandler handler = new(async (_, cancellationToken) =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return new HttpResponseMessage(HttpStatusCode.Created);
			});

			ReportUploadResult result = await Uploader(handler, timeout: TimeSpan.FromMilliseconds(50)).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Contains("didn't answer", result.Message);
			Assert.Equal(2, handler.Requests.Count);
		}

		[Fact]
		public async Task A_rate_limit_waits_as_long_as_the_site_asks_then_tries_once_more()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}", TimeSpan.FromSeconds(7)), Reply(HttpStatusCode.Created, CreatedJson));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.True(result.Accepted);
			Assert.Equal([TimeSpan.FromSeconds(7)], waits);
			Assert.Equal(2, handler.Requests.Count);
		}

		[Fact]
		public async Task A_second_rate_limit_is_not_waited_out()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(7)));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Contains("limiting uploads", result.Message);
			Assert.Equal(2, handler.Requests.Count);
			Assert.Single(waits);
		}

		[Fact]
		public async Task A_long_rate_limit_is_reported_rather_than_waited_out()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromMinutes(40)));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Contains("40 minutes", result.Message);
			Assert.Single(handler.Requests);
			Assert.Empty(waits);
		}

		[Fact]
		public async Task A_report_over_the_size_limit_is_not_sent()
		{
			string big = Path.Combine(folder, "big.zip");
			using (FileStream stream = new(big, FileMode.Create))
				stream.SetLength(HttpReportUploader.MaxBytes + 1);
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));

			ReportUploadResult result = await Uploader(handler).UploadAsync(big, Code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Empty(handler.Requests);
		}

		[Theory]
		[InlineData("")]
		[InlineData("abc_123456789")]
		[InlineData("ftu_has space")]
		public async Task A_malformed_code_is_not_sent(string code)
		{
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));

			ReportUploadResult result = await Uploader(handler).UploadAsync(Report(), code, CancellationToken.None);

			Assert.False(result.Accepted);
			Assert.Empty(handler.Requests);
		}

		[Fact]
		public void A_saved_code_round_trips_and_is_not_stored_as_text()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));

			store.Save(Code);

			Assert.Equal(Code, store.Load());
			byte[] stored = File.ReadAllBytes(store.FilePath);
			if (OperatingSystem.IsWindows())
			{
				Assert.False(Contains(stored, Encoding.UTF8.GetBytes(Code)));
				Assert.False(Contains(stored, Encoding.Unicode.GetBytes(Code)));
			}
		}

		[Fact]
		public void A_deleted_code_is_gone()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));
			store.Save(Code);

			Assert.True(store.Delete());

			Assert.Null(store.Load());
			Assert.False(store.Delete());
		}

		[Fact]
		public void A_damaged_code_file_reads_as_no_code()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));
			store.Save(Code);
			File.WriteAllBytes(store.FilePath, [1, 2, 3, 4]);

			Assert.Null(store.Load());
		}

		[Fact]
		public void Something_that_is_not_an_upload_code_is_not_saved()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));

			Assert.Throws<ArgumentException>(() => store.Save("password123"));
			Assert.False(File.Exists(store.FilePath));
		}

		[Fact]
		public async Task Login_and_logout_save_and_delete_the_code_without_printing_it()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));
			StringWriter output = new(), error = new();

			Assert.Equal(0, await UploadCommands.MainAsync(["login", "--code", Code], new StringReader(""), output, error, store));
			Assert.Equal(Code, store.Load());
			Assert.Equal(0, await UploadCommands.MainAsync(["logout"], new StringReader(""), output, error, store));

			Assert.Null(store.Load());
			Assert.DoesNotContain(Code, output.ToString() + error.ToString());
		}

		[Fact]
		public async Task Login_reads_a_pasted_code_when_none_is_given()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));
			StringWriter output = new();

			await UploadCommands.MainAsync(["login"], new StringReader(Code + "\n"), output, new StringWriter(), store);

			Assert.Equal(Code, store.Load());
			Assert.DoesNotContain(Code, output.ToString());
		}

		[Fact]
		public async Task Upload_shows_what_will_be_sent_and_sends_nothing_unless_the_user_says_yes()
		{
			UploadCodeStore store = Stored();
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));
			StringWriter output = new();
			string zip = Report();

			int exit = await UploadCommands.MainAsync(["upload", zip], new StringReader("\n"), output, new StringWriter(), store, _ => Uploader(handler));

			Assert.Equal(5, exit);
			Assert.Empty(handler.Requests);
			string text = output.ToString();
			Assert.Contains("run.json", text);
			Assert.Contains("log.txt", text);
			Assert.Contains("Anonymised: yes", text);
			Assert.Contains("Not uploaded.", text);
		}

		[Fact]
		public async Task Upload_sends_the_report_when_the_user_says_yes_and_never_prints_the_code()
		{
			UploadCodeStore store = Stored();
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));
			StringWriter output = new(), error = new();
			string zip = Report();
			byte[] before = File.ReadAllBytes(zip);

			int exit = await UploadCommands.MainAsync(["upload", zip], new StringReader("y\n"), output, error, store, _ => Uploader(handler));

			Assert.Equal(0, exit);
			Assert.Single(handler.Requests);
			Assert.DoesNotContain(Code, output.ToString() + error.ToString());
			// The zip goes out exactly as written, with no trace of the code.
			Assert.Equal(before, File.ReadAllBytes(zip));
			Assert.False(Contains(before, Encoding.UTF8.GetBytes(Code)));
			using ZipArchive archive = ZipFile.OpenRead(zip);
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				using StreamReader reader = new(entry.Open());
				Assert.DoesNotContain(Code, reader.ReadToEnd());
			}
		}

		[Fact]
		public async Task Upload_with_yes_sends_without_asking()
		{
			UploadCodeStore store = Stored();
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));

			int exit = await UploadCommands.MainAsync(["upload", Report(), "--yes"], new StringReader(""), new StringWriter(), new StringWriter(), store, _ => Uploader(handler));

			Assert.Equal(0, exit);
			Assert.Single(handler.Requests);
		}

		[Fact]
		public async Task A_report_with_the_users_presets_is_described_as_not_anonymised()
		{
			StringWriter output = new();

			await UploadCommands.OfferAsync(Report(includeUserPresets: true), yes: false, new StringReader("n\n"), output, Stored(),
				Uploader(new FakeHandler(Reply(HttpStatusCode.Created, CreatedJson))), CancellationToken.None);

			Assert.Contains("Anonymised: no", output.ToString());
		}

		[Fact]
		public async Task Upload_without_a_saved_code_sends_nothing_and_says_how_to_log_in()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.Created, CreatedJson));
			StringWriter output = new();

			int exit = await UploadCommands.MainAsync(["upload", Report(), "--yes"], new StringReader(""), output, new StringWriter(),
				new UploadCodeStore(Path.Combine(folder, "empty")), _ => Uploader(handler));

			Assert.Equal(5, exit);
			Assert.Empty(handler.Requests);
			Assert.Contains("fractone-probe login", output.ToString());
		}

		[Fact]
		public async Task A_failed_upload_keeps_the_code_out_of_the_output_and_says_how_to_retry()
		{
			FakeHandler handler = new(Reply(HttpStatusCode.Unauthorized, "{\"error\":\"bad code\"}"));
			StringWriter output = new();

			await UploadCommands.MainAsync(["upload", Report(), "--yes"], new StringReader(""), output, new StringWriter(), Stored(), _ => Uploader(handler));

			Assert.Contains("fractone-probe upload", output.ToString());
			Assert.DoesNotContain(Code, output.ToString());
		}

		[Fact]
		public void A_run_parses_the_upload_options()
		{
			CliOptions options = Program.Parse(["--slot", "500", "--upload", "--yes", "--site", "http://localhost:8080"]);

			Assert.True(options.Upload);
			Assert.True(options.Yes);
			Assert.Equal("http://localhost:8080/", options.Site!.AbsoluteUri);
			Assert.Throws<ArgumentException>(() => Program.Parse(["--slot", "500", "--site", "http://example.com"]));
		}

		private UploadCodeStore Stored()
		{
			UploadCodeStore store = new(Path.Combine(folder, "store"));
			store.Save(Code);
			return store;
		}

		private static bool Contains(byte[] haystack, byte[] needle) =>
			haystack.AsSpan().IndexOf(needle) >= 0;
	}
}
