using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Fractal.Companion.Transport
{
	// A serial port and what the system calls it ("FM3 Communications Port (COM5)" on Windows; the path elsewhere).
	public sealed record SerialPortInfo(string Port, string Description);

	public interface ISerialPortCatalog
	{
		IReadOnlyList<SerialPortInfo> List();
	}

	// Picks the device's port from a list, kept apart from the system lookup so it can be tested without real ports.
	public static partial class SerialPortDiscovery
	{
		// COMn on Windows, a device path elsewhere: names a serial port rather than a MIDI port.
		public static bool LooksLikeSerialPort(string name) =>
			ComPortName().IsMatch(name) || name.StartsWith("/dev/", StringComparison.Ordinal);

		// An explicit port must exist as given; otherwise the one whose description names the device, or on macOS the only
		// USB modem port (it has no friendly name there). Null when nothing fits or the choice is ambiguous.
		public static SerialPortInfo? Find(IReadOnlyList<SerialPortInfo> ports, string deviceName, string? explicitPort = null)
		{
			ArgumentNullException.ThrowIfNull(ports);
			if (explicitPort is not null)
				return ports.FirstOrDefault(port => string.Equals(port.Port, explicitPort, StringComparison.OrdinalIgnoreCase));
			SerialPortInfo[] named = [.. ports.Where(port => Names(port.Description, deviceName))];
			if (named.Length == 1)
				return named[0];
			if (named.Length > 1)
				return null;
			SerialPortInfo[] modems = [.. ports.Where(port => port.Port.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal))];
			return modems.Length == 1 ? modems[0] : null;
		}

		// "FM3" as a word, so a port called e.g. "XFM31" doesn't match.
		private static bool Names(string description, string deviceName) =>
			Regex.IsMatch(description, $@"(?<![A-Za-z0-9]){Regex.Escape(deviceName)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

		[GeneratedRegex(@"^COM\d+$", RegexOptions.IgnoreCase)]
		private static partial Regex ComPortName();
	}

	// The ports present now. On Windows the friendly names come from the registry's device tree (what Device Manager
	// shows), read-only and with no extra package; ports that are no longer plugged in are left out.
	public sealed class SystemSerialPortCatalog : ISerialPortCatalog
	{
		public IReadOnlyList<SerialPortInfo> List()
		{
			string[] present = SerialPort.GetPortNames();
			IReadOnlyDictionary<string, string> friendly = OperatingSystem.IsWindows() ? WindowsFriendlyNames() : new Dictionary<string, string>();
			return [.. present
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Order(StringComparer.OrdinalIgnoreCase)
				.Select(port => new SerialPortInfo(port, friendly.TryGetValue(port, out string? name) ? name : port))];
		}

		[SupportedOSPlatform("windows")]
		private static Dictionary<string, string> WindowsFriendlyNames()
		{
			Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
			try
			{
				using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
				if (root is null)
					return names;
				// Enum\<enumerator>\<device id>\<instance>: the instance holds FriendlyName, its Device Parameters the PortName.
				foreach (string enumerator in root.GetSubKeyNames())
				{
					using RegistryKey? bus = TryOpen(root, enumerator);
					if (bus is null) continue;
					foreach (string deviceId in bus.GetSubKeyNames())
					{
						using RegistryKey? device = TryOpen(bus, deviceId);
						if (device is null) continue;
						foreach (string instanceId in device.GetSubKeyNames())
						{
							using RegistryKey? instance = TryOpen(device, instanceId);
							using RegistryKey? parameters = instance is null ? null : TryOpen(instance, "Device Parameters");
							if (parameters?.GetValue("PortName") is string port && instance!.GetValue("FriendlyName") is string name)
								names[port] = name;
						}
					}
				}
			}
			catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				// Without the names the ports are still listed, and --device-port still works.
			}
			return names;
		}

		// Some device keys deny reading to ordinary users; skip those rather than lose every name.
		[SupportedOSPlatform("windows")]
		private static RegistryKey? TryOpen(RegistryKey parent, string name)
		{
			try
			{
				return parent.OpenSubKey(name);
			}
			catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				return null;
			}
		}
	}
}
