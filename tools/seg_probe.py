# -*- coding: utf-8 -*-
"""点一下分段按钮的某一项，并在切换瞬间连拍，用来核对白块是否有「滑动过渡」。

用法: python seg_probe.py <标签文字> [帧数] [间隔秒]
例:   python seg_probe.py 作废对照 6 0.04

判定方法：取每帧的白块左边界，若出现「介于起止位置之间」的中间态，说明动画在跑；
若每帧白块都只有起始位 / 终点位两种取值，则是硬切（无过渡）。
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

PROC = "Dip3Grouper.Desktop"
LABEL = sys.argv[1] if len(sys.argv) > 1 else "作废对照"
FRAMES = int(sys.argv[2]) if len(sys.argv) > 2 else 8
GAP = float(sys.argv[3]) if len(sys.argv) > 3 else 0.035

# 分段控件的几何位置（窗口客户区内坐标，150% DPI 实测）：
#   轨道 y 88..130，中线 108；四个标签的中心 x 依次如下（「现行项目」白块实测 780..900）。
TRACK_Y = 96
LABEL_X = {"现行项目": 775, "作废对照": 877, "旧版物价": 979, "新旧比对": 1082}


def pids_of(name):
    raw = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {name}.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, encoding="gbk", errors="ignore").stdout
    return [int(x.split(",")[1].strip('"')) for x in raw.strip().splitlines()
            if x.count(",") >= 1 and x.split(",")[1].strip('"').isdigit()]


def main_window(pids):
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                if "分组器" in buf.value:
                    found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def rect_of(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def is_track(c):
    """分段轨道底色 B.SegBg（实测 233,238,246；不同主题/DPI 会有 ±6 偏差）。"""
    return abs(c[0] - 233) < 7 and abs(c[1] - 238) < 7 and abs(c[2] - 246) < 7


def white_run(img, y):
    """该行最长的纯白连续段（白块），返回 (起始x, 结束x) 或 None。"""
    px = img.load()
    w = img.width
    best = None
    start = None
    for x in range(w):
        c = px[x, y]
        is_white = c[0] > 250 and c[1] > 250 and c[2] > 250
        if is_white and start is None:
            start = x
        elif not is_white and start is not None:
            if best is None or x - 1 - start > best[1] - best[0]:
                best = (start, x - 1)
            start = None
    if start is not None and (best is None or w - 1 - start > best[1] - best[0]):
        best = (start, w - 1)
    return best


def main():
    pids = pids_of(PROC)
    hwnd = main_window(pids) if pids else None
    if not hwnd:
        print("没找到窗口，先启动应用")
        return 1

    user32.SetForegroundWindow(hwnd)
    time.sleep(0.4)
    l, t, r, b = rect_of(hwnd)
    print(f"窗口 {l},{t} {r - l}x{b - t}")

    # 标题行里动态定位四个标签：取轨道色/白块所在行，按深色文字块切成 4 段求中心。
    # 窗口每次启动位置可能不同，写死坐标必然点偏，所以每次现测。
    shot = ImageGrab.grab(bbox=(l, t, r, b))
    px = shot.load()

    def is_seg(c):
        return is_track(c) or (c[0] > 250 and c[1] > 250 and c[2] > 250)

    # 找轨道所在行：右半部分连续出现轨道色/白块最多的那一段取中
    band = [y for y in range(20, 260)
            if sum(1 for x in range(shot.width // 3, shot.width - 10, 3) if is_seg(px[x, y])) > 120]
    if not band:
        print("没定位到分段轨道，dump 一帧")
        shot.save("seg_dump.png")
        return 2
    track_y = band[0] + 4          # 靠上沿，避开文字基线

    # 文字块 → 标签中心（只认宽度 12~90 的窄块，太宽的是右侧统计文字）
    text_y = band[0] + (band[-1] - band[0]) // 2
    cols = [x for x in range(shot.width // 3, shot.width) if sum(px[x, text_y]) < 620]
    groups = []
    if cols:
        st = prev = cols[0]
        for x in cols[1:]:
            if x > prev + 26:
                groups.append((st, prev))
                st = x
            prev = x
        groups.append((st, prev))
    centers = [(a + b) // 2 for a, b in groups if 12 <= b - a <= 90]
    if len(centers) < 4:
        print(f"标签只认出 {len(centers)} 个：{centers}，dump 一帧")
        shot.save("seg_dump.png")
        return 2
    names = ["现行项目", "作废对照", "旧版物价", "新旧比对"]
    label_centers = dict(zip(names, centers[:4]))
    print(f"轨道行 {band[0]}..{band[-1]}，扫色行 y={track_y}，标签中心 {label_centers}")
    cx = l + label_centers[LABEL]
    cy = t + track_y
    print(f"点击 {LABEL} @ ({cx},{cy})")

    user32.SetCursorPos(cx, cy)
    time.sleep(0.15)
    user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.02)
    user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP

    runs = []
    for i in range(FRAMES):
        img = ImageGrab.grab(bbox=(l, t, r, b))
        seg = white_run(img, track_y)
        runs.append(seg)
        img.save(f"seg_f{i}.png")
        if GAP:
            time.sleep(GAP)

    print("每帧白块左右边界（左边界即滑动位置）：")
    for i, sg in enumerate(runs):
        print(f"  f{i}: {sg}")
    xs = [sg[0] for sg in runs if sg]
    if not xs:
        print("→ 没抓到白块，人工看 seg_f*.png")
        return 0
    span = max(xs) - min(xs)
    middles = sorted({x for x in xs if min(xs) + 3 < x < max(xs) - 3})
    if span <= 3:
        print("→ 白块没动（点击可能没生效）")
    elif middles:
        print(f"→ 有滑动过渡：位置 {min(xs)} → {max(xs)}，中间态 {middles}")
    else:
        print(f"→ 只有起止两态 {min(xs)} / {max(xs)}，说明是硬切、没有过渡")
    return 0


if __name__ == "__main__":
    sys.exit(main())
