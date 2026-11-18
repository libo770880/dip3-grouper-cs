# -*- coding: utf-8 -*-
"""把窗口真正置前后用屏幕拷贝截图（解决 WPF/D3D 下 PrintWindow 黑屏）。

用法: python capture_window3.py <进程名> <输出路径>
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
user32.SetProcessDPIAware()

PROC_NAME = sys.argv[1] if len(sys.argv) > 1 else "Dip3Grouper.Desktop"
OUT = sys.argv[2] if len(sys.argv) > 2 else "window.png"

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
title = ctypes.create_unicode_buffer(512)
user32.GetWindowTextW(hwnd, title, 512)

# --- 解除前台锁：附加到前台线程的输入队列 ---
user32.ShowWindow(hwnd, 9)  # SW_RESTORE
fg = user32.GetForegroundWindow()
fg_tid = user32.GetWindowThreadProcessId(fg, None)
my_tid = kernel32.GetCurrentThreadId()
attached = False
if fg_tid and fg_tid != my_tid:
    attached = bool(user32.AttachThreadInput(my_tid, fg_tid, True))
# ALT 键微触发，绕过 SetForegroundWindow 限制
user32.keybd_event(0x12, 0, 0, 0)          # ALT down
ok = user32.SetForegroundWindow(hwnd)
user32.keybd_event(0x12, 0, 2, 0)          # ALT up (KEYEVENTF_KEYUP)
if attached:
    user32.AttachThreadInput(my_tid, fg_tid, False)
time.sleep(0.9)

now_fg = user32.GetForegroundWindow()
img = ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom), all_screens=True)
img.save(OUT)
print("SAVED %s | title=%s | %dx%d | setfg=%s | now_fg_is_target=%s"
      % (OUT, title.value, W, H, ok, now_fg == hwnd))
