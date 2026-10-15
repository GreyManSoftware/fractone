using Fractal.Companion.Auditioning;
using Fractal.Companion.Protocol;

namespace Fractal.Fm9.Tests
{
	// A busy FM9 (straight after a scene change or a recording, the player playing) can cut a dump short or send other
	// frames in the middle of it; level_scenes once failed on exactly that.
	public sealed class Fm9StoredPresetReaderTests : IDisposable
	{
		private readonly TemporaryFolder _folder = new();
		private readonly Fm9PresetDump _metallica = Fm9PresetDump.Parse(File.ReadAllBytes(Fixtures.Path("metallica-eight-scenes.syx")));
		private readonly List<TimeSpan> _waits = [];

		public void Dispose() => _folder.Dispose();

		private FakeFm9 Unit() => new(_metallica) { EditBuffer = _metallica, Preset = 505 };

		private Fm9StoredPresetReader Reader(FakeFm9 fm9) => new(fm9, delayAsync: (delay, _) =>
		{
			_waits.Add(delay);
			return Task.CompletedTask;
		});

		private static IReadOnlyList<FractalSysExFrame> CutShort(IReadOnlyList<FractalSysExFrame> frames) => [.. frames.Take(3)];

		[Fact]
		public async Task FramesTheFm9SendsInTheMiddleOfADumpAreIgnored()
		{
			FakeFm9 fm9 = Unit();
			FractalSysExFrame tempo = FakeFm9.Frame(0x10, []), sceneReply = FakeFm9.Frame(0x0C, [2]);
			fm9.OnRead = (_, frames) => [sceneReply, frames[0], frames[1], tempo, .. frames.Skip(2), tempo];

			Fm9StoredPresetReadResult read = await Reader(fm9).ReadAsync(0x3FFF, CancellationToken.None);

			Assert.True(read.Dump.HasSameRawPatch(_metallica));
			Assert.Equal(1, fm9.Reads);
		}

		[Fact]
		public async Task TheTailOfAnEarlierDumpBeforeTheHeaderIsDropped()
		{
			FakeFm9 fm9 = Unit();
			fm9.OnRead = (_, frames) => [frames[^2], frames[^1], .. frames];

			Fm9StoredPresetReadResult read = await Reader(fm9).ReadAsync(0x3FFF, CancellationToken.None);

			Assert.True(read.Dump.HasSameRawPatch(_metallica));
		}

		[Fact]
		public async Task IncompleteDumpsAreRetriedWithGrowingPausesUntilOneIsComplete()
		{
			FakeFm9 fm9 = Unit();
			fm9.OnRead = (read, frames) => read <= 3 ? CutShort(frames) : frames;

			Fm9StoredPresetReadResult result = await Reader(fm9).ReadAsync(0x3FFF, CancellationToken.None);

			Assert.True(result.Dump.HasSameRawPatch(_metallica));
			Assert.Equal(4, fm9.Reads);
			Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], _waits);
		}

		[Fact]
		public async Task ADumpThatNeverCompletesSaysWhatCameBackAndThatTheFm9MayBeBusy()
		{
			FakeFm9 fm9 = Unit();
			fm9.OnRead = (_, frames) => [.. CutShort(frames), FakeFm9.Frame(0x10, [])];

			InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => Reader(fm9).ReadAsync(0x3FFF, CancellationToken.None));

			Assert.Equal(5, fm9.Reads);
			Assert.Equal([0.25, 0.5, 1, 2], _waits.Select(wait => wait.TotalSeconds));
			Assert.Contains("after 5 attempts", error.Message, StringComparison.Ordinal);
			Assert.Contains("edit buffer", error.Message, StringComparison.Ordinal);
			Assert.Contains("header, 2 data frame(s), no footer; 1 other frame(s) (0x10) dropped", error.Message, StringComparison.Ordinal);
			Assert.Contains("try again", error.Message, StringComparison.Ordinal);
		}

		[Fact]
		public async Task AnEmptySlotIsReportedAtOnceRatherThanRetried()
		{
			FakeFm9 fm9 = Unit();

			await Assert.ThrowsAsync<Fm9EmptyPresetSlotException>(() => Reader(fm9).ReadAsync(12, CancellationToken.None));

			Assert.Equal(1, fm9.Reads);
		}

		[DictionaryFact]
		public async Task ReadingTheEditBufferForLevellingSurvivesABusyFm9AndWaitsWithTheDevicesClock()
		{
			FakeFm9 fm9 = Unit();
			fm9.OnRead = (read, frames) => read == 1 ? CutShort(frames) : [FakeFm9.Frame(0x10, []), .. frames];
			List<TimeSpan> deviceWaits = [];
			Fm9Device device = new(new Fm9Connection(() => fm9, _folder.File("midi.lock")), new Fm9Settings { WorkFolder = _folder.Root })
			{
				Wait = (delay, _) =>
				{
					deviceWaits.Add(delay);
					return Task.CompletedTask;
				}
			};

			Fm9ReadResult read = await device.ReadEditBufferAsync("reads/buffer.syx", CancellationToken.None);

			Assert.Equal(_metallica.ToArray(), File.ReadAllBytes(read.Path));
			Assert.Equal([TimeSpan.FromMilliseconds(250)], deviceWaits);
		}
	}
}
