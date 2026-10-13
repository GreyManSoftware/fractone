using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Fractal.Fm9
{
	// A minimal Chrome DevTools Protocol client over the browser's WebSocket: commands with ids, replies matched to them,
	// and events handed to one handler. Enough for MediaCapturer; flat sessions carry a page's sessionId.
	internal sealed class DevToolsClient : IAsyncDisposable
	{
		private readonly ClientWebSocket _socket = new();
		private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
		private readonly CancellationTokenSource _closing = new();
		private readonly SemaphoreSlim _send = new(1, 1);
		private Task _receive = Task.CompletedTask;
		private int _next;

		public Action<string, JsonElement, string?>? OnEvent { get; set; }

		public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
		{
			_socket.Options.KeepAliveInterval = TimeSpan.Zero;
			await _socket.ConnectAsync(endpoint, cancellationToken);
			_receive = Task.Run(ReceiveAsync);
		}

		public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null,
			CancellationToken cancellationToken = default, TimeSpan? timeout = null)
		{
			int id = Interlocked.Increment(ref _next);
			TaskCompletionSource<JsonElement> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_pending[id] = reply;
			Dictionary<string, object?> message = new() { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } };
			if (sessionId is not null) message["sessionId"] = sessionId;
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
			await _send.WaitAsync(cancellationToken);
			try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
			finally { _send.Release(); }
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
			using (limit.Token.Register(() => reply.TrySetException(new TimeoutException($"The browser did not answer {method}."))))
				try { return await reply.Task; }
				finally { _pending.TryRemove(id, out _); }
		}

		private async Task ReceiveAsync()
		{
			byte[] buffer = new byte[1 << 16];
			using MemoryStream message = new();
			try
			{
				while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
				{
					WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, _closing.Token);
					if (result.MessageType == WebSocketMessageType.Close) break;
					message.Write(buffer, 0, result.Count);
					if (!result.EndOfMessage) continue;
					Dispatch(message.ToArray());
					message.SetLength(0);
				}
			}
			catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
			foreach (TaskCompletionSource<JsonElement> waiting in _pending.Values)
				waiting.TrySetException(new IOException("The browser closed the connection."));
		}

		private void Dispatch(byte[] bytes)
		{
			using JsonDocument document = JsonDocument.Parse(bytes);
			JsonElement root = document.RootElement;
			if (root.TryGetProperty("id", out JsonElement id) && _pending.TryGetValue(id.GetInt32(), out TaskCompletionSource<JsonElement>? reply))
			{
				if (root.TryGetProperty("error", out JsonElement error))
					reply.TrySetException(new InvalidOperationException($"The browser refused: {error.GetProperty("message").GetString()}"));
				else
					reply.TrySetResult(root.TryGetProperty("result", out JsonElement result) ? result.Clone() : default);
			}
			else if (root.TryGetProperty("method", out JsonElement method))
			{
				OnEvent?.Invoke(method.GetString()!, root.TryGetProperty("params", out JsonElement parameters) ? parameters.Clone() : default,
					root.TryGetProperty("sessionId", out JsonElement session) ? session.GetString() : null);
			}
		}

		public async ValueTask DisposeAsync()
		{
			await _closing.CancelAsync();
			try
			{
				if (_socket.State == WebSocketState.Open)
					await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
			}
			catch (Exception exception) when (exception is WebSocketException or TimeoutException or OperationCanceledException) { }
			try { await _receive.WaitAsync(TimeSpan.FromSeconds(2)); }
			catch (TimeoutException) { }
			_socket.Dispose();
			_closing.Dispose();
			_send.Dispose();
		}
	}
}
