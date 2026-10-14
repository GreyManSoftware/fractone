using Fractal.Application;
using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9CabRankerTests
	{
		[Theory]
		// A Legacy 4x12 (the cab in the author's Ride blend), a Factory 2 4x12 and a Factory 1 1x12.
		[InlineData(3, 38)]
		[InlineData(1, 965)]
		[InlineData(0, 329)]
		public void TheCabATargetWasMadeWithRanksFirstWithTheGentleEqThatShapedIt(int bank, int type)
		{
			// The difference a stem asks for: that cab's measured curve plus a broad 2 dB bump at 1.2 kHz.
			ParametricEqBand bump = new(ParametricEqShape.Peaking, 1200, 1, 2);
			double[] eq = ParametricEqResponse.Decibels([bump], Fm9CabRanker.Frequencies, 48_000);
			double[] need = Fm9CabRanker.Curve(bank, type).Select((value, index) => value + eq[index]).ToArray();

			Fm9CabCandidate best = Fm9CabRanker.Rank(need, count: 3)[0];

			Assert.Equal((bank, type), (best.Bank, best.Type));
			Assert.True(best.GapDecibels < 0.3, $"gap {best.GapDecibels}");
			Assert.True(best.GapWithoutEqDecibels > best.GapDecibels);
			Assert.All(best.Eq, band => Assert.InRange(Math.Abs(band.GainDecibels), 0, Fm9CabRanker.DefaultMaximumEqDecibels));
		}

		[Fact]
		public void AFilterKeepsTheRankingToMatchingCabs()
		{
			double[] need = [.. Fm9CabRanker.Curve(1, 965)];

			IReadOnlyList<Fm9CabCandidate> ranked = Fm9CabRanker.Rank(need, count: 5, nameFilter: "1x12");

			Assert.All(ranked, candidate => Assert.Contains("1x12", candidate.Name, StringComparison.OrdinalIgnoreCase));
			Assert.Throws<ArgumentException>(() => Fm9CabRanker.Rank(need, nameFilter: "no such cab"));
		}

		[Fact]
		public void EveryFactoryCabHasACurveOnTheSameGrid()
		{
			Assert.Equal(60, Fm9CabRanker.Frequencies[0], 0.01);
			Assert.Equal(Fm9CabRanker.Frequencies.Count, Fm9CabRanker.Curve(0, 0).Count);
			Assert.Equal(Fm9CabRanker.Frequencies.Count, Fm9CabRanker.Curve(3, 188).Count);
		}
	}
}
