using Fractal.Core;
using Fractone.Mcp.Tools;

namespace Fractone.Mcp.Tests
{
	public sealed class HintTests
	{
		private static AudioMeasurement Measured(double sub, double air) => new(48_000, 2, 30, 30, -18, -20, -3, 0,
		[
			new("sub", 20, 60, sub), new("lows", 60, 250, -2), new("mud", 250, 1000, -10), new("1-2k", 1000, 2000, -14),
			new("bite", 2000, 4000, -8), new("fizz", 4000, 8000, -15), new("air", 8000, 12000, air)
		]);

		[Fact]
		public void AnIsolatedGuitarIsNotTakenForAFullMix() =>
			Assert.Empty(ToneTools.Hints(Measured(sub: -28, air: -35)));

		[Fact]
		public void BassAndKickBelow60HzMarkAFullMix() =>
			Assert.Contains("full mix (bass, kick)", Assert.Single(ToneTools.Hints(Measured(sub: -12, air: -35))));

		[Fact]
		public void CymbalsAbove8kHzMarkAFullMix() =>
			Assert.Contains("full mix (cymbals)", Assert.Single(ToneTools.Hints(Measured(sub: -28, air: -18))));
	}
}
