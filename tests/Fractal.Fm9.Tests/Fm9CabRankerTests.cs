using Fractal.Application;
using Fractal.Core;

namespace Fractal.Fm9.Tests
{
	public sealed class Fm9CabRankerTests
	{
		// Every cab costs an EQ fit (about a second), so the ranking tests rank their targets among near neighbours: each
		// target factory cab with the types either side of it (often the same cab, another mic), and the 1960TV with Dynamic 1
		// at each position, with a Condenser, and a Recto with Dynamic 1. One Heavy test ranks the whole roster.
		private static readonly (int Bank, int Type)[] FactoryTargets = [(3, 38), (1, 965), (0, 329)];

		private static bool Roster(int bank, int type, Fm9DynaCabPlacement? dynaCab) => dynaCab is null
			? FactoryTargets.Any(target => target.Bank == bank && Math.Abs(target.Type - type) <= 1)
			: dynaCab.Type == "4x12 1960TV" && (dynaCab.Mic == "Dynamic 1" || (dynaCab.Mic, dynaCab.Position) == ("Condenser", 4)) ||
				(dynaCab.Type, dynaCab.Mic, dynaCab.Position) == ("4x12 RECTO STRAIGHT", "Dynamic 1", 4);

		// Factory against DynaCab needs only one factory family: the Legacy 4x12 and its neighbours.
		private static bool MixedRoster(int bank, int type, Fm9DynaCabPlacement? dynaCab) =>
			Roster(bank, type, dynaCab) && (dynaCab is not null || (bank == 3 && Math.Abs(type - 38) <= 1));

		private static IReadOnlyList<Fm9CabCandidate> Rank(IReadOnlyList<double> need, int count = 10, string? nameFilter = null,
			Fm9CabSource include = Fm9CabSource.Factory) =>
			Fm9CabRanker.Rank(need, count, nameFilter, Fm9CabRanker.DefaultMaximumEqDecibels, include,
				include == Fm9CabSource.Both ? MixedRoster : Roster);

		private static Fm9CabPick Pick(IReadOnlyList<double> need, bool allowDynaCab) =>
			Fm9CabRanker.Pick(need, allowDynaCab, Fm9CabRanker.DefaultMaximumEqDecibels, MixedRoster);

		// The difference a stem asks for: that cab's measured curve plus a broad 2 dB bump at 1.2 kHz.
		private static double[] NeedWithBump(IReadOnlyList<double> curve)
		{
			ParametricEqBand bump = new(ParametricEqShape.Peaking, 1200, 1, 2);
			double[] eq = ParametricEqResponse.Decibels([bump], Fm9CabRanker.Frequencies, 48_000);
			return curve.Select((value, index) => value + eq[index]).ToArray();
		}

		[Theory]
		// A Legacy 4x12 (the cab in the author's Ride blend), a Factory 2 4x12 and a Factory 1 1x12.
		[InlineData(3, 38)]
		[InlineData(1, 965)]
		[InlineData(0, 329)]
		public void TheCabATargetWasMadeWithRanksFirstWithTheGentleEqThatShapedIt(int bank, int type)
		{
			Fm9CabCandidate best = Rank(NeedWithBump(Fm9CabRanker.Curve(bank, type)), count: 3)[0];

			Assert.Equal((bank, type), (best.Bank, best.Type));
			Assert.True(best.GapDecibels < 0.3, $"gap {best.GapDecibels}");
			Assert.True(best.GapWithoutEqDecibels > best.GapDecibels);
			Assert.All(best.Eq, band => Assert.InRange(Math.Abs(band.GainDecibels), 0, Fm9CabRanker.DefaultMaximumEqDecibels));
		}

		[Fact]
		public void AFilterKeepsTheRankingToMatchingCabs()
		{
			double[] need = [.. Fm9CabRanker.Curve(1, 965)];

			IReadOnlyList<Fm9CabCandidate> ranked = Rank(need, count: 5, nameFilter: "1x12");

			Assert.NotEmpty(ranked);
			Assert.All(ranked, candidate => Assert.Contains("1x12", candidate.Name, StringComparison.OrdinalIgnoreCase));
		}

		[Theory]
		[InlineData(Fm9CabSource.Factory)]
		[InlineData(Fm9CabSource.DynaCab)]
		public void AFilterNothingMatchesIsRefused(Fm9CabSource include) =>
			Assert.Throws<ArgumentException>(() => Fm9CabRanker.Rank([.. Fm9CabRanker.Curve(1, 965)], nameFilter: "no such cab", include: include));

		[Fact]
		public void TheRankerFitsAtLeastOneCabAtATime()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => Fm9CabRanker.MaxParallelism = 0);
			Assert.Equal(2, Fm9CabRanker.MaxParallelism);
		}

		[Fact]
		public void EveryFactoryCabHasACurveOnTheSameGrid()
		{
			Assert.Equal(60, Fm9CabRanker.Frequencies[0], 0.01);
			Assert.Equal(Fm9CabRanker.Frequencies.Count, Fm9CabRanker.Curve(0, 0).Count);
			Assert.Equal(Fm9CabRanker.Frequencies.Count, Fm9CabRanker.Curve(3, 188).Count);
		}

		private static readonly Fm9DynaCabPlacement Plexi = new("4x12 1960TV", "Dynamic 1", 4, 1);

		[Fact]
		public void EveryMeasuredDynaCabPlacementHasACurveOnTheSameGrid()
		{
			IReadOnlyList<Fm9DynaCabPlacement> placements = Fm9CabRanker.DynaCabPlacements;

			// 45 types x 4 mics at positions 2, 4 and 6, 1 cm out, plus a few extra positions.
			Assert.True(placements.Count >= 540, $"{placements.Count} placements");
			Assert.Equal(45, placements.Select(placement => placement.Type).Distinct().Count());
			Assert.Equal(["Condenser", "Ribbon", "Dynamic 1", "Dynamic 2"], placements.Select(placement => placement.Mic).Distinct());
			Assert.All(placements, placement => Assert.Equal(Fm9CabRanker.Frequencies.Count, Fm9CabRanker.Curve(placement).Count));
			Assert.Contains(Plexi, placements);
		}

		[DictionaryFact]
		public void DynaCabTypesAndMicsAreNamedAsTheCabBlockNamesThem()
		{
			IReadOnlyList<Fm9SettingInfo> settings = Fm9Presets.ListSettings("Cab");
			IReadOnlyList<string> types = settings.Single(setting => setting.Name == "Dynacab Type1").Choices;
			IReadOnlyList<string> mics = settings.Single(setting => setting.Name == "Dynacab Mic1").Choices;

			Assert.All(Fm9CabRanker.DynaCabPlacements, placement =>
			{
				Assert.Contains(placement.Type, types);
				Assert.Contains(placement.Mic, mics);
			});
		}

		[Fact]
		public void TheDynaCabPlacementATargetWasMadeWithRanksFirstWithItsSettings()
		{
			double[] need = [.. Fm9CabRanker.Curve(Plexi)];

			Fm9CabCandidate best = Rank(need, count: 3, include: Fm9CabSource.Both)[0];

			Assert.Equal(Plexi, best.DynaCab);
			Assert.True(best.GapDecibels < 0.3, $"gap {best.GapDecibels}");
			Assert.Equal("DYNA-CAB", best.CabSettings["Mode"]);
			Assert.Equal("4x12 1960TV", best.CabSettings["Dynacab Type1"]);
			Assert.Equal("Dynamic 1", best.CabSettings["Dynacab Mic1"]);
			Assert.Equal("4", best.CabSettings["Dyna Cab"]);
			Assert.Equal("1", best.CabSettings["Distance"]);
			// Distance is in centimetres on the FM9 (0-24 cm).
			Assert.EndsWith("position 4, 1 cm", best.Name, StringComparison.Ordinal);
		}

		[Fact]
		public void FactoryCabsAloneAreRankedUnlessDynaCabIsIncluded()
		{
			double[] need = [.. Fm9CabRanker.Curve(Plexi)];

			IReadOnlyList<Fm9CabCandidate> factory = Rank(need, count: 5);
			IReadOnlyList<Fm9CabCandidate> dynaCab = Rank(need, count: 5, include: Fm9CabSource.DynaCab);

			Assert.Equal(5, factory.Count);
			Assert.All(factory, candidate => Assert.Null(candidate.DynaCab));
			Assert.All(factory, candidate => Assert.Equal("LEGACY", candidate.CabSettings["Mode"]));
			Assert.Equal(5, dynaCab.Count);
			Assert.All(dynaCab, candidate => Assert.NotNull(candidate.DynaCab));
		}

		[Fact]
		[Trait("Category", "Heavy")]
		public void AFactoryCabRanksFirstAmongEveryMeasuredCabAndPlacement()
		{
			// The whole roster, factory and DynaCab together, as the product ranks it: the cheap estimate must keep the
			// target among the cabs it fits.
			Fm9CabCandidate best = Fm9CabRanker.Rank(NeedWithBump(Fm9CabRanker.Curve(3, 38)), count: 1, include: Fm9CabSource.Both)[0];

			Assert.Equal((3, 38), (best.Bank, best.Type));
			Assert.True(best.GapDecibels < 0.3, $"gap {best.GapDecibels}");
		}
	
		[Fact]
		public void TheToneMatchPicksADynaCabOnlyWhenItBeatsTheBestFactoryCab()
		{
			Fm9CabPick dyna = Pick([.. Fm9CabRanker.Curve(Plexi)], allowDynaCab: true);
			Fm9CabPick factoryOnly = Pick([.. Fm9CabRanker.Curve(Plexi)], allowDynaCab: false);
			Fm9CabPick factoryWins = Pick([.. Fm9CabRanker.Curve(3, 38)], allowDynaCab: true);

			Assert.True(dyna.ChoosesDynaCab);
			Assert.Equal(Plexi, dyna.Chosen.DynaCab);
			Assert.True(dyna.Chosen.GapDecibels < dyna.Factory!.GapDecibels);
			Assert.False(factoryOnly.ChoosesDynaCab);
			Assert.Null(factoryOnly.DynaCab);
			Assert.False(factoryWins.ChoosesDynaCab);
			Assert.Equal((3, 38), (factoryWins.Chosen.Bank, factoryWins.Chosen.Type));
			Assert.True(factoryWins.DynaCab is null || factoryWins.DynaCab.GapDecibels >= factoryWins.Chosen.GapDecibels);
		}

		[Fact]
		public void ThePlayingCabsCurveIsKnownOnlyForMeasuredCabs()
		{
			Assert.Equal(Fm9CabRanker.Curve(3, 38), Fm9CabRanker.PlayingCurve(3, 38, null));
			Assert.Equal(Fm9CabRanker.Curve(Plexi), Fm9CabRanker.PlayingCurve(-1, -1, Plexi with { Type = "4x12 1960tv" }));
			Assert.Null(Fm9CabRanker.PlayingCurve(-1, -1, Plexi with { Position = 3 }));
		}

		[DictionaryFact]
		public void TheToneDesignersDynaCabRosterIsTheFm9DictionarysOwn()
		{
			Assert.Equal(Fractal.Companion.Catalog.Fm9BlockSettings.Find("Cab", "Dynacab Type1")!.Choices, ToneDesignDynaCab.Types);
			Assert.Equal(Fractal.Companion.Catalog.Fm9BlockSettings.Find("Cab", "Dynacab Mic2")!.Choices, ToneDesignDynaCab.Mics);
			Assert.Equal(ToneDesignDynaCab.Modes, Fractal.Companion.Catalog.Fm9BlockSettings.Find("Cab", "Mode")!.Choices);
			// The setting names the proposals use sit at FracCompat's ids (MODE 31, TYPE 85-86, MIC 89-90, R 93-94, Z 97-98, MUTE2 25).
			Assert.Equal([85, 86, 89, 90, 93, 94, 97, 98, 25, 31],
				ToneDesignDynaCab.SlotParameters.Append(ToneDesignDynaCab.ModeParameter)
					.Select(name => Fractal.Companion.Catalog.Fm9BlockSettings.Find("Cab", name)!.Id));
			Assert.Equal((0d, 24d), (Fractal.Companion.Catalog.Fm9BlockSettings.Find("Cab", "Distance 3")!.Minimum!.Value,
				Fractal.Companion.Catalog.Fm9BlockSettings.Find("Cab", "Distance 3")!.Maximum!.Value));
		}
	}
}
