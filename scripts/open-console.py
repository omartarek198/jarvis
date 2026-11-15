"""Open the JARVIS console as a chromeless fullscreen app on the portrait (JARVIS) monitor.

Finds the portrait display at runtime (height > width), launches Edge in --app mode
(no tabs/address bar) positioned there, fullscreen. Falls back to the default browser
on the primary monitor if no portrait display exists.
"""
import ctypes
import subprocess
from ctypes import wintypes

URL = "http://localhost:5588"
user32 = ctypes.windll.user32


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def monitors():
    found = []
    proc_t = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.POINTER(RECT), ctypes.c_void_p)

    def cb(hmon, hdc, rect, data):
        r = rect.contents
        found.append((r.left, r.top, r.right - r.left, r.bottom - r.top))
        return 1

    user32.EnumDisplayMonitors(None, None, proc_t(cb), None)
    return found


portrait = next(((x, y, w, h) for x, y, w, h in monitors() if h > w), None)
if portrait:
    x, y, w, h = portrait
    subprocess.Popen([
        "cmd", "/c", "start", "", "msedge",
        f"--app={URL}",
        f"--window-position={x},{y}",
        f"--window-size={w},{h}",
        "--start-fullscreen",
        "--autoplay-policy=no-user-gesture-required",
    ])
    print(f"console opened fullscreen on portrait monitor at ({x},{y}) {w}x{h}")
else:
    subprocess.Popen(["cmd", "/c", "start", "", URL])
    print("no portrait monitor found — opened in the default browser")
