using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Fractone.Mcp.Tests
{
	// Agents start the server in their session's folder; watching it kept idle servers busy in busy repositories.
	public sealed class HostTests
	{
		private static void AssertNothingWatched(IConfiguration configuration, string contentRoot)
		{
			Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.TrimEndingDirectorySeparator(contentRoot));
			IEnumerable<FileConfigurationSource> files = ((IConfigurationRoot)configuration).Providers
				.OfType<FileConfigurationProvider>().Select(provider => provider.Source);
			Assert.All(files, source => Assert.False(source.ReloadOnChange, $"{source.Path} is watched for changes."));
		}

		[Fact]
		public void TheStdioHostNeitherRootsInNorWatchesTheWorkingFolder()
		{
			HostApplicationBuilder builder = Host.CreateApplicationBuilder(Server.HostSettings());
			AssertNothingWatched(builder.Configuration, builder.Environment.ContentRootPath);
		}

		[Fact]
		public void TheHttpHostNeitherRootsInNorWatchesTheWorkingFolder()
		{
			WebApplicationBuilder builder = WebApplication.CreateBuilder(Server.WebOptions());
			AssertNothingWatched(builder.Configuration, builder.Environment.ContentRootPath);
		}

		[Fact]
		public void TheDefaultHostWouldWatchItsConfigurationFiles()
		{
			// Guards the test above: without the settings the defaults do watch, so the assertion can fail.
			HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
			Assert.Contains(((IConfigurationRoot)builder.Configuration).Providers.OfType<FileConfigurationProvider>(),
				provider => provider.Source.ReloadOnChange);
		}
	}
}
