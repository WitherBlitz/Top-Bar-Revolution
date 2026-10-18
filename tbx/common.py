"""Shared, UI-free pieces of TopBar Settings: paths, the widget schema, color
helpers, Win32 icon extraction, memory stats and config I/O."""

import base64
import copy
import ctypes
import datetime
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tkinter as tk
import zlib
import tkinter.font as tkfont
from ctypes import wintypes
from tkinter import colorchooser, filedialog, messagebox, ttk

APP_DIR = os.path.join(os.environ.get("APPDATA", ""), "TopBar")
CONFIG_PATH = os.path.join(APP_DIR, "config.json")
BACKUP_PATH = os.path.join(APP_DIR, "config.backup.json")
EXE_PATH = os.path.join(APP_DIR, "TopBar.exe")


def resource(name):
    """A bundled file: next to the script, or inside a PyInstaller build."""
    here = os.path.dirname(os.path.abspath(__file__))
    bases = [getattr(sys, "_MEIPASS", None), here, os.path.dirname(here)]
    for base in filter(None, bases):
        for candidate in (os.path.join(base, name),
                          os.path.join(base, "assets", name)):
            if os.path.exists(candidate):
                return candidate
    return None

# ----------------------------------------------------------- widget schema ---
# Field types: bool, int, text, color, path, file, choice ([(value, label)])
# Defaults mirror the ones baked into TopBar.exe.

NAMES = {
    "TopBar.Widgets.Shortcut": "Shortcut",
    "TopBar.Widgets.LineSpacer": "Separator line",
    "TopBar.Widgets.CustomText": "Custom text",
    "TopBar.Widgets.RAMUsageText": "RAM usage (text)",
    "TopBar.Widgets.RAMUsageBar": "RAM usage (bar)",
    "TopBar.Widgets.CPULoad": "CPU load (graph)",
    "TopBar.Widgets.Clock": "Clock",
    "TopBar.Widgets.TextFromFile": "Text from file",
    "TopBarExtras.Widgets.CPULoad": "CPU load (adjustable rate)",
    "TopBarExtras.Widgets.RAMUsage": "RAM usage (% and GB)",
    "TopBarExtras.Widgets.GPUUsage": "GPU usage (bar)",
    "TopBarExtras.Widgets.RemoteStat": "Network PC",
}

# Stock CPULoad hardcodes a 1s timer; the extension in extensions\ adds an
# "interval" setting. Both draw the same graph, so treat them alike.
CPU_TYPES = ("TopBar.Widgets.CPULoad", "TopBarExtras.Widgets.CPULoad")

# Matches the extension widget: {used} {total} {free} {percent}, each with an
# optional .NET numeric format after a colon.
RAM_FORMAT = "{percent:0} %  {used:0.0}/{total:0} GB"
RAM_TOKEN = re.compile(r"\{(\w+)(?::([^}]*))?\}")


def format_ram(fmt, percent, used, total, free):
    values = {"used": used, "total": total, "free": free,
              "percent": float(percent)}

    def sub(m):
        v = values.get(m.group(1).lower())
        if v is None:
            return m.group(0)
        spec = m.group(2) or "0.0"
        decimals = len(spec.split(".")[1]) if "." in spec else 0
        return "%.*f" % (decimals, v)

    return RAM_TOKEN.sub(sub, fmt)


def format_values(fmt, values, default_spec="0"):
    """The extension widgets' {name:0.0} templates, for any set of values."""
    def sub(m):
        v = values.get(m.group(1).lower())
        if v is None:
            return m.group(0)
        spec = m.group(2) or default_spec
        decimals = len(spec.split(".")[1]) if "." in spec else 0
        return "%.*f" % (decimals, v)

    return RAM_TOKEN.sub(sub, fmt)

SCHEMA = {
    "TopBar.Widgets.Shortcut": [
        ("title", "text", "", "Tooltip / title"),
        ("command", "path",
         os.path.join(os.environ.get("WINDIR", "C:\\Windows"), "explorer.exe"),
         "Application"),
        ("icon", "file", "", "Icon override"),
    ],
    "TopBar.Widgets.LineSpacer": [
        ("color", "color", "#888888", "Color"),
        ("width", "int", 1, "Width"),
        ("height", "int", 10, "Height"),
    ],
    "TopBar.Widgets.CustomText": [
        ("text", "text", "Text ", "Text"),
        ("color", "color", "#BBBBBB", "Color"),
    ],
    "TopBar.Widgets.RAMUsageText": [
        ("color", "color", "#BBBBBB", "Color"),
    ],
    "TopBar.Widgets.RAMUsageBar": [
        ("color", "color", "from-percentage", "Fill color", ["from-percentage"]),
        ("background-color", "color", "#33BBBBBB", "Track color"),
        ("width", "int", 60, "Width"),
        ("height", "int", 10, "Height"),
        ("radius", "int", 2, "Corner radius"),
    ],
    "TopBar.Widgets.CPULoad": [
        ("color", "color", "rainbow", "Color", ["rainbow", "from-percentage"]),
        ("line-width", "int", 5, "Bar width"),
        ("radius", "int", 2, "Corner radius"),
    ],
    "TopBarExtras.Widgets.GPUUsage": [
        ("label", "text", "auto", "Name"),
        ("format", "text", "{percent:0}%", "Format"),
        ("vram", "bool", False, "Show VRAM"),
        ("bar", "bool", True, "Show the bar"),
        ("value-position", "choice", "before", "Percentage",
         [("before", "Before the bar"), ("after", "After the bar")]),
        ("color", "color", "#BBBBBB", "Text color"),
        ("bar-color", "color", "from-percentage", "Bar color", ["from-percentage"]),
        ("background-color", "color", "#33BBBBBB", "Track color"),
        ("width", "int", 60, "Bar width"),
        ("height", "int", 10, "Bar height"),
        ("radius", "int", 2, "Corner radius"),
        ("interval", "int", 1000, "Refresh every (ms)"),
    ],
    # Added from the Network page, which fills in "pc" and the rest.
    "TopBarExtras.Widgets.RemoteStat": [
        ("pc", "text", "", "PC (computer name)"),
        ("metric", "choice", "gpu", "Shows", [("gpu", "GPU"), ("cpu", "CPU"), ("ram", "RAM")]),
        ("gpu", "int", 0, "GPU number (0 = first)"),
        ("label", "text", "auto", "Name"),
        ("show-pc", "bool", True, "Show the PC's name"),
        ("vram", "bool", False, "Show VRAM (GPUs)"),
        ("bar", "bool", True, "Show the bar"),
        ("format", "text", "{percent:0}%", "Format"),
        ("value-position", "choice", "before", "Reading",
         [("before", "Before the bar"), ("after", "After the bar")]),
        ("offline", "choice", "wake", "While that PC is off",
         [("wake", "Wake button"), ("hide", "Hide it"), ("show", "Show “offline”")]),
        ("color", "color", "#BBBBBB", "Text color"),
        ("bar-color", "color", "from-percentage", "Bar color", ["from-percentage"]),
        ("background-color", "color", "#33BBBBBB", "Track color"),
        ("width", "int", 60, "Bar width"),
        ("height", "int", 10, "Bar height"),
        ("radius", "int", 2, "Corner radius"),
    ],
    "TopBarExtras.Widgets.RAMUsage": [
        ("format", "text", RAM_FORMAT, "Format"),
        ("color", "color", "#BBBBBB", "Color"),
        ("interval", "int", 1000, "Refresh every (ms)"),
    ],
    "TopBarExtras.Widgets.CPULoad": [
        ("color", "color", "rainbow", "Color", ["rainbow", "from-percentage"]),
        ("interval", "int", 3000, "Refresh every (ms)"),
        ("line-width", "int", 5, "Bar width"),
        ("radius", "int", 0, "Corner radius"),
    ],
    "TopBar.Widgets.Clock": [
        ("date", "bool", True, "Show date"),
        ("seconds", "bool", False, "Show seconds"),
        ("text-color", "color", "#BBBBBB", "Color"),
    ],
    "TopBar.Widgets.TextFromFile": [
        ("file", "file", "", "File"),
        ("color", "color", "#BBBBBB", "Color"),
    ],
}

# ------------------------------------------------------------ color helpers ---

NAMED = {
    "white": (255, 255, 255), "black": (0, 0, 0), "red": (255, 0, 0),
    "lime": (0, 255, 0), "blue": (0, 0, 255), "yellow": (255, 255, 0),
    "cyan": (0, 255, 255), "magenta": (255, 0, 255), "gray": (128, 128, 128),
    "orange": (255, 165, 0), "green": (0, 128, 0), "silver": (192, 192, 192),
}


def parse_color(value, fallback=(255, 187, 187, 187)):
    """Parse a WPF color string into (a, r, g, b)."""
    if not isinstance(value, str):
        return fallback
    s = value.strip()
    if not s:
        return fallback
    if not s.startswith("#"):
        rgb = NAMED.get(s.lower())
        return (255,) + rgb if rgb else fallback
    h = s[1:]
    try:
        if len(h) == 3:  # #RGB
            return (255,) + tuple(int(c * 2, 16) for c in h)
        if len(h) == 4:  # #ARGB
            return tuple(int(c * 2, 16) for c in h)
        if len(h) == 6:  # #RRGGBB
            return (255, int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))
        if len(h) == 8:  # #AARRGGBB
            return (int(h[0:2], 16), int(h[2:4], 16),
                    int(h[4:6], 16), int(h[6:8], 16))
    except ValueError:
        pass
    return fallback


def format_color(a, r, g, b):
    if a >= 255:
        return "#%02X%02X%02X" % (r, g, b)
    return "#%02X%02X%02X%02X" % (a, r, g, b)


def blend(fg_argb, bg_rgb):
    """Composite an (a, r, g, b) color over an opaque (r, g, b) background."""
    a, r, g, b = fg_argb
    t = a / 255.0
    return tuple(int(round(f * t + k * (1 - t))) for f, k in zip((r, g, b), bg_rgb))


def hexs(rgb):
    return "#%02X%02X%02X" % rgb


def hsl_rgb(h, s, l):
    """h in degrees, s/l in 0..1."""
    c = (1 - abs(2 * l - 1)) * s
    x = c * (1 - abs((h / 60.0) % 2 - 1))
    m = l - c / 2
    seg = int(h // 60) % 6
    r, g, b = [(c, x, 0), (x, c, 0), (0, c, x),
               (0, x, c), (x, 0, c), (c, 0, x)][seg]
    return tuple(int(round((v + m) * 255)) for v in (r, g, b))


def percentage_color(pct):
    """Green at 0%, red at 100% - TopBar's "from-percentage" mode."""
    return hsl_rgb(max(0.0, 120.0 * (1.0 - pct)), 0.75, 0.55)


# Stand-in wallpaper the previews composite the translucent bar over.
DESKTOP = (86, 96, 120)


def bar_bg_of(cfg):
    c = cfg["settings"]["color"]
    argb = (int(c.get("alpha", 100)), int(c.get("red", 0)),
            int(c.get("green", 0)), int(c.get("blue", 0)))
    return blend(argb, DESKTOP)


# ------------------------------------------------- icon extraction (Win32) ---
# TopBar draws a Shortcut with Icon.ExtractAssociatedIcon(command) whenever its
# "icon" setting is empty. We mirror that with ExtractIconEx so the preview
# shows the icon the bar will really use.


class ICONINFO(ctypes.Structure):
    _fields_ = [("fIcon", wintypes.BOOL), ("xHotspot", wintypes.DWORD),
                ("yHotspot", wintypes.DWORD), ("hbmMask", wintypes.HBITMAP),
                ("hbmColor", wintypes.HBITMAP)]


class BITMAP(ctypes.Structure):
    _fields_ = [("bmType", wintypes.LONG), ("bmWidth", wintypes.LONG),
                ("bmHeight", wintypes.LONG), ("bmWidthBytes", wintypes.LONG),
                ("bmPlanes", wintypes.WORD), ("bmBitsPixel", wintypes.WORD),
                ("bmBits", ctypes.c_void_p)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD),
                ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG),
                ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER),
                ("bmiColors", wintypes.DWORD * 3)]


class SHFILEINFOW(ctypes.Structure):
    _fields_ = [("hIcon", wintypes.HICON), ("iIcon", ctypes.c_int),
                ("dwAttributes", wintypes.DWORD),
                ("szDisplayName", wintypes.WCHAR * 260),
                ("szTypeName", wintypes.WCHAR * 80)]


# Explicit prototypes: without them ctypes marshals HANDLEs as C ints and
# overflows on real 64-bit handle values.
_user32 = ctypes.WinDLL("user32", use_last_error=True)
_gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
_shell32 = ctypes.WinDLL("shell32", use_last_error=True)

_user32.GetIconInfo.argtypes = [wintypes.HICON, ctypes.POINTER(ICONINFO)]
_user32.GetIconInfo.restype = wintypes.BOOL
_user32.DestroyIcon.argtypes = [wintypes.HICON]
_user32.DestroyIcon.restype = wintypes.BOOL
_user32.GetDC.argtypes = [wintypes.HWND]
_user32.GetDC.restype = wintypes.HDC
_user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
_user32.ReleaseDC.restype = ctypes.c_int
_gdi32.GetObjectW.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p]
_gdi32.GetObjectW.restype = ctypes.c_int
_gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
_gdi32.DeleteObject.restype = wintypes.BOOL
_gdi32.GetDIBits.argtypes = [wintypes.HDC, wintypes.HBITMAP, wintypes.UINT,
                             wintypes.UINT, ctypes.c_void_p,
                             ctypes.POINTER(BITMAPINFO), wintypes.UINT]
_gdi32.GetDIBits.restype = ctypes.c_int
_shell32.ExtractIconExW.argtypes = [wintypes.LPCWSTR, ctypes.c_int,
                                    ctypes.POINTER(wintypes.HICON),
                                    ctypes.POINTER(wintypes.HICON),
                                    wintypes.UINT]
_shell32.ExtractIconExW.restype = wintypes.UINT
_shell32.SHGetFileInfoW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD,
                                    ctypes.POINTER(SHFILEINFOW),
                                    wintypes.UINT, wintypes.UINT]
_shell32.SHGetFileInfoW.restype = ctypes.c_void_p


def _hicon_for(path):
    """Best-effort HICON for a file (exe, dll, ico, lnk, document)."""
    large, small = wintypes.HICON(), wintypes.HICON()
    try:
        n = _shell32.ExtractIconExW(path, 0, ctypes.byref(large),
                                    ctypes.byref(small), 1)
    except OSError:
        n = 0
    if n and large:
        if small:
            _user32.DestroyIcon(small)
        return large
    if n and small:
        return small
    info = SHFILEINFOW()
    if _shell32.SHGetFileInfoW(path, 0, ctypes.byref(info),
                               ctypes.sizeof(info), 0x100):  # SHGFI_ICON
        if info.hIcon:
            return wintypes.HICON(info.hIcon)
    return None


def icon_rgba(path):
    """Return (width, height, RGBA bytearray) for a file's icon, else None."""
    if not path or not os.path.isfile(path):
        return None
    hicon = _hicon_for(path)
    if not hicon:
        return None
    ii = ICONINFO()
    hdc = None
    try:
        if not _user32.GetIconInfo(hicon, ctypes.byref(ii)):
            return None
        bm = BITMAP()
        if not _gdi32.GetObjectW(ii.hbmColor, ctypes.sizeof(bm),
                                 ctypes.byref(bm)):
            return None
        w, h = int(bm.bmWidth), int(bm.bmHeight)
        if w <= 0 or h <= 0 or w * h > 512 * 512:
            return None
        hdc = _user32.GetDC(None)
        bmi = BITMAPINFO()
        bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bmi.bmiHeader.biWidth = w
        bmi.bmiHeader.biHeight = -h  # top-down
        bmi.bmiHeader.biPlanes = 1
        bmi.bmiHeader.biBitCount = 32
        bmi.bmiHeader.biCompression = 0  # BI_RGB
        buf = (ctypes.c_ubyte * (w * h * 4))()
        if not _gdi32.GetDIBits(hdc, ii.hbmColor, 0, h, buf,
                                ctypes.byref(bmi), 0):
            return None
        px = bytearray(w * h * 4)
        has_alpha = False
        for i in range(w * h):
            b, g, r, a = buf[i * 4:i * 4 + 4]
            if a:
                has_alpha = True
            px[i * 4], px[i * 4 + 1] = r, g
            px[i * 4 + 2], px[i * 4 + 3] = b, a
        if not has_alpha and ii.hbmMask:
            # Legacy icon: derive alpha from the AND mask (1 = transparent).
            mbuf = (ctypes.c_ubyte * (w * h * 4))()
            if _gdi32.GetDIBits(hdc, ii.hbmMask, 0, h, mbuf,
                                ctypes.byref(bmi), 0):
                for i in range(w * h):
                    px[i * 4 + 3] = 0 if mbuf[i * 4] else 255
            else:
                for i in range(w * h):
                    px[i * 4 + 3] = 255
        return w, h, px
    except OSError:
        return None
    finally:
        if hdc:
            _user32.ReleaseDC(None, hdc)
        for hbm in (ii.hbmColor, ii.hbmMask):
            if hbm:
                _gdi32.DeleteObject(hbm)
        _user32.DestroyIcon(hicon)


def resize_rgba(px, w, h, size):
    """Box-filter down to size x size, averaging in premultiplied space."""
    out = bytearray(size * size * 4)
    for y in range(size):
        y0 = y * h // size
        y1 = max(y0 + 1, (y + 1) * h // size)
        for x in range(size):
            x0 = x * w // size
            x1 = max(x0 + 1, (x + 1) * w // size)
            sr = sg = sb = sa = n = 0
            for yy in range(y0, y1):
                row = yy * w * 4
                for xx in range(x0, x1):
                    i = row + xx * 4
                    a = px[i + 3]
                    sr += px[i] * a
                    sg += px[i + 1] * a
                    sb += px[i + 2] * a
                    sa += a
                    n += 1
            o = (y * size + x) * 4
            if sa:
                out[o] = min(255, sr // sa)
                out[o + 1] = min(255, sg // sa)
                out[o + 2] = min(255, sb // sa)
            out[o + 3] = sa // n if n else 0
    return out


def rgba_png(px, w, h):
    """Encode RGBA bytes as a PNG. Tk 8.6 only takes base64 for GIF and PNG,
    and PNG keeps the alpha channel so icons composite over whatever is
    behind them."""
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)  # filter type: none
        raw += px[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
            + chunk(b"IEND", b""))


def rgba_photo(px, size):
    data = base64.b64encode(rgba_png(px, size, size)).decode("ascii")
    return tk.PhotoImage(data=data)


def find_main_exe(folder):
    """Pick the launchable exe in a folder - best match on the folder name."""
    try:
        exes = [e for e in os.listdir(folder)
                if e.lower().endswith(".exe")
                and os.path.isfile(os.path.join(folder, e))]
    except OSError:
        return None
    if not exes:
        return None
    skip = ("unins", "setup", "install", "update", "crash", "helper", "vcredist")
    good = [e for e in exes if not e.lower().startswith(skip)] or exes
    stem = os.path.basename(folder.rstrip("\\/")).lower()
    for e in good:
        if os.path.splitext(e)[0].lower() == stem:
            return os.path.join(folder, e)
    for e in good:
        if stem in os.path.splitext(e)[0].lower():
            return os.path.join(folder, e)
    good.sort(key=lambda e: os.path.getsize(os.path.join(folder, e)),
              reverse=True)
    return os.path.join(folder, good[0])


# ------------------------------------------------------------- system probe ---

class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong),
                ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]


def ram_stats():
    """(percent, used GiB, total GiB, free GiB)."""
    try:
        st = MEMORYSTATUSEX()
        st.dwLength = ctypes.sizeof(st)
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(st))
        gb = float(1 << 30)
        total = st.ullTotalPhys / gb
        free = st.ullAvailPhys / gb
        return int(st.dwMemoryLoad), total - free, total, free
    except Exception:
        return 42, 27.0, 64.0, 37.0


def ram_percent():
    return ram_stats()[0]


def short_cpu(name):
    """'AMD Ryzen 7 5700X3D 8-Core Processor' -> 'Ryzen 7 5700X3D': the brand
    and the filler go, the way the bar's CPU label has it. The engine does the
    same for what it shares on the network."""
    s = re.sub(r"\((R|TM)\)", "", name, flags=re.I)
    s = re.sub(r"\s+(w/|with)\s+Radeon.*$", "", s, flags=re.I)
    s = re.sub(r"@.*$", "", s)
    s = re.sub(r"\b\d+(st|nd|rd|th)\s+Gen\b", "", s, flags=re.I)
    s = re.sub(r"\b(AMD|Intel|Core|Processor|CPU|\d+-Core)\b", "", s, flags=re.I)
    return re.sub(r"\s+", " ", s).strip() or name.strip()


def cpu_short_name():
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r"HARDWARE\DESCRIPTION\System\CentralProcessor\0") as k:
            return short_cpu(str(winreg.QueryValueEx(k, "ProcessorNameString")[0]))
    except OSError:
        return ""


# -------------------------------------------------------------- config i/o ---

DEFAULT_CONFIG = {
    "config-version": 1,
    "settings": {
        "use-blur": True,
        "color": {"red": 0, "green": 0, "blue": 0, "alpha": 100},
        "current-window": {"color": "#ffffff"},
    },
    "widgets": [],
}


def config_mtime():
    try:
        return os.path.getmtime(CONFIG_PATH)
    except OSError:
        return None


def load_config():
    with open(CONFIG_PATH, "r", encoding="utf-8-sig") as fh:
        cfg = json.load(fh)
    cfg.setdefault("config-version", 1)
    s = cfg.setdefault("settings", {})
    s.setdefault("use-blur", True)
    c = s.setdefault("color", {})
    for k, v in (("red", 0), ("green", 0), ("blue", 0), ("alpha", 100)):
        c.setdefault(k, v)
    s.setdefault("current-window", {}).setdefault("color", "#ffffff")
    cfg.setdefault("widgets", [])
    for w in cfg["widgets"]:
        w.setdefault("settings", {})
    return cfg




def save_config(cfg):
    """Write config.json atomically.

    The engine inside TopBar watches this file and applies changes the moment
    it sees them, so it must never catch a half-written file: write a sibling
    temp file and swap it into place in one step.
    """
    os.makedirs(APP_DIR, exist_ok=True)
    if os.path.isfile(CONFIG_PATH) and not os.path.isfile(BACKUP_PATH):
        shutil.copy2(CONFIG_PATH, BACKUP_PATH)
    tmp = CONFIG_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(cfg, fh, indent=2)
        fh.write("\n")
    os.replace(tmp, CONFIG_PATH)


def topbar_running():
    try:
        out = subprocess.run(["tasklist", "/NH", "/FI", "IMAGENAME eq TopBar.exe"],
                             capture_output=True, text=True,
                             creationflags=subprocess.CREATE_NO_WINDOW).stdout
        return "TopBar.exe" in out
    except OSError:
        return False


def restart_topbar():
    subprocess.run(["taskkill", "/IM", "TopBar.exe", "/F"], capture_output=True,
                   creationflags=subprocess.CREATE_NO_WINDOW)
    if os.path.isfile(EXE_PATH):
        subprocess.Popen([EXE_PATH], cwd=APP_DIR,
                         creationflags=subprocess.DETACHED_PROCESS
                         | subprocess.CREATE_NEW_PROCESS_GROUP)
