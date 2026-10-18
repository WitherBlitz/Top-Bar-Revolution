"""Themes, the engine's "extras" block, and moving widgets between layouts.

A theme sets three things at once:
  * TopBar's own bar settings (background color, blur, window-title color)
  * the colors of the widgets on the bar
  * the "extras" block the in-process engine reads: font, bar height,
    backdrop, pills and the classic / split layout

The classic themes leave extras at their defaults, which the engine treats as
"stock TopBar" - so picking one gives back the original look exactly.
"""

import copy
import json
import os

from .common import APP_DIR, CPU_TYPES, cpu_short_name

ENGINE = "TopBarExtras.Widgets.Engine"
GROUP = "TopBarExtras.Widgets.Group"
GPU = "TopBarExtras.Widgets.GPUUsage"
REMOTE = "TopBarExtras.Widgets.RemoteStat"
MARKERS = (ENGINE, GROUP)
DLL_PATH = os.path.join(APP_DIR, "extensions", "TopBarExtras.dll")

# Defaults reproduce stock TopBar; every theme starts from these.
EXTRAS_DEFAULT = {
    "font": {"family": "", "size": 0, "weight": ""},
    "height": 18,
    "title": {"show": True},
    "backdrop": {"mode": "stock", "tint": "#101010", "opacity": 45},
    "layout": "classic",
    "pills": {"enabled": False, "fill": "#B3161616", "border": "#1FFFFFFF",
              "radius": 99, "height": 22, "padding": 12, "gap": 8,
              "spacing": 12, "edge": 6},
    # Clicking the clock drops down a calendar with a stopwatch and timer.
    "clock-panel": {"enabled": True, "week-starts": "sunday"},
    # Sharing this PC's usage with the other PCs on the network, and showing
    # theirs (the engine's Lan.cs). "known" - PCs already seen - is added as
    # they turn up.
    "lan": {"share": True, "listen": True, "auto-add": True, "name": ""},
}

# What a theme decides. Everything else in "extras" - the clock panel, the
# network settings, the saved classic order - is the user's and survives a
# theme change.
THEME_KEYS = ("font", "height", "title", "backdrop", "layout", "pills")

TEXT_TYPES = ("TopBar.Widgets.CustomText", "TopBar.Widgets.RAMUsageText",
              "TopBar.Widgets.TextFromFile", "TopBarExtras.Widgets.RAMUsage")

MONO = {"family": "JetBrains Mono", "size": 11, "weight": ""}


def _theme(id_, name, sub, stock, widgets, extras=None, mock=None):
    x = copy.deepcopy(EXTRAS_DEFAULT)
    for k, v in (extras or {}).items():
        if isinstance(v, dict):
            x[k].update(v)
        else:
            x[k] = v
    return {"id": id_, "name": name, "sub": sub, "stock": stock,
            "widgets": widgets, "extras": x, "mock": mock or {}}


def _stock(blur, rgba, title):
    r, g, b, a = rgba
    return {"use-blur": blur, "color": {"red": r, "green": g, "blue": b, "alpha": a},
            "current-window": {"color": title}}


def _colors(text, separator, track, cpu, ram, radius):
    return {"text": text, "separator": separator, "track": track,
            "cpu": cpu, "ram": ram, "radius": radius}


THEMES = [
    _theme("pills", "Floating Pills", "your reference",
           _stock(True, (0, 0, 0, 100), "#D6D6D6"),
           _colors("#D6D6D6", "#4DFFFFFF", "#33FFFFFF", "#D6D6D6", "#D6D6D6", 3),
           {"font": MONO, "height": 28, "title": {"show": False},
            "backdrop": {"mode": "clear", "opacity": 0}, "layout": "split",
            "pills": {"enabled": True, "fill": "#B3161616", "border": "#1FFFFFFF"}},
           {"bar": None, "pill": "#1d1d1d", "ink": "#d6d6d6"}),
    _theme("glass", "Frosted Glass", "acrylic, soft pills",
           _stock(True, (0, 0, 0, 100), "#EDEDED"),
           _colors("#EDEDED", "#4DFFFFFF", "#33FFFFFF", "#EDEDED", "#EDEDED", 3),
           {"font": MONO, "height": 28, "title": {"show": False},
            "backdrop": {"mode": "acrylic", "tint": "#121212", "opacity": 45},
            "layout": "split",
            "pills": {"enabled": True, "fill": "#1FFFFFFF", "border": "#26FFFFFF"}},
           {"bar": "#3a3d44", "pill": "#50535a", "ink": "#ededed"}),
    _theme("strip", "Frosted Strip", "acrylic, classic layout",
           _stock(True, (0, 0, 0, 100), "#EDEDED"),
           _colors("#EDEDED", "#40FFFFFF", "#33FFFFFF", "#EDEDED", "#EDEDED", 2),
           {"font": MONO, "height": 22,
            "backdrop": {"mode": "acrylic", "tint": "#101010", "opacity": 55}},
           {"bar": "#34373d", "ink": "#ededed"}),
    _theme("classic", "Classic", "the original look",
           _stock(True, (0, 0, 0, 100), "#ffffff"),
           _colors("#bbbbbb", "#888888", "#33bbbbbb", "rainbow", "from-percentage", 2),
           mock={"bar": "#2b2f38", "ink": "#bbbbbb", "rainbow": True}),
    _theme("gnome", "GNOME", "solid black",
           _stock(False, (0, 0, 0, 255), "#ffffff"),
           _colors("#ffffff", "#4dffffff", "#33ffffff", "#ffffff", "#ffffff", 2),
           mock={"bar": "#000000", "ink": "#ffffff"}),
    _theme("macdark", "macOS dark", "frosted black",
           _stock(True, (0, 0, 0, 150), "#ffffff"),
           _colors("#ffffff", "#38ffffff", "#30ffffff", "#ffffff", "#ffffff", 5),
           mock={"bar": "#1e2026", "ink": "#ffffff"}),
    _theme("maclight", "macOS light", "frosted white",
           _stock(True, (246, 246, 248, 200), "#1d1d1f"),
           _colors("#1d1d1f", "#33000000", "#22000000", "#1d1d1f", "#1d1d1f", 5),
           mock={"bar": "#e9e9ec", "ink": "#1d1d1f"}),
    _theme("ubuntu", "Ubuntu", "aubergine",
           _stock(False, (44, 0, 30, 255), "#ffffff"),
           _colors("#ffffff", "#4dffffff", "#33ffffff", "#e95420", "#e95420", 2),
           mock={"bar": "#2c001e", "ink": "#ffffff", "accent": "#e95420"}),
    _theme("kde", "KDE Plasma", "breeze dark",
           _stock(True, (49, 54, 59, 215), "#eff0f1"),
           _colors("#eff0f1", "#4deff0f1", "#33eff0f1", "#3daee9", "#3daee9", 2),
           mock={"bar": "#31363b", "ink": "#eff0f1", "accent": "#3daee9"}),
]
THEMES_BY_ID = {t["id"]: t for t in THEMES}


# --------------------------------------------------------------- engine ---

def engine_available():
    """True when the installed extension DLL carries the engine widgets."""
    try:
        with open(DLL_PATH, "rb") as fh:
            data = fh.read()
    except OSError:
        return False
    return b"\x00Engine\x00" in data and b"\x00Group\x00" in data


def extras(cfg):
    """The config's extras block, with every default filled in."""
    x = cfg.setdefault("extras", {})
    for k, v in EXTRAS_DEFAULT.items():
        if isinstance(v, dict):
            x.setdefault(k, {})
            for kk, vv in v.items():
                x[k].setdefault(kk, vv)
        else:
            x.setdefault(k, v)
    return x


def ensure_engine(cfg):
    """The engine must be the first widget - or absent, if its DLL is.

    Naming a widget type TopBar can't find makes it pop an error dialog on
    every start, so without the DLL both marker types are stripped.
    """
    ws = cfg.setdefault("widgets", [])
    if engine_available():
        ws[:] = [w for w in ws if w.get("type") != ENGINE]
        ws.insert(0, {"type": ENGINE, "settings": {}})
    else:
        ws[:] = [w for w in ws if w.get("type") not in MARKERS]


# --------------------------------------------------------------- layout ---

def _plain(ws):
    return [w for w in ws if w.get("type") not in MARKERS]


def has_groups(cfg):
    return any(w.get("type") == GROUP for w in cfg.get("widgets", []))


def to_split(cfg):
    """Stats on the left, the clock centered, shortcuts on the right - the
    arrangement in the reference. The classic order is kept so switching
    back restores it exactly."""
    ws = cfg["widgets"]
    if has_groups(cfg):
        return
    plain = _plain(ws)
    extras(cfg)["saved_order"] = copy.deepcopy(plain)
    clocks = [w for w in plain if w["type"] == "TopBar.Widgets.Clock"]
    shortcuts = [w for w in plain if w["type"] == "TopBar.Widgets.Shortcut"]
    stats = [w for w in plain if w not in clocks and w not in shortcuts]

    def trim(seq):
        while seq and seq[0]["type"] == "TopBar.Widgets.LineSpacer":
            seq.pop(0)
        while seq and seq[-1]["type"] == "TopBar.Widgets.LineSpacer":
            seq.pop()
        return seq

    groups = []
    for align, items in (("left", trim(stats)), ("center", clocks),
                         ("right", shortcuts)):
        if items:
            groups.append({"type": GROUP, "settings": {"align": align}})
            groups.extend(items)
    ws[:] = groups
    ensure_engine(cfg)


IDENTITY = {"TopBar.Widgets.Shortcut": "command",
            "TopBar.Widgets.CustomText": "text",
            "TopBar.Widgets.TextFromFile": "file"}


def _identity(w):
    """What makes a widget the same widget, ignoring colors a theme changes."""
    key = IDENTITY.get(w.get("type"))
    return (w.get("type"), w.get("settings", {}).get(key) if key else None)


def to_classic(cfg):
    """Drop the groups and put the widgets back in the order they had before
    they were split. Widgets are matched by identity (a shortcut by its
    command, a text by its text), so theme recolors and edits carry over;
    separators the split trimmed come back; anything added meanwhile goes on
    the end."""
    ws = cfg["widgets"]
    saved = extras(cfg).pop("saved_order", None)
    if not has_groups(cfg):
        return
    plain = _plain(ws)
    if saved is None:
        ws[:] = plain
    else:
        pool = list(plain)
        restored = []
        for old in saved:
            match = next((w for w in pool if _identity(w) == _identity(old)), None)
            if match is not None:
                pool.remove(match)
                restored.append(match)
            elif old.get("type") == "TopBar.Widgets.LineSpacer":
                restored.append(copy.deepcopy(old))
        ws[:] = restored + pool
    ensure_engine(cfg)


# --------------------------------------------------------------- themes ---

def restyle_widgets(cfg, colors):
    for w in cfg.get("widgets", []):
        s = w.setdefault("settings", {})
        t = w.get("type", "")
        if t in TEXT_TYPES:
            s["color"] = colors["text"]
        elif t == "TopBar.Widgets.Clock":
            s["text-color"] = colors["text"]
        elif t == "TopBar.Widgets.LineSpacer":
            s["color"] = colors["separator"]
        elif t == "TopBar.Widgets.RAMUsageBar":
            s["color"] = colors["ram"]
            s["background-color"] = colors["track"]
            s["radius"] = colors["radius"]
        elif t in CPU_TYPES:
            s["color"] = colors["cpu"]
            s["radius"] = colors["radius"]
        elif t in (GPU, REMOTE):
            s["color"] = colors["text"]
            s["bar-color"] = colors["ram"]
            s["background-color"] = colors["track"]
            s["radius"] = colors["radius"]


def apply_theme(cfg, theme):
    st = cfg.setdefault("settings", {})
    st["use-blur"] = theme["stock"]["use-blur"]
    st["color"] = dict(theme["stock"]["color"])
    st.setdefault("current-window", {})["color"] = theme["stock"]["current-window"]["color"]

    x = copy.deepcopy(theme["extras"])
    for k, v in cfg.get("extras", {}).items():
        if k not in THEME_KEYS and k != "theme":
            x[k] = v
    x["theme"] = theme["id"]
    cfg["extras"] = x
    if x["layout"] == "split":
        to_split(cfg)
    else:
        to_classic(cfg)
    # After the layout move, so separators it restores get this theme's color.
    restyle_widgets(cfg, theme["widgets"])
    ensure_engine(cfg)


# ------------------------------------------------------------ first run ---

# TopBar writes this layout (as its built-in config) on its very first start.
# A config still exactly like it has never been touched, so it's safe to swap
# for the starter bar below.
STOCK_TYPES = ["TopBar.Widgets.Shortcut", "TopBar.Widgets.Shortcut", "TopBar.Widgets.LineSpacer",
               "TopBar.Widgets.CustomText", "TopBar.Widgets.RAMUsageText",
               "TopBar.Widgets.RAMUsageBar", "TopBar.Widgets.LineSpacer",
               "TopBar.Widgets.CustomText", "TopBar.Widgets.CPULoad",
               "TopBar.Widgets.LineSpacer", "TopBar.Widgets.Clock"]


def is_untouched(cfg):
    return "extras" not in cfg and [w.get("type") for w in cfg.get("widgets", [])] == STOCK_TYPES


def starter(cfg):
    """A new PC's first bar, like the one it was set up from: RAM, CPU and GPU
    on the left - where the other PCs' GPUs join them - the clock in the
    middle, and TopBar's stock shortcuts on the right, as Floating Pills."""
    sep = {"type": "TopBar.Widgets.LineSpacer", "settings": {"color": "#888888", "width": 1, "height": 10}}
    shortcuts = [w for w in cfg.get("widgets", []) if w.get("type") == "TopBar.Widgets.Shortcut"]
    cfg["widgets"] = [
        {"type": GROUP, "settings": {"align": "left"}},
        {"type": "TopBar.Widgets.CustomText", "settings": {"text": "RAM: "}},
        {"type": "TopBarExtras.Widgets.RAMUsage",
         "settings": {"format": "{used:0.0}/{total:0} GB", "interval": 1000}},
        {"type": "TopBar.Widgets.RAMUsageBar", "settings": {"width": 60, "height": 10}},
        copy.deepcopy(sep),
        {"type": "TopBar.Widgets.CustomText", "settings": {"text": (cpu_short_name() or "CPU") + " "}},
        {"type": "TopBarExtras.Widgets.CPULoad", "settings": {"interval": 3000, "line-width": 5}},
        copy.deepcopy(sep),
        {"type": GPU, "settings": {"label": "auto", "format": "{percent:0}%", "value-position": "before",
                                   "width": 60, "height": 10, "interval": 1000}},
        {"type": GROUP, "settings": {"align": "center"}},
        {"type": "TopBar.Widgets.Clock", "settings": {"date": True, "seconds": True}},
    ]
    if shortcuts:
        cfg["widgets"].append({"type": GROUP, "settings": {"align": "right"}})
        cfg["widgets"].extend(shortcuts)
    cfg.pop("extras", None)
    apply_theme(cfg, THEMES_BY_ID["pills"])
    return cfg
