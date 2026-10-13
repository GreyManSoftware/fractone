using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Fm9
{
	// The user's choices for how careful writes are. All off by default; Careful switches every one on. Whatever the
	// setting, every write reports what it replaced.
	public sealed record Fm9Safeguards
	{
		// Preset slots that may be written, as numbers or ranges ("500-505"). Empty means every slot.
		public IReadOnlyList<string> WritableSlots { get; init; } = [];
		// Keep a copy of the slot's previous preset (and the readbacks) in the backups folder.
		public bool BackupBeforeStore { get; init; }
		// Read the stored slot back after a store and compare it with what was sent.
		public bool VerifyAfterStore { get; init; }
		// Refuse to load into the edit buffer when it holds changes that are neither the last thing these tools loaded nor
		// the stored preset, i.e. unsaved edits someone made on the FM9 or in FM9-Edit.
		public bool RefuseLoadIfBufferChanged { get; init; }
		// Writes need a second call carrying a one-time confirmation code from the first (enforced by the MCP bridge).
		public bool ConfirmWrites { get; init; }

		public static Fm9Safeguards Careful { get; } = new()
		{
			BackupBeforeStore = true,
			VerifyAfterStore = true,
			RefuseLoadIfBufferChanged = true,
			ConfirmWrites = true
		};

		public bool AllowsSlot(int slot) => WritableSlots.Count == 0 || WritableSlots.Any(range => Contains(range, slot));

		private static bool Contains(string range, int slot)
		{
			string[] parts = range.Split('-', 2, StringSplitOptions.TrimEntries);
			int first = int.Parse(parts[0], CultureInfo.InvariantCulture);
			int last = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : first;
			return slot >= first && slot <= last;
		}
	}

	public sealed record Fm9MidiSettings
	{
		// Part of the port names to use (Windows lists the FM9 as "FM9" or "2- FM9" and so on).
		public string Input { get; init; } = "FM9";
		public string Output { get; init; } = "FM9";
		// The FM9's MIDI channel for preset changes.
		public int Channel { get; init; } = 1;
	}

	public sealed record Fm9Settings
	{
		public Fm9MidiSettings Midi { get; init; } = new();
		// Where presets, backups, readbacks, recordings and the preset index live. Relative tool paths resolve here.
		public string WorkFolder { get; init; } = DefaultWorkFolder;
		// FM9-Edit's block library (Documents\Fractal Audio\FM9-Edit\blocks).
		public string LibraryFolder { get; init; } = DefaultLibraryFolder;
		public Fm9Safeguards Safeguards { get; init; } = new();

		public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fractone");

		public static string DefaultPath => Path.Combine(Folder, "settings.json");

		// Before the Fractone name the settings lived in %APPDATA%\fm9-tools; they move on first use. Returns what moved.
		public static IReadOnlyList<string> MigrateFromOldFolder()
		{
			string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fm9-tools");
			if (!Directory.Exists(old) || File.Exists(DefaultPath)) return [];
			Directory.CreateDirectory(Folder);
			List<string> moved = [];
			foreach (string name in new[] { "settings.json", "http-token" })
				if (File.Exists(Path.Combine(old, name)) && !File.Exists(Path.Combine(Folder, name)))
				{
					File.Copy(Path.Combine(old, name), Path.Combine(Folder, name));
					moved.Add(name);
				}
			return moved;
		}

		// The stem separator's add-on (Python, PyTorch, Demucs, caches and the model; up to 5 GB), installed on first use.
		public string SeparatorFolder { get; init; } = DefaultSeparatorFolder;

		public static string DefaultSeparatorFolder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fractone", "separator");

		// The headless browser capture_media uses (about 100 MB), installed on first use.
		public string BrowserFolder { get; init; } = DefaultBrowserFolder;

		public static string DefaultBrowserFolder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fractone", "browser");

		public static string DefaultWorkFolder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fractone");

		public static string DefaultLibraryFolder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fractal Audio", "FM9-Edit", "blocks");

		public string BackupFolder => Path.Combine(WorkFolder, "backups");

		private static readonly JsonSerializerOptions Json = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			ReadCommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never
		};

		// The settings file, or the defaults when there is none (the bridge works before setup has run).
		public static Fm9Settings Load(string? path = null)
		{
			string file = path ?? DefaultPath;
			return File.Exists(file) ? JsonSerializer.Deserialize<Fm9Settings>(File.ReadAllText(file), Json) ?? new() : new();
		}

		public void Save(string? path = null)
		{
			string file = path ?? DefaultPath;
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
		}

		// A tool's path argument: absolute as given, otherwise inside the work folder.
		public string Resolve(string path)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(path);
			return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkFolder, path));
		}
	}
}
