using System.Security.Cryptography;
using System.Text;
using Fractone.Mcp;
using Fractal.Fm9;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string Usage = """
	fractone-mcp: unofficial tools for Fractal Audio's FM9 (Axe-Fx III and FM3 planned). Not affiliated with Fractal Audio Systems.

	  fractone-mcp                         serve MCP over stdio (how Claude Code and other agents start it)
	  fractone-mcp --http [--port 39009]   serve MCP on http://127.0.0.1:<port>/mcp, bearer token required
	  fractone-mcp setup [options]         write the settings file (MIDI ports, folders, safeguards)
	      --careful                   switch every safeguard on
	      --writable <slots>          writable slots, e.g. 500-505 (repeatable); default every slot
	      --work <folder>             work folder for presets, backups, recordings and the index
	      --library <folder>          FM9-Edit's block library folder
	      --midi <name>               part of the FM9's MIDI port names (default FM9)
	      --separator <folder>        where the stem separator add-on installs (up to 5 GB)
	      --browser <folder>          where capture_media's headless browser installs (about 100 MB)
	  fractone-mcp setup-separator         install the stem separator now (otherwise it installs on first use)
	  fractone-mcp setup-browser           install capture_media's headless browser now (otherwise on first use)
	  fractone-mcp build <proposal.json> [out.syx] [--name <name>]   build a tone-design proposal into a preset
	  fractone-mcp guide                   print the tone designer's rules
	  fractone-mcp ports                   list MIDI ports, recording devices and ASIO drivers
	  fractone-mcp --version

	  --settings <file>               use another settings file (default %APPDATA%\fractone\settings.json)
	""";

List<string> arguments = [.. args];
try
{
	string settingsPath = Take(arguments, "--settings") ?? Fm9Settings.DefaultPath;
	// Settings from before the Fractone name move once; stderr keeps stdout free for MCP.
	if (settingsPath == Fm9Settings.DefaultPath && Fm9Settings.MigrateFromOldFolder() is { Count: > 0 } moved)
		Console.Error.WriteLine($"fractone-mcp: moved {string.Join(" and ", moved)} from %APPDATA%\\fm9-tools to {Fm9Settings.Folder}.");
	switch (arguments.FirstOrDefault())
	{
		case null:
			await ServeStdioAsync(settingsPath);
			return 0;
		case "--http":
			arguments.RemoveAt(0);
			await ServeHttpAsync(settingsPath, int.Parse(Take(arguments, "--port") ?? "39009", System.Globalization.CultureInfo.InvariantCulture));
			return 0;
		case "setup":
			arguments.RemoveAt(0);
			Setup(settingsPath, arguments);
			return 0;
		case "build" when arguments.Count >= 2:
			{
				arguments.RemoveAt(0);
				string? name = Take(arguments, "--name");
				Fm9Settings settings = Fm9Settings.Load(settingsPath);
				string output = arguments.Count >= 2 ? Path.GetFullPath(arguments[1])
					: Path.Combine(settings.WorkFolder, "designs", $"{Path.GetFileNameWithoutExtension(arguments[0])}.syx");
				Fm9BuildResult result = Fm9ToneDesigner.Build(File.ReadAllText(arguments[0]), output, name);
				foreach (string adjustment in result.Adjustments) Console.WriteLine($"adjusted: {adjustment}");
				foreach (string correction in result.Corrections) Console.WriteLine($"correct: {correction}");
				Console.WriteLine(result.Passed ? $"Wrote '{result.PresetName}' to {result.Output}" : "Not built: fix the corrections above and build again.");
				return result.Passed ? 0 : 2;
			}
		case "guide":
			Console.WriteLine(Fm9ToneDesigner.Guide());
			return 0;
		case "setup-separator":
			{
				StemSeparator separator = new(Fm9Settings.Load(settingsPath).SeparatorFolder);
				SeparatorDevice device = await StemSeparator.DetectAsync(CancellationToken.None);
				Console.WriteLine($"Installing the stem separator for {device.Name} ({device.Kind}) into {separator.Folder}.");
				await separator.InstallAsync(Console.WriteLine, CancellationToken.None);
				return 0;
			}
		case "setup-browser":
			{
				MediaCapturer capturer = new(Fm9Settings.Load(settingsPath).BrowserFolder);
				if (capturer.Installed) Console.WriteLine($"Already installed: {capturer.Executable}");
				else await capturer.InstallAsync(Console.WriteLine, CancellationToken.None);
				return 0;
			}
		case "ports":
			Fm9MidiPorts ports = Fm9Connection.ListPorts();
			Console.WriteLine("MIDI in:  " + string.Join(", ", ports.Inputs));
			Console.WriteLine("MIDI out: " + string.Join(", ", ports.Outputs));
			Console.WriteLine("Recording devices: " + string.Join(", ", Fm9AudioRecorder.CaptureDevices()));
			Console.WriteLine("ASIO drivers: " + string.Join(", ", Fm9Reamper.Drivers()));
			return 0;
		case "--version":
			Console.WriteLine(typeof(Fm9Context).Assembly.GetName().Version?.ToString(3));
			return 0;
		default:
			Console.Error.WriteLine(Usage);
			return 1;
	}
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or InvalidDataException or
	NotSupportedException or FormatException or System.Text.Json.JsonException or HttpRequestException)
{
	Console.Error.WriteLine(exception.Message);
	return 1;
}

static string? Take(List<string> arguments, string option)
{
	int index = arguments.IndexOf(option);
	if (index < 0) return null;
	if (index + 1 >= arguments.Count) throw new ArgumentException($"{option} needs a value.");
	string value = arguments[index + 1];
	arguments.RemoveRange(index, 2);
	return value;
}

static async Task ServeStdioAsync(string settingsPath)
{
	HostApplicationBuilder builder = Host.CreateApplicationBuilder();
	// stdout carries MCP; every log line goes to stderr.
	builder.Logging.ClearProviders();
	builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
	builder.Logging.SetMinimumLevel(LogLevel.Warning);
	builder.Services.AddSingleton(new Fm9Context(settingsPath));
	builder.Services.AddMcpServer(Server.Configure).WithStdioServerTransport().WithToolsFromAssembly(typeof(Fm9Context).Assembly);
	await builder.Build().RunAsync();
}

static async Task ServeHttpAsync(string settingsPath, int port)
{
	string token = Server.HttpToken();
	WebApplicationBuilder builder = WebApplication.CreateBuilder();
	builder.Logging.SetMinimumLevel(LogLevel.Warning);
	// Loopback only: an agent in a container reaches it through host.docker.internal; nothing on the network can.
	builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, port));
	builder.Services.AddSingleton(new Fm9Context(settingsPath));
	builder.Services.AddMcpServer(Server.Configure).WithHttpTransport().WithToolsFromAssembly(typeof(Fm9Context).Assembly);
	WebApplication app = builder.Build();
	app.Use(async (http, next) =>
	{
		if (!Server.Authorised(http, token))
		{
			http.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}
		await next();
	});
	app.MapMcp("/mcp");
	Console.Error.WriteLine($"fractone-mcp: serving http://127.0.0.1:{port}/mcp; send 'Authorization: Bearer <token>' with the token in {Server.HttpTokenPath}.");
	await app.RunAsync();
}

static void Setup(string settingsPath, List<string> arguments)
{
	Fm9Settings settings = Fm9Settings.Load(settingsPath);
	bool careful = arguments.Remove("--careful");
	List<string> writable = [];
	while (Take(arguments, "--writable") is string slots) writable.Add(slots);
	string? work = Take(arguments, "--work"), library = Take(arguments, "--library"), midi = Take(arguments, "--midi"),
		separator = Take(arguments, "--separator"), browser = Take(arguments, "--browser");
	if (arguments.Count > 0) throw new ArgumentException($"Unknown setup option: {arguments[0]}.");
	Fm9Safeguards safeguards = careful ? Fm9Safeguards.Careful with { WritableSlots = settings.Safeguards.WritableSlots } : settings.Safeguards;
	if (writable.Count > 0)
	{
		// Parse each range now, so a typo fails here rather than at the first store.
		foreach (string range in writable) _ = new Fm9Safeguards { WritableSlots = [range] }.AllowsSlot(0);
		safeguards = safeguards with { WritableSlots = writable };
	}
	settings = settings with
	{
		WorkFolder = work is null ? settings.WorkFolder : Path.GetFullPath(work),
		LibraryFolder = library is null ? settings.LibraryFolder : Path.GetFullPath(library),
		SeparatorFolder = separator is null ? settings.SeparatorFolder : Path.GetFullPath(separator),
		BrowserFolder = browser is null ? settings.BrowserFolder : Path.GetFullPath(browser),
		Midi = midi is null ? settings.Midi : settings.Midi with { Input = midi, Output = midi },
		Safeguards = safeguards
	};
	Directory.CreateDirectory(settings.WorkFolder);
	settings.Save(settingsPath);
	Console.WriteLine($"Settings: {Path.GetFullPath(settingsPath)}");
	Console.WriteLine($"  work folder:      {settings.WorkFolder}");
	Console.WriteLine($"  FM9-Edit library: {settings.LibraryFolder}{(Directory.Exists(settings.LibraryFolder) ? "" : " (not found yet)")}");
	Console.WriteLine($"  stem separator:   {settings.SeparatorFolder}{(new StemSeparator(settings.SeparatorFolder).Installed ? "" : " (installs on first use)")}");
	Console.WriteLine($"  media browser:    {settings.BrowserFolder}{(new MediaCapturer(settings.BrowserFolder).Installed ? "" : " (installs on first use)")}");
	try
	{
		Fm9MidiPorts ports = Fm9Connection.ListPorts();
		bool found = ports.Inputs.Any(port => port.Contains(settings.Midi.Input, StringComparison.OrdinalIgnoreCase));
		Console.WriteLine($"  MIDI '{settings.Midi.Input}':       {(found ? "found" : "not found; ports: " + string.Join(", ", ports.Inputs))}");
	}
	catch (PlatformNotSupportedException exception)
	{
		Console.WriteLine($"  MIDI: {exception.Message}");
	}
	Console.WriteLine($"  dictionary:       {Fractal.Companion.Catalog.Fm9EffectDefinitions.FindInstalled() ?? "not found: open FM9-Edit once with the FM9 connected"}");
	Fm9Safeguards current = settings.Safeguards;
	Console.WriteLine("  safeguards:");
	Console.WriteLine($"    writable slots:            {(current.WritableSlots.Count == 0 ? "all" : string.Join(", ", current.WritableSlots))}");
	Console.WriteLine($"    keep a backup on store:    {current.BackupBeforeStore}");
	Console.WriteLine($"    verify after store:        {current.VerifyAfterStore}");
	Console.WriteLine($"    refuse over unsaved edits: {current.RefuseLoadIfBufferChanged}");
	Console.WriteLine($"    confirm writes:            {current.ConfirmWrites}");
	if (!current.BackupBeforeStore && !current.VerifyAfterStore)
		Console.WriteLine("  Run 'fractone-mcp setup --careful' to switch every safeguard on.");
}

namespace Fractone.Mcp
{
	internal static class Server
	{
		public static void Configure(ModelContextProtocol.Server.McpServerOptions options)
		{
			options.ServerInfo = new() { Name = "fractone", Version = typeof(Fm9Context).Assembly.GetName().Version?.ToString(3) ?? "0" };
			options.ServerInstructions =
				"Fractone: unofficial tools for a Fractal Audio FM9 on this computer (not affiliated with Fractal Audio Systems). Presets travel as .syx file paths (relative paths are in the work " +
				"folder); offline edits always write a new file. Writes to the FM9 report what they replaced. Call status first.";
		}

		public static string HttpTokenPath => Path.Combine(Path.GetDirectoryName(Fm9Settings.DefaultPath)!, "http-token");

		// The bearer token the HTTP transport requires, created on first use and kept beside the settings.
		public static string HttpToken()
		{
			if (File.Exists(HttpTokenPath)) return File.ReadAllText(HttpTokenPath).Trim();
			string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
			Directory.CreateDirectory(Path.GetDirectoryName(HttpTokenPath)!);
			File.WriteAllText(HttpTokenPath, token);
			return token;
		}

		// The token must match, and a browser page from another site (DNS rebinding) is refused by its Origin.
		public static bool Authorised(HttpContext http, string token)
		{
			string? origin = http.Request.Headers.Origin;
			if (origin is not null && !(Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && uri.IsLoopback)) return false;
			byte[] supplied = Encoding.UTF8.GetBytes(http.Request.Headers.Authorization.ToString());
			return CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes($"Bearer {token}"));
		}
	}
}
