using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoughCut.Desktop;

/// Extensions are given without a leading dot, for example "json" or "mkv".
public sealed record FileFilter(string Label, params string[] Extensions)
{
    public string Patterns => string.Join(";", Extensions.Select(extension => "*." + extension));
    public string SpaceSeparatedPatterns => string.Join(' ', Extensions.Select(extension => "*." + extension));
}

/// CupriFace 0.26.1 exposes no file dialog and the shell ships SDL2, which predates SDL_ShowOpenFileDialog,
/// so the picker calls the platform directly: comdlg32 on Windows, zenity or kdialog on Linux.
public static class NativeFileDialog
{
    private const int MaxPathChars = 32768;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;
    private const uint BifReturnOnlyFileSystemDirectories = 0x00000001;
    private const uint BifNewDialogStyle = 0x00000040;
    private const uint BifEditBox = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public nint Owner;
        public nint Root;
        public nint DisplayName;
        public nint Title;
        public uint Flags;
        public nint Callback;
        public nint Parameter;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SHBrowseForFolderW(ref BrowseInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(nint identifier, nint path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint memory);

    private static string? _linuxPicker;
    private static bool _linuxProbed;

    public static bool Available => OperatingSystem.IsWindows() || LinuxPicker() is not null;

    /// Handle of the calling thread's active window, so the dialog is modal to the RoughCut window.
    /// Must be read on the UI thread; zero simply means an unowned dialog.
    public static nint ActiveWindow() => OperatingSystem.IsWindows() ? GetActiveWindow() : 0;

    /// Blocks until the person chooses a file or cancels, so call it off the UI thread.
    /// Returns null when the dialog is cancelled or no picker is available.
    public static string? OpenFile(string title, FileFilter filter, string? initialDirectory, nint owner)
    {
        if (OperatingSystem.IsWindows()) return OpenWindows(title, filter, initialDirectory, owner);
        if (LinuxPicker() is { } picker) return OpenLinux(picker, title, filter, initialDirectory);
        return null;
    }

    /// Blocks until the person chooses a directory or cancels, so call it off the UI thread. Returns null
    /// when the dialog is cancelled or no picker is available. An edit lives in a folder, so choosing where
    /// it goes has to be possible without typing a path.
    public static string? OpenFolder(string title, string? initialDirectory, nint owner)
    {
        if (OperatingSystem.IsWindows()) return FolderWindows(title, initialDirectory, owner);
        if (LinuxPicker() is { } picker) return FolderLinux(picker, title, initialDirectory);
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FolderWindows(string title, string? initialDirectory, nint owner)
    {
        // Same apartment requirement as the file dialog: the shell's folder browser loads extensions.
        string? selected = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { selected = ShowWindowsFolderDialog(title, initialDirectory, owner); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return selected;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowWindowsFolderDialog(string title, string? initialDirectory, nint owner)
    {
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var buffer = Marshal.AllocHGlobal(MaxPathChars * 2);
        var identifier = nint.Zero;
        try
        {
            var info = new BrowseInfo
            {
                Owner = owner,
                Title = titlePtr,
                // A modern folder browser with an editable path and a resizable window, rather than the
                // cramped 1995 tree the default flags give.
                Flags = BifReturnOnlyFileSystemDirectories | BifNewDialogStyle | BifEditBox,
            };
            identifier = SHBrowseForFolderW(ref info);
            if (identifier == nint.Zero) return null;
            if (!SHGetPathFromIDListW(identifier, buffer)) return null;
            var chosen = Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrWhiteSpace(chosen)) return null;
            // The browser returns an existing directory; a new edit folder is created inside it by the
            // caller, which is what the initial directory is for.
            return chosen;
        }
        finally
        {
            if (identifier != nint.Zero) CoTaskMemFree(identifier);
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(titlePtr);
        }
    }

    private static string? FolderLinux(string picker, string title, string? initialDirectory)
    {
        var start = new ProcessStartInfo(picker) { RedirectStandardOutput = true, UseShellExecute = false };
        if (picker.EndsWith("zenity", StringComparison.Ordinal))
        {
            start.ArgumentList.Add("--file-selection");
            start.ArgumentList.Add("--directory");
            start.ArgumentList.Add("--title=" + title);
            if (!string.IsNullOrWhiteSpace(initialDirectory))
                start.ArgumentList.Add("--filename=" + initialDirectory + Path.DirectorySeparatorChar);
        }
        else
        {
            start.ArgumentList.Add("--getexistingdirectory");
            start.ArgumentList.Add(string.IsNullOrWhiteSpace(initialDirectory) ? "." : initialDirectory);
        }
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The folder picker did not start.");
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(10))) process.Kill(entireProcessTree: true);
            var selected = output.Trim();
            return process.ExitCode == 0 && selected.Length > 0 ? selected : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? OpenWindows(string title, FileFilter filter, string? initialDirectory, nint owner)
    {
        // The Windows common dialog loads shell extensions that require a single-threaded apartment.
        string? selected = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { selected = ShowWindowsDialog(title, filter, initialDirectory, owner); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return selected;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowWindowsDialog(string title, FileFilter filter, string? initialDirectory, nint owner)
    {
        var filterPtr = Marshal.StringToHGlobalUni(
            $"{filter.Label} ({filter.Patterns})\0{filter.Patterns}\0All files (*.*)\0*.*\0\0");
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var directoryPtr = string.IsNullOrWhiteSpace(initialDirectory) ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        var file = Marshal.AllocHGlobal(MaxPathChars * 2);
        // OFN_NOCHANGEDIR is documented as ineffective for GetOpenFileName, and browsing really does move the
        // process working directory, which would break every workspace-relative path. Restore it ourselves.
        var workingDirectory = Environment.CurrentDirectory;
        try
        {
            Marshal.WriteInt16(file, 0, 0);
            var options = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                Filter = filterPtr,
                FilterIndex = 1,
                File = file,
                MaxFile = MaxPathChars,
                InitialDirectory = directoryPtr,
                Title = titlePtr,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir
            };
            if (GetOpenFileName(ref options)) return Marshal.PtrToStringUni(file);
            // Zero means the person cancelled; anything else is a real failure worth surfacing.
            var error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException($"The file dialog failed (code 0x{error:X4}).");
            return null;
        }
        finally
        {
            try { Environment.CurrentDirectory = workingDirectory; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(titlePtr);
            if (directoryPtr != 0) Marshal.FreeHGlobal(directoryPtr);
        }
    }

    private static string? OpenLinux(string picker, string title, FileFilter filter, string? initialDirectory)
    {
        var start = new ProcessStartInfo(picker) { RedirectStandardOutput = true, UseShellExecute = false };
        if (picker.EndsWith("zenity", StringComparison.Ordinal))
        {
            start.ArgumentList.Add("--file-selection");
            start.ArgumentList.Add("--title=" + title);
            start.ArgumentList.Add($"--file-filter={filter.Label} | {filter.SpaceSeparatedPatterns}");
            if (!string.IsNullOrWhiteSpace(initialDirectory))
                start.ArgumentList.Add("--filename=" + initialDirectory + Path.DirectorySeparatorChar);
        }
        else
        {
            start.ArgumentList.Add("--getopenfilename");
            start.ArgumentList.Add(string.IsNullOrWhiteSpace(initialDirectory) ? "." : initialDirectory);
            start.ArgumentList.Add($"{filter.SpaceSeparatedPatterns}|{filter.Label}");
        }
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The file picker did not start.");
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(10))) process.Kill(entireProcessTree: true);
            var selected = output.Trim();
            return process.ExitCode == 0 && selected.Length > 0 ? selected : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? LinuxPicker()
    {
        if (_linuxProbed) return _linuxPicker;
        _linuxProbed = true;
        if (!OperatingSystem.IsLinux()) return _linuxPicker = null;
        foreach (var candidate in new[] { "/usr/bin/zenity", "/usr/bin/kdialog" })
            if (File.Exists(candidate)) return _linuxPicker = candidate;
        return _linuxPicker = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDirectory;
        public nint Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public nint DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
        public nint Reserved1;
        public int Reserved2;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName options);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();
}
