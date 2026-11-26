using System.Runtime.InteropServices;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace desk;

internal static class Native
{
	[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public struct PROCESSENTRY32
	{
		public uint dwSize;

		public uint cntUsage;

		public uint th32ProcessID;

		public nint th32DefaultHeapID;

		public uint th32ModuleID;

		public uint cntThreads;

		public uint th32ParentProcessID;

		public int pcPriClassBase;

		public uint dwFlags;

		[MarshalAs (UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct STARTUPINFO
	{
		public int cb;

		public string? lpReserved;

		public string? lpDesktop;

		public string? lpTitle;

		public int dwX;

		public int dwY;

		public int dwXSize;

		public int dwYSize;

		public int dwXCountChars;

		public int dwYCountChars;

		public int dwFillAttribute;

		public int dwFlags;

		public short wShowWindow;

		public short cbReserved2;

		public nint lpReserved2;

		public nint hStdInput;

		public nint hStdOutput;

		public nint hStdError;
	}

	public struct PROCESS_INFORMATION
	{
		public nint hProcess;

		public nint hThread;

		public uint dwProcessId;

		public uint dwThreadId;
	}

	public const uint PROCESS_QUERY_LIMITED_INFORMATION = 4096u;

	public const uint TOKEN_DUPLICATE = 2u;

	public const uint TOKEN_ALL_ACCESS = 983551u;

	public const int SecurityImpersonation = 2;

	public const int TokenPrimary = 1;

	public const uint CREATE_NO_WINDOW = 134217728u;

	public const uint TH32CS_SNAPPROCESS = 2u;

	[DllImport ("kernel32.dll")]
	public static extern uint WTSGetActiveConsoleSessionId ();

	[DllImport ("kernel32.dll", SetLastError = true)]
	public static extern bool ProcessIdToSessionId (uint dwProcessId, out uint pSessionId);

	[DllImport ("kernel32.dll", SetLastError = true)]
	public static extern nint OpenProcess (uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

	[DllImport ("kernel32.dll", SetLastError = true)]
	public static extern bool CloseHandle (nint hObject);

	[DllImport ("kernel32.dll", SetLastError = true)]
	public static extern nint CreateToolhelp32Snapshot (uint dwFlags, uint th32ProcessID);

	[DllImport ("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern bool Process32First (nint hSnapshot, ref PROCESSENTRY32 lppe);

	[DllImport ("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	public static extern bool Process32Next (nint hSnapshot, ref PROCESSENTRY32 lppe);

	[DllImport ("advapi32.dll", SetLastError = true)]
	public static extern bool OpenProcessToken (nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

	[DllImport ("advapi32.dll", SetLastError = true)]
	public static extern bool DuplicateTokenEx (nint hExistingToken, uint dwDesiredAccess, nint lpTokenAttributes, int ImpersonationLevel, int TokenType, out nint phNewToken);

	[DllImport ("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern bool CreateProcessAsUser (nint hToken, string? lpApplicationName, string? lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
}
