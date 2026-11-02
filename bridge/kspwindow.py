"""Places the window of a KSP just started, on Windows: KSP opens it in the middle of the screen, and Unity 2019
has no setting to open it anywhere else, so it is moved from outside once it exists.

Python standard library only (ctypes), so that stdio-bridge.py keeps needing nothing else.
"""
import ctypes
import os
import threading
import time

WINDOW_TIMEOUT = 120
POLL_PERIOD = 0.2

SPI_GETWORKAREA = 0x0030
DWMWA_EXTENDED_FRAME_BOUNDS = 9
MONITOR_DEFAULTTONEAREST = 2
SWP_NOSIZE = 0x0001
SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010
DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4


def place_window(pid, x, timeout=WINDOW_TIMEOUT):
    """Moves the window of process pid so that its visible left edge is x pixels right of the left edge of the
    work area of the primary screen, keeping its vertical position and its size. A window still full screen
    after timeout seconds is left where it is. Waits up to timeout seconds for the window to show and leave full
    screen. Never raises: returns one line telling what
    was done, or why nothing was."""
    if os.name != "nt":
        return "window not moved: only done on Windows"
    try:
        return _place(pid, x, timeout)
    except Exception as e:
        return "window not moved: %s" % e


def place_window_later(pid, x, report, timeout=WINDOW_TIMEOUT):
    """Does what place_window does, on a thread of its own, and passes its line to report."""
    threading.Thread(target=lambda: report(place_window(pid, x, timeout)), daemon=True).start()


def _place(pid, x, timeout):
    from ctypes import wintypes
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    dwmapi = ctypes.WinDLL("dwmapi")

    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT), ("rcWork", wintypes.RECT),
                    ("dwFlags", wintypes.DWORD)]

    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [enum_proc, wintypes.LPARAM]
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
    user32.MonitorFromWindow.restype = wintypes.HMONITOR
    user32.GetMonitorInfoW.argtypes = [wintypes.HMONITOR, ctypes.POINTER(MONITORINFO)]
    user32.SystemParametersInfoW.argtypes = [wintypes.UINT, wintypes.UINT, ctypes.c_void_p, wintypes.UINT]
    user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, wintypes.UINT]
    dwmapi.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]

    # Physical pixels on this thread: otherwise, with a display scale above 100 %, GetWindowRect and the work area
    # would be scaled while the DWM frame bounds are not. Absent before Windows 10 1607: scaled coordinates then.
    try:
        user32.SetThreadDpiAwarenessContext.argtypes = [ctypes.c_void_p]
        user32.SetThreadDpiAwarenessContext.restype = ctypes.c_void_p
        user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    except AttributeError:
        pass

    def find():
        found = []

        def visit(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                name = ctypes.create_unicode_buffer(64)
                user32.GetClassNameW(hwnd, name, 64)
                if name.value == "UnityWndClass":
                    found.append(hwnd)
                    return False
            return True

        user32.EnumWindows(enum_proc(visit), 0)
        return found[0] if found else None

    def bounds(hwnd):
        # The window rectangle includes the invisible resize border of Windows 10; the DWM frame does not.
        rect, frame = wintypes.RECT(), wintypes.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
            raise ctypes.WinError(ctypes.get_last_error())
        result = dwmapi.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ctypes.byref(frame),
                                              ctypes.sizeof(frame))
        if result != 0:
            raise OSError("DwmGetWindowAttribute failed: 0x%08X" % (result & 0xFFFFFFFF))
        return rect, frame

    began = time.time()
    hwnd = find()
    while hwnd is None:
        if time.time() - began > timeout:
            return "window not moved: no KSP window after %.0f s" % timeout
        time.sleep(POLL_PERIOD)
        hwnd = find()

    def full_screen():
        monitor = MONITORINFO()
        monitor.cbSize = ctypes.sizeof(MONITORINFO)
        if not user32.GetMonitorInfoW(user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST),
                                      ctypes.byref(monitor)):
            raise ctypes.WinError(ctypes.get_last_error())
        screen = monitor.rcMonitor
        return frame.left <= screen.left and frame.top <= screen.top and frame.right >= screen.right \
            and frame.bottom >= screen.bottom

    # Unity opens the window with the screen mode of its registry preferences, which every KSP install of the
    # user shares, and KSP applies the one of its settings.cfg only later: a window full screen at first may
    # still turn into a window.
    rect, frame = bounds(hwnd)
    while full_screen():
        if time.time() - began > timeout:
            return "window not moved: full screen"
        time.sleep(POLL_PERIOD)
        rect, frame = bounds(hwnd)

    work = wintypes.RECT()
    if not user32.SystemParametersInfoW(SPI_GETWORKAREA, 0, ctypes.byref(work), 0):
        raise ctypes.WinError(ctypes.get_last_error())
    left = work.left + x - (frame.left - rect.left)
    if not user32.SetWindowPos(hwnd, None, left, rect.top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE):
        raise ctypes.WinError(ctypes.get_last_error())

    rect, frame = bounds(hwnd)
    return "window moved after %.1f s: visible left edge %d px right of the work area, top %d, %d x %d" % (
        time.time() - began, frame.left - work.left, frame.top, frame.right - frame.left, frame.bottom - frame.top)
