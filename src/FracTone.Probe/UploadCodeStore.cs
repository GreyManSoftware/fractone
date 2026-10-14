using System.Security.Cryptography;
using System.Text;

namespace FracTone.Probe
{
	// The user's upload code, kept per user so it is pasted once. On Windows it is encrypted with DPAPI for the current
	// user, so the file is useless to anyone else and never holds the code as text; elsewhere the file is readable by its
	// owner only. The code is never printed, logged or put into a report.
	public sealed class UploadCodeStore
	{
		private const string FileName = "upload-code.bin";
		// Ties the DPAPI blob to this purpose, so another program's blob can't be passed off as the code.
		private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FracTone.Probe upload code v1");

		public UploadCodeStore(string? folder = null)
		{
			Folder = folder ?? DefaultFolder;
		}

		public static string DefaultFolder =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FracTone", "probe");

		public string Folder { get; }

		public string FilePath => Path.Combine(Folder, FileName);

		public void Save(string code)
		{
			if (!HttpReportUploader.IsWellFormedCode(code))
				throw new ArgumentException("That isn't an upload code: codes start with ftu_ and come from your FracTone account page.", nameof(code));
			Directory.CreateDirectory(Folder);
			byte[] plain = Encoding.UTF8.GetBytes(code);
			try
			{
				byte[] stored = OperatingSystem.IsWindows()
					? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
					: plain;
				string temporary = FilePath + ".tmp";
				if (OperatingSystem.IsWindows())
					File.WriteAllBytes(temporary, stored);
				else
				{
					using FileStream stream = new(temporary, new FileStreamOptions
					{
						Mode = FileMode.Create,
						Access = FileAccess.Write,
						UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
					});
					stream.Write(stored);
				}
				File.Move(temporary, FilePath, overwrite: true);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plain);
			}
		}

		// Null when nothing is saved or the file can't be decrypted (another user's or another PC's copy).
		public string? Load()
		{
			if (!File.Exists(FilePath))
				return null;
			try
			{
				byte[] stored = File.ReadAllBytes(FilePath);
				byte[] plain = OperatingSystem.IsWindows()
					? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser)
					: stored;
				string code = Encoding.UTF8.GetString(plain);
				return HttpReportUploader.IsWellFormedCode(code) ? code : null;
			}
			catch (CryptographicException)
			{
				return null;
			}
		}

		public bool Delete()
		{
			if (!File.Exists(FilePath))
				return false;
			File.Delete(FilePath);
			return true;
		}
	}
}
