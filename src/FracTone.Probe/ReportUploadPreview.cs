using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FracTone.Probe
{
	public sealed record ReportUploadEntry(string Name, long Bytes);

	// What an upload would send, read from the zip itself rather than from the run, so an older or hand-picked zip is
	// described truthfully before the user says yes.
	public sealed record ReportUploadPreview(string ZipPath, long Bytes, IReadOnlyList<ReportUploadEntry> Entries, bool? Anonymised)
	{
		public static ReportUploadPreview Read(string zipPath)
		{
			FileInfo file = new(zipPath);
			List<ReportUploadEntry> entries = [];
			bool? anonymised = null;
			using (ZipArchive zip = ZipFile.OpenRead(file.FullName))
			{
				foreach (ZipArchiveEntry entry in zip.Entries)
				{
					entries.Add(new ReportUploadEntry(entry.FullName, entry.Length));
					if (entry.FullName == "run.json" && entry.Length < 16 * 1024 * 1024)
						anonymised = ReadAnonymised(entry);
				}
			}
			return new ReportUploadPreview(file.FullName, file.Length, entries, anonymised);
		}

		private static bool? ReadAnonymised(ZipArchiveEntry entry)
		{
			try
			{
				using Stream stream = entry.Open();
				using JsonDocument document = JsonDocument.Parse(stream);
				return document.RootElement.ValueKind == JsonValueKind.Object
					&& document.RootElement.TryGetProperty("userPresetsIncluded", out JsonElement included)
					&& included.ValueKind is JsonValueKind.True or JsonValueKind.False
					? !included.GetBoolean()
					: null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		public string Describe()
		{
			StringBuilder text = new();
			text.Append(CultureInfo.InvariantCulture, $"This will send {Path.GetFileName(ZipPath)} ({Size(Bytes)}) to your FracTone account:\n");
			foreach (ReportUploadEntry entry in Entries)
				text.Append(CultureInfo.InvariantCulture, $"  {entry.Name} ({Size(entry.Bytes)})\n");
			text.Append("Anonymised: ").Append(Anonymised switch
			{
				true => "yes (your preset names and your own presets are withheld)",
				false => "no (your own presets and preset names are included)",
				null => "unknown (run.json doesn't say)"
			}).Append('\n');
			return text.ToString();
		}

		private static string Size(long bytes) =>
			bytes < 1024 ? $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes"
			: bytes < 1024 * 1024 ? $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB"
			: $"{(bytes / (1024.0 * 1024)).ToString("0.##", CultureInfo.InvariantCulture)} MB";
	}
}
