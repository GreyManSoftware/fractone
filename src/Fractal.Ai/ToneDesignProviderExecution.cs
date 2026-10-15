using System.Net;
using System.Diagnostics;
using Fractal.Application;

namespace Fractal.Ai
{
	public sealed class ToneDesignProviderResilienceOptions
	{
		public int EndToEndTimeoutSeconds { get; set; } = 300;

		public int AttemptTimeoutSeconds { get; set; } = 120;

		public int MaximumCorrectionAttempts { get; set; } = 2;

		internal TimeSpan EndToEndTimeout =>
			TimeSpan.FromSeconds(EndToEndTimeoutSeconds);

		internal TimeSpan AttemptTimeout =>
			TimeSpan.FromSeconds(AttemptTimeoutSeconds);

		// The clock the timeouts run on; tests fire them without waiting whole seconds. Internal, so configuration
		// binding never sees it.
		internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;
	}

	internal static class ToneDesignProviderExecution
	{
		public static async Task<T> ExecuteAsync<T>(
			string provider,
			ToneDesignProviderResilienceOptions options,
			CancellationToken cancellationToken,
			Func<CancellationToken, Task<T>> operation)
		{
			long started = Stopwatch.GetTimestamp();
			using Activity? activity = ToneDesignTelemetry.StartProviderActivity(provider);
			using CancellationTokenSource limit = new(options.EndToEndTimeout, options.TimeProvider);
			using CancellationTokenSource timeoutSource =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

			try
			{
				T result = await operation(timeoutSource.Token);
				ToneDesignTelemetry.RecordProvider(
					provider,
					"succeeded",
					Stopwatch.GetElapsedTime(started));
				activity?.SetStatus(ActivityStatusCode.Ok);
				return result;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				ToneDesignTelemetry.RecordProvider(
					provider,
					"cancelled",
					Stopwatch.GetElapsedTime(started));
				activity?.SetTag("outcome", "cancelled");
				throw;
			}
			catch (OperationCanceledException exception)
			{
				RecordFailure(
					provider,
					ToneDesignProviderErrorKind.Timeout,
					started,
					activity);
				throw new ToneDesignProviderException(
					ToneDesignProviderErrorKind.Timeout,
					provider,
					$"{provider} did not complete within the {options.EndToEndTimeoutSeconds}-second end-to-end limit.",
					exception);
			}
			catch (ToneDesignProviderException exception)
			{
				RecordFailure(provider, exception.Kind, started, activity);
				throw;
			}
			catch (ToneDesignChatRequestValidationException)
			{
				ToneDesignTelemetry.RecordProvider(
					provider,
					"request-validation",
					Stopwatch.GetElapsedTime(started));
				activity?.SetTag("outcome", "request-validation");
				throw;
			}
			catch (Exception exception)
			{
				RecordFailure(
					provider,
					ToneDesignProviderErrorKind.Internal,
					started,
					activity);
				throw new ToneDesignProviderException(
					ToneDesignProviderErrorKind.Internal,
					provider,
					$"{provider} failed while processing the tone-design request.",
					exception);
			}
		}

		private static void RecordFailure(
			string provider,
			ToneDesignProviderErrorKind kind,
			long started,
			Activity? activity)
		{
			string errorClass = ToneDesignTelemetry.ErrorClass(kind);
			ToneDesignTelemetry.RecordProvider(
				provider,
				"failed",
				Stopwatch.GetElapsedTime(started),
				kind);
			activity?.SetTag("outcome", "failed");
			activity?.SetTag("error.type", errorClass);
			activity?.SetStatus(ActivityStatusCode.Error, errorClass);
		}

		public static async Task<T> ExecuteAttemptAsync<T>(
			string provider,
			ToneDesignProviderResilienceOptions options,
			CancellationToken overallToken,
			Func<CancellationToken, Task<T>> operation)
		{
			using CancellationTokenSource limit = new(options.AttemptTimeout, options.TimeProvider);
			using CancellationTokenSource attemptSource =
				CancellationTokenSource.CreateLinkedTokenSource(overallToken, limit.Token);

			try
			{
				return await operation(attemptSource.Token);
			}
			catch (OperationCanceledException) when (overallToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException exception)
			{
				throw new ToneDesignProviderException(
					ToneDesignProviderErrorKind.Timeout,
					provider,
					$"{provider} did not complete an attempt within the {options.AttemptTimeoutSeconds}-second limit.",
					exception);
			}
			catch (HttpRequestException exception)
			{
				throw new ToneDesignProviderException(
					ToneDesignProviderErrorKind.Unavailable,
					provider,
					$"{provider} is unavailable.",
					exception);
			}
		}

		public static void EnsureSuccess(
			string provider,
			HttpResponseMessage response)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw CreateStatusException(provider, response);
			}
		}

		public static ToneDesignProviderException InvalidResponse(
			string provider,
			Exception exception)
		{
			return new ToneDesignProviderException(
				ToneDesignProviderErrorKind.InvalidResponse,
				provider,
				$"{provider} returned an invalid tone-design response.",
				exception)
			{
				Issues = [exception.Message]
			};
		}

		private static ToneDesignProviderException CreateStatusException(
			string provider,
			HttpResponseMessage response)
		{
			HttpStatusCode statusCode = response.StatusCode;
			if (statusCode == HttpStatusCode.TooManyRequests)
			{
				return new ToneDesignProviderException(
					ToneDesignProviderErrorKind.RateLimited,
					provider,
					$"{provider} rate-limited the tone-design request.")
				{
					RetryAfter = response.Headers.RetryAfter?.Delta
				};
			}

			if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
			{
				return new ToneDesignProviderException(
					ToneDesignProviderErrorKind.Timeout,
					provider,
					$"{provider} reported that the tone-design request timed out.");
			}

			if (statusCode is HttpStatusCode.BadGateway or
				HttpStatusCode.ServiceUnavailable or
				HttpStatusCode.Unauthorized or
				HttpStatusCode.Forbidden or
				HttpStatusCode.NotFound)
			{
				return new ToneDesignProviderException(
					ToneDesignProviderErrorKind.Unavailable,
					provider,
					$"{provider} is unavailable or incorrectly configured.");
			}

			return new ToneDesignProviderException(
				statusCode >= HttpStatusCode.InternalServerError
					? ToneDesignProviderErrorKind.Internal
					: ToneDesignProviderErrorKind.InvalidResponse,
				provider,
				$"{provider} rejected the tone-design request with HTTP {(int)statusCode}.");
		}
	}
}
