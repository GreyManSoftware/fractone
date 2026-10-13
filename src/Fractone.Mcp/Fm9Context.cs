using System.Collections.Concurrent;
using System.Globalization;
using Fractal.Companion.Protocol;
using System.Security.Cryptography;
using Fractal.Fm9;
using ModelContextProtocol;

namespace Fractone.Mcp
{
	// What every tool shares: the settings (re-read on each call, so edits apply at once), the one FM9 connection, the
	// confirmation codes and where files go.
	public sealed class Fm9Context : IDisposable
	{
		private readonly string _settingsPath;
		private readonly ConcurrentDictionary<string, (string Action, DateTimeOffset Expires)> _confirmations = new();

		public Fm9Context(string settingsPath, Func<Fm9Connection>? connection = null)
		{
			_settingsPath = settingsPath;
			Connection = connection?.Invoke() ?? Fm9Connection.ForSettings(() => Settings.Midi);
		}

		public Fm9Settings Settings => Fm9Settings.Load(_settingsPath);
		public string SettingsPath => _settingsPath;
		public Fm9Connection Connection { get; }
		public Func<TimeSpan, CancellationToken, Task>? Wait { get; init; }

		public Fm9Device Device()
		{
			Fm9Settings settings = Settings;
			return Wait is null ? new Fm9Device(Connection, settings) : new Fm9Device(Connection, settings) { Wait = Wait };
		}

		public Fm9Live Live()
		{
			string state = Path.Combine(Settings.WorkFolder, ".state");
			return Wait is null ? new Fm9Live(Connection, state) : new Fm9Live(Connection, state) { Wait = Wait };
		}

		// A preset to compare: a file, a slot number, "live" (the edit buffer) or "last-load" (what these tools last loaded).
		public async Task<(Fm9UploadedPreset Preset, string Label)> PresetAsync(string source, CancellationToken cancellationToken)
		{
			string text = source.Trim();
			if (text.Equals("live", StringComparison.OrdinalIgnoreCase))
			{
				Fm9ReadResult read = await Device().ReadEditBufferAsync(Path.Combine("reads", $"edit buffer {DateTime.Now:yyyyMMdd-HHmmss}.syx"), cancellationToken);
				return (Fm9Presets.Load(read.Path), "the edit buffer");
			}
			if (text.Equals("last-load", StringComparison.OrdinalIgnoreCase))
			{
				string last = Device().LastLoad;
				return File.Exists(last) ? (Fm9Presets.Load(last), "the last load") : throw new McpException("Nothing has been loaded by these tools yet.");
			}
			string number = text.StartsWith("slot", StringComparison.OrdinalIgnoreCase) ? text[4..].Trim() : text;
			if (int.TryParse(number, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
			{
				Fm9PresetDump dump = await Device().ReadSlotAsync(slot, cancellationToken) ?? throw new McpException($"Preset {slot} is empty.");
				return (Fractal.Companion.Protocol.Fm9UploadedPreset.Read(dump), $"preset {slot}");
			}
			return (Fm9Presets.Load(Input(text)), Path.GetFileName(text));
		}

		// What a store would change in a slot, as diff lines (the first few, then a count).
		public async Task<IReadOnlyList<string>> StoreSummaryAsync(int slot, Fractal.Companion.Protocol.Fm9PresetDump incoming, CancellationToken cancellationToken)
		{
			Fractal.Companion.Protocol.Fm9PresetDump? current = await Device().ReadSlotAsync(slot, cancellationToken);
			if (current is null) return [$"Preset {slot} is empty."];
			try
			{
				IReadOnlyList<string> lines = Fm9Diff.Compare(Fractal.Companion.Protocol.Fm9UploadedPreset.Read(current),
					Fractal.Companion.Protocol.Fm9UploadedPreset.Read(incoming)).Lines();
				const int Shown = 15;
				return lines.Count == 0 ? ["No differences from what the slot holds."]
					: lines.Count <= Shown ? lines : [.. lines.Take(Shown), $"... and {lines.Count - Shown} more."];
			}
			catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
			{
				return [$"Could not compare with what the slot holds: {exception.Message}"];
			}
		}

		public string IndexPath => Path.Combine(Settings.WorkFolder, "index.json");

		// An input path resolved against the work folder; it must exist.
		public string Input(string path)
		{
			string full = Settings.Resolve(path);
			return File.Exists(full) ? full : throw new McpException($"No file at {full}.");
		}

		// Where an edit goes: the path asked for, or "edits/<input name> (<what>) <time>.syx" in the work folder.
		public string Output(string? output, string input, string what)
		{
			if (output is not null) return Settings.Resolve(output);
			string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			return Path.Combine(Settings.WorkFolder, "edits", $"{Path.GetFileNameWithoutExtension(input)} ({what}) {stamp}.syx");
		}

		// With confirmWrites on, a write first returns a one-time code describing exactly what it will do; the same call
		// with that code goes ahead. Codes last ten minutes and only for the action they describe.
		public string? RequireConfirmation(string action, string? code)
		{
			if (!Settings.Safeguards.ConfirmWrites) return null;
			DateTimeOffset now = DateTimeOffset.UtcNow;
			foreach (KeyValuePair<string, (string Action, DateTimeOffset Expires)> stale in _confirmations.Where(pair => pair.Value.Expires < now))
				_confirmations.TryRemove(stale.Key, out _);
			if (code is not null && _confirmations.TryRemove(code.Trim().ToUpperInvariant(), out (string Action, DateTimeOffset Expires) issued) &&
				issued.Action == action)
				return null;
			string fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
			_confirmations[fresh] = (action, now.AddMinutes(10));
			return fresh;
		}

		public void Dispose() => Connection.Dispose();

		// Runs a tool body, turning the library's own failures into messages the agent sees.
		public static async Task<T> Run<T>(Func<Task<T>> body)
		{
			try
			{
				return await body();
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or
				InvalidDataException or FileNotFoundException or DirectoryNotFoundException or TimeoutException or IOException or
				PlatformNotSupportedException or System.Text.Json.JsonException or HttpRequestException)
			{
				throw new McpException(exception.Message, exception);
			}
		}

		public static Task<T> RunSync<T>(Func<T> body) => Run(() => Task.FromResult(body()));
	}
}
