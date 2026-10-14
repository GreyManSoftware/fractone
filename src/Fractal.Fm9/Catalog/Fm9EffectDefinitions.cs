using System.Reflection;
using System.Text;

namespace Fractal.Companion.Catalog
{
	// How a parameter's stored 0..65534 value maps to what the FM9 displays. Read from the typecode's low byte, whose high
	// nibble FM9-Edit uses as the taper: 4 is logarithmic, 3 and 5-9 are linear. Verified on hardware by writing wire values
	// and reading FM9-Edit's knob text: Amp Input Trim (log, 0.1 x 100^x), Amp Gain, Level (-80..+20 dB), Balance, Gate
	// Threshold, Reverb Mix and Cab Drive (linear). Choices store their ordinal.
	public enum Fm9ParameterKind
	{
		Unused,
		Linear,
		Logarithmic,
		Choice,
		Integer,
		Internal
	}

	public sealed record Fm9EffectParameter(int Id, ushort TypeCode, double Minimum, double Maximum, double DisplayScale,
		double Step, IReadOnlyList<string> Labels, ushort Flags)
	{
		private int Taper => (TypeCode & 0xFF) >> 4;

		public Fm9ParameterKind Kind => Taper switch
		{
			_ when TypeCode == 0 => Fm9ParameterKind.Unused,
			// 0xA-0xC are bypass modes (labelled choices); 0x1 and 0x2 are choices and numbered lists.
			1 or 2 or 0xA or 0xB or 0xC when Labels.Count > 0 => Fm9ParameterKind.Choice,
			1 or 2 => Fm9ParameterKind.Integer,
			3 or 6 or 7 or 8 or 9 => Fm9ParameterKind.Linear,
			// Kind 5 (drives, resonances, slew) is a 0..10 knob over an internal curve of its own: Cab Drive wire 32767 reads
			// 5.00 and Delay Drive (stored 0.05..50) reads 1.00 at 6554 and 10.00 at full, both linear on hardware.
			5 => Fm9ParameterKind.Linear,
			4 => Fm9ParameterKind.Logarithmic,
			// 0xD global mix, 0xE scene-controlled bypass, 0xF engine-managed values.
			_ => Fm9ParameterKind.Internal
		};

		// The displayed range: stored minimum and maximum times the display scale (kind 5 is the 0..10 knob).
		public double DisplayMinimum => Taper == 5 ? 0 : Tidy(Minimum * Scale);
		public double DisplayMaximum => Taper == 5 ? KindFiveKnobMaximum : Tidy(Maximum * Scale);
		private const double KindFiveKnobMaximum = 10;
		private double Scale => DisplayScale == 0 ? 1 : DisplayScale;

		public double ToDisplay(int wire)
		{
			double fraction = Math.Clamp(wire, 0, 65534) / 65534.0;
			return Kind switch
			{
				Fm9ParameterKind.Logarithmic => DisplayMinimum * Math.Pow(DisplayMaximum / DisplayMinimum, fraction),
				Fm9ParameterKind.Linear => DisplayMinimum + (fraction * (DisplayMaximum - DisplayMinimum)),
				_ => throw new InvalidOperationException($"Parameter {Id} is not continuous.")
			};
		}

		public int ToWire(double display)
		{
			if (!double.IsFinite(display) || display < DisplayMinimum - Tolerance || display > DisplayMaximum + Tolerance)
				throw new ArgumentOutOfRangeException(nameof(display), $"Use a value from {DisplayMinimum:G} to {DisplayMaximum:G}.");
			double clamped = Math.Clamp(display, DisplayMinimum, DisplayMaximum);
			double fraction = Kind switch
			{
				Fm9ParameterKind.Logarithmic => Math.Log(clamped / DisplayMinimum) / Math.Log(DisplayMaximum / DisplayMinimum),
				Fm9ParameterKind.Linear => DisplayMaximum == DisplayMinimum ? 0 : (clamped - DisplayMinimum) / (DisplayMaximum - DisplayMinimum),
				_ => throw new InvalidOperationException($"Parameter {Id} is not continuous.")
			};
			return (int)Math.Round(fraction * 65534, MidpointRounding.AwayFromZero);
		}

		// The cache stores single-precision floats (0.1 reads as 0.10000000149): six significant digits is what the FM9 shows.
		private static double Tidy(double value) =>
			value == 0 ? 0 : double.Parse(value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

		private double Tolerance => Math.Max(1e-6, Math.Abs(DisplayMaximum - DisplayMinimum) * 1e-6);
	}

	public sealed record Fm9EffectSection(int Id, string? Family, IReadOnlyList<Fm9EffectParameter> Parameters)
	{
		public Fm9EffectParameter? Find(int id) => Parameters.FirstOrDefault(parameter => parameter.Id == id);
	}

	// FM9-Edit's parameter dictionary, which it downloads from the FM9 and caches as effectDefinitions_<model>_<firmware>.cache
	// (bundled here from firmware 12.00). Sections of records: u32 section id, u32 count, then per parameter u16 id, u16
	// typecode, u16, f32 minimum, maximum, display scale and step, u32 label count and length-prefixed labels, u16 flags
	// (0x8000 = enumerated), and a counted u32 array (Cab rosters carry one word per name).
	public sealed class Fm9EffectDefinitions
	{
		// Section ids to catalogue families, matched by every record's range and typecode against the community catalogue.
		// Input and Output have one section per instance (they share a layout).
		private static readonly IReadOnlyDictionary<int, string> SectionFamilies = new Dictionary<int, string>
		{
			[1] = "GLOBAL", [2] = "CONTROLLERS", [3] = "MOD", [7] = "COMP", [8] = "GEQ", [9] = "PEQ", [10] = "DISTORT",
			[11] = "CABINET", [12] = "REVERB", [13] = "DELAY", [14] = "MULTITAP", [15] = "PLEX", [16] = "CHORUS",
			[17] = "FLANGER", [18] = "ROTARY", [19] = "PHASER", [20] = "WAH", [21] = "FORMANT", [22] = "TREMOLO",
			[23] = "PITCH", [24] = "FILTER", [25] = "FUZZ", [26] = "ENHANCER", [28] = "MIXER", [29] = "FDBKSEND",
			[30] = "FDBKRET", [31] = "SYNTH", [33] = "MEGATAP", [34] = "CROSSOVER", [35] = "GATE", [36] = "RINGMOD",
			[37] = "MULTICOMP", [38] = "TENTAP", [39] = "RESONATOR", [40] = "VOLUME", [41] = "INPUT", [42] = "INPUT",
			[43] = "INPUT", [44] = "INPUT", [46] = "OUTPUT", [47] = "OUTPUT", [48] = "OUTPUT", [50] = "LOOPER",
			[53] = "MIDIBLOCK", [54] = "MULTIPLEXER", [57] = "PRESET"
		};

		// The dictionary section of a family (the first, for Input and Output); FM9-Edit also files library blocks under it.
		public static int? SectionOf(string family) =>
			SectionFamilies.Where(pair => string.Equals(pair.Value, family, StringComparison.OrdinalIgnoreCase)).Select(pair => (int?)pair.Key).Min();

		private static readonly Lazy<Fm9EffectDefinitions> BundledDefinitions = new(LoadBundled);

		private Fm9EffectDefinitions(IReadOnlyList<Fm9EffectSection> sections) => Sections = sections;

		public static Fm9EffectDefinitions Bundled => BundledDefinitions.Value;

		public IReadOnlyList<Fm9EffectSection> Sections { get; }

		// The family's section; Input and Output use their first instance's (all instances share one layout).
		public Fm9EffectSection? ForFamily(string family) =>
			Sections.FirstOrDefault(section => string.Equals(section.Family, family, StringComparison.OrdinalIgnoreCase));

		public static Fm9EffectDefinitions Parse(ReadOnlySpan<byte> bytes)
		{
			Reader reader = new(bytes.ToArray());
			reader.Offset = 0x2E;
			List<Fm9EffectSection> sections = [];
			while (reader.Offset < bytes.Length)
			{
				int id = checked((int)reader.UInt32());
				int count = checked((int)reader.UInt32());
				if (count > 4096) throw new InvalidDataException($"Section {id} claims {count} parameters.");
				List<Fm9EffectParameter> parameters = new(count);
				for (int index = 0; index < count; index++)
					parameters.Add(reader.Parameter());
				sections.Add(new(id, SectionFamilies.GetValueOrDefault(id), parameters));
			}
			if (reader.Offset != bytes.Length) throw new InvalidDataException("The FM9-Edit parameter cache has trailing bytes.");
			return new(sections);
		}

		// Where the dictionary was read from: a path, or "bundled".
		public static string BundledSource { get; private set; } = "";

		// The environment variable that points at a dictionary file explicitly.
		public const string PathVariable = "FRACTONE_EDIT_DEFINITIONS";
		// The name before FracTone, still read.
		public const string OldPathVariable = "FM9_EDIT_DEFINITIONS";

		// FM9-Edit downloads the dictionary from the FM9 and caches it per firmware (12 = the FM9's model byte, 12p0 =
		// firmware 12.00). The file also lists the user's own preset names, so a public build reads the user's copy instead
		// of shipping one: an explicit path first, then a bundled copy if this build has one, then FM9-Edit's own cache.
		private static Fm9EffectDefinitions LoadBundled()
		{
			if ((Environment.GetEnvironmentVariable(PathVariable) ?? Environment.GetEnvironmentVariable(OldPathVariable)) is { Length: > 0 } explicitPath)
				return LoadFile(explicitPath);
			using (Stream? stream = typeof(Fm9EffectDefinitions).Assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-effect-definitions.cache"))
			{
				if (stream is not null)
				{
					using MemoryStream copy = new();
					stream.CopyTo(copy);
					BundledSource = "bundled";
					return Parse(copy.ToArray());
				}
			}
			return FindInstalled() is string installed ? LoadFile(installed)
				: throw new InvalidOperationException(
					"FM9-Edit's parameter dictionary was not found. Open FM9-Edit once with the FM9 connected (it caches the " +
					$@"dictionary under %APPDATA%\Fractal Audio\FM9-Edit), or set {PathVariable} to the file.");
		}

		private static Fm9EffectDefinitions LoadFile(string path)
		{
			Fm9EffectDefinitions definitions = Parse(File.ReadAllBytes(path));
			BundledSource = Path.GetFullPath(path);
			return definitions;
		}

		// FM9-Edit's cached dictionary (effectDefinitions_<model 12>_<firmware>p<minor>.cache): firmware 12's, which these
		// tools' catalogues match, or else the newest. A newer firmware's dictionary still reads presets; writes stay limited
		// to firmware 12 by the device checks.
		public static string? FindInstalled()
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fractal Audio", "FM9-Edit");
			if (!Directory.Exists(folder)) return null;
			(int Major, int Minor) Version(string path)
			{
				string[] parts = Path.GetFileNameWithoutExtension(path).Split('_').Last().Split('p');
				return parts.Length == 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor) ? (major, minor) : (-1, -1);
			}
			string[] files = Directory.GetFiles(folder, "effectDefinitions_12_*.cache");
			return files.Where(path => Version(path).Major == 12).OrderByDescending(path => Version(path).Minor).FirstOrDefault()
				?? files.OrderByDescending(path => Version(path)).FirstOrDefault();
		}

		private sealed class Reader(byte[] bytes)
		{
			public int Offset { get; set; }

			public Fm9EffectParameter Parameter()
			{
				int id = UInt16();
				ushort type = UInt16();
				_ = UInt16();
				double minimum = Single(), maximum = Single(), scale = Single(), step = Single();
				int labelCount = checked((int)UInt32());
				if (labelCount > 4096) throw new InvalidDataException($"Parameter {id} claims {labelCount} labels.");
				string[] labels = new string[labelCount];
				for (int index = 0; index < labelCount; index++)
				{
					int length = checked((int)UInt32());
					if (length > 256) throw new InvalidDataException($"Parameter {id} has an oversized label.");
					labels[index] = Encoding.Latin1.GetString(Take(length)).TrimEnd();
				}
				ushort flags = UInt16();
				int extra = checked((int)UInt32());
				if (extra > 4096) throw new InvalidDataException($"Parameter {id} claims {extra} extra words.");
				Take(extra * sizeof(uint));
				return new(id, type, minimum, maximum, scale, step, labels, flags);
			}

			public uint UInt32() => BitConverter.ToUInt32(Take(4));
			private ushort UInt16() => BitConverter.ToUInt16(Take(2));
			private float Single() => BitConverter.ToSingle(Take(4));

			private ReadOnlySpan<byte> Take(int count)
			{
				if (count < 0 || Offset + count > bytes.Length) throw new InvalidDataException("The FM9-Edit parameter cache is truncated.");
				ReadOnlySpan<byte> span = bytes.AsSpan(Offset, count);
				Offset += count;
				return span;
			}
		}
	}
}
