# -*- coding: utf-8 -*-
"""按窗口标题定位并点击（多窗口程序里主窗口会抢走"最大窗口"，需要按标题区分）。

用法: python click_title.py <标题子串> <x比例> <y比例> [输出截图路径]
坐标是相对该窗口宽高的比例。
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
user32.SetProcessDPIAware()

TITLE = sys.argv[1]
FX = float(sys.argv[2])
FY = float(sys.argv[3])
OUT = sys.argv[4] if len(sys.argv) > 4 else None

EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
hits = []


def cb(hwnd, _lp):
    if user32.IsWindowVisible(hwnd):
        buf = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(hwnd, buf, 512)
        if TITLE in buf.value:
            r = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.right - r.left, r.bottom - r.top
            if w > 100 and h > 100:
                hits.append((hwnd, r, buf.value, w, h))
    return True


user32.EnumWindows(EnumProc(cb), 0)
if not hits:
    print("NO_WINDOW title=%r" % TITLE)
    sys.exit(1)

hwnd, rect, title, W, H = max(hits, key=lambda t: t[3] * t[4])

# 置前（AttachThreadInput + ALT 微触发，绕过前台锁）
user32.ShowWindow(hwnd, 9)
fg = user32.GetForegroundWindow()
fg_tid = user32.GetWindowThreadProcessId(fg, None)
my_tid = kernel32.GetCurrentThreadId()
attached = False
if fg_tid and fg_tid != my_tid:
    attached = bool(user32.AttachThreadInput(my_tid, fg_tid, True))
user32.keybd_event(0x12, 0, 0, 0)
user32.SetForegroundWindow(hwnd)
user32.keybd_event(0x12, 0, 2, 0)
if attached:
    user32.AttachThreadInput(my_tid, fg_tid, False)
time.sleep(0.7)

x = rect.left + int(W * FX)
y = rect.top + int(H * FY)
user32.SetCursorPos(x, y)
user32.mouse_event(2, 0, 0, 0, 0)   # LEFTDOWN
user32.mouse_event(4, 0, 0, 0, 0)   # LEFTUP
time.sleep(0.4)

msg = "CLICKED %d,%d | title=%s | %dx%d" % (x, y, title, W, H)
if OUT:
    time.sleep(0.5)
    ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom), all_screens=True).save(OUT)
    msg += " | SAVED " + OUT
print(msg)
