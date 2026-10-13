using System.Runtime.InteropServices;

namespace Fractal.Companion.Transport
{
	// SysEx input straight over WinMM. NAudio's MidiIn narrows the callback's pointer-sized parameters with ToInt32, which
	// overflows in a 64-bit process once a SysEx header or timestamp sits above 2^31 (it crashed every FM9 exchange on
	// 2026-10-04 while 32-bit runs worked), so the parameters stay pointer-sized here. Windows only: the transport checks
	// the platform before creating it.
	internal sealed class WinMmMidiInput : IDisposable
	{
		private const int CallbackFunction = 0x30000;
		private const int LongData = 0x3C4;
		private const int LongError = 0x3C6;
		private readonly MidiInProc _callback;
		private readonly List<IntPtr> _headers = [];
		private readonly object _syncRoot = new();
		private IntPtr _handle;
		private volatile bool _closing;

		public WinMmMidiInput(int deviceNumber, int bufferSize, int bufferCount)
		{
			_callback = OnMessage;
			Check(midiInOpen(out _handle, deviceNumber, _callback, IntPtr.Zero, CallbackFunction), "midiInOpen");
			try
			{
				for (int index = 0; index < bufferCount; index++)
				{
					IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf<MidiHeader>());
					Marshal.StructureToPtr(new MidiHeader
					{
						Data = Marshal.AllocHGlobal(bufferSize),
						BufferLength = bufferSize,
						Reserved = new IntPtr[8]
					}, header, false);
					_headers.Add(header);
					Check(midiInPrepareHeader(_handle, header, Marshal.SizeOf<MidiHeader>()), "midiInPrepareHeader");
					Check(midiInAddBuffer(_handle, header, Marshal.SizeOf<MidiHeader>()), "midiInAddBuffer");
				}
				Check(midiInStart(_handle), "midiInStart");
			}
			catch
			{
				Dispose();
				throw;
			}
		}

		public event Action<byte[]>? SysexReceived;

		public void Dispose()
		{
			lock (_syncRoot)
			{
				if (_handle == IntPtr.Zero) return;
				_closing = true;
				midiInStop(_handle);
				// Reset returns every queued buffer (as empty long data, not re-queued while closing).
				midiInReset(_handle);
				foreach (IntPtr header in _headers)
				{
					midiInUnprepareHeader(_handle, header, Marshal.SizeOf<MidiHeader>());
					Marshal.FreeHGlobal(Marshal.PtrToStructure<MidiHeader>(header).Data);
					Marshal.FreeHGlobal(header);
				}
				_headers.Clear();
				midiInClose(_handle);
				_handle = IntPtr.Zero;
			}
		}

		private void OnMessage(IntPtr handle, int message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
		{
			if (message is not (LongData or LongError) || parameter1 == IntPtr.Zero) return;
			MidiHeader header = Marshal.PtrToStructure<MidiHeader>(parameter1);
			if (header.BytesRecorded > 0 && !_closing)
			{
				byte[] bytes = new byte[header.BytesRecorded];
				Marshal.Copy(header.Data, bytes, 0, bytes.Length);
				try { SysexReceived?.Invoke(bytes); }
				catch (Exception) { /* A listener's failure must not take down the driver's callback thread. */ }
			}
			if (!_closing) midiInAddBuffer(handle, parameter1, Marshal.SizeOf<MidiHeader>());
		}

		private static void Check(int result, string function)
		{
			if (result != 0) throw new InvalidOperationException($"WinMM {function} failed with error {result}.");
		}

		private delegate void MidiInProc(IntPtr handle, int message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);

		[StructLayout(LayoutKind.Sequential)]
		private struct MidiHeader
		{
			public IntPtr Data;
			public int BufferLength;
			public int BytesRecorded;
			public IntPtr User;
			public int Flags;
			public IntPtr Next;
			public IntPtr ReservedPointer;
			public int Offset;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
			public IntPtr[] Reserved;
		}

		[DllImport("winmm.dll")] private static extern int midiInOpen(out IntPtr handle, int deviceId, MidiInProc callback, IntPtr instance, int flags);
		[DllImport("winmm.dll")] private static extern int midiInPrepareHeader(IntPtr handle, IntPtr header, int size);
		[DllImport("winmm.dll")] private static extern int midiInUnprepareHeader(IntPtr handle, IntPtr header, int size);
		[DllImport("winmm.dll")] private static extern int midiInAddBuffer(IntPtr handle, IntPtr header, int size);
		[DllImport("winmm.dll")] private static extern int midiInStart(IntPtr handle);
		[DllImport("winmm.dll")] private static extern int midiInStop(IntPtr handle);
		[DllImport("winmm.dll")] private static extern int midiInReset(IntPtr handle);
		[DllImport("winmm.dll")] private static extern int midiInClose(IntPtr handle);
	}
}
