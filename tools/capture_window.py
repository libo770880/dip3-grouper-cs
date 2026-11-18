# -*- coding: utf-8 -*-
"""截取指定进程的主窗口（含无边框窗口），保存为 PNG。

用法: python capture_window.py <进程名> <输出路径>
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

PROC_NAME = sys.argv[1] if len(sys.argv) > 1 else "Dip3Grouper.Desktop"
OUT = sys.argv[2] if len(sys.argv) > 2 else "window.png"

# 1) 用 tasklist 拿到 PID 集合（避免引入第三方依赖）
import subprocess

raw = subprocess.run(
    ["tasklist", "/FI", "IMAGENAME eq %s.exe" % PROC_NAME, "/FO", "CSV", "/NH"],
    capture_output=True,
).stdout.decode("gbk", "replace")

pids = set()
for line in raw.splitlines():
    parts = [p.strip('"') for p in line.split('","')]
    if len(parts) >= 2 and parts[0].lower() == PROC_NAME.lower() + ".exe":
        try:
            pids.add(int(parts[1]))
        except ValueError:
            pass

if not pids:
    print("NO_PROCESS")
    sys.exit(1)

# 2) 枚举顶层窗口，找出属于这些 PID 且可见的窗口
EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
found = []


def _cb(hwnd, _lparam):
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
    print("NO_VISIBLE_WINDOW")
    sys.exit(1)

# 取面积最大的那个（主窗口）
hwnd, rect, w, h = max(found, key=lambda t: t[2] * t[3])

title = ctypes.create_unicode_buffer(512)
user32.GetWindowTextW(hwnd, title, 512)

# 3) 置前并截图
user32.ShowWindow(hwnd, 9)  # SW_RESTORE
user32.SetForegroundWindow(hwnd)
time.sleep(0.9)

img = ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom), all_screens=True)
img.save(OUT)
print("SAVED %s | title=%s | %dx%d | windows=%d"
      % (OUT, title.value, w, h, len(found)))
