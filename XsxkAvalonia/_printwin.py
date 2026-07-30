# -*- coding: utf-8 -*-
"""PrintWindow 截图：即使窗口被遮挡也能拿到画面"""
import ctypes, sys
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# DPI 感知，拿到真实像素尺寸
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

title = "南信大选课助手 v2.1"
hwnd = user32.FindWindowW(None, title)
if not hwnd:
    # 退而求其次：枚举找包含关键字的窗口
    found = []
    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_cb(h, l):
        if user32.IsWindowVisible(h):
            buf = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(h, buf, 256)
            if "南信大" in buf.value:
                found.append((h, buf.value))
        return True
    user32.EnumWindows(enum_cb, 0)
    if not found:
        print("NOT FOUND"); sys.exit(1)
    hwnd = found[0][0]
    print("matched:", found[0][1])

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
w, h = rect.right - rect.left, rect.bottom - rect.top
print("size:", w, h)

hdc_screen = user32.GetDC(0)
hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
gdi32.SelectObject(hdc_mem, hbmp)
# PW_RENDERFULLCONTENT = 2
ok = user32.PrintWindow(hwnd, hdc_mem, 2)
print("PrintWindow:", ok)

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]
class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]

bmi = BITMAPINFO()
bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bmi.bmiHeader.biWidth = w
bmi.bmiHeader.biHeight = -h  # top-down
bmi.bmiHeader.biPlanes = 1
bmi.bmiHeader.biBitCount = 32
bmi.bmiHeader.biCompression = 0
buf = (ctypes.c_byte * (w * h * 4))()
gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bmi), 0)
gdi32.DeleteObject(hbmp)
gdi32.DeleteDC(hdc_mem)
user32.ReleaseDC(0, hdc_screen)

from PIL import Image
img = Image.frombuffer("RGBA", (w, h), bytes(buf), "raw", "BGRA", 0, 1)
out = sys.argv[1] if len(sys.argv) > 1 else "_shot.png"
img.save(out)
print("saved:", out)
