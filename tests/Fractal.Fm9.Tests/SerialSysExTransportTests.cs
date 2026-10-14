using System.Threading.Channels;
using Fractal.Companion.Protocol;
using Fractal.Companion.Transport;

namespace Fractal.Fm9.Tests
{
	public sealed class SerialSysExTransportTests
	{
		private const byte Fm3 = 0x11;

		[Fact]
		public async Task A_reply_split_across_reads_comes_back_as_one_frame()
		{
			byte[] reply = Frame(Fm3, 0x08, 0x0D, 0x00);
			using FakeSerialStream stream = new(_ => [reply[..3], reply[3..6], reply[6..]]);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30));

			IReadOnlyList<FractalSysExFrame> replies = await transport.RequestAsync(Request(Fm3, 0x08), CancellationToken.None);

			Assert.Equal(reply, Assert.Single(replies).Bytes.ToArray());
		}

		[Fact]
		public async Task Several_frames_in_one_read_are_all_returned()
		{
			byte[] first = Frame(Fm3, 0x0D, 0x01, 0x02);
			byte[] second = Frame(Fm3, 0x0C, 0x03);
			using FakeSerialStream stream = new(_ => [[.. first, .. second]]);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30));

			IReadOnlyList<FractalSysExFrame> replies = await transport.RequestAsync(Request(Fm3, 0x0D, 0x7F, 0x7F), CancellationToken.None);

			Assert.Equal([first, second], replies.Select(frame => frame.Bytes.ToArray()));
		}

		[Fact]
		public async Task Bytes_between_frames_and_cut_short_frames_are_dropped()
		{
			byte[] first = Frame(Fm3, 0x0D, 0x01);
			byte[] second = Frame(Fm3, 0x0C, 0x02);
			// Noise, a frame cut short by a new F0, a stray status byte inside a frame, then the real frames.
			byte[] noisy = [0x00, 0x42, 0xF0, 0x00, 0x01, .. first, 0x13, 0x37, 0xF0, 0x01, 0x90, 0x05, .. second, 0x7E];
			using FakeSerialStream stream = new(_ => [noisy]);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30));

			IReadOnlyList<FractalSysExFrame> replies = await transport.RequestAsync(Request(Fm3, 0x0D, 0x7F, 0x7F), CancellationToken.None);

			Assert.Equal([first, second], replies.Select(frame => frame.Bytes.ToArray()));
		}

		[Fact]
		public async Task A_request_nobody_answers_times_out()
		{
			using FakeSerialStream stream = new(_ => []);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30));

			await Assert.ThrowsAsync<TimeoutException>(() => transport.RequestAsync(Request(Fm3, 0x08), CancellationToken.None));
			Assert.Single(stream.Written);
		}

		[Fact]
		public async Task A_preset_dump_waits_through_pauses_until_its_footer()
		{
			byte[] header = Frame(Fm3, 0x77, 0x7F, 0x7F, 0x00, 0x40, 0x00);
			byte[] data = Frame(Fm3, 0x78, [.. Enumerable.Repeat((byte)0x11, 64)]);
			byte[] footer = Frame(Fm3, 0x79, 0x01, 0x02, 0x03);
			using FakeSerialStream stream = new(_ => [header, data[..10]]);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));

			Task<IReadOnlyList<FractalSysExFrame>> request = transport.RequestAsync(Request(Fm3, 0x03, 0x7F, 0x7F), CancellationToken.None);
			// Longer than the quiet gap: a dump is only over at its footer.
			await Task.Delay(150);
			Assert.False(request.IsCompleted);
			stream.Push([.. data[10..], .. data, .. footer]);
			IReadOnlyList<FractalSysExFrame> replies = await request.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.Equal([header, data, data, footer], replies.Select(frame => frame.Bytes.ToArray()));
		}

		[Fact]
		public async Task A_preset_dump_ends_at_the_footer_of_any_gen3_model()
		{
			byte[] footer = Frame(0x10, 0x79, 0x01, 0x02, 0x03);
			using FakeSerialStream stream = new(_ => [Frame(0x10, 0x77, 0x7F, 0x7F), footer]);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));

			IReadOnlyList<FractalSysExFrame> replies = await transport.RequestAsync(Request(0x10, 0x03, 0x7F, 0x7F), CancellationToken.None)
				.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.Equal(footer, replies[^1].Bytes.ToArray());
		}

		[Fact]
		public async Task Requests_and_sends_write_the_frame_unchanged()
		{
			FractalSysExFrame request = Request(Fm3, 0x08);
			FractalSysExFrame send = Request(Fm3, 0x0C, 0x7F);
			using FakeSerialStream stream = new(written => written[5] == 0x08 ? [Frame(Fm3, 0x08, 0x0D, 0x00)] : []);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));

			await transport.RequestAsync(request, CancellationToken.None);
			await transport.SendAsync(send, CancellationToken.None);

			Assert.Equal([request.Bytes.ToArray(), send.Bytes.ToArray()], stream.Written);
		}

		[Fact]
		public async Task Frames_arriving_outside_a_request_are_not_handed_to_the_next_one()
		{
			byte[] stale = Frame(Fm3, 0x0D, 0x09);
			byte[] reply = Frame(Fm3, 0x08, 0x0D, 0x00);
			using FakeSerialStream stream = new(_ => [reply]);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));
			stream.Push(stale);
			await Task.Delay(50);

			IReadOnlyList<FractalSysExFrame> replies = await transport.RequestAsync(Request(Fm3, 0x08), CancellationToken.None);

			Assert.Equal(reply, Assert.Single(replies).Bytes.ToArray());
		}

		[Fact]
		public async Task A_closed_port_fails_the_waiting_request_at_once()
		{
			using FakeSerialStream stream = new(_ => []);
			using SerialSysExTransport transport = new(stream, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20));

			Task<IReadOnlyList<FractalSysExFrame>> request = transport.RequestAsync(Request(Fm3, 0x08), CancellationToken.None);
			await Task.Delay(50);
			stream.EndOfStream();

			await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
		}

		[Fact]
		public async Task Selecting_a_preset_by_program_change_is_refused()
		{
			using FakeSerialStream stream = new(_ => []);
			using SerialSysExTransport transport = new(stream);

			await Assert.ThrowsAsync<NotSupportedException>(() => transport.SelectPresetAsync(5, 1, CancellationToken.None));
			Assert.Empty(stream.Written);
		}

		[Fact]
		public void The_framer_ignores_real_time_bytes_inside_a_frame()
		{
			SysExFramer framer = new();

			IReadOnlyList<byte[]> frames = framer.Push([0xF0, 0x00, 0xF8, 0x01, 0xFE, 0xF7]);

			Assert.Equal([0xF0, 0x00, 0x01, 0xF7], Assert.Single(frames));
		}

		[Fact]
		public void The_framer_drops_a_runaway_frame()
		{
			SysExFramer framer = new(maxFrameBytes: 8);

			IReadOnlyList<byte[]> frames = framer.Push([0xF0, .. Enumerable.Repeat((byte)0x01, 20), 0xF7, 0xF0, 0x02, 0xF7]);

			Assert.Equal([0xF0, 0x02, 0xF7], Assert.Single(frames));
			Assert.True(framer.DiscardedBytes > 0);
		}

		[Fact]
		public void Discovery_picks_the_port_named_for_the_FM3()
		{
			SerialPortInfo[] ports = [new("COM3", "Communications Port (COM3)"), new("COM7", "FM3 Communications Port (COM7)")];

			Assert.Equal("COM7", SerialPortDiscovery.Find(ports, "FM3")?.Port);
		}

		[Fact]
		public void Discovery_takes_an_explicit_port_only_if_it_exists()
		{
			SerialPortInfo[] ports = [new("COM3", "Communications Port (COM3)"), new("COM7", "FM3 Communications Port (COM7)")];

			Assert.Equal("COM3", SerialPortDiscovery.Find(ports, "FM3", "com3")?.Port);
			Assert.Null(SerialPortDiscovery.Find(ports, "FM3", "COM9"));
		}

		[Fact]
		public void Discovery_refuses_to_guess_between_two_FM3_ports()
		{
			SerialPortInfo[] ports = [new("COM5", "FM3 Communications Port (COM5)"), new("COM7", "FM3 Communications Port (COM7)")];

			Assert.Null(SerialPortDiscovery.Find(ports, "FM3"));
		}

		[Fact]
		public void Discovery_takes_the_only_usb_modem_on_macOS()
		{
			SerialPortInfo[] ports = [new("/dev/cu.Bluetooth-Incoming-Port", "/dev/cu.Bluetooth-Incoming-Port"), new("/dev/cu.usbmodem14101", "/dev/cu.usbmodem14101")];

			Assert.Equal("/dev/cu.usbmodem14101", SerialPortDiscovery.Find(ports, "FM3")?.Port);
		}

		[Fact]
		public void Discovery_matches_the_device_name_as_a_word()
		{
			SerialPortInfo[] ports = [new("COM4", "XFM31 Modem (COM4)")];

			Assert.Null(SerialPortDiscovery.Find(ports, "FM3"));
		}

		[Theory]
		[InlineData("COM5", true)]
		[InlineData("com12", true)]
		[InlineData("/dev/cu.usbmodem14101", true)]
		[InlineData("FM9", false)]
		[InlineData("Axe-Fx III", false)]
		[InlineData("COMMS", false)]
		public void Serial_port_names_are_told_from_MIDI_port_names(string name, bool serial)
		{
			Assert.Equal(serial, SerialPortDiscovery.LooksLikeSerialPort(name));
		}

		private static FractalSysExFrame Request(byte model, byte function, params byte[] payload) => new(Frame(model, function, payload));

		// A Fractal frame with its checksum: XOR of every byte before it, F0 included, 7 bits.
		private static byte[] Frame(byte model, byte function, params byte[] payload)
		{
			byte[] frame = [0xF0, 0x00, 0x01, 0x74, model, function, .. payload, 0x00, 0xF7];
			int checksum = 0;
			for (int index = 0; index < frame.Length - 2; index++)
				checksum ^= frame[index];
			frame[^2] = (byte)(checksum & 0x7F);
			return frame;
		}

		// An in-memory serial line: what the transport writes is recorded and answered by the responder, in the chunks it
		// returns, so each chunk is one read on the transport's side.
		private sealed class FakeSerialStream(Func<byte[], IReadOnlyList<byte[]>> responder) : Stream
		{
			private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
			private readonly List<byte[]> _written = [];
			private byte[] _remainder = [];

			public IReadOnlyList<byte[]> Written
			{
				get
				{
					lock (_written)
						return [.. _written];
				}
			}

			public void Push(byte[] chunk) => _incoming.Writer.TryWrite(chunk);

			public void EndOfStream() => _incoming.Writer.TryComplete();

			public override bool CanRead => true;
			public override bool CanWrite => true;
			public override bool CanSeek => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

			public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
			{
				if (_remainder.Length == 0)
				{
					if (!await _incoming.Reader.WaitToReadAsync(cancellationToken) || !_incoming.Reader.TryRead(out byte[]? chunk))
						return 0;
					_remainder = chunk;
				}
				int count = Math.Min(buffer.Length, _remainder.Length);
				_remainder.AsMemory(0, count).CopyTo(buffer);
				_remainder = _remainder[count..];
				return count;
			}

			public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
			{
				byte[] written = buffer.ToArray();
				lock (_written)
					_written.Add(written);
				foreach (byte[] chunk in responder(written))
					Push(chunk);
				return ValueTask.CompletedTask;
			}

			public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
			public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
			public override void Flush() { }
			public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();

			protected override void Dispose(bool disposing)
			{
				_incoming.Writer.TryComplete();
				base.Dispose(disposing);
			}
		}
	}
}
