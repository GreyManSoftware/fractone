using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FracTone.Probe
{
	// Posts a report zip to the site's probe-report API (docs/accounts.md, "Probe report uploads"). One retry on a 5xx
	// or a network failure, and one wait on a 429 when the site says how long; everything else is final, because
	// retrying a refused report or code can't change the answer. Messages are for players, and never carry the code.
	public sealed class HttpReportUploader : IReportUploader
	{
		public static readonly Uri DefaultSite = new("https://fractone.greymansoftware.com/");
		public const long MaxBytes = 20L * 1024 * 1024;
		public const string CodePrefix = "ftu_";

		private readonly HttpClient client;
		private readonly Uri endpoint;
		private readonly TimeSpan timeout;
		private readonly TimeSpan retryDelay;
		private readonly TimeSpan longestRetryAfter;
		private readonly Func<TimeSpan, CancellationToken, Task> delay;

		public HttpReportUploader(
			HttpClient client,
			Uri? site = null,
			TimeSpan? timeout = null,
			TimeSpan? retryDelay = null,
			TimeSpan? longestRetryAfter = null,
			Func<TimeSpan, CancellationToken, Task>? delay = null)
		{
			ArgumentNullException.ThrowIfNull(client);
			this.client = client;
			Uri baseUri = site ?? DefaultSite;
			if (!baseUri.AbsoluteUri.EndsWith('/'))
				baseUri = new Uri(baseUri.AbsoluteUri + "/");
			endpoint = new Uri(baseUri, "api/probe-reports");
			this.timeout = timeout ?? TimeSpan.FromMinutes(2);
			this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
			this.longestRetryAfter = longestRetryAfter ?? TimeSpan.FromMinutes(2);
			this.delay = delay ?? Task.Delay;
		}

		public Uri Endpoint => endpoint;

		public async Task<ReportUploadResult> UploadAsync(string zipPath, string uploadCode, CancellationToken cancellationToken)
		{
			if (!IsWellFormedCode(uploadCode))
				return new ReportUploadResult(false, "No valid upload code is saved. Make one on your FracTone account page, then run: fractone-probe login --code ftu_...");
			FileInfo file = new(zipPath);
			if (!file.Exists)
				return new ReportUploadResult(false, $"There is no report at {zipPath}.");
			if (file.Length > MaxBytes)
				return new ReportUploadResult(false, "The report is larger than the site's 20 MB limit, so it wasn't sent.");

			bool retriedFailure = false, waitedForRateLimit = false;
			while (true)
			{
				Attempt attempt = await SendAsync(file.FullName, uploadCode, cancellationToken);
				if (attempt.Result is not null)
					return attempt.Result;
				if (attempt.RetryAfter is TimeSpan wait)
				{
					if (waitedForRateLimit || wait > longestRetryAfter)
						return new ReportUploadResult(false, $"The site is limiting uploads right now (at most 20 an hour). Try again in {Describe(wait)}.");
					waitedForRateLimit = true;
					await delay(wait, cancellationToken);
					continue;
				}
				if (retriedFailure)
					return new ReportUploadResult(false, attempt.Failure!);
				retriedFailure = true;
				await delay(retryDelay, cancellationToken);
			}
		}

		public static bool IsWellFormedCode(string? code) =>
			!string.IsNullOrEmpty(code)
			&& code.StartsWith(CodePrefix, StringComparison.Ordinal)
			&& code.Length is > 8 and <= 200
			&& code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

		// One outcome per attempt: a final result, a wait the site asked for, or a retryable failure.
		private sealed record Attempt(ReportUploadResult? Result, TimeSpan? RetryAfter = null, string? Failure = null);

		private async Task<Attempt> SendAsync(string zipPath, string uploadCode, CancellationToken cancellationToken)
		{
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			limit.CancelAfter(timeout);
			try
			{
				// A fresh stream per attempt: a retry must send the whole zip again.
				await using FileStream body = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
				using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadCode);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
				request.Content = new StreamContent(body);
				request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
				using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token);
				string text = await response.Content.ReadAsStringAsync(limit.Token);
				return Interpret(response, text);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				return new Attempt(null, Failure: $"The site didn't answer within {Describe(timeout)}. Your report is still saved; try again later.");
			}
			catch (HttpRequestException exception)
			{
				// The exception text names the host and the socket error; it never holds request headers.
				return new Attempt(null, Failure: $"Couldn't reach {endpoint.Host}: {exception.Message} Check your internet connection and try again.");
			}
		}

		private static Attempt Interpret(HttpResponseMessage response, string text)
		{
			int status = (int)response.StatusCode;
			string? error = ReadString(text, "error");
			string detail = string.IsNullOrWhiteSpace(error) ? "" : $" The site said: {Trim(error)}";
			switch (response.StatusCode)
			{
				case HttpStatusCode.Created or HttpStatusCode.OK:
					string? id = ReadString(text, "id");
					string device = ReadString(text, "device") ?? "unknown device";
					string firmware = ReadString(text, "firmware") ?? "unknown";
					string outcome = ReadString(text, "outcome") ?? "unknown";
					return new Attempt(new ReportUploadResult(true,
						$"Uploaded: report {id ?? "(no id)"} for the {device}, firmware {firmware}, outcome {outcome}. Thank you.", id));
				case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
					return new Attempt(new ReportUploadResult(false,
						"The site didn't accept your upload code: it may have been revoked or replaced. Make a new one on your FracTone account page, then run: fractone-probe login --code ftu_..."));
				case HttpStatusCode.BadRequest:
					return new Attempt(new ReportUploadResult(false, "The site refused the file as not a valid probe report." + detail));
				case HttpStatusCode.RequestEntityTooLarge:
					return new Attempt(new ReportUploadResult(false, "The report is larger than the site's 20 MB limit."));
				case HttpStatusCode.UnsupportedMediaType:
					return new Attempt(new ReportUploadResult(false, "The site refused the upload's file type. Please update fractone-probe and try again."));
				case HttpStatusCode.Conflict:
					return new Attempt(new ReportUploadResult(false,
						"Your account already holds the most reports the site keeps (200). Ask the FracTone admin to clear old ones, then try again."));
				case HttpStatusCode.TooManyRequests:
					TimeSpan wait = RetryAfter(response) ?? TimeSpan.FromMinutes(1);
					return new Attempt(null, RetryAfter: wait);
			}
			if (status >= 500)
				return new Attempt(null, Failure: $"The site had a problem (HTTP {status}). Your report is still saved; try again later.");
			return new Attempt(new ReportUploadResult(false, $"The site answered with an unexpected HTTP {status}." + detail));
		}

		private static TimeSpan? RetryAfter(HttpResponseMessage response)
		{
			RetryConditionHeaderValue? header = response.Headers.RetryAfter;
			if (header?.Delta is TimeSpan delta)
				return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
			if (header?.Date is DateTimeOffset date)
			{
				TimeSpan until = date - DateTimeOffset.UtcNow;
				return until < TimeSpan.Zero ? TimeSpan.Zero : until;
			}
			return null;
		}

		private static string? ReadString(string json, string name)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(json);
				return document.RootElement.ValueKind == JsonValueKind.Object
					&& document.RootElement.TryGetProperty(name, out JsonElement value)
					? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
					: null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		// The site's error text is shown as data; keep it to one short line.
		private static string Trim(string text)
		{
			string line = text.ReplaceLineEndings(" ").Trim();
			return line.Length <= 200 ? line : line[..200] + "...";
		}

		private static string Describe(TimeSpan span) =>
			span.TotalSeconds < 90
				? $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} seconds"
				: $"{((int)Math.Ceiling(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} minutes";
	}
}
