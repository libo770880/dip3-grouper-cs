# -*- coding: utf-8 -*-
"""验证分段切换白块是否有滑动过渡（真实窗口 + 系统级鼠标点击 + 连拍）。

之前的失败原因不是界面问题，而是探针：
  - 写死屏幕坐标 → 窗口位置一变就点偏；
  - 用「轨道等分」估算 → 把整行背景误判成轨道，点到了窗外。

本版改成：
  1) 用 GetClientRect + ClientToScreen 拿到「客户区原点」，所有坐标都相对客户区，
     与窗口在屏幕上的位置无关；
  2) 轨道位置靠**截图里找白块**反推：先截一张，找 Segment.Bar 那条圆角轨道的包围盒，
     再按四个标签的等宽切成四段，点击目标段的中心；
  3) 点击后以 ~16ms 间隔连拍，逐帧量白块左边界，看是否出现中间态。

用法: python seg_probe2.py [目标标签] [输出目录]
例:   python seg_probe2.py 新旧比对 .tmp/segprobe
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

PROC = "Dip3Grouper.Desktop"
TARGET = sys.argv[1] if len(sys.argv) > 1 else "新旧比对"
OUT = sys.argv[2] if len(sys.argv) > 2 else ".tmp/segprobe"

LABELS = ["现行项目", "作废对照", "旧版物价", "新旧比对"]


def main_window():
    """按标题找主窗口。

    ⚠ 不要用 tasklist 拿 PID 再筛进程：本机某些 Python 子进程环境里 tasklist 返回空，
       会误报「没有运行中的进程」。直接枚举顶层窗口按标题匹配最可靠。
    """
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if "分组器" in buf.value:
                found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def client_origin(hwnd):
    """客户区左上角在屏幕上的坐标（与窗口位置无关的锚点）。"""
    pt = wt.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    rc = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rc))
    return pt.x, pt.y, rc.right, rc.bottom


def click(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.02)
    user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.02)
    user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP


def find_track(img, ox, oy):
    """在截图里找分段轨道（Segment.Bar）：一条浅灰圆角长条，位于标题行右侧。

    用「同色横向连续段」启发式：逐行扫描，找连续 ≥200px 且颜色接近 B.SegBg 的区段。
    返回 (left, top, right, bottom) 屏幕坐标。
    """
    w, h = img.size
    px = img.load()
    best = None
    # 轨道在窗口上部，扫描 60..200 行足够
    for y in range(oy + 40, min(oy + 260, h)):
        run = 0
        start = 0
        for x in range(ox, w):
            r, g, b = px[x, y][:3]
            # 浅灰底：R≈G≈B 且亮度 225..248（卡片白是 255，页面底更亮或更暗）
            near = 222 <= r <= 250 and abs(r - g) <= 4 and abs(g - b) <= 4
            if near:
                if run == 0:
                    start = x
                run += 1
            else:
                if run >= 260:
                    if best is None or run > best[2] - best[0]:
                        best = (start, y, start + run, y)
                run = 0
        if run >= 260 and (best is None or run > best[2] - best[0]):
            best = (start, y, start + run, y)
    return best


def white_left(img, track, oy_range):
    """在白块所在的一条横线上找白色圆角的左边界。"""
    px = img.load()
    x0, _, x1, _ = track
    hits = []
    for y in oy_range:
        for x in range(x0, x1):
            r, g, b = px[x, y][:3]
            if r >= 250 and g >= 250 and b >= 250:
                hits.append(x)
                break
    return min(hits) if hits else None


def main():
    os.makedirs(OUT, exist_ok=True)
    hwnd = main_window()
    if not hwnd:
        print("找不到主窗口")
        return 1
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.35)

    ox, oy, cw, ch = client_origin(hwnd)
    print(f"客户区原点=({ox},{oy}) 尺寸={cw}x{ch}")

    shot = ImageGrab.grab(bbox=(ox, oy, ox + cw, oy + ch))
    shot.save(os.path.join(OUT, "before.png"))
    track = find_track(shot, 0, 0)
    if not track:
        print("没找到分段轨道（先看 before.png 确认界面状态）")
        return 1
    tx0, ty0, tx1, ty1 = track
    print(f"轨道(客户区坐标) x={tx0}..{tx1} y={ty0}..")

    # 轨道高度靠下探确定：找这一段里白/浅灰仍在的行
    band = range(ty0, min(ty0 + 60, ch))
    left_before = white_left(shot, track, band)
    print(f"白块左边界(切换前) = {left_before}")

    idx = LABELS.index(TARGET) if TARGET in LABELS else 3
    seg_w = (tx1 - tx0) / len(LABELS)
    cx = ox + tx0 + seg_w * (idx + 0.5)
    cy = oy + ty0 + 14
    print(f"点击「{TARGET}」客户区=({tx0 + seg_w * (idx + 0.5):.0f},{ty0 + 14}) 屏幕=({cx:.0f},{cy:.0f})")

    click(cx, cy)
    t0 = time.time()
    frames = []
    for i in range(14):
        img = ImageGrab.grab(bbox=(ox + tx0 - 4, oy + ty0 - 4, ox + tx1 + 4, oy + ty1 + 40))
        img.save(os.path.join(OUT, f"f{i:02d}.png"))
        px = img.load()
        left = None
        for y in range(4, img.size[1]):
            for x in range(px and img.size[0]):
                r, g, b = px[x, y][:3]
                if r >= 250 and g >= 250 and b >= 250:
                    left = tx0 - 4 + x
                    break
            if left is not None:
                break
        frames.append((round((time.time() - t0) * 1000), left))
        time.sleep(0.012)

    print("\n帧序列（毫秒, 白块左边界·客户区坐标）：")
    for ms, left in frames:
        print(f"  +{ms:4d}ms  left={left}")

    vals = sorted({v for _, v in frames if v is not None})
    print(f"\n出现过的左边界取值：{vals}")
    mids = [v for v in vals if left_before is not None and v > left_before + 2 and v < vals[-1] - 2]
    if len(vals) >= 3 and mids:
        print("PASS：存在中间态，白块是滑过去的")
        return 0
    print("FAIL：只有起点/终点两个取值，仍是硬切")
    return 2


if __name__ == "__main__":
    sys.exit(main())
