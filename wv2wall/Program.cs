using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        CommandLineParseResult result = CommandLineParser.Parse(args);
        if (result.MessageKind != CommandLineMessageKind.None)
        {
            MessageBox.Show(
                result.Message,
                result.MessageKind == CommandLineMessageKind.Help ? "wv2wall - Help" :
                result.MessageKind == CommandLineMessageKind.Warning ? "wv2wall - Warning" : "wv2wall - Error",
                MessageBoxButtons.OK,
                result.MessageKind == CommandLineMessageKind.Help ? MessageBoxIcon.Information :
                result.MessageKind == CommandLineMessageKind.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Error);

            if (result.ShouldExit)
                return;
        }

        Application.Run(new WallpaperContext(
            result.Url,
            result.ScriptContent,
            result.Mode,
            result.Monitor));
    }
}

internal sealed class WallpaperContext : ApplicationContext
{
    private string _url;
    private readonly string _scriptContent;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly List<ToolStripMenuItem> _monitorItems = new();
    private readonly List<DeskForm> _forms = new();
    private readonly Control _eventDispatcher = new();
    private readonly System.Windows.Forms.Timer _fullscreenDebounceTimer;
    private readonly System.Windows.Forms.Timer _fullscreenFallbackTimer;
    private readonly List<IntPtr> _winEventHooks = new();
    private ToolStripMenuItem? _spanItem;
    private ToolStripMenuItem? _allItem;
    private ToolStripMenuItem? _pauseWhenFullscreenItem;
    private ToolStripMenuItem? _suspendWallpaperItem;
    private WallpaperMode _mode = WallpaperMode.Span;
    private WallpaperMode _pendingMode = WallpaperMode.Span;
    private int _selectedMonitor;
    private int _pendingMonitor;
    private bool _switching;
    private bool _exiting;
    private bool _pauseWhenFullscreenEnabled = true;
    private bool _manualSuspendEnabled;
    private bool _fullscreenMonitoringStarted;
    private bool _fullscreenMonitoringDisposed;
    private int _fullscreenReevaluationPosted;
    private bool _suspensionUpdateRunning;
    private bool _suspensionUpdatePending;

    private WinEventDelegate? _winEventProc;

    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private LowLevelMouseProc? _mouseProc;
    private LowLevelKeyboardProc? _keyboardProc;
    private bool _isWallpaperFocused = true;
    private bool _isInteractingWithDesktop = false;

    // Hook constants
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_CANCELMODE = 0x001F;
    private const int WM_CLOSE = 0x0010;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000A;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    public WallpaperContext(string url, string scriptContent, WallpaperMode mode, int monitor)
    {
        _url = url;
        _scriptContent = scriptContent;

        _menu = new ContextMenuStrip();
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "wv2wall",
            Visible = true
        };

        _mode = mode;
        _selectedMonitor = monitor;

        BuildMenu();
        InstallHooks();

        _eventDispatcher.CreateControl();
        _fullscreenDebounceTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _fullscreenDebounceTimer.Tick += (_, _) =>
        {
            _fullscreenDebounceTimer.Stop();
            _ = ReconcileFullscreenStateAsync();
        };

        _fullscreenFallbackTimer = new System.Windows.Forms.Timer { Interval = 7500 };
        _fullscreenFallbackTimer.Tick += (_, _) => QueueFullscreenReevaluation();

        ApplyMode();

        _winEventProc = WinEventCallback;
        InstallWinEventHooks();
        _fullscreenMonitoringStarted = true;
        _fullscreenFallbackTimer.Start();
        QueueFullscreenReevaluation();
    }


    private void InstallHooks()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        IntPtr hMod = GetModuleHandle(module.ModuleName);

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
    }

    private void UninstallHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private void InstallWinEventHooks()
    {
        if (_winEventProc == null)
            return;

        uint[] events =
        {
            EVENT_SYSTEM_FOREGROUND,
            EVENT_OBJECT_LOCATIONCHANGE,
            EVENT_SYSTEM_MOVESIZEEND
        };

        foreach (uint eventId in events)
        {
            IntPtr hook = SetWinEventHook(
                eventId,
                eventId,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (hook != IntPtr.Zero)
                _winEventHooks.Add(hook);
        }
    }

    private void WinEventCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_exiting || _fullscreenMonitoringDisposed)
            return;

        if (eventType == EVENT_OBJECT_LOCATIONCHANGE &&
            (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF))
        {
            return;
        }

        QueueFullscreenReevaluation();
    }

    private void QueueFullscreenReevaluation()
    {
        if (_exiting || _fullscreenMonitoringDisposed || !_fullscreenMonitoringStarted)
            return;

        if (Interlocked.Exchange(ref _fullscreenReevaluationPosted, 1) != 0)
            return;

        try
        {
            _eventDispatcher.BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _fullscreenReevaluationPosted, 0);
                if (_exiting || _fullscreenMonitoringDisposed)
                    return;

                _fullscreenDebounceTimer.Stop();
                _fullscreenDebounceTimer.Start();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _fullscreenReevaluationPosted, 0);
        }
    }

    private void DisposeFullscreenMonitoring()
    {
        if (_fullscreenMonitoringDisposed)
            return;

        _fullscreenMonitoringDisposed = true;
        _fullscreenMonitoringStarted = false;
        _fullscreenDebounceTimer.Stop();
        _fullscreenFallbackTimer.Stop();
        _fullscreenDebounceTimer.Dispose();
        _fullscreenFallbackTimer.Dispose();

        foreach (IntPtr hook in _winEventHooks)
            UnhookWinEvent(hook);

        _winEventHooks.Clear();
        _eventDispatcher.Dispose();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isWallpaperFocused)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)info.vkCode;

                // 1. ALWAYS PASS-THROUGH CRITICAL SYSTEM KEYS
                // VK_LWIN(0x5B), VK_RWIN(0x5C), VK_SNAPSHOT(0x2C)
                if (vk == 0x5B || vk == 0x5C || vk == 0x2C)
                {
                    if (_forms.Count > 0) _forms[0].PostKey(msg, vk, (int)info.scanCode, (int)info.flags);
                    return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                }

                // 2. HANDLE ALT+TAB
                // VK_TAB = 0x09
                if (vk == 0x09)
                {
                    bool altDown = (GetKeyState(0x12) & 0x8000) != 0; // VK_MENU
                    if (altDown)
                    {
                        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                    }
                }

                // 3. HANDLE MODIFIERS (Shift, Ctrl, Alt, CapsLock)
                // Pass to OS AND WebView
                // VK_SHIFT(0x10), VK_CONTROL(0x11), VK_MENU(0x12), VK_CAPITAL(0x14)
                if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x14 ||
                    vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5)
                {
                    if (_forms.Count > 0) _forms[0].PostKey(msg, vk, (int)info.scanCode, (int)info.flags);
                    return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                }

                // 4. CONTENT KEYS
                if (_forms.Count > 0)
                {
                    _forms[0].PostKey(msg, vk, (int)info.scanCode, (int)info.flags);
                    return (IntPtr)1; // Swallow
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private const int LVM_SETITEMSTATE = 0x1000 + 43;
    private const int LVIS_SELECTED = 0x0002;
    private const int LVIS_FOCUSED = 0x0001;
    private const int LVIF_STATE = 0x0008;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    private void DeselectDesktopIcons(IntPtr hWndDesktop)
    {
        uint pid;
        GetWindowThreadProcessId(hWndDesktop, out pid);
        IntPtr hProcess = OpenProcess(ProcessAccessFlags.VirtualMemoryOperation | ProcessAccessFlags.VirtualMemoryRead | ProcessAccessFlags.VirtualMemoryWrite,
            false, (int)pid);
        if (hProcess == IntPtr.Zero) return;

        IntPtr memPtr = IntPtr.Zero;
        try
        {
            int size = Marshal.SizeOf<LVITEM>();
            memPtr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)size, AllocationType.Commit, MemoryProtection.ReadWrite);
            if (memPtr == IntPtr.Zero) return;

            LVITEM item = new LVITEM();
            item.mask = LVIF_STATE;
            item.state = 0; // clear flags
            item.stateMask = LVIS_SELECTED | LVIS_FOCUSED;

            byte[] buffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(item, ptr, false);
            Marshal.Copy(ptr, buffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            int bytesWritten;
            WriteProcessMemory(hProcess, memPtr, buffer, size, out bytesWritten);

            SendMessage(hWndDesktop, LVM_SETITEMSTATE, (IntPtr)(-1), memPtr);
        }
        catch
        {
        }
        finally
        {
            if (memPtr != IntPtr.Zero) VirtualFreeEx(hProcess, memPtr, 0, FreeType.Release);
            CloseHandle(hProcess);
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var pt = new Point(info.pt.X, info.pt.Y);

            if (_isInteractingWithDesktop)
            {
                if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP)
                {
                    _isInteractingWithDesktop = false;
                }

                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            IntPtr hWndUnderMouse = WindowFromPoint(info.pt);

            if (IsDesktopWindow(hWndUnderMouse))
            {
                if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP)
                {
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                }

                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONDBLCLK)
                {
                    if (IsOverDesktopIcon(hWndUnderMouse, pt))
                    {
                        _isInteractingWithDesktop = true;
                        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                    }

                    _isWallpaperFocused = true;
                    DeselectDesktopIcons(hWndUnderMouse);

                    PostMessage(GetShellWindow(), WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                    IntPtr menuWnd = FindWindow("#32768", null);
                    if (menuWnd != IntPtr.Zero && IsWindowVisible(menuWnd))
                    {
                        PostMessage(menuWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        PostMessage(menuWnd, WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero);
                    }
                }

                if (msg == WM_LBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    _isWallpaperFocused = true;
                }

                DeskForm? targetForm = null;
                foreach (var form in _forms)
                {
                    if (form.Bounds.Contains(pt))
                    {
                        targetForm = form;
                        break;
                    }
                }

                if (targetForm != null)
                {
                    IntPtr wvHwnd = targetForm.GetWebViewHandle();
                    if (wvHwnd != IntPtr.Zero)
                    {
                        var clientPt = new POINT { X = pt.X, Y = pt.Y };
                        ScreenToClient(wvHwnd, ref clientPt);

                        if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                        {
                            short delta = (short)(info.mouseData >> 16);
                            targetForm.DispatchScroll(clientPt.X, clientPt.Y, delta);
                        }
                        else
                        {
                            IntPtr newLParam = (IntPtr)((clientPt.Y << 16) | (clientPt.X & 0xFFFF));
                            IntPtr newWParam = (IntPtr)GetModifierKeys();
                            PostMessage(wvHwnd, msg, newWParam, newLParam);
                        }

                        if (msg != WM_MOUSEMOVE)
                        {
                            return (IntPtr)1;
                        }
                    }
                }
            }
            else
            {
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    _isWallpaperFocused = false;
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private int GetModifierKeys()
    {
        int keys = 0;
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0) keys |= 0x0001;
        if ((GetAsyncKeyState(0x02) & 0x8000) != 0) keys |= 0x0002;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) keys |= 0x0004;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) keys |= 0x0008;
        if ((GetAsyncKeyState(0x04) & 0x8000) != 0) keys |= 0x0010;
        return keys;
    }

    private bool IsDesktopWindow(IntPtr hwnd)
    {
        foreach (var form in _forms)
            if (form.Handle == hwnd)
                return true;

        IntPtr root = GetAncestor(hwnd, GetAncestorFlags.GetRoot);
        if (root == IntPtr.Zero) root = hwnd;

        var sb = new StringBuilder(256);
        GetClassName(root, sb, 256);
        string cls = sb.ToString();

        return cls == "Progman" || cls == "WorkerW";
    }

    private bool IsOverDesktopIcon(IntPtr hWnd, Point pt)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, 256);
        if (sb.ToString() != "SysListView32") return false;

        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);

        IntPtr hProcess = OpenProcess(ProcessAccessFlags.VirtualMemoryOperation | ProcessAccessFlags.VirtualMemoryRead | ProcessAccessFlags.VirtualMemoryWrite,
            false, (int)pid);
        if (hProcess == IntPtr.Zero) return false;

        IntPtr memPtr = IntPtr.Zero;
        try
        {
            POINT clientPt = new POINT { X = pt.X, Y = pt.Y };
            ScreenToClient(hWnd, ref clientPt);

            int size = Marshal.SizeOf<LVHITTESTINFO>();
            memPtr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)size, AllocationType.Commit, MemoryProtection.ReadWrite);
            if (memPtr == IntPtr.Zero) return false;

            LVHITTESTINFO info = new LVHITTESTINFO();
            info.pt = clientPt;

            byte[] buffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, ptr, false);
            Marshal.Copy(ptr, buffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            int bytesWritten;
            WriteProcessMemory(hProcess, memPtr, buffer, size, out bytesWritten);

            SendMessage(hWnd, 0x1000 + 18, IntPtr.Zero, memPtr);

            int bytesRead;
            ReadProcessMemory(hProcess, memPtr, buffer, size, out bytesRead);

            ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(buffer, 0, ptr, size);
            info = Marshal.PtrToStructure<LVHITTESTINFO>(ptr);
            Marshal.FreeHGlobal(ptr);

            if ((info.flags & 0x000E) != 0 || info.iItem != -1)
            {
                return true;
            }
        }
        catch
        {
        }
        finally
        {
            if (memPtr != IntPtr.Zero) VirtualFreeEx(hProcess, memPtr, 0, FreeType.Release);
            CloseHandle(hProcess);
        }

        return false;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _monitorItems.Clear();

        _menu.Items.Add(new ToolStripMenuItem("Set URL...", null, (_, _) => AskForUrl()));
        _menu.Items.Add(new ToolStripSeparator());

        _spanItem = new ToolStripMenuItem("Span (virtual screen)", null, (_, _) => SetMode(WallpaperMode.Span));
        _allItem = new ToolStripMenuItem("All monitors", null, (_, _) => SetMode(WallpaperMode.All));
        _menu.Items.Add(_spanItem);
        _menu.Items.Add(_allItem);
        _menu.Items.Add(new ToolStripSeparator());

        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem($"Monitor {i + 1}", null, (_, _) => SetSingle(index));
            _monitorItems.Add(item);
            _menu.Items.Add(item);
        }

        _pauseWhenFullscreenItem = new ToolStripMenuItem("Pause when fullscreen")
        {
            CheckOnClick = true,
            Checked = _pauseWhenFullscreenEnabled
        };
        _pauseWhenFullscreenItem.Click += (_, _) =>
        {
            _pauseWhenFullscreenEnabled = _pauseWhenFullscreenItem.Checked;
            if (_pauseWhenFullscreenEnabled)
            {
                QueueFullscreenReevaluation();
            }
            else
            {
                ClearFullscreenOcclusion();
                RequestWallpaperSuspensionUpdate();
            }
        };
        _menu.Items.Add(_pauseWhenFullscreenItem);

        _suspendWallpaperItem = new ToolStripMenuItem("Suspend wallpaper")
        {
            CheckOnClick = true,
            Checked = _manualSuspendEnabled
        };
        _suspendWallpaperItem.Click += (_, _) =>
        {
            _manualSuspendEnabled = _suspendWallpaperItem.Checked;
            RequestWallpaperSuspensionUpdate();
        };
        _menu.Items.Add(_suspendWallpaperItem);

        _menu.Items.Add(new ToolStripMenuItem("Reload WebView2", null, (_, _) => ReloadWebViews()));

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        _tray.ContextMenuStrip = _menu;
        UpdateChecks();
    }

    private void ReloadWebViews()
    {
        foreach (DeskForm form in _forms.ToArray())
        {
            try
            {
                form.ReloadWebView();
            }
            catch
            {
                // A WebView may still be initializing or closing.
            }
        }
    }

    private Task ReconcileFullscreenStateAsync()
    {
        if (_exiting || _fullscreenMonitoringDisposed)
            return Task.CompletedTask;

        if (!_pauseWhenFullscreenEnabled)
        {
            ClearFullscreenOcclusion();
            RequestWallpaperSuspensionUpdate();
            return Task.CompletedTask;
        }

        ClearFullscreenOcclusion();
        if (TryGetFullscreenMonitor(out IntPtr fullscreenMonitor))
        {
            foreach (DeskForm form in _forms.ToArray())
            {
                if (form.MonitorHandle == fullscreenMonitor)
                    form.FullscreenOccluded = true;
            }
        }

        RequestWallpaperSuspensionUpdate();
        return Task.CompletedTask;
    }

    private void ClearFullscreenOcclusion()
    {
        foreach (DeskForm form in _forms.ToArray())
            form.FullscreenOccluded = false;
    }

    private void RequestWallpaperSuspensionUpdate()
    {
        if (_exiting)
            return;

        if (_suspensionUpdateRunning)
        {
            _suspensionUpdatePending = true;
            return;
        }

        _suspensionUpdateRunning = true;
        _ = ApplyWallpaperSuspensionAsync();
    }

    private async Task ApplyWallpaperSuspensionAsync()
    {
        try
        {
            do
            {
                _suspensionUpdatePending = false;

                foreach (DeskForm form in _forms.ToArray())
                {
                    bool shouldSuspend = _manualSuspendEnabled ||
                        (_pauseWhenFullscreenEnabled && form.FullscreenOccluded);

                    if (shouldSuspend)
                    {
                        if (!form.RendererSuspended && await form.TrySuspendAsync())
                            form.RendererSuspended = true;
                    }
                    else if (form.RendererSuspended)
                    {
                        try
                        {
                            form.Resume();
                            form.RendererSuspended = false;
                        }
                        catch
                        {
                            // A WebView may be closing or still initializing.
                        }
                    }
                }
            }
            while (_suspensionUpdatePending && !_exiting);
        }
        finally
        {
            _suspensionUpdateRunning = false;
            if (_suspensionUpdatePending && !_exiting)
                RequestWallpaperSuspensionUpdate();
        }
    }

    private bool TryGetFullscreenMonitor(out IntPtr fullscreenMonitor)
    {
        fullscreenMonitor = IntPtr.Zero;

        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground))
            return false;

        if (IsExcludedForegroundWindow(foreground) || !GetWindowRect(foreground, out RECT windowRect))
            return false;

        IntPtr monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        const int tolerance = 2;
        bool coversMonitor = windowRect.Left <= monitorInfo.Monitor.Left + tolerance &&
            windowRect.Top <= monitorInfo.Monitor.Top + tolerance &&
            windowRect.Right >= monitorInfo.Monitor.Right - tolerance &&
            windowRect.Bottom >= monitorInfo.Monitor.Bottom - tolerance;

        if (!coversMonitor)
            return false;

        fullscreenMonitor = monitor;
        return true;
    }

    private bool IsExcludedForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == GetShellWindow())
            return true;

        GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == (uint)Environment.ProcessId)
            return true;

        var className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW";
    }

    private void AskForUrl()
    {
        using var input = new InputBox("Enter URL", "Please enter the wallpaper URL:", _url);
        if (input.ShowDialog() == DialogResult.OK)
        {
            _url = input.InputText;
            ApplyMode();
        }
    }

    private void SetMode(WallpaperMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        ApplyMode();
        UpdateChecks();
    }

    private void SetSingle(int index)
    {
        _selectedMonitor = index;
        _mode = WallpaperMode.Single;
        ApplyMode();
        UpdateChecks();
    }

    private void ApplyMode()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;

        if (_selectedMonitor >= screens.Length) _selectedMonitor = 0;

        if (_forms.Count > 0)
        {
            _switching = true;
            _pendingMode = _mode;
            _pendingMonitor = _selectedMonitor;

            foreach (var form in _forms.ToArray())
            {
                form.Close();
            }

            return;
        }

        CreateFormsForMode(_mode, _selectedMonitor, screens);
    }

    private void CreateFormsForMode(WallpaperMode mode, int selectedMonitor, Screen[] screens)
    {
        _forms.Clear();
        var virtualBounds = SystemInformation.VirtualScreen;

        if (mode == WallpaperMode.Span)
        {
            // Span has one WebView across the virtual screen. It is intentionally
            // not associated with an individual monitor for automatic pausing.
            _forms.Add(CreateForm(virtualBounds, virtualBounds, IntPtr.Zero));
        }
        else if (mode == WallpaperMode.All)
        {
            foreach (var screen in screens)
            {
                _forms.Add(CreateForm(screen.Bounds, virtualBounds, GetMonitorHandle(screen.Bounds)));
            }
        }
        else
        {
            var bounds = screens[selectedMonitor].Bounds;
            _forms.Add(CreateForm(bounds, virtualBounds, GetMonitorHandle(bounds)));
        }

        RequestWallpaperSuspensionUpdate();
        if (_fullscreenMonitoringStarted)
            QueueFullscreenReevaluation();
    }

    private static IntPtr GetMonitorHandle(Rectangle bounds)
    {
        RECT rect = new()
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };

        return MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
    }

    private DeskForm CreateForm(Rectangle targetBounds, Rectangle virtualBounds, IntPtr monitorHandle)
    {
        var form = new DeskForm(_url, _scriptContent, targetBounds, virtualBounds, monitorHandle);
        form.FormClosed += OnFormClosed;
        form.Show();
        return form;
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is DeskForm form)
        {
            _forms.Remove(form);
            form.Dispose();
            if (_exiting && _forms.Count == 0)
            {
                UninstallHooks();
                DisposeFullscreenMonitoring();
                _tray.Visible = false;
                _tray.Dispose();
                ExitThread();
                return;
            }

            if (_switching && _forms.Count == 0)
            {
                _switching = false;
                CreateFormsForMode(_pendingMode, _pendingMonitor, Screen.AllScreens);
            }
        }
    }

    private void UpdateChecks()
    {
        if (_spanItem != null) _spanItem.Checked = _mode == WallpaperMode.Span;
        if (_allItem != null) _allItem.Checked = _mode == WallpaperMode.All;

        for (int i = 0; i < _monitorItems.Count; i++)
        {
            _monitorItems[i].Checked = _mode == WallpaperMode.Single && i == _selectedMonitor;
        }
    }

    private void Exit()
    {
        _exiting = true;
        DisposeFullscreenMonitoring();
        foreach (var form in _forms.ToArray())
        {
            form.Close();
        }

        if (_forms.Count == 0)
        {
            UninstallHooks();
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }
    }

    internal enum WallpaperMode
    {
        Span,
        All,
        Single
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    private enum GetAncestorFlags
    {
        GetParent = 1,
        GetRoot = 2,
        GetRootOwner = 3
    }

    [Flags]
    private enum ProcessAccessFlags : uint
    {
        VirtualMemoryOperation = 0x0008,
        VirtualMemoryRead = 0x0010,
        VirtualMemoryWrite = 0x0020,
    }

    [Flags]
    private enum AllocationType
    {
        Commit = 0x1000,
        Release = 0x8000,
    }

    [Flags]
    private enum MemoryProtection
    {
        ReadWrite = 0x04,
    }

    [Flags]
    private enum FreeType
    {
        Release = 0x8000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, GetAncestorFlags flags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, FreeType dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

public sealed class DeskForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private readonly WebView2 _wv = new();
    private readonly string _url;
    private readonly string _scriptContent;
    private readonly Rectangle _targetBounds;
    private readonly Rectangle _virtualBounds;
    internal IntPtr MonitorHandle { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool FullscreenOccluded { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool RendererSuspended { get; set; }
    private readonly CancellationTokenSource _initCts = new();
    private bool _initStarted;
    private bool _closing;
    private const int HR_RESOURCE_NOT_READY = unchecked((int)0x8007139F);

    public DeskForm(
        string url,
        string scriptContent,
        Rectangle targetBounds,
        Rectangle virtualBounds,
        IntPtr monitorHandle)
    {
        _url = url;
        _scriptContent = scriptContent;
        _targetBounds = targetBounds;
        _virtualBounds = virtualBounds;
        MonitorHandle = monitorHandle;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = targetBounds;

        _wv.Dock = DockStyle.Fill;
        Controls.Add(_wv);

        Shown += async (_, _) =>
        {
            if (_initStarted) return;
            _initStarted = true;
            AttachToWorkerW();

            await InitializeWebViewAsync();
        };

        FormClosed += (_, _) =>
        {
            _closing = true;
            _initCts.Cancel();
            if (!_wv.IsDisposed) _wv.Dispose();
        };
    }

    public void DispatchScroll(int x, int y, int delta)
    {
        if (_wv == null || _wv.IsDisposed || _wv.CoreWebView2 == null) return;
        try
        {
            double scaledDelta = -delta * (100.0 / 120.0);
            string val = scaledDelta.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string json = $"{{\"type\":\"mouseWheel\",\"x\":{x},\"y\":{y},\"deltaX\":0,\"deltaY\":{val}}}";
            _wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json);
        }
        catch
        {
        }
    }

    public async Task<bool> TrySuspendAsync()
    {
        if (_closing || _wv.IsDisposed || _wv.CoreWebView2 == null)
            return false;

        try
        {
            return await _wv.CoreWebView2.TrySuspendAsync();
        }
        catch
        {
            return false;
        }
    }

    public void Resume()
    {
        if (_closing || _wv.IsDisposed || _wv.CoreWebView2 == null)
            return;

        _wv.CoreWebView2.Resume();
    }

    public void ReloadWebView()
    {
        if (_closing || _wv.IsDisposed || _wv.CoreWebView2 == null)
            return;

        _wv.CoreWebView2.Reload();
    }

    public void PostKey(int msg, int vkCode, int scanCode, int flags)
    {
        if (_wv == null || _wv.IsDisposed || _wv.CoreWebView2 == null) return;

        IntPtr wvHwnd = GetWebViewHandle();
        if (wvHwnd == IntPtr.Zero) return;

        // Reconstruct lParam
        int repeatCount = 1;
        bool extended = (flags & 1) != 0;
        bool altDown = (flags & 0x20) != 0;
        bool up = (flags & 0x80) != 0;

        long newLParam = repeatCount;
        newLParam |= (long)scanCode << 16;
        if (extended) newLParam |= 1L << 24;

        // Handling Shortcuts (Alt+Key):
        // If we receive WM_SYSKEYDOWN (Alt+Key), we want to convert it to WM_KEYDOWN
        // so the browser sees it as a "Content" keystroke (e.g. shortcut) instead of "System" (Menu).
        // HOWEVER, we must be careful:
        // 1. If we send WM_KEYDOWN with bit 29 (Alt) SET, Chrome treats it as Alt+Key. Correct.
        // 2. If we send WM_SYSKEYDOWN, Chrome treats it as Menu access. Incorrect for web apps.

        int finalMsg = msg;

        if (msg == 0x0104) // WM_SYSKEYDOWN
        {
            finalMsg = 0x0100; // WM_KEYDOWN
            // We KEEP bit 29 set in lParam to indicate Alt is held.
        }
        else if (msg == 0x0105) // WM_SYSKEYUP
        {
            finalMsg = 0x0101; // WM_KEYUP
        }

        // Apply Alt bit (Bit 29)
        if (altDown) newLParam |= 1L << 29;

        // Apply Transition state (Bit 31 - KeyUp) and Previous State (Bit 30)
        // Note: For KeyDown, Bit 31 is 0. For KeyUp, it is 1.
        if (up) newLParam |= 1L << 31;
        if (up || (msg == 0x0100 && (flags & 0x40) != 0)) // Check context code for repeat? No, just rely on KeyUp msg
        {
            // Actually, for WM_KEYUP/SYSKEYUP, bit 30 and 31 should be 1.
            // But let's trust the logic: KeyUp msg implies bit 31=1.
            // Previous state (bit 30) is 1 if key was down before.
        }

        // Simpler approach for 30/31:
        if (msg == 0x0101 || msg == 0x0105) // UP
        {
            newLParam |= 1L << 30; // Previous state was down
            newLParam |= 1L << 31; // Transition state is up
        }
        else // DOWN
        {
            // Bit 30: The previous key state. The value is 1 if the key is down before the message is sent, or it is zero if the key is up.
            // For auto-repeat, this would be 1. We assume 0 for first press.
            // We can just check the 'up' flag from the hook, which is 0 for down.
        }

        PostMessage(wvHwnd, finalMsg, (IntPtr)vkCode, (IntPtr)newLParam);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    public IntPtr GetWebViewHandle()
    {
        if (_wv == null || _wv.IsDisposed || _wv.CoreWebView2 == null)
            return IntPtr.Zero;
        return FindWindowRecursive(_wv.Handle, "Chrome_RenderWidgetHostHWND");
    }

    private IntPtr FindWindowRecursive(IntPtr parent, string className)
    {
        IntPtr found = FindWindowEx(parent, IntPtr.Zero, className, null);
        if (found != IntPtr.Zero) return found;

        IntPtr child = FindWindowEx(parent, IntPtr.Zero, null, null);
        while (child != IntPtr.Zero)
        {
            found = FindWindowRecursive(child, className);
            if (found != IntPtr.Zero) return found;
            child = FindWindowEx(parent, child, null, null);
        }

        return IntPtr.Zero;
    }

    private async Task InitializeWebViewAsync()
    {
        int attempt = 0;
        while (!_closing && !_initCts.IsCancellationRequested && attempt < 6)
        {
            try
            {
                await _wv.EnsureCoreWebView2Async();
                if (_closing || _initCts.IsCancellationRequested || _wv.CoreWebView2 == null) return;

                _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _wv.CoreWebView2.Settings.AreDevToolsEnabled = false;

                if (!string.IsNullOrWhiteSpace(_scriptContent))
                {
                    await _wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_scriptContent);
                }

                _wv.CoreWebView2.Navigate(_url);
                return;
            }
            catch (COMException ex) when (ex.ErrorCode == HR_RESOURCE_NOT_READY)
            {
                attempt++;
                try
                {
                    await Task.Delay(200, _initCts.Token);
                }
                catch
                {
                    return;
                }
            }
            catch (Exception) when (_closing || _initCts.IsCancellationRequested || IsDisposed)
            {
                return;
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private void AttachToWorkerW()
    {
        var host = WorkerW.FindDesktopHost();
        if (host != IntPtr.Zero)
        {
            SetParent(Handle, host);
            ApplyBounds();
        }
    }

    private void ApplyBounds()
    {
        int x = _targetBounds.Left - _virtualBounds.Left;
        int y = _targetBounds.Top - _virtualBounds.Top;
        SetWindowPos(Handle, HWND_BOTTOM, x, y, _targetBounds.Width, _targetBounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static class WorkerW
    {
        private const int WM_SPAWN_WORKERW = 0x052C;
        private const string ShellViewClass = "SHELLDLL_DefView";
        private const string WorkerWClass = "WorkerW";

        public static IntPtr FindDesktopHost()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;

            SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero,
                SendMessageTimeoutFlags.SMTO_NORMAL, 1000, out _);

            IntPtr defViewInProgman = FindWindowEx(progman, IntPtr.Zero, ShellViewClass, null);
            if (defViewInProgman != IntPtr.Zero)
            {
                IntPtr childWorker = FindWindowEx(progman, IntPtr.Zero, WorkerWClass, null);
                if (childWorker != IntPtr.Zero) return childWorker;
                return progman;
            }

            IntPtr workerw = IntPtr.Zero;

            EnumWindows((top, _) =>
            {
                IntPtr defView = FindWindowEx(top, IntPtr.Zero, ShellViewClass, null);
                if (defView != IntPtr.Zero)
                {
                    workerw = FindWindowEx(IntPtr.Zero, top, WorkerWClass, null);
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return workerw != IntPtr.Zero ? workerw : progman;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
        SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0000,
        SMTO_BLOCK = 0x0001,
        SMTO_ABORTIFHUNG = 0x0002,
        SMTO_NOTIMEOUTIFNOTHUNG = 0x0008
    }
}

public class InputBox : Form
{
    private TextBox textBox;
    private Button okButton;
    private Button cancelButton;

    public string InputText => textBox.Text;

    public InputBox(string title, string prompt, string defaultText)
    {
        Text = title;
        Size = new Size(400, 160);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        Label label = new Label { Left = 10, Top = 10, Width = 360, Text = prompt };
        textBox = new TextBox { Left = 10, Top = 35, Width = 360, Text = defaultText };
        okButton = new Button { Text = "OK", Left = 210, Top = 80, Width = 75, DialogResult = DialogResult.OK };
        cancelButton = new Button { Text = "Cancel", Left = 295, Top = 80, Width = 75, DialogResult = DialogResult.Cancel };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.Add(label);
        Controls.Add(textBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);
    }
}
