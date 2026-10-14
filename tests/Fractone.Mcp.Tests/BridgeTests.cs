using System.Text.Json;
using Fractal.Fm9;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fractone.Mcp.Tests
{
	// Starts the real fractone-mcp over stdio, as Claude Code does, with its own settings file and work folder.
	public sealed class BridgeTests : IAsyncLifetime
	{
		private readonly string _root = Directory.CreateTempSubdirectory("fractone-mcp-tests-").FullName;
		private McpClient _client = null!;
		private readonly List<string> _errors = [];

		private string Work => Path.Combine(_root, "work");

		public async Task InitializeAsync()
		{
			string settings = Path.Combine(_root, "settings.json");
			new Fm9Settings { WorkFolder = Work, LibraryFolder = Path.Combine(_root, "library") }.Save(settings);
			Directory.CreateDirectory(Work);
			File.Copy(Path.Combine(AppContext.BaseDirectory, "Presets", "metallica-eight-scenes.syx"), Path.Combine(Work, "metallica.syx"));
			_client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
			{
				Name = "fm9",
				Command = "dotnet",
				Arguments = [Path.Combine(AppContext.BaseDirectory, "fractone-mcp.dll"), "--settings", settings],
				StandardErrorLines = line => { lock (_errors) _errors.Add(line); }
			}));
		}

		public async Task DisposeAsync()
		{
			await _client.DisposeAsync();
			try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
		}

		[DictionaryFact]
		public async Task EveryToolSaysWhetherItChangesAnything()
		{
			IList<McpClientTool> tools = await _client.ListToolsAsync();
			Dictionary<string, ToolAnnotations?> byName = tools.ToDictionary(tool => tool.Name, tool => tool.ProtocolTool.Annotations);

			// The server key (fractone) namespaces the tools, so their own names carry no device prefix.
			Assert.All(byName.Keys, name => Assert.Matches("^[a-z]+(_[a-z]+)*$", name));
			Assert.DoesNotContain(byName.Keys, name => name.StartsWith("fm9", StringComparison.Ordinal));
			Assert.True(byName["store_preset"]!.DestructiveHint);
			Assert.True(byName["audition"]!.DestructiveHint);
			Assert.True(byName["read_preset"]!.ReadOnlyHint);
			Assert.True(byName["describe_preset"]!.ReadOnlyHint);
			Assert.False(byName["set_channel"]!.DestructiveHint);
			// Live knob, channel and scene changes touch only the edit buffer; storing it is destructive.
			Assert.False(byName["set_parameters"]!.DestructiveHint);
			Assert.False(byName["set_block"]!.DestructiveHint);
			Assert.True(byName["get_parameters"]!.ReadOnlyHint);
			Assert.True(byName["store_edit_buffer"]!.DestructiveHint);
			Assert.True(byName["diff"]!.ReadOnlyHint);
			Assert.False(byName["undo"]!.DestructiveHint);
			Assert.Contains("restore_snapshot", byName.Keys);
			Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
			Assert.Contains("tone_match", byName.Keys);
			Assert.True(byName["rank_cabs"]!.ReadOnlyHint);
			Assert.Contains("library_save", byName.Keys);
			Assert.Contains("search", byName.Keys);
		}

		[DictionaryFact]
		public async Task APresetIsDescribedFromAPathInTheWorkFolder()
		{
			JsonElement summary = await Call("describe_preset", new() { ["file"] = "metallica.syx" });

			Assert.Contains(summary.GetProperty("blocks").EnumerateArray(), block => block.GetProperty("block").GetString() == "Amp 1");
			Assert.Equal(8, summary.GetProperty("scenes").GetArrayLength());
		}

		[DictionaryFact]
		public async Task AnEditWritesANewFileInTheWorkFolder()
		{
			JsonElement result = await Call("set_channel", new()
			{
				["file"] = "metallica.syx", ["block"] = "Amp", ["channel"] = "B",
				["settings"] = new Dictionary<string, string> { ["Gain"] = "7" }
			});

			string output = result.GetProperty("output").GetString()!;
			Assert.StartsWith(Path.Combine(Work, "edits"), output, StringComparison.OrdinalIgnoreCase);
			Assert.Equal("7", Fm9Presets.GetChannel(output, new("Amp", 1), "B")["Gain"]);
		}

		[DictionaryFact]
		public async Task AMistakeComesBackAsAMessageTheAgentCanAct()
		{
			CallToolResult result = await _client.CallToolAsync("get_channel",
				new Dictionary<string, object?> { ["file"] = "metallica.syx", ["block"] = "Banjo" });

			Assert.True(result.IsError);
			Assert.Contains("Unknown block type 'Banjo'", Text(result), StringComparison.Ordinal);
		}

		[DictionaryFact]
		public async Task AProposalIsBuiltIntoAPresetWithoutAnAiService()
		{
			string proposal = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ride-proposal.json"));

			JsonElement result = await Call("build_preset", new() { ["proposal"] = proposal, ["name"] = "Agent Design" });

			Assert.True(result.GetProperty("passed").GetBoolean(), result.ToString());
			Assert.Equal("Agent Design", result.GetProperty("presetName").GetString());
			Assert.True(File.Exists(result.GetProperty("output").GetString()));
		}

		[DictionaryFact]
		public async Task TwoPresetFilesAreDiffedByBlockAndChannel()
		{
			await Call("set_channel", new()
			{
				["file"] = "metallica.syx", ["block"] = "Amp", ["channel"] = "B",
				["settings"] = new Dictionary<string, string> { ["Gain"] = "2.75" }, ["output"] = "edited.syx"
			});

			JsonElement diff = await Call("diff", new() { ["a"] = "metallica.syx", ["b"] = "edited.syx" });

			JsonElement block = diff.GetProperty("blocks").EnumerateArray().Single();
			Assert.Equal(("Amp 1", "B"), (block.GetProperty("block").GetString(), block.GetProperty("channel").GetString()));
			Assert.EndsWith("-> 2.75", block.GetProperty("changes")[0].GetString(), StringComparison.Ordinal);
		}

		[DictionaryFact]
		public async Task TheGuideIsAvailableToTheAgent()
		{
			CallToolResult result = await _client.CallToolAsync("tone_design_guide", new Dictionary<string, object?>());

			Assert.Contains("Return JSON only", Text(result), StringComparison.Ordinal);
		}

		[DictionaryFact]
		public async Task SearchingBeforeAnIndexExistsSaysHowToBuildOne()
		{
			CallToolResult result = await _client.CallToolAsync("search", new Dictionary<string, object?> { ["model"] = "IIC++" });

			Assert.True(result.IsError);
			Assert.Contains("index_build", Text(result), StringComparison.Ordinal);
		}

		[DictionaryFact]
		public async Task AnIndexBuiltFromFilesAnswersSearches()
		{
			await Call("index_build", new() { ["sources"] = new[] { "metallica.syx" } });
			string model = Fm9Presets.Describe(Path.Combine(Work, "metallica.syx")).Blocks.Single(block => block.Block == "Amp 1")
				.Channels.First(channel => channel.Scenes.Count > 0).Model!;

			JsonElement hits = await Call("search", new() { ["model"] = model });

			Assert.True(hits.GetArrayLength() > 0);
		}

		[DictionaryFact]
		public async Task FavouritesAreSavedToTheLibraryFolderAndListed()
		{
			await Call("library_save", new() { ["file"] = "metallica.syx", ["block"] = "Amp", ["name"] = "Test amps" });

			JsonElement listed = await Call("library_list", new() { ["blockType"] = "Amp" });

			Assert.Equal("Test amps", listed[0].GetProperty("name").GetString());
			Assert.StartsWith(Path.Combine(_root, "library", "Amplifier"), listed[0].GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);
		}

		private async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments)
		{
			CallToolResult result = await _client.CallToolAsync(tool, arguments);
			Assert.True(result.IsError != true, Text(result) + Environment.NewLine + string.Join(Environment.NewLine, _errors));
			return JsonDocument.Parse(Text(result)).RootElement.Clone();
		}

		private static string Text(CallToolResult result) =>
			string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
	}

	public sealed class ConfirmationTests
	{
		[Fact]
		public void AWriteNeedsTheCodeIssuedForExactlyThatWriteWhenConfirmationIsOn()
		{
			string root = Directory.CreateTempSubdirectory("fm9-confirm-").FullName;
			string settings = Path.Combine(root, "settings.json");
			new Fm9Settings { WorkFolder = root, Safeguards = new Fm9Safeguards { ConfirmWrites = true } }.Save(settings);
			using Fm9Context context = new(settings, () => new Fm9Connection(() => throw new InvalidOperationException("no FM9 in this test")));

			string code = context.RequireConfirmation("store A into 3", null)!;

			Assert.NotNull(context.RequireConfirmation("store B into 3", code));
			string again = context.RequireConfirmation("store A into 3", null)!;
			Assert.Null(context.RequireConfirmation("store A into 3", again));
			Assert.NotNull(context.RequireConfirmation("store A into 3", again));
		}

		[Fact]
		public void NoCodeIsNeededWhenConfirmationIsOff()
		{
			string root = Directory.CreateTempSubdirectory("fm9-confirm-").FullName;
			string settings = Path.Combine(root, "settings.json");
			new Fm9Settings { WorkFolder = root }.Save(settings);
			using Fm9Context context = new(settings, () => new Fm9Connection(() => throw new InvalidOperationException("no FM9 in this test")));

			Assert.Null(context.RequireConfirmation("store A into 3", null));
		}
	}
}
