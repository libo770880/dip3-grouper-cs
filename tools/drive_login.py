# -*- coding: utf-8 -*-
"""驱动后台登录窗：真实键盘输入（过输入法）+ 回车，并把结果打印出来。
用法: python drive_login.py <text> [--ime-open] [--enter2]
"""
import ctypes, os, sys, time, pathlib
from ctypes import wintypes

u32 = ctypes.windll.user32
imm32 = ctypes.windll.imm32
k32 = ctypes.windll.kernel32

TMP = os.environ.get("TEMP", r"C:/Users/Administrator/AppData/Local/Temp")
TRACE = pathlib.Path(TMP) / "dip3-login-trace.log"
TITLE = "后台管理登录"

class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]

class _I(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]

class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", _I)]

def find_window(title=TITLE):
    found = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(h, _l):
        n = u32.GetWindowTextLengthW(h)
        buf = ctypes.create_unicode_buffer(n + 1)
        u32.GetWindowTextW(h, buf, n + 1)
        pid = wintypes.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if buf.value == title:
            found.append((h, pid.value, bool(u32.IsWindowVisible(h))))
        return True
    u32.EnumWindows(cb, 0)
    return found

def foreground(hwnd):
    """把窗口提到前台：先 attach 到当前前台线程的输入队列以绕开前台锁，再 SetForegroundWindow"""
    cur = k32.GetCurrentThreadId()
    fg = u32.GetForegroundWindow()
    fgt = u32.GetWindowThreadProcessId(fg, None) if fg else 0
    tgt = u32.GetWindowThreadProcessId(hwnd, None)
    u32.ShowWindow(hwnd, 5)          # SW_SHOW
    if fgt:
        u32.AttachThreadInput(cur, fgt, True)
    u32.AttachThreadInput(cur, tgt, True)
    u32.SetForegroundWindow(hwnd)
    u32.BringWindowToTop(hwnd)
    u32.AttachThreadInput(cur, tgt, False)
    if fgt:
        u32.AttachThreadInput(cur, fgt, False)
    time.sleep(0.35)
    return u32.GetForegroundWindow() == hwnd

def _send(vk, up):
    inp = INPUT(type=1, u=_I(ki=KEYBDINPUT(wVk=vk, wScan=0,
                 dwFlags=0x0002 if up else 0, time=0, dwExtraInfo=None)))
    u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))

def tap(vk):
    _send(vk, False); time.sleep(0.012); _send(vk, True); time.sleep(0.02)

def type_text(s):
    for ch in s:
        r = u32.VkKeyScanW(ord(ch))
        tap(r & 0xFF)

def ime_state(hwnd):
    imc = imm32.ImmGetContext(hwnd)
    open_ = imm32.ImmGetOpenStatus(imc) if imc else None
    conv = wintypes.DWORD(); sent = wintypes.DWORD()
    if imc:
        imm32.ImmGetConversionStatus(imc, ctypes.byref(conv), ctypes.byref(sent))
    layout = u32.GetKeyboardLayout(u32.GetWindowThreadProcessId(hwnd, None))
    return f"himc={imc} open={open_} conv=0x{conv.value:04x} layout=0x{layout & 0xFFFF:04x}"

def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    flags = [a for a in sys.argv[1:] if a.startswith("--")]
    text = args[0] if args else "admin123"

    hwnd = None
    for _ in range(60):
        w = find_window()
        if w:
            hwnd = w[0][0]
            break
        time.sleep(0.5)
    if not hwnd:
        print("!! 没找到登录窗口"); return 2

    print("登录窗口 hwnd =", hwnd)
    print("输入法状态(打字前):", ime_state(hwnd))

    if "--ime-toggle" in flags:
        # Ctrl+Space = 中英切换；用真实按键让输入法在当前窗口进入中文态
        CTRL, SPACE = 0x11, 0x20
        _send(CTRL, False); time.sleep(0.05)
        tap(SPACE); time.sleep(0.05); _send(CTRL, True)
        time.sleep(0.5)
        print("已发送 Ctrl+Space 切换输入法:", ime_state(hwnd))

    if "--ime-open" in flags:
        imc = imm32.ImmGetContext(hwnd)
        imm32.ImmSetOpenStatus(imc, True)
        imm32.ImmSetConversionStatus(imc, 0x0001, 0x0000)   # IME_CMODE_NATIVE
        time.sleep(0.3)
        print("已强制打开输入法(中文模式):", ime_state(hwnd))

    if not foreground(hwnd):
        print("!! 抢前台失败，放弃注入（避免打到别的程序上）"); return 3
    print("前台已确认，开始输入…")
    type_text(text)
    time.sleep(0.15)
    if "--tab" in flags:
        # 把焦点从密码框挪走（Tab），验证"焦点不在输入框时回车也能提交"
        tap(0x09)
        time.sleep(0.2)
        print("已按 Tab 移位焦点")
    print("→ 回车 #1")
    tap(0x0D)
    time.sleep(0.6)
    alive1 = bool(find_window())
    print("回车 #1 后窗口还在:", alive1)

    if "--enter2" in flags and alive1:
        print("→ 回车 #2")
        tap(0x0D)
        time.sleep(0.6)
        print("回车 #2 后窗口还在:", bool(find_window()))

    print("输入法状态(打字后):", ime_state(hwnd))
    print("---- trace ----")
    try:
        print(TRACE.read_text(encoding="utf-8", errors="replace"))
    except Exception as e:
        print("(读取 trace 失败)", e)
    return 0

sys.exit(main())
