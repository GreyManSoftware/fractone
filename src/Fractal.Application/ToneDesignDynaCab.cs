using System.Globalization;
using System.Text.Json;

namespace Fractal.Application
{
	// One DynaCab slot of a Cab block: the measured cab type and mic (by ordinal, as the device stores them), the mic's
	// Position across the speaker (0 at the dust cap to 10 at the edge) and its Distance from the grille (cm).
	public sealed record ToneDesignDynaCabSlot(int Type, int Mic, double Position, double DistanceCentimetres);

	// DynaCab, the Cab block's multi-mic mode, in the FM9 dictionary's own setting names. Proposals keep the FM9's names and
	// ordinals (the gen-3 exporter carries them to the FM3 and Axe-Fx III by id). Facts are FracCompat's
	// (dynacab-devices.md, 2026-10-15): the 45 types, 4 mics and ids 31, 85-105 are the same on all three units; the FM9
	// and FM3 have 2 slots per Cab block, the III 4; the FM3 has one Cab block. FracTone authors slots 1 and 2 only.
	public static class ToneDesignDynaCab
	{
		public const string ModeParameter = "Mode";
		public const string Legacy = "LEGACY";
		public const string DynaCab = "DYNA-CAB";
		public const int LegacyOrdinal = 0;
		public const int DynaCabOrdinal = 1;
		public const string SecondSlotMuteParameter = "Mute2";
		// Mute choices: "--" plays the slot, MUTE silences it (SOLO is never authored).
		public const int Unmuted = 0;
		public const int Muted = 1;
		public const double MinimumPosition = 0;
		public const double MaximumPosition = 10;
		public const double MinimumDistance = 0;
		public const double MaximumDistance = 24;
		public const double CentimetresPerInch = 2.54;
		// The slots FracTone shows and authors; an Axe-Fx III's slots 3 and 4 keep its template's values.
		public const int AuthoredSlots = 2;

		// The FM9 dictionary's names for slot 1 and 2 settings (FM9-Edit labels, made unique: slot 2's Distance is "Distance 3").
		private static readonly string[] TypeParameters = ["Dynacab Type1", "Dynacab Type2"];
		private static readonly string[] MicParameters = ["Dynacab Mic1", "Dynacab Mic2"];
		private static readonly string[] PositionParameters = ["Dyna Cab", "Dyna Cab 2"];
		private static readonly string[] DistanceParameters = ["Distance", "Distance 3"];

		// Ordinal = position, firmware 12.00 (FM9), 13.00 (FM3), 32.06 (III; ordinal 6 reads "1x12 BLACK MAGIK" there).
		public static IReadOnlyList<string> Types { get; } =
		[
			"1x8 5F1 TWEED", "1x8 PRINCETONE", "1x10 BF PRINCETONE", "1x10 METRO BLUES", "1x10 SF PRINCETONE", "1x12 AC20",
			"1x12 BLACK MAGICK", "1x12 CAR AMBLER", "1x12 DELUXE TWEED", "1x12 DELUXE VERB", "1x12 DIV13 CJ11", "1x12 G12T-100",
			"1x12 HOT KITTY", "1x12 JR BLUES", "1x12 NUCLEAR TONE", "1x12 SCHOLZ", "1x12 TWEED 20112", "1x12 VIBRATO LUX",
			"1x15 HEART KEY", "1x15 PORTABASS", "1x15 VIBROVERB", "2x10 HEART KEY", "2x10 SUPER 6G4", "2x12 5153 STEALTH",
			"2x12 65 BASSGUY", "2x12 CHIEFMAN", "2x12 CLASS-A 30W", "2x12 DOUBLE VERB", "2x12 LEAD 80", "2x12 TEXAS STAR",
			"4x10 BASSGUY RI", "4x12 1960TV", "4x12 5153", "4x12 5153 STEALTH", "4x12 CITRUS", "4x12 FRIEDMAN GB",
			"4x12 FRIEDMAN V30", "4x12 LERXST", "4x12 RECTO SLANT", "4x12 RECTO STRAIGHT", "4x12 RUMBLE EV12L",
			"4x12 RUMBLE EV12S", "4x12 SOLO 100", "4x12 USA MC90", "8x10 SV BASS"
		];

		public static IReadOnlyList<string> Mics { get; } = ["Condenser", "Ribbon", "Dynamic 1", "Dynamic 2"];

		public static IReadOnlyList<string> Modes { get; } = [Legacy, DynaCab];

		// Every setting name this mode owns, for stripping and for the gen-3 checks.
		public static IReadOnlyList<string> SlotParameters { get; } =
			[.. TypeParameters, .. MicParameters, .. PositionParameters, .. DistanceParameters, SecondSlotMuteParameter];

		public static string TypeParameter(int slot) => TypeParameters[SlotIndex(slot)];
		public static string MicParameter(int slot) => MicParameters[SlotIndex(slot)];
		public static string PositionParameter(int slot) => PositionParameters[SlotIndex(slot)];
		public static string DistanceParameter(int slot) => DistanceParameters[SlotIndex(slot)];

		// DynaCab slots per Cab block on each unit.
		public static int Slots(FractalDeviceId? device) => device == FractalDeviceId.AxeFxIii ? 4 : 2;

		public static bool IsSetting(string name) =>
			string.Equals(name, ModeParameter, StringComparison.OrdinalIgnoreCase) ||
			SlotParameters.Contains(name, StringComparer.OrdinalIgnoreCase);

		public static bool IsCab(ToneDesignBlockProposal block) =>
			string.Equals(block?.BlockType, "Cab", StringComparison.OrdinalIgnoreCase);

		// Whether these Cab settings play DynaCab (Mode by ordinal or name).
		public static bool IsDynaCab(IReadOnlyDictionary<string, string> parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			return Find(parameters, ModeParameter) is string mode && Ordinal(mode, Modes) == DynaCabOrdinal;
		}

		// A type, mic or mode given by name (any case; "BLACK MAGIK" is the III's spelling of ordinal 6) or by ordinal.
		public static int? Ordinal(string? value, IReadOnlyList<string> options)
		{
			ArgumentNullException.ThrowIfNull(options);
			string text = value?.Trim() ?? string.Empty;
			if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
				return number >= 0 && number < options.Count ? number : null;
			string normalised = Normalise(text);
			for (int index = 0; index < options.Count; index++)
				if (Normalise(options[index]) == normalised)
					return index;
			return null;
		}

		// "1x12 Black Magik" and "1x12 BLACK MAGICK" are the same cab; spacing, case and the K/CK spelling don't matter.
		private static string Normalise(string name) =>
			new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("MAGICK", "MAGIK", StringComparison.Ordinal);

		// The slots these settings describe (slot 2 only when it plays), or empty when the Cab is not in DynaCab mode.
		public static IReadOnlyList<ToneDesignDynaCabSlot> ReadSlots(IReadOnlyDictionary<string, string> parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			if (!IsDynaCab(parameters))
				return [];
			List<ToneDesignDynaCabSlot> slots = [];
			for (int slot = 1; slot <= AuthoredSlots; slot++)
			{
				if (slot == 2 && (Find(parameters, TypeParameter(2)) is null ||
					Find(parameters, SecondSlotMuteParameter) is string mute && mute.Trim() is not ("0" or "--")))
					continue;
				if (Ordinal(Find(parameters, TypeParameter(slot)), Types) is not int type ||
					Ordinal(Find(parameters, MicParameter(slot)), Mics) is not int mic)
					continue;
				slots.Add(new(type, mic, Number(Find(parameters, PositionParameter(slot))) ?? 5,
					Number(Find(parameters, DistanceParameter(slot))) ?? 4.8));
			}
			return slots;
		}

		// Cab settings that play these slots in DynaCab mode (ordinals as the device stores them); slot 2 is muted when
		// only one slot is given. The factory Bank and Cab Type stay as they are: they play if the mode is set back to Legacy.
		public static IReadOnlyDictionary<string, string> WithSlots(IReadOnlyDictionary<string, string> parameters,
			IReadOnlyList<ToneDesignDynaCabSlot> slots)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(slots);
			if (slots.Count is < 1 or > AuthoredSlots)
				throw new ArgumentException($"A DynaCab Cab plays 1 or {AuthoredSlots} slots.", nameof(slots));
			Dictionary<string, string> result = new(parameters, StringComparer.Ordinal);
			foreach (string name in SlotParameters.Append(ModeParameter))
				Remove(result, name);
			result[ModeParameter] = Text(DynaCabOrdinal);
			for (int index = 0; index < slots.Count; index++)
			{
				ToneDesignDynaCabSlot slot = slots[index];
				result[TypeParameter(index + 1)] = Text(slot.Type);
				result[MicParameter(index + 1)] = Text(slot.Mic);
				result[PositionParameter(index + 1)] = Number(Math.Clamp(slot.Position, MinimumPosition, MaximumPosition));
				result[DistanceParameter(index + 1)] = Number(Math.Clamp(slot.DistanceCentimetres, MinimumDistance, MaximumDistance));
			}
			result[SecondSlotMuteParameter] = Text(slots.Count == 2 ? Unmuted : Muted);
			return result;
		}

		// Cab settings without DynaCab: Mode and every slot setting removed, so the template's Legacy cab plays.
		public static IReadOnlyDictionary<string, string> WithoutDynaCab(IReadOnlyDictionary<string, string> parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			Dictionary<string, string> result = new(parameters, StringComparer.Ordinal);
			foreach (string name in SlotParameters.Append(ModeParameter))
				Remove(result, name);
			return result;
		}

		// Reads the contract's "dynaCab" object ({"slots":[{type,mic,position,distanceCm}]}); null or absent is a factory cab.
		public static IReadOnlyList<ToneDesignDynaCabSlot>? ParseContract(JsonElement block)
		{
			if (!block.TryGetProperty("dynaCab", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
				return null;
			if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("slots", out JsonElement slots) ||
				slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() is < 1 or > AuthoredSlots)
				throw new InvalidDataException($"dynaCab must be null or an object with 1 or {AuthoredSlots} slots.");
			List<ToneDesignDynaCabSlot> result = [];
			foreach (JsonElement slot in slots.EnumerateArray())
			{
				string? typeName = slot.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
				string? micName = slot.TryGetProperty("mic", out JsonElement mic) && mic.ValueKind == JsonValueKind.String ? mic.GetString() : null;
				int typeOrdinal = Ordinal(typeName, Types) ?? throw new InvalidDataException(
					$"DynaCab type '{typeName}' is not one of the 45 DynaCab types; copy a name exactly from the DynaCab roster or use a factory cab (dynaCab null).");
				int micOrdinal = Ordinal(micName, Mics) ?? throw new InvalidDataException(
					$"DynaCab mic '{micName}' must be one of {string.Join(", ", Mics)}.");
				double position = slot.TryGetProperty("position", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble()
					: throw new InvalidDataException("Every DynaCab slot needs a numeric position (0-10).");
				double distance = slot.TryGetProperty("distanceCm", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble()
					: throw new InvalidDataException("Every DynaCab slot needs a numeric distanceCm (0-24).");
				result.Add(new(typeOrdinal, micOrdinal, position, distance));
			}
			return result;
		}

		// What a device can't hold or a value outside the device's ranges, for checks and the gen-3 exporter.
		public static IReadOnlyList<string> Problems(ToneDesignBlockProposal cab, FractalDeviceId? device)
		{
			ArgumentNullException.ThrowIfNull(cab);
			List<string> problems = [];
			if (!IsCab(cab))
				return problems;
			foreach (string channel in cab.GetAuthoredChannels())
			{
				IReadOnlyDictionary<string, string> parameters = cab.GetParameters(channel);
				if (!IsDynaCab(parameters))
					continue;
				string label = $"{ToneDesignChannels.Label($"Cab {cab.Instance}", channel)}";
				for (int slot = 1; slot <= AuthoredSlots; slot++)
				{
					if (Find(parameters, TypeParameter(slot)) is string type && Ordinal(type, Types) is null)
						problems.Add($"{label}: DynaCab type '{type}' is not one of the {Types.Count} DynaCab types.");
					if (Find(parameters, MicParameter(slot)) is string mic && Ordinal(mic, Mics) is null)
						problems.Add($"{label}: DynaCab mic '{mic}' must be one of {string.Join(", ", Mics)}.");
					if (Find(parameters, PositionParameter(slot)) is string position &&
						Number(position) is not (>= MinimumPosition and <= MaximumPosition))
						problems.Add($"{label}: DynaCab slot {slot} position must be {MinimumPosition:0}-{MaximumPosition:0}.");
					if (Find(parameters, DistanceParameter(slot)) is string distance &&
						Number(distance) is not (>= MinimumDistance and <= MaximumDistance))
						problems.Add($"{label}: DynaCab slot {slot} distance must be {MinimumDistance:0}-{MaximumDistance:0} cm.");
				}
			}
			if (device == FractalDeviceId.Fm3 && cab.Instance > 1 && cab.GetAuthoredChannels().Any(channel => IsDynaCab(cab.GetParameters(channel))))
				problems.Add($"The FM3 has one Cab block, so Cab {cab.Instance} can't play DynaCab.");
			return problems;
		}

		// The system-prompt section offered when the player allows DynaCab.
		public static string DescribeForPrompt(FractalDeviceId? device) => string.Join(Environment.NewLine,
		[
			$"DynaCab (allowed by the player for this preset): the Cab block may play a DynaCab instead of a factory cab. DynaCab is the Cab block's multi-mic mode: a measured cab with a mic you place. It is available on this {(device switch { FractalDeviceId.Fm3 => "FM3", FractalDeviceId.AxeFxIii => "Axe-Fx III", _ => "FM9" })} with {AuthoredSlots} slots per Cab block.",
			"Prefer DynaCab only when a DynaCab of the era-correct cab or speaker exists (the same cabinet family and speakers the artist used for that recording); otherwise use the factory cab and set dynaCab to null. A DynaCab is chosen only when it is tonally closer to the target than the factory cab.",
			"Whether or not you choose DynaCab, Bank, Cab Type and modelSelection still name the closest factory cab: it plays if the player switches the Cab back to Legacy. Put the DynaCab in dynaCab: {\"slots\":[{\"type\",\"mic\",\"position\",\"distanceCm\"}]}, one slot, or two to blend two mics (the second slot is muted when absent).",
			$"DynaCab types (copy exactly): {string.Join("; ", Types)}.",
			$"Mics: {string.Join(", ", Mics)}. Dynamic 1 and Dynamic 2 are the classic close-miked rock and metal sound (mid-forward, tight); Ribbon is darker and smoother, good blended with a dynamic or for vintage and clean tones; Condenser is fuller with more detailed highs and air.",
			"Position is 0 (on the dust cap: brightest, most bite) to 10 (at the cone's edge: darkest, softest); 2-5 is the usual cap-edge sweet spot, 5-7 tames fizz. distanceCm is 0-24 cm from the grille: 1-5 cm is a close mic with more bass (proximity), 8-24 cm is looser and more room-like. Start close (1-5 cm) unless the recording sounds distant.",
			"Use DynaCab only for guitar or bass cabs that match the source rig; keep the Cab Low Cut and High Cut as for a factory cab."
		]);

		private static int SlotIndex(int slot) => slot is >= 1 and <= AuthoredSlots ? slot - 1
			: throw new ArgumentOutOfRangeException(nameof(slot), $"FracTone authors DynaCab slots 1-{AuthoredSlots}.");

		private static string? Find(IReadOnlyDictionary<string, string> parameters, string name) =>
			parameters.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

		private static void Remove(Dictionary<string, string> parameters, string name)
		{
			foreach (string key in parameters.Keys.Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToArray())
				parameters.Remove(key);
		}

		private static double? Number(string? value) =>
			double.TryParse(value?.Replace("cm", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float,
				CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;

		private static string Number(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

		private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
	}
}
