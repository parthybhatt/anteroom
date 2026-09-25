using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Anteroom.App.Models;

namespace Anteroom.App.Services;

public enum OpenResult { Focused, Raised, Resumed, CopiedCommand, Failed }

/// <summary>
/// Brings a session's terminal back to the front. Windows deliberately makes stealing focus hard,
/// so we use the standard AttachThreadInput dance; only when there is no window left to go to do we
/// fall back to resuming the session in a fresh terminal.
/// </summary>
public static class WindowFocus
{
    public static OpenResult Open(SessionState session)
    {
        var hwnd = session.Hwnd;
        var result = OpenVsCode(session) ?? OpenHostWindow(session) ?? Resume(session);

        Log.Write($"open session={Short(session.SessionId)} host={session.WindowProcess ?? "-"} " +
                  $"hwnd={hwnd:X} -> {result}");
        return result;
    }

    /// <summary>The window the shim captured, when it is still alive. Null means there is none left.</summary>
    private static OpenResult? OpenHostWindow(SessionState session)
    {
        if (session.Hwnd == nint.Zero || !IsWindow(session.Hwnd))
        {
            // Gone for real - forget it, so the tab stops offering to focus a dead handle.
            session.Hwnd = nint.Zero;
            return null;
        }

        return Focus(session.Hwnd) ? OpenResult.Focused : Flash(session.Hwnd);
    }

    private static OpenResult Resume(SessionState session)
    {
        if (LaunchResume(session)) return OpenResult.Resumed;
        return CopyResumeCommand(session) ? OpenResult.CopiedCommand : OpenResult.Failed;
    }

    public static string ResumeCommand(SessionState session) => $"claude --resume {session.SessionId}";

    public static bool Focus(nint hwnd)
    {
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            // The right to take the foreground belongs to the *calling* thread, so ours is the thread
            // that has to join the foreground thread's input queue. Attaching the foreground thread to
            // the target's - the old pairing - leaves Anteroom with no claim of its own, and Windows
            // answers SetForegroundWindow by flashing the taskbar instead of switching. Measured
            // side by side against a VS Code window with a third window in front: the old pairing
            // never moved the foreground, this one moved it every time.
            uint ours = GetCurrentThreadId();
            uint foregroundThread = ThreadOf(GetForegroundWindow());
            uint targetThread = ThreadOf(hwnd);

            bool joinedForeground = foregroundThread != 0 && foregroundThread != ours &&
                                    AttachThreadInput(ours, foregroundThread, true);
            bool joinedTarget = targetThread != 0 && targetThread != ours && targetThread != foregroundThread &&
                                AttachThreadInput(ours, targetThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (joinedForeground) AttachThreadInput(ours, foregroundThread, false);
                if (joinedTarget) AttachThreadInput(ours, targetThread, false);
            }

            // Only the foreground window counts. IsWindowVisible used to be accepted here, which
            // is true of almost any live window, so a refused focus still reported success.
            return WaitForForeground(hwnd, 300);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Raises the VS Code window whose workspace holds this session, picked out by folder rather
    /// than by the captured handle - VS Code hangs every window off a single Code.exe, so the handle
    /// the shim walked up to only ever says "some VS Code window". Null when this is not a VS Code
    /// session, or when no window has the folder open.
    /// </summary>
    private static OpenResult? OpenVsCode(SessionState session)
    {
        if (session.WindowProcess is not { } host) return null;
        // The Claude desktop app hosts sessions too, and "Claude Code.exe" contains "Code" without
        // being the editor - same ordering as the tab's own tooltip.
        if (host.Contains("claude", StringComparison.OrdinalIgnoreCase)) return null;
        if (!host.Contains("Code", StringComparison.OrdinalIgnoreCase)) return null;

        if (!Directory.Exists(session.Cwd)) return null;
        if (FindVsCodeWindow(session.Cwd!) is not { } window) return null;

        if (Focus(window.Hwnd)) return OpenResult.Focused;

        // Focus can still be refused - another virtual desktop, a foreground lock we lost the race
        // to. VS Code is allowed to raise its own windows, so hand it the folder the way the `code`
        // command does and let it resolve folder to window internally. The folder passed is the one
        // the window is actually titled for, never a subfolder, because `code <subfolder>` opens a
        // second window rather than focusing the workspace that contains it.
        if (window.Exe is { } exe && Start(new ProcessStartInfo(exe)
            {
                Arguments = $"\"{window.Folder}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
        {
            // Relaying through a second Code.exe takes about a second, so this waits longer than a
            // plain SetForegroundWindow would - but it still has to be seen to be claimed.
            if (WaitForForeground(window.Hwnd, 1500)) return OpenResult.Focused;
        }

        return Flash(window.Hwnd);
    }

    private readonly record struct VsCodeWindow(nint Hwnd, string? Exe, string Folder);

    /// <summary>
    /// The VS Code window holding this cwd, with the folder it is titled for. Titles are " - "
    /// separated - "Program.cs - anteroom - work - Visual Studio Code" - so the folder is one
    /// segment among the open file, the profile name and the app name. Matching the tail of the
    /// title, as this did before, only ever held for people running the default profile.
    /// </summary>
    private static VsCodeWindow? FindVsCodeWindow(string cwd)
    {
        var windows = new List<(nint Hwnd, string[] Segments)>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != nint.Zero) return true;

            var title = TitleOf(hwnd);
            if (title is null) return true;

            // Insiders adds a segment of its own rather than renaming this one, so a plain
            // segment match covers both builds.
            var segments = title.Split(" - ", StringSplitOptions.TrimEntries);
            if (!segments.Contains("Visual Studio Code", StringComparer.OrdinalIgnoreCase)) return true;

            windows.Add((hwnd, segments));
            return true;
        }, nint.Zero);

        if (windows.Count == 0) return null;

        foreach (var folder in WorkspaceChain(cwd))
        {
            foreach (var (hwnd, segments) in windows)
            {
                if (!segments.Contains(folder.Name, StringComparer.OrdinalIgnoreCase)) continue;
                return new VsCodeWindow(hwnd, ExeOf(hwnd), folder.FullName);
            }
        }

        return null;
    }

    /// <summary>
    /// The cwd and the folders above it, nearest first: a session started in a subfolder still
    /// belongs to the window that opened the repo. The walk stops at the repo root, above which a
    /// folder name says nothing about which workspace a window holds.
    /// </summary>
    private static IEnumerable<DirectoryInfo> WorkspaceChain(string cwd)
    {
        var dir = new DirectoryInfo(cwd);
        for (int depth = 0; depth < 8 && dir is not null; depth++)
        {
            yield return dir;
            // A worktree or submodule keeps a .git file rather than a directory.
            if (Path.Exists(Path.Combine(dir.FullName, ".git"))) yield break;
            dir = dir.Parent;
        }
    }

    private static string? ExeOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out int pid);
        try { return Process.GetProcessById(pid).MainModule?.FileName; }
        catch { return null; /* the process went away, or denies us its module list */ }
    }

    /// <summary>
    /// For a window that is alive but that Windows will not let us raise: mark it in the taskbar and
    /// say so. Resuming the session a second time in a new terminal - what a live window used to
    /// fall through to - is the one outcome worse than not switching.
    /// </summary>
    private static OpenResult Flash(nint hwnd)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 3
        };
        FlashWindowEx(ref info);
        return OpenResult.Raised;
    }

    /// <summary>Foreground changes lag the call that asks for them, so give the window a moment.</summary>
    private static bool WaitForForeground(nint hwnd, int milliseconds)
    {
        for (int waited = 0; ; waited += 50)
        {
            if (GetForegroundWindow() == hwnd) return true;
            if (waited >= milliseconds) return false;
            Thread.Sleep(50);
        }
    }

    private static uint ThreadOf(nint hwnd) => hwnd == nint.Zero ? 0 : GetWindowThreadProcessId(hwnd, out _);

    private static string Short(string sessionId) => sessionId.Length >= 8 ? sessionId[..8] : sessionId;

    private static bool LaunchResume(SessionState session)
    {
        var workingDirectory = Directory.Exists(session.Cwd) ? session.Cwd! : Environment.CurrentDirectory;
        var resume = ResumeCommand(session);

        // Windows Terminal first; it is what most Claude Code sessions live in.
        var wt = FindOnPath("wt.exe");
        if (wt is not null)
        {
            return Start(new ProcessStartInfo(wt)
            {
                Arguments = $"-d \"{workingDirectory}\" cmd /k {resume}",
                UseShellExecute = true
            });
        }

        return Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/k {resume}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        });
    }

    private static bool CopyResumeCommand(SessionState session)
    {
        try
        {
            System.Windows.Clipboard.SetText(ResumeCommand(session));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenFolder(SessionState session)
    {
        if (!Directory.Exists(session.Cwd)) return false;
        return Start(new ProcessStartInfo(session.Cwd!) { UseShellExecute = true });
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // PATH is unreadable; caller falls back to cmd.exe.
        }
        return null;
    }

    /// <summary>Title of a live window, used to show the user which terminal a tab points at.</summary>
    public static string? TitleOf(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd)) return null;
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
}
