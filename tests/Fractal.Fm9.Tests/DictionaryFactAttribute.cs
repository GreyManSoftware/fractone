using Fractal.Companion.Catalog;

namespace Fractal.Fm9.Tests
{
	// A test that needs FM9-Edit's parameter dictionary. The dictionary is Fractal's and lists the user's own preset names,
	// so it is never redistributed: these tests run where FM9-Edit is installed (or FRACTONE_EDIT_DEFINITIONS points at its
	// cache) and are skipped, saying why, elsewhere.
	public sealed class DictionaryFactAttribute : FactAttribute
	{
		private static readonly Lazy<bool> Available = new(() =>
		{
			try
			{
				_ = Fm9EffectDefinitions.Bundled;
				return true;
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException or InvalidDataException or TypeInitializationException)
			{
				return false;
			}
		});

		public DictionaryFactAttribute()
		{
			if (!Available.Value)
				Skip = "Needs FM9-Edit's parameter dictionary: open FM9-Edit once with the FM9 connected, or set FRACTONE_EDIT_DEFINITIONS.";
		}
	}
}
