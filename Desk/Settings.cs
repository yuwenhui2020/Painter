using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal static class Settings
{
	private static readonly string FilePath = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData), AppIdentity.DataFolder, "settings.json");

	private static readonly string HostFilePath = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.CommonApplicationData), AppIdentity.DataFolder, "host.json");

	private static readonly string LegacyFilePath = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData), AppIdentity.LegacyDataFolder, "settings.json");

	private static readonly string LegacyHostFilePath = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.CommonApplicationData), AppIdentity.LegacyDataFolder, "host.json");

	private static readonly byte[] Key = new byte[8] { 90, 60, 119, 25, 78, 98, 8, 209 };

	public static string LocalHostPassword { get; set; } = "";

	public static List<SavedConnection> SavedConnections { get; set; } = new List<SavedConnection> ();

	public static int HostPort { get; set; } = 9000;

	/// <summary>是否启用"远程访问密码"（即他人控制本机 / 访问 Web 端时要输入的密码）。
	/// 关闭后任何人无需密码即可访问控制；本机未设置密码时该开关无意义（托盘里会隐藏该项）。</summary>
	public static bool RemotePasswordEnabled { get; set; } = true;

	/// <summary>是否启用后台进程查杀（由任务栏托盘右键菜单控制，外置 killlist 文件提供名单）。</summary>
	public static bool KillEnabled { get; set; } = false;

	private static string Obf (string s)
	{
		if (string.IsNullOrEmpty (s)) {
			return "";
		}
		try {
			byte[] inArray = ProtectedData.Protect (Encoding.UTF8.GetBytes (s), null, DataProtectionScope.CurrentUser);
			return Convert.ToBase64String (inArray);
		} catch {
			byte[] bytes = Encoding.UTF8.GetBytes (s);
			for (int i = 0; i < bytes.Length; i++) {
				bytes [i] ^= Key [i % Key.Length];
			}
			return Convert.ToBase64String (bytes);
		}
	}

	private static string Unobf (string s)
	{
		if (string.IsNullOrEmpty (s)) {
			return "";
		}
		try {
			byte[] encryptedData = Convert.FromBase64String (s);
			byte[] bytes = ProtectedData.Unprotect (encryptedData, null, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString (bytes);
		} catch {
			try {
				byte[] array = Convert.FromBase64String (s);
				for (int i = 0; i < array.Length; i++) {
					array [i] ^= Key [i % Key.Length];
				}
				return Encoding.UTF8.GetString (array);
			} catch {
				return "";
			}
		}
	}

	private static string ObfLm (string s)
	{
		if (string.IsNullOrEmpty (s)) {
			return "";
		}
		try {
			byte[] inArray = ProtectedData.Protect (Encoding.UTF8.GetBytes (s), null, DataProtectionScope.LocalMachine);
			return Convert.ToBase64String (inArray);
		} catch {
			return Convert.ToBase64String (Encoding.UTF8.GetBytes (s));
		}
	}

	private static string UnobfLm (string s)
	{
		if (string.IsNullOrEmpty (s)) {
			return "";
		}
		try {
			byte[] bytes = ProtectedData.Unprotect (Convert.FromBase64String (s), null, DataProtectionScope.LocalMachine);
			return Encoding.UTF8.GetString (bytes);
		} catch {
			try {
				return Encoding.UTF8.GetString (Convert.FromBase64String (s));
			} catch {
				return "";
			}
		}
	}

	/// <summary>新目录还没有该文件、而改名前的 desk 目录里有，就先复制过来。
	/// 文件里的密文用的是 DPAPI（当前用户 / 本机），同机换目录照样能解，所以整份复制即可。
	/// 保留旧文件不动，出问题还能回退。</summary>
	private static void AdoptLegacyFile (string target, string legacy)
	{
		try {
			if (File.Exists (target) || !File.Exists (legacy)) {
				return;
			}
			Directory.CreateDirectory (Path.GetDirectoryName (target));
			File.Copy (legacy, target);
		} catch {
		}
	}

	public static void SaveHostConfig ()
	{
		try {
			Directory.CreateDirectory (Path.GetDirectoryName (HostFilePath));
			using MemoryStream memoryStream = new MemoryStream ();
			using Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter (memoryStream);
			utf8JsonWriter.WriteStartObject ();
			utf8JsonWriter.WriteNumber ("port", HostPort);
			utf8JsonWriter.WriteString ("pwd", ObfLm (LocalHostPassword));
			utf8JsonWriter.WriteEndObject ();
			utf8JsonWriter.Flush ();
			File.WriteAllBytes (HostFilePath, memoryStream.ToArray ());
		} catch {
		}
	}

	public static (int port, string pwd) LoadHostConfig ()
	{
		try {
			AdoptLegacyFile (HostFilePath, LegacyHostFilePath);
			if (File.Exists (HostFilePath)) {
				using (JsonDocument jsonDocument = JsonDocument.Parse (File.ReadAllText (HostFilePath))) {
					int item = HostPort;
					string item2 = LocalHostPassword;
					if (jsonDocument.RootElement.TryGetProperty ("port", out var value)) {
						item = value.GetInt32 ();
					}
					if (jsonDocument.RootElement.TryGetProperty ("pwd", out var value2)) {
						item2 = UnobfLm (value2.GetString () ?? "");
					}
					return (port: item, pwd: item2);
				}
			}
		} catch {
		}
		return (port: HostPort, pwd: LocalHostPassword);
	}

	public static void Load ()
	{
		try {
			AdoptLegacyFile (FilePath, LegacyFilePath);
			if (!File.Exists (FilePath)) {
				return;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse (File.ReadAllText (FilePath));
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.TryGetProperty ("lhp", out var value)) {
				LocalHostPassword = Unobf (value.GetString () ?? "");
			}
			if (rootElement.TryGetProperty ("rpe", out var value8)) {
				RemotePasswordEnabled = value8.GetBoolean ();
			}
			if (rootElement.TryGetProperty ("kill", out var value9)) {
				KillEnabled = value9.GetBoolean ();
			}
			if (!rootElement.TryGetProperty ("conns", out var value2) || value2.ValueKind != JsonValueKind.Array) {
				return;
			}
			SavedConnections = new List<SavedConnection> ();
			foreach (JsonElement item in value2.EnumerateArray ()) {
				SavedConnection savedConnection = new SavedConnection ();
				if (item.TryGetProperty ("h", out var value3)) {
					savedConnection.Host = value3.GetString () ?? "";
				}
				if (item.TryGetProperty ("p", out var value4)) {
					savedConnection.Port = value4.GetInt32 ();
				}
				if (item.TryGetProperty ("w", out var value5)) {
					savedConnection.Password = Unobf (value5.GetString () ?? "");
				}
				if (item.TryGetProperty ("r", out var value6)) {
					savedConnection.UseRelay = value6.GetBoolean ();
				}
				if (item.TryGetProperty ("t", out var value7)) {
					savedConnection.Token = value7.GetString () ?? "";
				}
				SavedConnections.Add (savedConnection);
			}
		} catch {
		}
	}

	public static void Save ()
	{
		try {
			Directory.CreateDirectory (Path.GetDirectoryName (FilePath));
			using MemoryStream memoryStream = new MemoryStream ();
			using Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter (memoryStream, new JsonWriterOptions {
				Indented = true
			});
			utf8JsonWriter.WriteStartObject ();
			utf8JsonWriter.WriteString ("lhp", Obf (LocalHostPassword));
			utf8JsonWriter.WriteBoolean ("rpe", RemotePasswordEnabled);
			utf8JsonWriter.WriteBoolean ("kill", KillEnabled);
			utf8JsonWriter.WriteStartArray ("conns");
			foreach (SavedConnection savedConnection in SavedConnections) {
				utf8JsonWriter.WriteStartObject ();
				utf8JsonWriter.WriteString ("h", savedConnection.Host);
				utf8JsonWriter.WriteNumber ("p", savedConnection.Port);
				utf8JsonWriter.WriteString ("w", Obf (savedConnection.Password));
				utf8JsonWriter.WriteBoolean ("r", savedConnection.UseRelay);
				utf8JsonWriter.WriteString ("t", savedConnection.Token);
				utf8JsonWriter.WriteEndObject ();
			}
			utf8JsonWriter.WriteEndArray ();
			utf8JsonWriter.WriteEndObject ();
			utf8JsonWriter.Flush ();
			File.WriteAllBytes (FilePath, memoryStream.ToArray ());
		} catch {
		}
	}
}
