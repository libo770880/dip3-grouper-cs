# -*- coding: utf-8 -*-
"""快速连拍：不等置前，按窗口矩形直接屏幕拷贝，用于抓取"一闪而过"的画面（如启动页）。

用法: python shot_windows.py <进程名> <输出前缀> [帧数] [间隔秒]
输出: <前缀>-<序号>.png，并在 stdout 打印每帧的窗口标题与耗时。
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

PROC = sys.argv[1] if len(sys.argv) > 1 else "Dip3Grouper.Desktop"
PREFIX = sys.argv[2] if len(sys.argv) > 2 else "shot"
FRAMES = int(sys.argv[3]) if len(sys.argv) > 3 else 8
GAP = float(sys.argv[4]) if len(sys.argv) > 4 else 0.25


def pids_of(name):
    raw = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq %s.exe" % name, "/FO", "CSV", "/NH"],
        capture_output=True).stdout.decode("gbk", "replace")
    out = set()
    for line in raw.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].lower() == name.lower() + ".exe":
            try:
                out.add(int(parts[1]))
            except ValueError:
                pass
    return out


PROC_NEW = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
t0 = time.time()

for i in range(FRAMES):
    pids = pids_of(PROC)
    best = None
    if pids:
        def cb(hwnd, _lp):
            global best
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value in pids and user32.IsWindowVisible(hwnd):
                r = wt.RECT()
                if user32.GetWindowRect(hwnd, ctypes.byref(r)):
                    w, h = r.right - r.left, r.bottom - r.top
                    if w > 200 and h > 150 and (best is None or w * h > best[3] * best[4]):
                        buf = ctypes.create_unicode_buffer(256)
                        user32.GetWindowTextW(hwnd, buf, 256)
                        best = (hwnd, r, buf.value, w, h)
            return True

        user32.EnumWindows(PROC_NEW(cb), 0)

    if best:
        _, r, title, w, h = best
        img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), all_screens=True)
        path = "%s-%d.png" % (PREFIX, i)
        img.save(path)
        print("t=%.2fs  %s  title=%r %dx%d" % (time.time() - t0, path, title, w, h), flush=True)
    else:
        print("t=%.2fs  (no visible window)" % (time.time() - t0), flush=True)

    time.sleep(GAP)
