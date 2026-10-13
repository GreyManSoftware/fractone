using System.Numerics;

namespace Fractal.Core
{
	internal static class FastFourierTransform
	{
		public static void TransformInPlace(Complex[] values, bool inverse = false)
		{
			int count = values.Length;

			if (count == 0 || (count & (count - 1)) != 0)
			{
				throw new ArgumentException("FFT input length must be a power of two.", nameof(values));
			}

			for (int index = 1, reversed = 0; index < count; index++)
			{
				int bit = count >> 1;

				while ((reversed & bit) != 0)
				{
					reversed ^= bit;
					bit >>= 1;
				}

				reversed ^= bit;

				if (index < reversed)
				{
					(values[index], values[reversed]) = (values[reversed], values[index]);
				}
			}

			double direction = inverse ? 1 : -1;

			for (int size = 2; size <= count; size <<= 1)
			{
				double angle = direction * 2 * Math.PI / size;
				Complex phaseStep = new(Math.Cos(angle), Math.Sin(angle));

				for (int offset = 0; offset < count; offset += size)
				{
					Complex phase = Complex.One;
					int halfSize = size / 2;

					for (int index = 0; index < halfSize; index++)
					{
						Complex even = values[offset + index];
						Complex odd = values[offset + index + halfSize] * phase;
						values[offset + index] = even + odd;
						values[offset + index + halfSize] = even - odd;
						phase *= phaseStep;
					}
				}
			}

			if (inverse)
			{
				for (int index = 0; index < values.Length; index++)
				{
					values[index] /= count;
				}
			}
		}
	}
}
