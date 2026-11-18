# -*- coding: utf-8 -*-
"""用 PrintWindow(PW_RENDERFULLCONTENT) 截取窗口，即使被遮挡也能截到。

用法: python capture_window2.py <进程名> <输出路径>
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()

PROC_NAME = sys.argv[1] if len(sys.argv) > 1 else "Dip3Grouper.Desktop"
OUT = sys.argv[2] if len(sys.argv) > 2 else "window.png"

# --- 找 PID ---
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

# --- 找可见主窗口 ---
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

# --- PrintWindow 到 32bpp DIB ---
class BMIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

hdc_screen = user32.GetDC(None)
hdc = gdi32.CreateCompatibleDC(hdc_screen)
bmi = BMIH()
bmi.biSize = ctypes.sizeof(BMIH)
bmi.biWidth = W
bmi.biHeight = -H          # 负数 = 自顶向下
bmi.biPlanes = 1
bmi.biBitCount = 32
bmi.biCompression = 0      # BI_RGB
bmi.biSizeImage = W * H * 4

bits = ctypes.c_void_p()
hbmp = gdi32.CreateDIBSection(hdc, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
if not hbmp or not bits.value:
    print("DIB_FAIL"); sys.exit(1)
old = gdi32.SelectObject(hdc, hbmp)

PW_RENDERFULLCONTENT = 2
ok = user32.PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)
if not ok:
    ok = user32.PrintWindow(hwnd, hdc, 0)
if not ok:
    print("PRINTWINDOW_FAIL"); sys.exit(1)

buf = (ctypes.c_ubyte * (W * H * 4)).from_address(bits.value)
raw_bytes = bytes(buf)

gdi32.SelectObject(hdc, old)
gdi32.DeleteObject(hbmp)
gdi32.DeleteDC(hdc)
user32.ReleaseDC(None, hdc_screen)

# --- BGRA -> RGBA -> PNG ---
from PIL import Image
img = Image.frombytes("RGBA", (W, H), raw_bytes, "raw", "BGRA")
img.save(OUT)
print("SAVED %s | title=%s | %dx%d | printwindow=%s" % (OUT, title.value, W, H, ok))
