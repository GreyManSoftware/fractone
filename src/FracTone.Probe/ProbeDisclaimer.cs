namespace FracTone.Probe
{
	// The short disclaimer the CLI and GUI show before touching a device; the full text is on the FracTone site. The
	// licence's own warranty disclaimer still applies to the code. Acceptance is remembered per user, per version.
	public sealed class ProbeDisclaimer
	{
		// Raise this when the notice changes in a way people must accept again.
		public const int Version = 1;
		public const string Url = "https://fractone.greymansoftware.com/disclaimer";
		private const string FileName = "disclaimer-accepted.txt";

		public static readonly string Notice =
			"FracTone's tools are free and provided as-is, at your own risk, with no warranty. We're not responsible for " +
			"damage to your device, lost or changed presets, data loss or any other harm. Back up your presets first " +
			"(FM9-Edit, FM3-Edit, Axe-Edit or Fractal-Bot). FracTone is not affiliated with or endorsed by Fractal Audio " +
			"Systems. Full disclaimer: " + Url;

		public ProbeDisclaimer(string? folder = null)
		{
			Folder = folder ?? UploadCodeStore.DefaultFolder;
		}

		public string Folder { get; }

		public string FilePath => Path.Combine(Folder, FileName);

		public bool IsAccepted
		{
			get
			{
				try
				{
					return File.Exists(FilePath) && int.TryParse(File.ReadAllText(FilePath).Trim(), out int version) && version >= Version;
				}
				catch (IOException)
				{
					return false;
				}
			}
		}

		public void Accept()
		{
			Directory.CreateDirectory(Folder);
			File.WriteAllText(FilePath, Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		// Prints the notice; then true if it was accepted before, by --accept-disclaimer, or by answering y now.
		public bool Confirm(bool acceptedByFlag, TextReader input, TextWriter output)
		{
			output.WriteLine(Notice);
			if (IsAccepted)
				return true;
			if (!acceptedByFlag)
			{
				output.Write("Do you accept this disclaimer? [y/N] ");
				string answer = input.ReadLine()?.Trim() ?? "";
				if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase) && !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
				{
					output.WriteLine("Not accepted, so nothing was run. Pass --accept-disclaimer to accept it without the question.");
					return false;
				}
			}
			Accept();
			return true;
		}
	}
}
