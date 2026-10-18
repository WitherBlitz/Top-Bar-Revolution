"""Draws the bar the way TopBar + the engine will show it, and the small
abstract previews on the theme cards."""

import datetime
import json
import os
import re
import time
import tkinter as tk
import tkinter.font as tkfont

from . import lan, themes
from .common import (APP_DIR, CPU_TYPES, RAM_FORMAT, blend, format_ram,
                     format_values, hexs, hsl_rgb, icon_rgba, parse_color,
                     percentage_color, resize_rgba, rgba_photo)
from .ui import rrect

# A dusky wallpaper, so clear and frosted backdrops read as see-through.
WALL_TOP = (58, 66, 86)
WALL_BOTTOM = (112, 104, 118)
CPU_LOADS = [0.18, 0.42, 0.30, 0.66, 0.24, 0.51, 0.38, 0.72,
             0.29, 0.45, 0.33, 0.57, 0.22, 0.61, 0.35, 0.48]


def wall_at(t):
    return tuple(int(a + (b - a) * t) for a, b in zip(WALL_TOP, WALL_BOTTOM))


def clock_text(s):
    now = datetime.datetime.now()
    t = "%#I:%M:%S %p" if s.get("seconds") else "%#I:%M %p"
    return now.strftime(("%#m/%#d/%Y " + t) if s.get("date") else t)


class BarPreview:
    def __init__(self):
        self.icons = {}
        self.fonts = {}
        self.ram_full = (40, 25.0, 64.0, 39.0)

    # -- resources ---------------------------------------------------------

    def gpu_info(self):
        if not hasattr(self, "_gpu"):
            try:
                with open(os.path.join(APP_DIR, "gpu.json"), encoding="utf-8") as fh:
                    self._gpu = json.load(fh)
            except (OSError, ValueError):
                self._gpu = {}
        return self._gpu

    def font_for(self, x, scale):
        f = x.get("font", {})
        family = f.get("family") or "Segoe UI"
        base = f.get("size") or 12          # WPF's default is 12 DIPs
        # WPF sizes are pixels; Tk sizes are points unless negative.
        size = -max(7, int(round(base * scale)))
        weight = "bold" if str(f.get("weight", "")).lower() in ("bold", "semibold", "medium") else "normal"
        key = (family, size, weight)
        if key not in self.fonts:
            self.fonts[key] = tkfont.Font(family=family, size=size, weight=weight)
        return self.fonts[key]

    def icon(self, s, size):
        override = str(s.get("icon", "") or "").strip()
        path = override or str(s.get("command", "") or "").strip()
        if not path:
            return None
        key = (path.lower(), size)
        if key not in self.icons:
            img = None
            got = icon_rgba(path)
            if got:
                w, h, px = got
                try:
                    img = rgba_photo(resize_rgba(px, w, h, size), size)
                except tk.TclError:
                    img = None
            self.icons[key] = img
        return self.icons[key]

    # -- one widget ----------------------------------------------------------

    def widget(self, cv, w, x, cy, draw, f, scale, bg, solid):
        t = w.get("type", "")
        s = w.get("settings", {})

        def text(txt, color):
            if draw:
                cv.create_text(x, cy, text=txt, anchor="w", fill=solid(color), font=f)
            return f.measure(txt)

        if t == "TopBar.Widgets.CustomText":
            return text(str(s.get("text", "")), s.get("color"))
        if t == "TopBar.Widgets.RAMUsageText":
            return text("%d %%" % self.ram_full[0], s.get("color"))
        if t == "TopBarExtras.Widgets.RAMUsage":
            return text(format_ram(s.get("format", RAM_FORMAT), *self.ram_full), s.get("color"))
        if t == "TopBar.Widgets.Clock":
            return text(clock_text(s), s.get("text-color"))
        if t == "TopBar.Widgets.TextFromFile":
            sample = "(file)"
            path = s.get("file", "")
            if path and os.path.isfile(path):
                try:
                    with open(path, "r", encoding="utf-8", errors="replace") as fh:
                        sample = fh.readline().strip()[:40] or "(empty)"
                except OSError:
                    sample = "(unreadable)"
            return text(sample, s.get("color"))
        if t == "TopBar.Widgets.LineSpacer":
            wd = max(1, int(s.get("width", 1) or 1))
            ht = int(s.get("height", 10) or 10) * scale
            if draw:
                cv.create_rectangle(x, cy - ht / 2, x + wd, cy + ht / 2,
                                    fill=solid(s.get("color"), "#888888"), width=0)
            return wd
        if t == "TopBar.Widgets.Shortcut":
            size = max(10, int(round(16 * scale)))
            if draw:
                img = self.icon(s, size)
                if img is not None:
                    cv.create_image(x, cy, image=img, anchor="w")
                else:
                    rrect(cv, x, cy - size / 2, x + size, cy + size / 2, 3,
                          fill=hexs(blend((90, 224, 108, 117), bg)), outline="")
            return size
        if t == themes.REMOTE:
            return self.remote(cv, w, x, cy, draw, f, scale, solid)
        if t == "TopBarExtras.Widgets.GPUUsage":
            # The preview can't run DXGI; the widget leaves its detection in
            # gpu.json. The load shown is illustrative.
            info = self.gpu_info()
            label = s.get("label", "auto")
            name = (info.get("short") or "GPU") if label == "auto" else str(label)
            vram = float(info.get("vram_gb", 0) or 0)
            return self.meter(cv, s, x, cy, draw, f, scale, solid, name,
                              with_vram(s, s.get("format", "{percent:0}%")),
                              {"percent": 23.0, "vram_used": 2.8, "vram_total": vram},
                              {"percent": 100.0, "vram_used": vram, "vram_total": vram})
        if t == "TopBar.Widgets.RAMUsageBar":
            wd = int(s.get("width", 60) or 60) * scale
            ht = int(s.get("height", 10) or 10) * scale
            rad = int(s.get("radius", 2) or 0) * scale
            if draw:
                pct = self.ram_full[0] / 100.0
                rrect(cv, x, cy - ht / 2, x + wd, cy + ht / 2, rad,
                      fill=solid(s.get("background-color"), "#33BBBBBB"), outline="")
                col = s.get("color", "from-percentage")
                fill = hexs(percentage_color(pct)) if col == "from-percentage" else solid(col)
                rrect(cv, x, cy - ht / 2, x + max(2, wd * pct), cy + ht / 2, rad,
                      fill=fill, outline="")
            return wd
        if t in CPU_TYPES:
            lw = max(1, int(s.get("line-width", 5) or 5)) * scale
            cores = min(os.cpu_count() or 8, 16)
            gap = 6 * scale                # stock margins are 3px a side
            if draw:
                col = s.get("color", "rainbow")
                rad = int(s.get("radius", 0) or 0) * scale if t.startswith("TopBarExtras") else 0
                for i in range(cores):
                    load = CPU_LOADS[i % len(CPU_LOADS)]
                    h = max(2, 12 * scale * load)
                    bx = x + i * (lw + gap)
                    if col == "rainbow":
                        fill = hexs(hsl_rgb(360.0 * i / cores, 0.78, 0.78))
                    elif col == "from-percentage":
                        fill = hexs(percentage_color(load))
                    else:
                        fill = solid(col)
                    base = cy + 6 * scale
                    rrect(cv, bx, base - h, bx + lw, base, min(rad, lw / 2), fill=fill, outline="")
            return cores * lw + (cores - 1) * gap
        return 0

    def peers(self):
        """{PC: what the engine last heard from it}, re-read every 2s at most."""
        now = time.time()
        if now - getattr(self, "_peers_at", 0) > 2:
            self._peers_at = now
            data = lan.read_peers()
            self._peers = {str(p.get("id", "")).upper(): p for p in (data or {}).get("peers", [])
                           if data.get("fresh")}
            self._known = lan.known_pcs()
        return self._peers

    def use(self, cfg):
        """The bar about to be drawn: note each network PC's first reading -
        the one that turns into its wake button while the PC is off."""
        seen, self.leaders = set(), set()
        for w in cfg.get("widgets", []):
            if w.get("type") == themes.REMOTE:
                pc = str(w.get("settings", {}).get("pc", "")).upper()
                if pc not in seen:
                    seen.add(pc)
                    self.leaders.add(id(w))

    def remote(self, cv, w, x, cy, draw, f, scale, solid):
        """Another PC's reading - live from peers.json. While that PC is off,
        its first reading is drawn as its wake button (as on the bar) and the
        rest dimmed "offline" (on the bar they hide - here they stay, so they
        can still be arranged)."""
        s = w.get("settings", {})
        metric = s.get("metric", "gpu")
        pc = str(s.get("pc", "") or "?")
        p = self.peers().get(pc.upper())
        online = bool(p and p.get("online"))
        if metric == "cpu":
            cpu = (p or {}).get("cpu") or {}
            part = cpu.get("name") or "CPU"
            values, widest = {"percent": float(cpu.get("load", 0))}, {"percent": 100.0}
        elif metric == "ram":
            ram = (p or {}).get("ram") or {}
            used, total = float(ram.get("used", 0)), float(ram.get("total", 0))
            part = "RAM"
            values = {"percent": 100.0 * used / total if total else 0.0, "used": used,
                      "total": total, "free": total - used}
            widest = {"percent": 100.0, "used": total, "total": total, "free": total}
        else:
            gpus = (p or {}).get("gpus") or []
            n = int(s.get("gpu", 0) or 0)
            named = [g for g in gpus if g.get("name") == s.get("gpu-name")]
            g = named[0] if len(named) == 1 else (gpus[n] if n < len(gpus) else {})
            part = g.get("short") or (s.get("gpu-name") and re.sub(
                r"\b(NVIDIA|GeForce|AMD|ATI|Radeon|Intel)\b|\((R|TM)\)", "", s["gpu-name"]).strip()) or "GPU"
            total = float(g.get("vram_total", 0))
            values = {"percent": float(g.get("load", 0)), "vram_used": float(g.get("vram_used", 0)),
                      "vram_total": total}
            widest = {"percent": 100.0, "vram_used": total, "vram_total": total}
        known = getattr(self, "_known", {}).get(pc.upper()) or {}
        pc_name = (p or {}).get("name") or known.get("name") or pc
        label = s.get("label", "auto")
        if label == "auto":
            name = "%s · %s" % (pc_name, part) if s.get("show-pc", True) else part
        else:
            name = str(label).replace("{pc}", pc_name).replace("{name}", part)
        fmt = s.get("format") or ("{used:0.0}/{total:0} GB" if metric == "ram" else "{percent:0}%")
        if metric == "gpu":
            fmt = with_vram(s, fmt)
        if online:
            return self.meter(cv, s, x, cy, draw, f, scale, solid, name, fmt, values, widest)

        def faded(value, default="#BBBBBB"):
            a, r, g_, b = parse_color(value if isinstance(value, str) else default)
            return solid("#%02X%02X%02X%02X" % (int(a * 0.55), r, g_, b), default)
        if (s.get("offline", "wake") == "wake" and id(w) in getattr(self, "leaders", ())
                and known.get("macs")):
            return self.meter(cv, s, x, cy, draw, f, scale, faded, pc_name, "", {}, {},
                              bar=False, icon=True)
        return self.meter(cv, s, x, cy, draw, f, scale, faded, name, "offline",
                          {"percent": 0.0}, {"percent": 0.0})

    def meter(self, cv, s, x, cy, draw, f, scale, solid, name, fmt, values, widest,
              bar=None, icon=False):
        """Name, reading and load bar, as the GPU widgets lay them out: 8px
        apart (6 after the power icon of a wake button), the reading keeping
        room for its widest value as it does on the bar, before the bar unless
        value-position says after. No reading and no bar take no room."""
        text = format_values(fmt, values) if fmt else ""
        room = max(f.measure(text), f.measure(re.sub("[0-9]", "8", format_values(fmt, widest)))) if text else 0
        if bar is None:
            bar = bool(s.get("bar", True))
        wd = int(s.get("width", 60) or 60) * scale if bar else 0
        ht = int(s.get("height", 10) or 10) * scale
        rad = int(s.get("radius", 2) or 0) * scale
        pct = values.get("percent", 0.0)
        first = str(s.get("value-position", "before")).lower() != "after"
        ink = solid(s.get("color"))
        parts = []
        if icon:
            parts.append(("icon", 10 * scale))
        if name:
            parts.append(("name", f.measure(name)))
        order_ = [("text", room), ("bar", wd)] if first else [("bar", wd), ("text", room)]
        parts += [p for p in order_ if p[1] > 0]
        xx = x
        for n, (kind, w) in enumerate(parts):
            if n:
                xx += (6 if parts[n - 1][0] == "icon" else 8) * scale
            if draw:
                if kind == "icon":
                    r = 4.2 * scale
                    ccx, ccy = xx + 5 * scale, cy + 0.6 * scale
                    cv.create_arc(ccx - r, ccy - r, ccx + r, ccy + r, start=120, extent=300,
                                  style="arc", outline=ink, width=max(1, 1.3 * scale))
                    cv.create_line(ccx, ccy - r - 0.6 * scale, ccx, ccy, fill=ink,
                                   width=max(1, 1.3 * scale), capstyle="round")
                elif kind == "name":
                    cv.create_text(xx, cy, text=name, anchor="w", fill=ink, font=f)
                elif kind == "text":
                    if first:
                        cv.create_text(xx + w, cy, text=text, anchor="e", fill=ink, font=f)
                    else:
                        cv.create_text(xx, cy, text=text, anchor="w", fill=ink, font=f)
                else:
                    rrect(cv, xx, cy - ht / 2, xx + w, cy + ht / 2, rad,
                          fill=solid(s.get("background-color"), "#33BBBBBB"), outline="")
                    col = s.get("bar-color", "from-percentage")
                    fill = hexs(percentage_color(pct / 100.0)) if col == "from-percentage" else solid(col)
                    rrect(cv, xx, cy - ht / 2, xx + max(2, w * pct / 100.0), cy + ht / 2, rad,
                          fill=fill, outline="")
            xx += w
        return xx - x

    # -- the bar ---------------------------------------------------------------

    def paint(self, cv, cfg):
        cv.delete("all")
        self.use(cfg)
        W = cv.winfo_width() or 900
        H = int(cv["height"])
        x = themes.extras(cfg)
        st = cfg.get("settings", {})
        pills = x["pills"]
        split = x["layout"] == "split"
        pills_on = bool(pills.get("enabled"))
        edge = 8

        # wallpaper
        bands = 40
        for i in range(bands):
            cv.create_rectangle(0, H * i / bands, W, H * (i + 1) / bands + 1,
                                fill=hexs(wall_at(i / (bands - 1.0))), width=0)

        widgets = [w for w in cfg.get("widgets", []) if w.get("type") != themes.ENGINE]

        # measure at full size, then shrink to fit the preview's width
        def span(gs, align):
            sel = [g for g in gs if g["align"] == align]
            return sum(g["width"] for g in sel) + 12 * max(0, len(sel) - 1)

        def measure(scale):
            layout = self._layout(widgets, x, split, pills_on, scale)
            return [span(layout, a) for a in ("left", "center", "right")]

        scale = 1.0
        for _ in range(4):
            left, center, right = measure(scale)
            # A centered group needs room for twice the wider side, not just the sum.
            need = (2 * max(left, right) + center + 48) if center else (left + right + 48)
            if need <= W or scale <= 0.55:
                break
            scale = max(0.55, scale * (W - 20) / float(need))
        # Still too much: on the bar the middle group slides aside then, so all
        # it takes is room for the three groups side by side.
        for _ in range(4):
            need = left + center + right + 48 + (24 * scale if center else 0)
            if need <= W or scale <= 0.3:
                break
            scale = max(0.3, scale * (W - 20) / float(need))
            left, center, right = measure(scale)
        bar_h = max(16, x["height"]) * scale
        cy = bar_h / 2

        # backdrop
        mode = x["backdrop"]["mode"]
        if mode == "stock":
            c = st.get("color", {})
            argb = (int(c.get("alpha", 100)), int(c.get("red", 0)),
                    int(c.get("green", 0)), int(c.get("blue", 0)))
            bg = blend(argb, wall_at(0.2))
            cv.create_rectangle(0, 0, W, bar_h, fill=hexs(bg), width=0)
        else:
            a, r, g, b = parse_color(x["backdrop"].get("tint", "#101010"), (255, 16, 16, 16))
            alpha = int(round(float(x["backdrop"].get("opacity", 45)) * 2.55))
            if mode == "clear":
                bg = blend((alpha, r, g, b), wall_at(0.2))
            else:
                # blur/acrylic: the wallpaper, smeared toward its mean, then tinted
                soft = blend((140,) + wall_at(0.5), wall_at(0.2))
                bg = blend((max(alpha, 40 if mode == "acrylic" else 0), r, g, b), soft)
            cv.create_rectangle(0, 0, W, bar_h, fill=hexs(bg), width=0)
            if mode == "acrylic":
                # a hint of acrylic's noise texture
                for i in range(0, int(W), 7):
                    for j in range(1, int(bar_h), 5):
                        if (i * 31 + j * 17) % 11 == 0:
                            cv.create_rectangle(i, j, i + 1, j + 1,
                                                fill=hexs(blend((40, 255, 255, 255), bg)), width=0)

        def solid(value, default="#BBBBBB", over=None):
            aa, rr, gg, bb = parse_color(value if isinstance(value, str) else default)
            return hexs(blend((aa, rr, gg, bb), over or bg))

        f = self.font_for(x, scale)
        title_on = x["title"].get("show", True)
        title_color = st.get("current-window", {}).get("color", "#ffffff")

        groups = self._layout(widgets, x, split, pills_on, scale)
        pill_h = min(bar_h, float(pills.get("height", 22)) * scale)
        pad = float(pills.get("padding", 12)) * scale
        radius = min(float(pills.get("radius", 99)), pill_h / 2)
        fill_argb = parse_color(pills.get("fill", "#B3161616"), (179, 22, 22, 22))
        border_argb = parse_color(pills.get("border", "#1FFFFFFF"), (0, 0, 0, 0))
        pill_bg = blend(fill_argb, bg)
        gap = float(pills.get("gap", 8)) * scale if pills_on else 6 * scale

        # where each group starts
        by_align = {"left": [], "center": [], "right": []}
        for g in groups:
            by_align[g["align"]].append(g)

        def total(gs):
            return sum(g["width"] for g in gs) + gap * max(0, len(gs) - 1)

        cursor = {"left": edge * scale, "right": W - edge * scale - total(by_align["right"]),
                  "center": (W - total(by_align["center"])) / 2.0}
        if title_on and (not split or not by_align["left"]):
            cv.create_text(4, cy, text="Active window title", anchor="w",
                           fill=solid(title_color, "#ffffff"), font=f)
        elif title_on:
            tw = f.measure("Active window title")
            cv.create_text(cursor["left"], cy, text="Active window title", anchor="w",
                           fill=solid(title_color, "#ffffff"), font=f)
            cursor["left"] += tw + 12 * scale

        # The middle group slides aside rather than overlap a long left or right
        # group - and if it can't clear both, the left wins, as on the bar.
        clear = max(16 * scale, 3 * gap)
        cursor["center"] = min(cursor["center"], cursor["right"] - clear - total(by_align["center"]))
        cursor["center"] = max(cursor["center"], cursor["left"] + total(by_align["left"]) + clear)
        # More than even the smallest preview text can hold: keep the middle
        # in view, drawn over the others, rather than off the end.
        cursor["center"] = min(cursor["center"], W - edge * scale - total(by_align["center"]))
        for align in ("left", "right", "center"):
            xx = cursor[align]
            for g in by_align[align]:
                if pills_on:
                    x0, x1 = xx, xx + g["width"]
                    rrect(cv, x0, cy - pill_h / 2, x1, cy + pill_h / 2, radius,
                          fill=hexs(pill_bg),
                          outline=hexs(blend(border_argb, pill_bg)) if border_argb[0] else "")
                    ix = x0 + pad
                    over = pill_bg
                else:
                    ix = xx
                    over = bg
                for n, (w, width) in enumerate(g["items"]):
                    self.widget(cv, w, ix, cy, True, f, scale, over,
                                lambda v, d="#BBBBBB", o=over: solid(v, d, o))
                    ix += width + g["spacing"]
                xx += g["width"] + gap
        return scale

    def _layout(self, widgets, x, split, pills_on, scale):
        """Groups -> pills -> (widget, width), mirroring the engine."""
        f = self.font_for(x, scale)
        spacing = (float(x["pills"].get("spacing", 12)) if pills_on else 4) * scale
        pad = float(x["pills"].get("padding", 12)) * scale if pills_on else 0
        dummy = _Measure()
        groups, align = [], "right"
        current = []

        def close():
            if current:
                items = [(w, self.widget(dummy, w, 0, 0, False, f, scale, (0, 0, 0),
                                         lambda *a, **k: "#000000")) for w in current]
                width = sum(wd for _, wd in items) + spacing * (len(items) - 1) + pad * 2
                groups.append({"align": align, "items": items, "width": width,
                               "spacing": spacing})
            del current[:]

        for w in widgets:
            t = w.get("type")
            if t == themes.GROUP:
                close()
                align = w.get("settings", {}).get("align", "right") if split else "right"
                continue
            if pills_on and t == "TopBar.Widgets.LineSpacer":
                close()
                continue
            if not pills_on and not split and t == themes.GROUP:
                continue
            current.append(w)
        close()
        if not pills_on and not split:
            # classic: one strip
            items = [it for g in groups for it in g["items"]]
            width = sum(wd for _, wd in items) + spacing * max(0, len(items) - 1)
            return [{"align": "right", "items": items, "width": width, "spacing": spacing}] if items else []
        return groups


def with_vram(s, fmt):
    """A GPU reading's format with its VRAM switch applied, as the widgets do."""
    if s.get("vram") and "vram" not in fmt.lower():
        return fmt + "  {vram_used:0.0}/{vram_total:0} GB"
    return fmt


class _Measure:
    """Stands in for a canvas when only measuring."""

    def __getattr__(self, name):
        return lambda *a, **k: None


# ------------------------------------------------------------- card mocks ---

def theme_mock(cv, x0, y0, x1, y1, theme):
    """Abstract preview in the spirit of Forge's theme cards: a wallpaper, the
    bar across the top in the theme's style, and a window below it."""
    m = theme["mock"]
    x = theme["extras"]
    W, H = x1 - x0, y1 - y0
    rrect(cv, x0, y0, x1, y1, 10, fill=hexs(wall_at(0.15)), outline="")
    for i in range(12):
        t = i / 11.0
        cv.create_rectangle(x0 + 6, y0 + 20 + (H - 26) * i / 12.0, x1 - 6,
                            y0 + 20 + (H - 26) * (i + 1) / 12.0 + 1,
                            fill=hexs(wall_at(0.15 + 0.7 * t)), width=0)
    ink = m.get("ink", "#dddddd")
    bar_h = 14 if x["layout"] == "split" else 11
    if m.get("bar"):
        rrect(cv, x0, y0, x1, y0 + bar_h + 6, 10, fill=m["bar"], outline="")
        cv.create_rectangle(x0, y0 + 10, x1, y0 + bar_h + 6, fill=m["bar"], width=0)
    cy = y0 + (bar_h + 6) / 2.0
    if x["layout"] == "split":
        pill = m.get("pill", "#1d1d1d")
        for px0, px1, dashes in ((x0 + 8, x0 + 58, 3), (x0 + 62, x0 + 100, 2),
                                  (x0 + W / 2 - 22, x0 + W / 2 + 22, 2),
                                  (x1 - 60, x1 - 8, 4)):
            rrect(cv, px0, cy - 5, px1, cy + 5, 5, fill=pill, outline="")
            step = (px1 - px0 - 12) / float(dashes)
            for d in range(dashes):
                sx = px0 + 6 + d * step
                cv.create_rectangle(sx, cy - 1, sx + step - 4, cy + 1, fill=ink, width=0)
    else:
        x_ = x1 - 14
        cols = [hexs(hsl_rgb(36 * i, 0.75, 0.7)) for i in range(8)] if m.get("rainbow") else None
        for i in range(8):
            c = cols[i] if cols else (m.get("accent") or ink)
            h = 2 + (i * 5 % 5)
            cv.create_rectangle(x_ - i * 4, cy + 3 - h, x_ - i * 4 + 2, cy + 3, fill=c, width=0)
        for w_ in (26, 16, 30):
            x_ -= 44
            cv.create_rectangle(x_ - w_ / 2, cy - 1, x_ + w_ / 2, cy + 1, fill=ink, width=0)
        cv.create_rectangle(x0 + 8, cy - 1, x0 + 40, cy + 1, fill=ink, width=0)
    # a window, like Forge's cards
    wx0, wy0, wx1, wy1 = x0 + 16, y0 + bar_h + 16, x1 - 16, y1 - 12
    rrect(cv, wx0, wy0, wx1, wy1, 8, fill="#0e0e0e", outline="")
    for i, c in enumerate(("#a14f4f", "#a47a3d", "#8f8f8f")):
        cv.create_oval(wx0 + 10 + i * 10, wy0 + 8, wx0 + 16 + i * 10, wy0 + 14, fill=c, outline="")
    cv.create_rectangle(wx0, wy0 + 21, wx1, wy0 + 22, fill="#262626", width=0)
    rrect(cv, wx0 + 10, wy0 + 30, wx0 + 60, wy1 - 10, 5, fill="#1c1c1c", outline="")
    for i, (w_, c) in enumerate(((0.9, "#e6e6e6"), (0.55, "#6a6a6a"), (0.7, "#5a5a5a"))):
        lx = wx0 + 72
        cv.create_rectangle(lx, wy0 + 34 + i * 10, lx + (wx1 - lx - 14) * w_,
                            wy0 + 38 + i * 10, fill=c, width=0)
    rrect(cv, wx0 + 72, wy0 + 66, wx0 + 110, wy0 + 76, 5, fill="#f2f2f2", outline="")
