namespace FracTone.Probe.Cli
{
	// login, logout and upload, plus the offer after a run with --upload. Nothing is sent until the user has seen the file
	// list and said yes (or passed --yes); the upload code is never echoed, logged or written anywhere but its store.
	internal static class UploadCommands
	{
		internal const string Usage =
			"fractone-probe login [--code ftu_...]   save your upload code (from your FracTone account page) for this Windows user\n" +
			"fractone-probe logout                   delete the saved upload code\n" +
			"fractone-probe upload <zip> [--yes] [--site url]\n" +
			"                                        upload an existing report after showing what will be sent";

		internal static bool IsCommand(string argument) => argument is "login" or "logout" or "upload";

		internal static async Task<int> MainAsync(string[] args, TextReader input, TextWriter output, TextWriter error,
			UploadCodeStore? store = null, Func<Uri?, IReportUploader>? uploaderFactory = null, CancellationToken cancellationToken = default)
		{
			store ??= new UploadCodeStore();
			uploaderFactory ??= CreateUploader;
			try
			{
				switch (args[0])
				{
					case "login":
						return Login(args[1..], input, output, store);
					case "logout":
						if (args.Length > 1)
							throw new ArgumentException($"Unknown option {args[1]}.");
						output.WriteLine(store.Delete() ? "Upload code deleted." : "No upload code was saved.");
						return 0;
					default:
						(string zip, bool yes, Uri? site) = ParseUpload(args[1..]);
						return await OfferAsync(zip, yes, input, output, store, uploaderFactory(site), cancellationToken) ? 0 : 5;
				}
			}
			catch (ArgumentException exception)
			{
				error.WriteLine(exception.Message);
				error.WriteLine(Usage);
				return 2;
			}
		}

		internal static IReportUploader CreateUploader(Uri? site) =>
			// One client for the process; the uploader enforces its own per-attempt timeout.
			new HttpReportUploader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, site);

		internal static Uri ParseSite(string value) =>
			Uri.TryCreate(value, UriKind.Absolute, out Uri? site) && (site.Scheme == Uri.UriSchemeHttps || site.IsLoopback)
				? site
				: throw new ArgumentException("--site takes an https:// address (http:// only for localhost).");

		private static int Login(string[] args, TextReader input, TextWriter output, UploadCodeStore store)
		{
			string? code = null;
			for (int index = 0; index < args.Length; index++)
			{
				if (args[index] == "--code" && index + 1 < args.Length)
					code = args[++index];
				else
					throw new ArgumentException(args[index] == "--code" ? "--code needs a value." : $"Unknown option {args[index]}.");
			}
			if (code is null)
			{
				// Reading it here keeps the code out of the shell's history.
				output.Write("Paste your upload code from your FracTone account page: ");
				code = input.ReadLine()?.Trim();
			}
			if (!HttpReportUploader.IsWellFormedCode(code))
				throw new ArgumentException("That isn't an upload code: codes start with ftu_ and come from your FracTone account page.");
			store.Save(code!);
			output.WriteLine(OperatingSystem.IsWindows()
				? "Upload code saved for this Windows user (encrypted)."
				: "Upload code saved for this user.");
			return 0;
		}

		private static (string Zip, bool Yes, Uri? Site) ParseUpload(string[] args)
		{
			string? zip = null;
			bool yes = false;
			Uri? site = null;
			for (int index = 0; index < args.Length; index++)
			{
				switch (args[index])
				{
					case "--yes" or "-y":
						yes = true;
						break;
					case "--site":
						site = index + 1 < args.Length ? ParseSite(args[++index]) : throw new ArgumentException("--site needs a value.");
						break;
					default:
						if (args[index].StartsWith('-') || zip is not null)
							throw new ArgumentException($"Unknown option {args[index]}.");
						zip = args[index];
						break;
				}
			}
			return (zip ?? throw new ArgumentException("upload needs the report zip to send."), yes, site);
		}

		// Shows what would be sent, asks, and uploads. True only when the site accepted the report.
		internal static async Task<bool> OfferAsync(string zipPath, bool yes, TextReader input, TextWriter output,
			UploadCodeStore store, IReportUploader uploader, CancellationToken cancellationToken)
		{
			if (!File.Exists(zipPath))
			{
				output.WriteLine($"There is no report at {zipPath}.");
				return false;
			}
			string? code = store.Load();
			if (code is null)
			{
				output.WriteLine("Not uploaded: no upload code is saved. Make one on your FracTone account page, then run:");
				output.WriteLine("  fractone-probe login --code ftu_...");
				output.WriteLine(Later(zipPath));
				return false;
			}
			ReportUploadPreview preview;
			try
			{
				preview = ReportUploadPreview.Read(zipPath);
			}
			catch (InvalidDataException)
			{
				output.WriteLine($"Not uploaded: {zipPath} isn't a readable zip.");
				return false;
			}
			output.Write(preview.Describe());
			if (!yes)
			{
				output.Write("Upload it? [y/N] ");
				string? answer = input.ReadLine()?.Trim();
				if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
				{
					output.WriteLine("Not uploaded.");
					output.WriteLine(Later(zipPath));
					return false;
				}
			}
			output.WriteLine("Uploading...");
			ReportUploadResult result = await uploader.UploadAsync(zipPath, code, cancellationToken);
			output.WriteLine(result.Message);
			if (!result.Accepted)
				output.WriteLine(Later(zipPath));
			return result.Accepted;
		}

		private static string Later(string zipPath) => $"The report stays at {zipPath}; send it later with: fractone-probe upload \"{zipPath}\"";
	}
}
