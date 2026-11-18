# -*- coding: utf-8 -*-
"""验证分段切换白块是否有滑动过渡（真实窗口 + 系统级鼠标点击 + 连拍）。

前几版失败的根因（都是探针问题，不是界面问题）：
  - 写死屏幕坐标 → 窗口位置一变就点偏；
  - 用 tasklist 取 PID → 某些 Python 子进程环境里 tasklist 返回空，误报「进程不存在」；
  - 把整行背景误判成轨道。

本版做法：
  1) 枚举顶层窗口按标题找主窗口（不用 tasklist）；
  2) 截窗口图后在**图内**找分段轨道：白块是纯白圆角块，轨道底色是浅灰；
     取「纯白像素最多」的那一行作为轨道的纵向中线，再沿这一行找白块的左右边界；
  3) 白块 → 轨道：白块左右各扩展得到轨道范围（轨道 = 白块 + 其余三个标签等宽）；
  4) 点击目标标签中心，切换瞬间以 ~10ms 连拍，逐帧量白块左边界；
  5) 判定：出现「既不是起点也不是终点」的中间值 → 有滑动过渡。

用法: python seg_probe3.py [目标标签] [输出目录]
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

TITLE = "DIP 3.0 分组器"
LABELS = ["现行项目", "作废对照", "旧版物价", "新旧比对"]
TARGET = sys.argv[1] if len(sys.argv) > 1 else "新旧比对"
COLLECT_S = float(sys.argv[3]) if len(sys.argv) > 3 else 1.2
OUT = sys.argv[2] if len(sys.argv) > 2 else ".tmp/segprobe"


def find_window():
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, b, n + 1)
            if b.value == TITLE:
                found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def click(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.02)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.025)
    user32.mouse_event(0x0004, 0, 0, 0, 0)


def is_white(p):
    r, g, b = p[:3]
    return r >= 250 and g >= 250 and b >= 250


def find_slide_row(img, y0, y1):
    """在窗口上部找「白块所在行」。

    白块是**圆角小块**（宽 70~130px），不是整条标题栏。所以要求：
      - 最长连续白段在 60..200px（排除整行白底 / 大块白卡片）；
      - 该段右侧紧跟非白（标题栏整行白会被排除）。
    """
    px = img.load()
    w = img.size[0]
    best = None
    for y in range(y0, y1):
        run = 0
        best_run = 0
        start = 0
        best_start = 0
        for x in range(0, w):
            if is_white(px[x, y]):
                if run == 0:
                    start = x
                run += 1
                if run > best_run:
                    best_run, best_start = run, start
            else:
                run = 0
        if 60 <= best_run <= 200 and (best is None or best_run > best[2]):
            best = (best_start, y, best_run)
    return best


class FastGrab:
    """GDI BitBlt 直抓内存位图，单帧 ~1ms（PIL 的 ImageGrab 走 GDI+ 整屏合成要 80ms+）。"""

    def __init__(self):
        self.gdi32 = ctypes.windll.gdi32
        self.user32 = ctypes.windll.user32
        self.screen = self.user32.GetDC(0)
        self.mem = self.gdi32.CreateCompatibleDC(self.screen)

    def grab_row(self, x, y, w):
        """只抓一条 1px 高的横线，返回该行的 RGB 列表。"""
        bmp = self.gdi32.CreateCompatibleBitmap(self.screen, w, 1)
        old = self.gdi32.SelectObject(self.mem, bmp)
        # ⚠ 开局屏 DC 的 BitBlt 在 DWM 合成下可能读到「过期画面」（本机实测：切换后
        #   连续 72 帧都是旧位置，等采样结束才跳到新位置）。用 CAPTUREBLT 强制连层叠面一起抓。
        SRCCOPY = 0x00CC0020
        CAPTUREBLT = 0x40000000
        self.gdi32.BitBlt(self.mem, 0, 0, w, 1, self.screen, x, y, SRCCOPY | CAPTUREBLT)

        class BMI(ctypes.Structure):
            _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                        ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                        ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                        ("biClrImportant", wt.DWORD), ("rgb", wt.DWORD * 3)]

        bmi = BMI()
        bmi.biSize = 40
        bmi.biWidth = w
        bmi.biHeight = -1        # 负数 = 自上而下，省一次翻转
        bmi.biPlanes = 1
        bmi.biBitCount = 32
        bmi.biCompression = 0    # BI_RGB
        buf = ctypes.create_string_buffer(w * 4)
        self.gdi32.GetDIBits(self.mem, bmp, 0, 1, buf, ctypes.byref(bmi), 0)
        self.gdi32.SelectObject(self.mem, old)
        self.gdi32.DeleteObject(bmp)

        raw = buf.raw
        return [(raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4]) for i in range(w)]


def slide_span(img, y):
    """给定行，返回白块的 (left, right)（取最长连续白段）。"""
    px = img.load()
    w = img.size[0]
    run = 0
    start = 0
    best_run = 0
    best_start = 0
    for x in range(w):
        if is_white(px[x, y]):
            if run == 0:
                start = x
            run += 1
            if run > best_run:
                best_run, best_start = run, start
        else:
            run = 0
    return (best_start, best_start + best_run) if best_run else (None, None)


def main():
    os.makedirs(OUT, exist_ok=True)
    hwnd = find_window()
    if not hwnd:
        print("找不到主窗口（先用 --show dz 启动）")
        return 1
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.5)

    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    box = (r.left, r.top, r.right, r.bottom)
    print(f"窗口 rect={box}")

    img = ImageGrab.grab(bbox=box)
    img.save(os.path.join(OUT, "win.png"))

    # 分段控件在标题行：窗口顶部 0~140 的横带里找白块
    hit = find_slide_row(img, 50, 140)
    if not hit:
        print("顶部找不到白块（看 win.png）")
        return 1
    sl, sy, sw = hit
    print(f"白块：left={sl} right={sl + sw} row={sy} width={sw}")

    # 轨道：白块左右各有一个 2px 内缩 + 轨道 3px 内边距；四个标签等宽
    # 用「白块宽度 + 4」反推单标签宽，轨道 = 4 × 标签宽 + 6
    segw = sw + 4
    track_left = sl - 5
    print(f"推算轨道 left={track_left} 单段宽={segw} 总宽={segw * 4 + 6}")

    idx = LABELS.index(TARGET) if TARGET in LABELS else 3
    cx_img = track_left + segw * idx + segw / 2
    cy_img = sy
    cx, cy = box[0] + cx_img, box[1] + cy_img
    print(f"点击「{TARGET}」图内=({cx_img:.0f},{cy_img}) 屏幕=({cx:.0f},{cy:.0f})")

    from_left = sl

    # 只截「分段控件那一条横带」，ImageGrab 单次耗时降到 3~5ms，
    # 才能在 180ms 的动画窗口里取到 20+ 帧（整窗截图单帧要 150ms，必然漏掉中间态）。
    band = (box[0] + track_left - 8, box[1] + sy - 8,
            box[0] + track_left + segw * 4 + 14, box[1] + sy + 8)
    print(f"采样横带={band}")

    # 点击前先确保窗口在前台（探针刚跑过一次，前台可能被别的窗口占了）
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.4)

    rows = []
    band_left = band[0] - box[0]      # 横带左边界在窗口内的坐标
    grab = FastGrab()
    band_w = band[2] - band[0]
    scan_y = band[1] + 8              # 横带中线（屏幕坐标）

    def span_from_row(px):
        run = 0
        best_run = 0
        best_start = 0
        start = 0
        for i, p in enumerate(px):
            if p[0] >= 250 and p[1] >= 250 and p[2] >= 250:
                if run == 0:
                    start = i
                run += 1
                if run > best_run:
                    best_run, best_start = run, start
            else:
                run = 0
        return (best_start, best_start + best_run) if best_run >= 20 else (None, None)

    stop_flag = {"v": False}

    def grabber(t_start):
        while time.time() - t_start < COLLECT_S:
            px = grab.grab_row(band[0], scan_y, band_w)
            l, rr = span_from_row(px)
            rows.append((round((time.time() - t_start) * 1000),
                         None if l is None else l + band_left,
                         None if rr is None else rr + band_left))
            time.sleep(0.0005)

    import threading
    t0 = time.time()
    th = threading.Thread(target=grabber, args=(t0,), daemon=True)
    th.start()
    time.sleep(0.25)          # 先采一段基线（确认采样回路稳定）
    click(cx, cy)
    th.join()

    time.sleep(0.4)
    after = ImageGrab.grab(bbox=box)
    after.save(os.path.join(OUT, "after.png"))
    hit2 = find_slide_row(after, 50, 140)
    print(f"切换后白块：{hit2}")

    print("\n帧序列（毫秒, 白块左右边界·窗口内坐标）：")
    print(f"总采样 {len(rows)} 帧")
    prev = None
    for ms, l, rr in rows:
        mark = ""
        if prev is not None and l != prev:
            mark = "   <== 变化"
        prev = l
        print(f"  +{ms:4d}ms  left={l}  right={rr}{mark}")

    vals = sorted({l for _, l, _ in rows if l is not None})
    print(f"\n出现过的左边界：{vals}")
    end = vals[-1] if vals else from_left
    mids = [v for v in vals if from_left + 3 < v < end - 3]
    if len(vals) >= 3 and mids:
        print(f"PASS：存在 {len(mids)} 个中间态，白块是滑过去的（{mids}）")
        return 0
    print("FAIL：只有起点/终点，仍是硬切")
    return 2


if __name__ == "__main__":
    sys.exit(main())
