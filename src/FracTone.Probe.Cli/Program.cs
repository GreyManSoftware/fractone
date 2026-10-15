using System.Globalization;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;
using Fractal.Fm9;

namespace FracTone.Probe.Cli
{
	internal sealed record CliOptions(
		int Slot,
		int Level,
		bool DryRun,
		string Out,
		string? DevicePort,
		string DryRunDevice,
		string? DryRunFirmware,
		bool IncludeUserPresets)
	{
		// After the run (failed runs too): show the report and upload it if the user agrees, or straight away with --yes.
		public bool Upload { get; init; }
		public bool Yes { get; init; }
		public Uri? Site { get; init; }
		public bool AcceptDisclaimer { get; init; }
	}

	internal static class Program
	{
		private const string Usage =
			"fractone-probe --slot N [--level 1|2|3] [--dry-run] [--out dir] [--device-port name]\n" +
			"  --slot N          the preset slot you've chosen as safe to use (required); it is backed up and put back\n" +
			"  --level 1|2|3     1 reads only (default); 2 also loads test presets into the edit buffer, restored at the end;\n" +
			"                    3 also stores a test preset to the safe slot, then puts its backup back (opt-in)\n" +
			"  --dry-run         print every frame a run would send, without opening any port\n" +
			"                    (--device fm9|fm3|axe3 and --firmware x.yy choose what to plan for; default fm9 12.00)\n" +
			"  --out dir         where the report zip goes (default: the current folder)\n" +
			"  --device-port s   the MIDI port name to use (default: the first port naming an FM9 or Axe-Fx III), or the\n" +
			"                    FM3's serial port (COMn; default: the one named \"FM3 Communications Port\");\n" +
			"                    --device fm3 also picks the FM3's serial port\n" +
			"  --include-backups put your own presets (the backups) and preset names into the report too\n" +
			"  --upload          offer to upload the report at the end (shows what will be sent and asks y/N)\n" +
			"  --yes             with --upload, upload without asking\n" +
			"  --site url        the FracTone site to upload to (default https://fractone.greymansoftware.com)\n" +
			"  --accept-disclaimer  accept the disclaimer without the y/N question (asked once per user otherwise)\n" +
			UploadCommands.Usage;

		public static async Task<int> Main(string[] args)
		{
			if (args.Length > 0 && UploadCommands.IsCommand(args[0]))
				return await UploadCommands.MainAsync(args, Console.In, Console.Out, Console.Error);
			CliOptions options;
			try
			{
				options = Parse(args);
			}
			catch (ArgumentException exception)
			{
				Console.Error.WriteLine(exception.Message);
				Console.Error.WriteLine(Usage);
				return 2;
			}
			if (options.DryRun)
				return DryRun(options, Console.Out);
			// Only a real run touches the device, so only it needs the disclaimer accepted.
			if (!new ProbeDisclaimer().Confirm(options.AcceptDisclaimer, Console.In, Console.Out))
				return 5;
			return await RunAsync(options);
		}

		internal static CliOptions Parse(string[] args)
		{
			int? slot = null;
			int level = 1;
			bool dryRun = false, includeUserPresets = false, upload = false, yes = false, acceptDisclaimer = false;
			Uri? site = null;
			string output = Directory.GetCurrentDirectory();
			string? port = null, firmware = null;
			string device = "fm9";
			for (int index = 0; index < args.Length; index++)
			{
				string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} needs a value.");
				switch (args[index])
				{
					case "--slot":
						slot = int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : throw new ArgumentException("--slot takes a preset number.");
						break;
					case "--level":
						level = Value() switch { "1" => 1, "2" => 2, "3" => 3, _ => throw new ArgumentException("--level is 1, 2 or 3.") };
						break;
					case "--dry-run":
						dryRun = true;
						break;
					case "--out":
						output = Value();
						break;
					case "--device-port":
						port = Value();
						break;
					case "--device":
						device = Value().ToLowerInvariant();
						break;
					case "--firmware":
						firmware = Value();
						break;
					case "--include-backups":
						includeUserPresets = true;
						break;
					case "--upload":
						upload = true;
						break;
					case "--yes" or "-y":
						yes = true;
						break;
					case "--accept-disclaimer":
						acceptDisclaimer = true;
						break;
					case "--site":
						site = UploadCommands.ParseSite(Value());
						break;
					case "--help" or "-h" or "/?":
						throw new ArgumentException("fractone-probe: collects evidence from an Axe-Fx III, FM3 or FM9 for FracTone.");
					default:
						throw new ArgumentException($"Unknown option {args[index]}.");
				}
			}
			if (slot is null)
				throw new ArgumentException("--slot is required: name one preset slot that is safe to use.");
			return new CliOptions(slot.Value, level, dryRun, output, port, device, firmware, includeUserPresets) { Upload = upload, Yes = yes, Site = site, AcceptDisclaimer = acceptDisclaimer };
		}

		internal static int DryRun(CliOptions options, TextWriter output)
		{
			Gen3Device device = options.DryRunDevice switch
			{
				"fm9" => Gen3Device.Fm9,
				"fm3" => Gen3Device.Fm3,
				"axe3" or "axe-fx3" or "axefx3" => Gen3Device.AxeFx3,
				_ => throw new ArgumentException("--device is fm9, fm3 or axe3.")
			};
			string firmware = options.DryRunFirmware ?? device.ScriptedFirmware[0];
			output.WriteLine($"Dry run for the {device.Name}, firmware {firmware}, safe slot {options.Slot}, level {options.Level}. No port is opened.");
			try
			{
				foreach (PlannedFrame frame in ProbePlanner.Plan(device, firmware, options.Slot, options.Level, options.Level == 3))
					output.WriteLine(frame.Frame.Length == 0 ? $"[{frame.Step}] ({frame.Note})" : $"[{frame.Step}] {frame.Frame}  ; {frame.Note}");
			}
			catch (Exception exception) when (exception is ProbeRefusedException or ArgumentOutOfRangeException)
			{
				output.WriteLine($"Refused: {exception.Message}");
				return 1;
			}
			return 0;
		}

		private static async Task<int> RunAsync(CliOptions options)
		{
			if (!OperatingSystem.IsWindows())
			{
				Console.Error.WriteLine("This version runs on Windows only.");
				return 2;
			}
			Console.WriteLine("Before starting: quit Axe-Edit, FM3-Edit, FM9-Edit and Fractal-Bot (they hold the port).");
			if (options.Level >= 2)
				Console.WriteLine("Turn the volume down: loading test presets is audible. Everything is put back at the end.");
			if (options.Level == 3)
				Console.WriteLine($"Level 3 stores a test preset to slot {options.Slot}, then puts its backup back and verifies it.");

			ProbePort port;
			try
			{
				port = ProbePorts.Open(options.DevicePort, options.DryRunDevice == "fm3");
			}
			catch (InvalidOperationException exception)
			{
				Console.Error.WriteLine(exception.Message);
				return 3;
			}
			Console.WriteLine($"Using {port.Description}.");
			using CancellationTokenSource cancel = new();
			Console.CancelKeyPress += (_, eventArgs) =>
			{
				// The first Ctrl+C cancels the run; restore still runs and is verified.
				eventArgs.Cancel = true;
				Console.WriteLine("Cancelling: putting everything back first...");
				cancel.Cancel();
			};
			ProbeRun run;
			try
			{
				ProbeEngine engine = new(port.Transport, log: Console.WriteLine);
				run = await engine.RunAsync(new ProbeOptions(options.Slot, options.Level) { AllowStore = options.Level == 3, ModelHint = port.ModelHint }, cancel.Token);
			}
			finally
			{
				(port.Transport as IDisposable)?.Dispose();
			}
			ProbeReportFiles files = ProbeReportWriter.Write(run, options.Out, options.IncludeUserPresets);
			Console.WriteLine();
			Console.WriteLine($"Outcome: {run.Outcome}{(run.StopReason is null ? "" : $" ({run.StopReason})")}");
			Console.WriteLine($"Left as found: {(run.Restore?.LeftAsFound == true ? "yes, verified" : "NOT verified")}");
			Console.WriteLine($"Report: {files.Zip} ({string.Join(", ", files.Entries)})");
			if (files.Backups is not null)
				Console.WriteLine($"Your backups (kept on this PC, not in the report): {files.Backups}");
			if (options.Upload)
			{
				Console.WriteLine();
				await UploadCommands.OfferAsync(files.Zip, options.Yes, Console.In, Console.Out, new UploadCodeStore(),
					UploadCommands.CreateUploader(options.Site), CancellationToken.None);
			}
			return run.Restore?.LeftAsFound == true ? run.Outcome is ProbeOutcome.Completed ? 0 : 1 : 4;
		}
	}
}
