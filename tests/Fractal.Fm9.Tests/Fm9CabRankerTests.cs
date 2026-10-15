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

			Fm9CabCandidate best = Fm9CabRanker.Rank(need, count: 3, include: Fm9CabSource.Both)[0];

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

			IReadOnlyList<Fm9CabCandidate> factory = Fm9CabRanker.Rank(need, count: 5);
			IReadOnlyList<Fm9CabCandidate> dynaCab = Fm9CabRanker.Rank(need, count: 5, include: Fm9CabSource.DynaCab);
			IReadOnlyList<Fm9CabCandidate> both = Fm9CabRanker.Rank(Fm9CabRanker.Curve(3, 38), count: 1, include: Fm9CabSource.Both);

			Assert.All(factory, candidate => Assert.Null(candidate.DynaCab));
			Assert.All(factory, candidate => Assert.Equal("LEGACY", candidate.CabSettings["Mode"]));
			Assert.All(dynaCab, candidate => Assert.NotNull(candidate.DynaCab));
			Assert.Equal((3, 38), (both[0].Bank, both[0].Type));
			Assert.Throws<ArgumentException>(() => Fm9CabRanker.Rank(need, nameFilter: "no such cab", include: Fm9CabSource.DynaCab));
		}
	
		[Fact]
		public void TheToneMatchPicksADynaCabOnlyWhenItBeatsTheBestFactoryCab()
		{
			Fm9CabPick dyna = Fm9CabRanker.Pick([.. Fm9CabRanker.Curve(Plexi)], allowDynaCab: true);
			Fm9CabPick factoryOnly = Fm9CabRanker.Pick([.. Fm9CabRanker.Curve(Plexi)], allowDynaCab: false);
			Fm9CabPick factoryWins = Fm9CabRanker.Pick([.. Fm9CabRanker.Curve(3, 38)], allowDynaCab: true);

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
