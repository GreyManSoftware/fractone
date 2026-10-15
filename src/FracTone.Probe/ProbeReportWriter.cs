using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FracTone.Probe
{
	public sealed record ProbeReportFiles(string Zip, string? Backups, IReadOnlyList<string> Entries);

	// One zip per run: run.json (every step's outcome, failures included), log.txt, exchanges.txt (every frame sent and
	// received, hex) and readbacks/*.syx exactly as the device sent them. Anonymised by default: the user's preset names
	// become placeholders and their presets' frames are withheld from the zip; the backups are saved beside it, on this PC
	// only. No paths go into the zip.
	public static class ProbeReportWriter
	{
		public static ProbeReportFiles Write(ProbeRun run, string folder, bool includeUserPresets = false)
		{
			ArgumentNullException.ThrowIfNull(run);
			Directory.CreateDirectory(folder);
			string stamp = run.StartedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			string device = (run.Device ?? "unknown").Replace(' ', '-').Replace('/', '-');
			string zipPath = Path.Combine(folder, $"fractone-probe-{device}-{stamp}.zip");
			string? backups = null;
			if (run.Readbacks.Any(readback => readback.Content == ProbeContent.User))
			{
				backups = Path.Combine(folder, $"fractone-probe-{device}-{stamp}-backups");
				Directory.CreateDirectory(backups);
				foreach (ProbeReadback readback in run.Readbacks.Where(readback => readback.Content == ProbeContent.User))
					File.WriteAllBytes(Path.Combine(backups, readback.Name), readback.ToSyx());
			}

			List<string> entries = [];
			using (FileStream stream = new(zipPath, FileMode.Create, FileAccess.Write))
			using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
			{
				void Add(string name, byte[] bytes)
				{
					ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
					using Stream target = entry.Open();
					target.Write(bytes);
					entries.Add(name);
				}
				Add("run.json", JsonSerializer.SerializeToUtf8Bytes(Anonymise(run, includeUserPresets), ProbeScript.Json));
				Add("log.txt", Encoding.UTF8.GetBytes(string.Join('\n', run.Log.Select(line => Redact(run, line, includeUserPresets))) + "\n"));
				Add("exchanges.txt", Encoding.UTF8.GetBytes(Exchanges(run, includeUserPresets)));
				foreach (ProbeReadback readback in run.Readbacks.Where(readback => includeUserPresets || readback.Content != ProbeContent.User))
					Add($"readbacks/{readback.Name}", readback.ToSyx());
			}
			return new ProbeReportFiles(zipPath, backups, entries);
		}

		// The run as uploaded: names replaced, everything else as recorded.
		public static object Anonymise(ProbeRun run, bool includeUserPresets) => new
		{
			run.Tool,
			run.ToolVersion,
			run.Os,
			run.StartedAt,
			run.FinishedAt,
			run.Device,
			run.Model,
			run.Firmware,
			run.DeviceVariant,
			run.DeviceVariantVerified,
			run.PresetSlots,
			run.FirmwareScripted,
			run.Script,
			run.LevelRequested,
			run.LevelRun,
			run.Slot,
			run.PresetFound,
			PresetFoundName = run.PresetFoundName is null ? null : includeUserPresets ? run.PresetFoundName : "[preset found]",
			run.SceneFound,
			run.UnsavedEdits,
			run.Outcome,
			StopReason = run.StopReason is null ? null : Redact(run, run.StopReason, includeUserPresets),
			Steps = run.Steps.Select(step => step with { Message = Redact(run, step.Message, includeUserPresets) }),
			run.Restore,
			UserPresetsIncluded = includeUserPresets
		};

		private static string Exchanges(ProbeRun run, bool includeUserPresets)
		{
			StringBuilder text = new();
			text.Append("# Every frame sent (>>) and received (<<), hex, in order. ");
			text.Append(includeUserPresets ? "User presets included.\n" : "The user's own presets and preset names are withheld.\n");
			foreach (ProbeExchange exchange in run.Exchanges)
			{
				string at = exchange.At.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
				text.Append(CultureInfo.InvariantCulture, $"{at} {exchange.Step} {exchange.Outcome}\n");
				text.Append(">> ").Append(Frame(exchange.Sent, exchange.Content, includeUserPresets)).Append('\n');
				foreach (byte[] received in exchange.Received)
					text.Append("<< ").Append(Frame(received, exchange.Content, includeUserPresets)).Append('\n');
			}
			return text.ToString();
		}

		private static string Frame(byte[] frame, ProbeContent content, bool includeUserPresets)
		{
			if (includeUserPresets || frame.Length < 8)
				return ProbeFrames.Hex(frame);
			// A user preset's dump frames carry their names and settings.
			if (content == ProbeContent.User && frame[5] is 0x77 or 0x78 or 0x79)
				return $"[user preset frame 0x{frame[5]:X2}, {frame.Length} bytes, withheld]";
			// 0D replies carry the preset's name (32 characters from byte 8).
			if (frame[5] == 0x0D && frame.Length >= 42)
			{
				byte[] copy = (byte[])frame.Clone();
				Array.Fill(copy, (byte)0x00, 8, 32);
				return ProbeFrames.Hex(copy.AsSpan(0, 8)) + " [name withheld] " + ProbeFrames.Hex(copy.AsSpan(40));
			}
			return ProbeFrames.Hex(frame);
		}

		private static string Redact(ProbeRun run, string text, bool includeUserPresets) =>
			includeUserPresets || string.IsNullOrWhiteSpace(run.PresetFoundName)
				? text
				: text.Replace(run.PresetFoundName, "[preset found]", StringComparison.Ordinal);
	}
}
