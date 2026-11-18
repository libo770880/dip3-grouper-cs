# -*- coding: utf-8 -*-
"""点击窗口内相对坐标并输入文本（SendInput Unicode）。

用法: python click_type.py <进程名> <rel_x> <rel_y> <文本> [回车]
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
user32.SetProcessDPIAware()

PROC_NAME = sys.argv[1]
REL_X, REL_Y = float(sys.argv[2]), float(sys.argv[3])
TEXT = sys.argv[4] if len(sys.argv) > 4 else ""
PRESS_ENTER = len(sys.argv) > 5 and sys.argv[5] == "enter"

raw = subprocess.run(
    ["tasklist", "/FI", "IMAGENAME eq %s.exe" % PROC_NAME, "/FO", "CSV", "/NH"],
    capture_output=True).stdout.decode("gbk", "replace")
pids = set()
for line in raw.splitlines():
    parts = [p.strip('"') for p in line.split('","')]
    if len(parts) >= 2 and parts[0].lower() == PROC_NAME.lower() + ".exe":
        try:
            pids.add(int(parts[1]))
        except ValueError:
            pass
if not pids:
    print("NO_PROCESS"); sys.exit(1)

EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
found = []


def _cb(hwnd, _lp):
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value in pids and user32.IsWindowVisible(hwnd):
        r = wt.RECT()
        if user32.GetWindowRect(hwnd, ctypes.byref(r)):
            w, h = r.right - r.left, r.bottom - r.top
            if w > 200 and h > 150:
                found.append((hwnd, r, w, h))
    return True


user32.EnumWindows(EnumWindowsProc(_cb), 0)
if not found:
    print("NO_VISIBLE_WINDOW"); sys.exit(1)

hwnd, rect, W, H = max(found, key=lambda t: t[2] * t[3])

# 置前
fg = user32.GetForegroundWindow()
fg_tid = user32.GetWindowThreadProcessId(fg, None)
my_tid = kernel32.GetCurrentThreadId()
att = bool(user32.AttachThreadInput(my_tid, fg_tid, True)) if fg_tid and fg_tid != my_tid else False
user32.keybd_event(0x12, 0, 0, 0)
user32.SetForegroundWindow(hwnd)
user32.keybd_event(0x12, 0, 2, 0)
if att:
    user32.AttachThreadInput(my_tid, fg_tid, False)
time.sleep(0.5)

# 前台必须是目标窗口才继续，防止按键打进别的应用
now_fg = user32.GetForegroundWindow()
if now_fg != hwnd:
    print("NOT_FOREGROUND"); sys.exit(2)

ax = rect.left + int(W * REL_X)
ay = rect.top + int(H * REL_Y)
user32.SetCursorPos(ax, ay)
time.sleep(0.15)
user32.mouse_event(0x0002, 0, 0, 0, 0)
user32.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.35)


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]


class INPUT(ctypes.Structure):
    class _I(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT)]
    _anonymous_ = ("_i",)
    _fields_ = [("type", wt.DWORD), ("_i", _I)]


INPUT_KEYBOARD = 1
KEYEVENTF_UNICODE = 0x0004
KEYEVENTF_KEYUP = 0x0002


def send_char(ch, up=False):
    flags = KEYEVENTF_UNICODE | (KEYEVENTF_KEYUP if up else 0)
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.ki = KEYBDINPUT(0, ord(ch), flags, 0, None)
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def set_clipboard(text):
    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002
    data = text.encode("utf-16-le") + b"\x00\x00"
    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    h = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
    p = kernel32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    kernel32.GlobalUnlock(h)
    if not user32.OpenClipboard(None):
        return False
    try:
        user32.EmptyClipboard()
        user32.SetClipboardData.argtypes = [wt.UINT, ctypes.c_void_p]
        user32.SetClipboardData(CF_UNICODETEXT, h)
    finally:
        user32.CloseClipboard()
    return True


# 中文 IME 会吞 SendInput 的逐字符输入 → 纯数字走 VK 键码（不受 IME 影响），其余走剪贴板
if TEXT:
    if TEXT.isdigit():
        for ch in TEXT:
            vk = 0x30 + int(ch)
            user32.keybd_event(vk, 0, 0, 0)
            user32.keybd_event(vk, 0, 2, 0)
            time.sleep(0.05)
        time.sleep(0.4)
    else:
        if not set_clipboard(TEXT):
            print("CLIPBOARD_FAIL"); sys.exit(1)
        user32.keybd_event(0x11, 0, 0, 0)          # Ctrl down
        user32.keybd_event(0x56, 0, 0, 0)          # V down
        user32.keybd_event(0x56, 0, 2, 0)          # V up
        user32.keybd_event(0x11, 0, 2, 0)          # Ctrl up
        time.sleep(0.5)

if PRESS_ENTER:
    user32.keybd_event(0x0D, 0, 0, 0)
    user32.keybd_event(0x0D, 0, 2, 0)

time.sleep(0.4)
print("TYPED %d chars at %d,%d enter=%s" % (len(TEXT), ax, ay, PRESS_ENTER))
