using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Treering.Core;

namespace Treering.Cli;

/// <summary>
/// 운영체제의 폴더 고르기 창. 브라우저는 고른 폴더의 전체 경로를 페이지에 알려 주지 않는다 —
/// 그래서 창은 이 컴퓨터에서 도는 서버가 띄우고, 고른 경로만 화면에 돌려준다.
/// Windows 는 탐색기의 그 창(IFileOpenDialog), macOS 는 <c>choose folder</c>,
/// Linux 는 zenity 나 kdialog 가 있으면 그것.
/// </summary>
static class FolderPicker
{
    public static bool Available =>
        OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || (OperatingSystem.IsLinux() && LinuxTool() is not null);

    /// <summary>고른 폴더, 취소했으면 <c>null</c>.</summary>
    public static Task<string?> Pick(string title, string? startIn)
    {
        if (OperatingSystem.IsWindows()) return OnStaThread(title, startIn);
        if (OperatingSystem.IsMacOS())
        {
            var prompt = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return Task.Run(() => Run("osascript", "-e", $"POSIX path of (choose folder with prompt \"{prompt}\")"));
        }

        var tool = LinuxTool();
        if (tool is null) return Task.FromResult<string?>(null);
        return Task.Run(() => tool.EndsWith("zenity", StringComparison.Ordinal)
            ? Run(tool, "--file-selection", "--directory", "--title=" + title)
            : Run(tool, "--getexistingdirectory", startIn ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "--title", title));
    }

    private static string? LinuxTool() =>
        new[] { "zenity", "kdialog" }.Select(Executables.Find).FirstOrDefault(Path.IsPathRooted);

    private static string? Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        // 취소는 0 이 아닌 종료 코드로 온다.
        if (process.ExitCode != 0 || output.Length == 0) return null;
        // macOS 는 끝에 / 를 붙여 준다. 루트 "/" 는 그대로 둔다.
        return output.Length > 1 ? output.TrimEnd('/') : output;
    }

    // 셸 창은 STA 스레드에서만 뜬다. ASP.NET 의 스레드는 MTA 라 따로 하나 세운다.
    [SupportedOSPlatform("windows")]
    private static Task<string?> OnStaThread(string title, string? startIn)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(PickWindows(title, startIn)); }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    [SupportedOSPlatform("windows")]
    private static string? PickWindows(string title, string? startIn)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
            dialog.SetTitle(title);

            if (startIn is not null && Directory.Exists(startIn)
                && SHCreateItemFromParsingName(startIn, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
            {
                dialog.SetFolder(folder);
                Marshal.ReleaseComObject(folder);
            }

            // 창의 주인을 지금 앞에 있는 창(브라우저)으로 둔다. 주인이 없으면 브라우저 뒤에 뜰 수 있다.
            var shown = dialog.Show(GetForegroundWindow());
            if (shown == ErrorCancelled) return null;
            // 취소가 아닌 실패는 숨기지 않는다 — 화면은 그걸 보고 경로 입력으로 넘어간다.
            Marshal.ThrowExceptionForHR(shown);

            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SigdnFileSysPath, out var name);
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog;

    // 메서드 순서가 곧 vtable 이다 — 쓰지 않는 것도 자리를 지켜야 한다.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
