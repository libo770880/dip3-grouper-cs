"""分段白块滑动取证（诊断脚本，不参与产品）。

为什么需要它：180ms 的滑动用「全窗口截图」采不到中间态 —— 一次全窗 Grab 就要
80~100ms，两帧之间白块早已就位。这里只截轨道那一条 300×40 的细带，一帧 ~15ms，
60fps 的补间能采到 3~8 个中间位置，足以证明「是滑过去的」还是「跳过去的」。

用法：python seg_slide_probe.py <输出目录> [点击偏移x 点击偏移y]
   点击偏移相对窗口左上角。默认点「现行项目」（轨道最左项）。
自动定位：在窗口上半部找纯白宽块（白块），取其纵向中心行作为截图条带。
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

from PIL import Image, ImageGrab

TITLE = "DIP 3.0 分组器"
u = ctypes.windll.user32
u.SetProcessDPIAware()


def hwnd():
    h = u.FindWindowW(None, TITLE)
    if not h:
        print("NO WINDOW")
        sys.exit(1)
    return h


def rect(h):
    r = wt.RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def click(x, y):
    u.SetCursorPos(x, y)
    time.sleep(0.03)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    u.mouse_event(0x0004, 0, 0, 0, 0)


def find_track(im, w, h):
    """找白块所在行：整条轨道是浅灰蓝底（约 238,240,250），白块是其中唯一的长白段。

    判据：某行里「白段」左右两侧紧邻都是轨道底色，且白段宽 70~100px（150% DPI 下的胶囊宽）。
    表格行、对话框、卡片也是白底，但它们左右没有轨道底色，会被这条排除掉。
    """
    def is_track(c):
        return 228 <= c[0] <= 246 and 232 <= c[1] <= 250 and 240 <= c[2] <= 255

    def is_white(c):
        return c[0] >= 250 and c[1] >= 250 and c[2] >= 250

    cands = []
    for y in range(20, min(h, int(h * 0.55))):
        run = 0
        st = None
        for x in range(w):
            if is_white(im.getpixel((x, y))):
                if st is None:
                    st = x
                run += 1
            else:
                if 55 <= run <= 100:
                    lx, rx = st - 2, x + 2
                    if 0 <= lx < w and 0 <= rx < w:
                        # 左右两侧紧邻都必须是轨道底色（白块与轨道只隔 2px 内边距）
                        if is_track(im.getpixel((lx, y))) and is_track(im.getpixel((rx, y))):
                            cands.append((run, st, x - 1, y))
                run = 0
                st = None
        if 55 <= run <= 100:
            lx = st - 2
            if 0 <= lx < w and is_track(im.getpixel((lx, y))):
                cands.append((run, st, w - 1, y))
    if not cands:
        return None
    # 同一白块会横跨十几行像素；取行数最集中的那一簇（=胶囊纵向主体），再取该簇中间行
    rows = sorted(c[3] for c in cands)
    clusters, cur = [], [rows[0]]
    for y in rows[1:]:
        if y - cur[-1] <= 2:
            cur.append(y)
        else:
            clusters.append(cur)
            cur = [y]
    clusters.append(cur)
    main = max(clusters, key=len)
    mid = (main[0] + main[-1]) // 2
    best = min(cands, key=lambda c: abs(c[3] - mid))
    return best


def refine(im, rx0, rx1, ry):
    """像素级校准白块边界：右边界取「纯白→非白」最后一列，左边界取连续区间起点。

    分段控件白块宽 76~88px（150% 下）、轨道里一共 4 项、间距 4px，
    所以白块右边界 +6 一定是轨道浅灰蓝底色。用这个特征再核一遍，
    避免把「白底按钮/表格」误当成白块。
    """
    w, h = im.size
    for dy in (-2, 0, 2):
        y = min(max(0, ry + dy), h - 1)
        # 从右边界往右扫，确认紧邻是浅灰蓝
        rt = min(rx1 + 6, w - 1)
        c = im.getpixel((rt, y))
        if 225 <= c[0] <= 245 and 230 <= c[1] <= 250 and 238 <= c[2] <= 255:
            return True
    return False


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else ".tmp/segprobe"
    os.makedirs(out, exist_ok=True)
    h = hwnd()
    u.SetForegroundWindow(h)
    u.ShowWindow(h, 9)
    time.sleep(0.8)
    L, T, W, H = rect(h)
    print(f"rect {L},{T} {W}x{H}")

    full = ImageGrab.grab(bbox=(L, T, L + W, T + H), all_screens=True).convert("RGB")
    track = find_track(full, W, H)
    if not track:
        print("FAIL 找不到白块，无法定位轨道")
        sys.exit(2)
    run, x0, x1, ty = track
    print(f"白块候选 x={x0}..{x1} (宽 {run}) y={ty}  相邻底色校验={refine(full, x0, x1, ty)}")
    # 白块中心即当前选中项中心；轨道向左再留 260px，覆盖整条轨道
    band_x = max(0, x0 - 260)
    band_y = max(0, ty - 20)
    box = (L + band_x, T + band_y, L + band_x + 340, T + band_y + 40)
    print(f"截图条带 窗口内 x={band_x}..{band_x + 340} y={band_y}..{band_y + 40}")

    tx = int(sys.argv[2]) if len(sys.argv) > 2 else (x0 + x1) // 2
    tdy = int(sys.argv[3]) if len(sys.argv) > 3 else ty
    print(f"点击 ({tx},{tdy}) 相对窗口")
    u.SetCursorPos(L + tx, T + tdy)
    time.sleep(0.05)
    t0 = time.time()
    u.mouse_event(0x0002, 0, 0, 0, 0)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    u.SetCursorPos(L + W - 250, T + H - 160)   # 移开鼠标，免得悬停高亮干扰
    n = 0
    while time.time() - t0 < 3.0:
        ImageGrab.grab(bbox=box, all_screens=True).save(os.path.join(out, f"f{n:03d}.png"))
        n += 1
    print(f"frames {n}  平均 {3000 / n:.0f}ms/帧")


if __name__ == "__main__":
    main()
