# -*- coding: utf-8 -*-
"""点击窗口内相对坐标并截图（用于验证后台页面渲染）。

用法: python click_and_shot.py <进程名> <输出路径> <rel_x> <rel_y>
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

PROC_NAME, OUT = sys.argv[1], sys.argv[2]
REL_X, REL_Y = float(sys.argv[3]), float(sys.argv[4])

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
time.sleep(0.6)

ax = rect.left + int(W * REL_X)
ay = rect.top + int(H * REL_Y)
user32.SetCursorPos(ax, ay)
time.sleep(0.15)
user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
time.sleep(1.4)

# 重新枚举（后台可能是新窗口）
found.clear()
user32.EnumWindows(EnumWindowsProc(_cb), 0)
hwnd2, rect2, W2, H2 = max(found, key=lambda t: t[2] * t[3])
title2 = ctypes.create_unicode_buffer(512)
user32.GetWindowTextW(hwnd2, title2, 512)

img = ImageGrab.grab(bbox=(rect2.left, rect2.top, rect2.right, rect2.bottom), all_screens=True)
img.save(OUT)
print("CLICKED %d,%d | SAVED %s | title=%s | %dx%d" % (ax, ay, OUT, title2.value, W2, H2))
