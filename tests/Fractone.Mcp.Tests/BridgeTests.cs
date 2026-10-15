using System.Text.Json;
using Fractal.Application;
using Fractal.Core;
using Fractal.Fm9;
using Fractone.Mcp.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fractone.Mcp.Tests
{
	// The real fractone-mcp over stdio, as Claude Code starts it, with its own settings file and work folder. Starting it
	// takes seconds, so each test class shares one server (IClassFixture); tests that write give their outputs their own
	// names so they never meet in the shared work folder.
	public sealed class BridgeServer : IAsyncLifetime
	{
		private readonly List<string> _errors = [];

		public string Root { get; } = Directory.CreateTempSubdirectory("fractone-mcp-tests-").FullName;

		public string Work => Path.Combine(Root, "work");

		public McpClient Client { get; private set; } = null!;

		public async Task InitializeAsync()
		{
			string settings = Path.Combine(Root, "settings.json");
			new Fm9Settings { WorkFolder = Work, LibraryFolder = Path.Combine(Root, "library") }.Save(settings);
			Directory.CreateDirectory(Work);
			File.Copy(Path.Combine(AppContext.BaseDirectory, "Presets", "metallica-eight-scenes.syx"), Path.Combine(Work, "metallica.syx"));
			Client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
			{
				Name = "fm9",
				Command = "dotnet",
				Arguments = [Path.Combine(AppContext.BaseDirectory, "fractone-mcp.dll"), "--settings", settings],
				StandardErrorLines = line => { lock (_errors) _errors.Add(line); }
			}));
		}

		public async Task DisposeAsync()
		{
			await Client.DisposeAsync();
			try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
		}

		public async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments)
		{
			CallToolResult result = await Client.CallToolAsync(tool, arguments);
			string errors;
			lock (_errors) errors = string.Join(Environment.NewLine, _errors);
			Assert.True(result.IsError != true, Text(result) + Environment.NewLine + errors);
			return JsonDocument.Parse(Text(result)).RootElement.Clone();
		}

		public static string Text(CallToolResult result) =>
			string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
	}

	public sealed class BridgeTests(BridgeServer server) : IClassFixture<BridgeServer>
	{
		private readonly McpClient _client = server.Client;

		private string Work => server.Work;

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
			// The one write on this server left to name its own output (names are stamped to the second), so it can't collide.
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
		public async Task FavouritesAreSavedToTheLibraryFolderAndListed()
		{
			await Call("library_save", new() { ["file"] = "metallica.syx", ["block"] = "Amp", ["name"] = "Test amps" });

			JsonElement listed = await Call("library_list", new() { ["blockType"] = "Amp" });

			Assert.Equal("Test amps", listed[0].GetProperty("name").GetString());
			Assert.StartsWith(Path.Combine(server.Root, "library", "Amplifier"), listed[0].GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);
		}

		[DictionaryFact]
		public async Task ARankedDynaCabIsPlayedBySettingItsCabSettingsOnAChannel()
		{
			// Which cab ranks first is Fm9CabRankerTests' concern; this checks a ranking entry's settings play on the FM9.
			Fm9DynaCabPlacement placement = new("4x12 RECTO STRAIGHT", "Ribbon", 2, 1);
			IReadOnlyList<ParametricEqBand> flat = Fm9ParametricEq.Limits.Select((limit, band) =>
				new ParametricEqBand(limit.Shape, Fm9ParametricEq.DefaultFrequencies[band], Fm9ParametricEq.DefaultQ, 0)).ToArray();
			Fm9CabRankingEntry best = ToneTools.Ranking([new Fm9CabCandidate(-1, -1, "DynaCab", 0, 0, flat, placement)]).Cabs[0];

			JsonElement result = await Call("set_channel", new()
			{
				["file"] = "metallica.syx", ["block"] = "Cab", ["channel"] = "D", ["settings"] = best.CabSettings, ["output"] = "dynacab.syx"
			});

			IReadOnlyDictionary<string, string> channel = Fm9Presets.GetChannel(result.GetProperty("output").GetString()!, new("Cab", 1), "D");
			Assert.Equal("DynaCab 4x12 RECTO STRAIGHT, Ribbon", Fm9Names.Model("Cab", channel));
			Assert.Equal(5, best.ParametricEqSettings.Keys.Count(key => key.StartsWith("Type ", StringComparison.Ordinal)));
		}

		private Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments) => server.Call(tool, arguments);

		private static string Text(CallToolResult result) => BridgeServer.Text(result);
	}

	// Search answers from the index, so these two need a server whose work folder has none until the second builds it.
	public sealed class IndexBridgeTests(BridgeServer server) : IClassFixture<BridgeServer>
	{
		[DictionaryFact]
		public async Task SearchingSaysHowToBuildAnIndexUntilOneBuiltFromFilesAnswersIt()
		{
			CallToolResult before = await server.Client.CallToolAsync("search", new Dictionary<string, object?> { ["model"] = "IIC++" });
			Assert.True(before.IsError);
			Assert.Contains("index_build", BridgeServer.Text(before), StringComparison.Ordinal);

			await server.Call("index_build", new() { ["sources"] = new[] { "metallica.syx" } });
			string model = Fm9Presets.Describe(Path.Combine(server.Work, "metallica.syx")).Blocks.Single(block => block.Block == "Amp 1")
				.Channels.First(channel => channel.Scenes.Count > 0).Model!;

			JsonElement hits = await server.Call("search", new() { ["model"] = model });

			Assert.True(hits.GetArrayLength() > 0);
		}
	}

	public sealed class ToneToolsTests
	{
		[Fact]
		public void RankCabsTakesFactoryDynaCabOrBoth()
		{
			Assert.Equal(Fm9CabSource.Factory, ToneTools.CabSource(null));
			Assert.Equal(Fm9CabSource.DynaCab, ToneTools.CabSource("DynaCab"));
			Assert.Equal(Fm9CabSource.Both, ToneTools.CabSource("both"));
			Assert.Throws<ArgumentException>(() => ToneTools.CabSource("user"));
		}
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
