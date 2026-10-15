using Fractal.Fm9;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fractone.Mcp.Tests
{
	// Installs without the plugin (NuGet, the MCP Registry) get the skills from the server: embedded from the plugin's
	// SKILL.md files and served as prompts and resources.
	public sealed class SkillTests
	{
		private static readonly string[] Names =
			["fractone-levelling", "fractone-library", "fractone-operations", "fractone-search", "fractone-tone-designer", "fractone-tone-match"];

		[Fact]
		public void EveryPluginSkillIsEmbeddedWithItsFrontmatter()
		{
			Assert.Equal(Names, Skill.All.Select(skill => skill.Name));
			Assert.All(Skill.All, skill =>
			{
				Assert.False(string.IsNullOrWhiteSpace(skill.Description));
				Assert.StartsWith("---", skill.Text, StringComparison.Ordinal);
				Assert.Contains($"name: {skill.Name}", skill.Text, StringComparison.Ordinal);
				Assert.Equal(File.ReadAllText(Path.Combine(PluginSkills, skill.Name, "SKILL.md")), skill.Text);
			});
		}

		[Fact]
		public void FrontmatterWithoutADescriptionIsRefused()
		{
			Assert.Throws<InvalidDataException>(() => Skill.Parse("---\nname: x\n---\n# X"));
			Assert.Throws<InvalidDataException>(() => Skill.Parse("# no frontmatter"));
		}

		[Fact]
		public async Task TheServerListsAndServesEachSkillAsAPromptAndAResource()
		{
			string root = Directory.CreateTempSubdirectory("fractone-skills-tests-").FullName;
			try
			{
				string settings = Path.Combine(root, "settings.json");
				new Fm9Settings { WorkFolder = Path.Combine(root, "work"), LibraryFolder = Path.Combine(root, "library") }.Save(settings);
				await using McpClient client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
				{
					Name = "fractone",
					Command = "dotnet",
					Arguments = [Path.Combine(AppContext.BaseDirectory, "fractone-mcp.dll"), "--settings", settings]
				}));

				Assert.Contains("fractone://skills/", client.ServerInstructions, StringComparison.Ordinal);
				IList<McpClientPrompt> prompts = await client.ListPromptsAsync();
				Assert.Equal(Names, prompts.Select(prompt => prompt.Name).Order(StringComparer.Ordinal));
				Assert.All(prompts, prompt => Assert.Equal(Skill.All.Single(skill => skill.Name == prompt.Name).Description, prompt.Description));
				IList<McpClientResource> resources = await client.ListResourcesAsync();
				Assert.Equal(Names.Select(name => $"fractone://skills/{name}"), resources.Select(resource => resource.Uri).Order(StringComparer.Ordinal));
				Assert.All(resources, resource => Assert.False(string.IsNullOrWhiteSpace(resource.Description)));

				string expected = File.ReadAllText(Path.Combine(PluginSkills, "fractone-tone-match", "SKILL.md"));
				GetPromptResult prompt = await client.GetPromptAsync("fractone-tone-match");
				Assert.Equal(expected, Assert.IsType<TextContentBlock>(Assert.Single(prompt.Messages).Content).Text);
				ReadResourceResult resource = await client.ReadResourceAsync("fractone://skills/fractone-tone-match");
				Assert.Equal(expected, Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
			}
			finally
			{
				try { Directory.Delete(root, recursive: true); } catch (IOException) { }
			}
		}

		// The plugin's SKILL.md files, copied beside the tests (the csproj links them), as the source of truth.
		private static string PluginSkills => Path.Combine(AppContext.BaseDirectory, "skills");
	}
}
