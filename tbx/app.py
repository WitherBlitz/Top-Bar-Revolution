"""TopBar Settings - laid out like Forge's settings: a sidebar of sections,
a scrolling page of rows and cards, and a footer with the config path and
the primary actions. Every change is saved as it's made; the engine inside
TopBar picks the file up and applies it live."""

import copy
import ctypes
import json
import os
import subprocess
import sys
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox

from . import VERSION, lan, order, themes, ui
from .arrange import ArrangeStrip
from .common import (APP_DIR, CONFIG_PATH, CPU_TYPES, DEFAULT_CONFIG, EXE_PATH,
                     NAMES, SCHEMA, config_mtime, find_main_exe, load_config,
                     parse_color, format_color, ram_stats, resource,
                     restart_topbar, save_config, topbar_running)
from .preview import BarPreview, theme_mock
from .ui import (CARD, CARD_HI, FAINT, INK, LINE, MUTED, PANEL, TEXT, WHITE,
                 WIN, Banner, Button, Chip, Dropdown, NavItem, Row, Scroll,
                 Segmented, Slider, Swatch, Toggle, font, heading, rrect)

TITLE = "TopBar Settings"
INSTALL_KEY = (r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
               r"\{FD187DC3-B573-4BD4-A120-BB9C1F4A4283}_is1")
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
WATCH_LOG = os.path.join(APP_DIR, "display-watch.log")

FONT_CHOICES = [("", "Segoe UI  (classic)"), ("JetBrains Mono", "JetBrains Mono  (like the reference)"),
                ("Cascadia Mono", "Cascadia Mono"), ("Consolas", "Consolas"),
                ("DejaVu Sans Mono", "DejaVu Sans Mono"), ("Segoe UI Semibold", "Segoe UI Semibold"),
                ("Bahnschrift", "Bahnschrift")]


def install_dir():
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, INSTALL_KEY) as k:
            return winreg.QueryValueEx(k, "InstallLocation")[0]
    except OSError:
        return os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "TopBar Extras")


class App(tk.Tk):
    def __init__(self):
        ui.load_private_fonts(resource("fonts") or os.path.join(APP_DIR, "extensions", "fonts"))
        super().__init__()
        self.title(TITLE)
        self.geometry("1180x860")
        self.minsize(1000, 660)
        self.configure(bg=PANEL)
        icon = resource("topbar_settings.ico")
        if icon:
            try:
                self.iconbitmap(default=icon)
            except tk.TclError:
                pass

        try:
            self.cfg = load_config()
        except FileNotFoundError:
            self.cfg = copy.deepcopy(DEFAULT_CONFIG)
        except Exception as exc:
            messagebox.showerror(TITLE, "Couldn't read config.json:\n\n%s" % exc)
            self.cfg = copy.deepcopy(DEFAULT_CONFIG)
        self.engine = themes.engine_available()
        self.mtime = config_mtime()
        self.save_job = None
        self.preview = BarPreview()
        self.page = None
        self.sel = None

        # Without the engine as the first widget nothing here can apply live,
        # and the right-click Settings entry wouldn't exist either.
        before = json.dumps(self.cfg["widgets"])
        themes.ensure_engine(self.cfg)
        themes.extras(self.cfg)
        migrated = lan.migrate(self.cfg)
        if json.dumps(self.cfg["widgets"]) != before or migrated:
            # Not save_now(): the status line it reports to isn't built yet.
            try:
                save_config(self.cfg)
                self.mtime = config_mtime()
            except OSError:
                pass

        self._build()
        self._dark_titlebar()
        self.show("appearance")
        self.bind("<Control-s>", lambda e: self.save_now())
        self.protocol("WM_DELETE_WINDOW", self.on_close)
        self.after(1500, self._tick)

    # -- chrome ------------------------------------------------------------

    def _dark_titlebar(self):
        try:
            self.update_idletasks()
            hwnd = ctypes.windll.user32.GetParent(self.winfo_id())
            on = ctypes.c_int(1)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(on), ctypes.sizeof(on))
            self.withdraw()
            self.deiconify()
        except Exception:
            pass

    def _build(self):
        head = tk.Frame(self, bg=PANEL)
        head.pack(fill="x")
        tk.Label(head, text="Settings", fg=TEXT, bg=PANEL, font=font(15, "bold")).pack(
            side="left", padx=(26, 0), pady=(20, 18))
        tag = "TopBar Extras %s" % VERSION
        f = font(9, "bold", mono=True)
        pill = tk.Canvas(head, width=f.measure(tag) + 26, height=26, bg=PANEL, highlightthickness=0)
        rrect(pill, 0, 0, f.measure(tag) + 25, 25, 12, fill=CARD_HI, outline="")
        pill.create_text((f.measure(tag) + 26) / 2, 13, text=tag, fill="#d8d8d8", font=f)
        pill.pack(side="right", padx=(0, 26))
        tk.Frame(self, bg=LINE, height=1).pack(fill="x")

        foot = tk.Frame(self, bg=PANEL)
        foot.pack(side="bottom", fill="x")
        tk.Frame(foot, bg=LINE, height=1).pack(fill="x", side="top")
        inner = tk.Frame(foot, bg=PANEL)
        inner.pack(fill="x", padx=26, pady=14)
        self.status = tk.Label(inner, text=CONFIG_PATH, fg=MUTED, bg=PANEL,
                               font=font(9, mono=True), anchor="w")
        self.status.pack(side="left")
        Button(inner, "Save", self.save_now, kind="primary", size=11, height=40, pad=24).pack(side="right")
        Button(inner, "Restart TopBar", self.restart, size=11, height=40, pad=20).pack(side="right", padx=(0, 12))

        body = tk.Frame(self, bg=PANEL)
        body.pack(fill="both", expand=True)
        side = tk.Frame(body, bg=PANEL, width=236)
        side.pack(side="left", fill="y")
        side.pack_propagate(False)
        tk.Frame(body, bg=LINE, width=1).pack(side="left", fill="y")
        self.scroll = Scroll(body, bg=PANEL)
        self.scroll.pack(side="left", fill="both", expand=True)

        self.nav = {}
        sections = [("Customize", [("appearance", "Appearance"), ("widgets", "Widgets")]),
                    ("System", [("network", "Network"), ("monitors", "Monitors")]),
                    ("This app", [("about", "About")])]
        for i, (title, items) in enumerate(sections):
            tk.Label(side, text=title, fg=MUTED, bg=PANEL, font=font(10), anchor="w").pack(
                fill="x", padx=(28, 0), pady=(22 if i == 0 else 26, 6))
            for key, label in items:
                item = NavItem(side, label, lambda k=key: self.show(k))
                item.pack(padx=(16, 16), anchor="w")
                self.nav[key] = item

    def show(self, key, keep_scroll=False):
        self.page = key
        for k, item in self.nav.items():
            item.select(k == key)
        y = self.scroll.cv.yview()[0] if keep_scroll else 0
        for child in self.scroll.body.winfo_children():
            child.destroy()
        self.preview_cv = None
        page = tk.Frame(self.scroll.body, bg=PANEL)
        page.pack(fill="both", expand=True, padx=30, pady=(22, 30))
        getattr(self, "page_" + key)(page)
        self.update_idletasks()
        self.scroll._sync()
        self.scroll.cv.yview_moveto(y)
        self.scroll._draw_bar()

    # -- saving ------------------------------------------------------------

    def changed(self, repaint=True, final=True):
        """Every control calls this. Saving is debounced so a burst of
        edits lands as one write (and one live apply in TopBar)."""
        if repaint:
            self.paint_preview()
        if not final:
            return
        if self.save_job:
            self.after_cancel(self.save_job)
        self.save_job = self.after(350, self.save_now)
        self.status.configure(text="Saving\u2026", fg=MUTED)

    def save_now(self):
        if self.save_job:
            self.after_cancel(self.save_job)
            self.save_job = None
        try:
            save_config(self.cfg)
        except OSError as exc:
            self.status.configure(text="Couldn't save: %s" % exc, fg="#e57373")
            return False
        self.mtime = config_mtime()
        live = self.engine and topbar_running()
        self.status.configure(text=CONFIG_PATH + ("   \u00b7   saved, applied live" if live
                                                  else "   \u00b7   saved"), fg=MUTED)
        return True

    def restart(self):
        self.save_now()
        restart_topbar()
        self.status.configure(text=CONFIG_PATH + "   \u00b7   TopBar restarted", fg=MUTED)

    def on_close(self):
        if self.save_job:
            self.save_now()
        self.destroy()

    def _tick(self):
        self.preview.ram_full = ram_stats()
        lan.want_live()
        if self.page == "network":
            self.refresh_network()
        disk = config_mtime()
        if disk is not None and self.mtime is not None and disk != self.mtime and not self.save_job:
            # Someone else wrote config.json (TopBar's own menu, a text editor):
            # adopt it rather than overwrite it on the next save.
            try:
                self.cfg = load_config()
                themes.extras(self.cfg)
                self.mtime = disk
                self.show(self.page, keep_scroll=True)
                self.status.configure(text=CONFIG_PATH + "   \u00b7   reloaded changes made elsewhere")
            except Exception:
                pass
        else:
            self.paint_preview()
        self.after(2000, self._tick)

    # -- shared bits -------------------------------------------------------

    def banner(self, parent):
        if not self.engine:
            text = ("The TopBar Extras engine isn't installed, so appearance options can't be "
                    "applied. Reinstall TopBar Extras to enable them.")
        elif topbar_running():
            text = "Changes apply immediately and are saved automatically."
        else:
            text = "Changes are saved automatically. TopBar isn't running \u2014 they'll show when it starts."
        Banner(parent, text).pack(fill="x", pady=(0, 18))

    def preview_block(self, parent):
        self.preview_cv = tk.Canvas(parent, height=58, bg=PANEL, highlightthickness=0)
        self.preview_cv.pack(fill="x", pady=(0, 4))
        self.preview_cv.bind("<Configure>", lambda e: self.paint_preview())
        tk.Label(parent, text="Preview \u2014 approximate; shortcut icons are your real ones.",
                 fg=FAINT, bg=PANEL, font=font(9), anchor="w").pack(fill="x", pady=(0, 6))

    def paint_preview(self):
        cv = getattr(self, "preview_cv", None)
        if cv is not None and cv.winfo_exists():
            self.preview.paint(cv, self.cfg)

    # -- Appearance ----------------------------------------------------------

    def page_appearance(self, p):
        self.banner(p)
        self.preview_block(p)
        x = themes.extras(self.cfg)

        heading(p, "Theme", pady=(14, 10))
        grid = tk.Frame(p, bg=PANEL)
        grid.pack(fill="x")
        for c in range(3):
            grid.columnconfigure(c, weight=1, uniform="card")
        current = x.get("theme") or self.guess_theme()
        for i, t in enumerate(themes.THEMES):
            card = ThemeCard(grid, t, t["id"] == current, lambda t=t: self.pick_theme(t))
            card.grid(row=i // 3, column=i % 3, sticky="ew", padx=(0 if i % 3 == 0 else 7, 0 if i % 3 == 2 else 7), pady=7)

        heading(p, "Typography")
        f = x["font"]
        row = Row(p, "Font", "Every piece of text on the bar. JetBrains Mono ships with TopBar "
                              "Extras and matches your reference.")
        row.pack(fill="x")
        choices = list(FONT_CHOICES)
        if f.get("family") and f["family"] not in dict(choices):
            choices.append((f["family"], f["family"]))
        Dropdown(row.right, choices, f.get("family", ""), lambda v: self.set_x(("font", "family"), v)).pack()

        default_size = self.theme_value(("font", "size")) or 12
        row = Row(p, "Text size", "Size of the bar's text in pixels. Classic TopBar uses 12.")
        row.pack(fill="x")
        Slider(row.right, 8, 18, int(f.get("size") or 12), default_size,
               fmt=lambda v: "%dpx" % v,
               command=lambda v, final: self.set_x(("font", "size"), int(v), final)).pack()

        row = Row(p, "Weight")
        row.pack(fill="x")
        Segmented(row.right, [("", "Regular"), ("Medium", "Medium"), ("Bold", "Bold")],
                  f.get("weight", ""), lambda v: self.set_x(("font", "weight"), v)).pack()

        heading(p, "Bar")
        row = Row(p, "Height", "How tall the bar is. Windows are kept clear of the whole height.")
        row.pack(fill="x")
        Slider(row.right, 18, 40, int(x["height"]), self.theme_value(("height",)) or 18,
               fmt=lambda v: "%dpx" % v,
               command=lambda v, final: self.set_x(("height",), int(v), final)).pack()

        bd = x["backdrop"]
        row = Row(p, "Backdrop", "Stock is TopBar's own tint and blur. Clear shows the wallpaper; "
                                 "Frosted is Windows' acrylic glass.")
        row.pack(fill="x")
        Segmented(row.right, [("stock", "Stock"), ("clear", "Clear"), ("blur", "Blur"),
                              ("acrylic", "Frosted")], bd["mode"],
                  lambda v: self.set_x(("backdrop", "mode"), v, rebuild=True)).pack()

        if bd["mode"] == "stock":
            st = self.cfg["settings"]
            c = st["color"]
            row = Row(p, "Bar color", "TopBar's own background tint.")
            row.pack(fill="x")
            wrap = tk.Frame(row.right, bg=PANEL)
            wrap.pack()
            Swatch(wrap, format_color(255, c["red"], c["green"], c["blue"]), self.set_bar_rgb,
                   alpha=False).pack(side="left", padx=(0, 14))
            Slider(wrap, 0, 255, int(c.get("alpha", 100)), None, width=150,
                   fmt=lambda v: "%d%%" % round(v / 2.55),
                   command=self.set_bar_alpha).pack(side="left")
            row = Row(p, "Blur behind the bar")
            row.pack(fill="x")
            Toggle(row.right, st.get("use-blur", True), self.set_blur).pack()
        else:
            row = Row(p, "Tint", "Color laid over the backdrop, and how strongly.")
            row.pack(fill="x")
            wrap = tk.Frame(row.right, bg=PANEL)
            wrap.pack()
            Swatch(wrap, bd.get("tint", "#101010"), lambda v: self.set_x(("backdrop", "tint"), v),
                   alpha=False).pack(side="left", padx=(0, 14))
            Slider(wrap, 0, 100, int(bd.get("opacity", 45)), self.theme_value(("backdrop", "opacity")),
                   width=150, fmt=lambda v: "%d%%" % v,
                   command=lambda v, final: self.set_x(("backdrop", "opacity"), int(v), final)).pack(side="left")

        row = Row(p, "Active window title", "The name of the focused window, at the left edge.")
        row.pack(fill="x")
        wrap = tk.Frame(row.right, bg=PANEL)
        wrap.pack()
        Swatch(wrap, self.cfg["settings"]["current-window"].get("color", "#ffffff"),
               self.set_title_color).pack(side="left", padx=(0, 14))
        Toggle(wrap, x["title"].get("show", True),
               lambda v: self.set_x(("title", "show"), v)).pack(side="left")

        row = Row(p, "Text color", "Recolors every text widget and the clock at once.")
        row.pack(fill="x")
        Swatch(row.right, self.first_text_color(), self.set_text_color).pack()

        heading(p, "Clock")
        cp = x["clock-panel"]
        row = Row(p, "Calendar on click", "Clicking the clock drops down a month calendar with "
                                          "a stopwatch and a timer. They keep running while it's "
                                          "closed, and a finished timer chimes and reopens it.")
        row.pack(fill="x")
        Toggle(row.right, cp.get("enabled", True),
               lambda v: self.set_x(("clock-panel", "enabled"), v)).pack()
        row = Row(p, "Week starts on")
        row.pack(fill="x")
        Segmented(row.right, [("sunday", "Sunday"), ("monday", "Monday")],
                  cp.get("week-starts", "sunday"),
                  lambda v: self.set_x(("clock-panel", "week-starts"), v)).pack()

        heading(p, "Layout")
        row = Row(p, "Arrangement", "Split puts stats on the left, the clock in the middle and "
                                    "shortcuts on the right. Classic keeps everything at the right, "
                                    "in its original order.")
        row.pack(fill="x")
        Segmented(row.right, [("classic", "Classic"), ("split", "Split")], x["layout"],
                  self.set_layout).pack()

        pl = x["pills"]
        row = Row(p, "Pills", "Rounded containers around groups of widgets. With pills on, "
                              "a separator starts a new pill instead of drawing a line.")
        row.pack(fill="x")
        Toggle(row.right, pl.get("enabled", False),
               lambda v: self.set_x(("pills", "enabled"), v, rebuild=True)).pack()
        if pl.get("enabled"):
            row = Row(p, "Pill color", "Fill and outline. Both keep their transparency.")
            row.pack(fill="x")
            wrap = tk.Frame(row.right, bg=PANEL)
            wrap.pack()
            Swatch(wrap, pl.get("fill"), lambda v: self.set_x(("pills", "fill"), v)).pack(side="left", padx=(0, 10))
            Swatch(wrap, pl.get("border"), lambda v: self.set_x(("pills", "border"), v)).pack(side="left")
            for key, label, lo, hi, desc in (
                    ("height", "Pill height", 14, 40, "Can't be taller than the bar."),
                    ("radius", "Roundness", 0, 20, "Corner radius; the far end means fully round."),
                    ("spacing", "Spacing", 4, 32, "Room between widgets inside a pill."),
                    ("gap", "Gap", 0, 32, "Room between neighbouring pills.")):
                row = Row(p, label, desc)
                row.pack(fill="x")
                value = int(pl.get(key, themes.EXTRAS_DEFAULT["pills"][key]))
                if key == "radius":
                    value = min(value, 20)
                Slider(row.right, lo, hi, value, self.theme_value(("pills", key)),
                       fmt=(lambda v: "full" if v >= 20 else "%dpx" % v) if key == "radius"
                       else (lambda v: "%dpx" % v),
                       command=lambda v, final, k=key: self.set_x(
                           ("pills", k), 99 if (k == "radius" and v >= 20) else int(v), final)).pack()

    def guess_theme(self):
        """A config that predates themes: match its bar settings to a classic
        theme, so the card for the look you already have shows as selected."""
        x = themes.extras(self.cfg)
        if x["layout"] != "classic" or x["backdrop"]["mode"] != "stock" or x["pills"].get("enabled"):
            return None
        st = self.cfg["settings"]
        for t in themes.THEMES:
            if (t["extras"]["layout"] == "classic" and t["extras"]["backdrop"]["mode"] == "stock"
                    and t["stock"]["color"] == st.get("color")
                    and t["stock"]["use-blur"] == st.get("use-blur")):
                return t["id"]
        return None

    def theme_value(self, path):
        t = themes.THEMES_BY_ID.get(themes.extras(self.cfg).get("theme"))
        v = t["extras"] if t else themes.EXTRAS_DEFAULT
        for k in path:
            v = v.get(k) if isinstance(v, dict) else None
        if path == ("pills", "radius") and v is not None:
            v = min(v, 20)
        return v

    def pick_theme(self, theme):
        themes.apply_theme(self.cfg, theme)
        self.changed(repaint=False)
        self.show("appearance", keep_scroll=True)

    def set_x(self, path, value, final=True, rebuild=False):
        node = themes.extras(self.cfg)
        for k in path[:-1]:
            node = node.setdefault(k, {})
        node[path[-1]] = value
        self.changed(final=final)
        if rebuild:
            self.show("appearance", keep_scroll=True)

    def set_layout(self, v):
        if v == "split":
            themes.to_split(self.cfg)
        else:
            themes.to_classic(self.cfg)
        themes.extras(self.cfg)["layout"] = v
        self.changed()

    def set_bar_rgb(self, v):
        a, r, g, b = parse_color(v, (255, 0, 0, 0))
        c = self.cfg["settings"]["color"]
        c["red"], c["green"], c["blue"] = r, g, b
        self.changed()

    def set_bar_alpha(self, v, final):
        self.cfg["settings"]["color"]["alpha"] = int(v)
        self.changed(final=final)

    def set_blur(self, v):
        self.cfg["settings"]["use-blur"] = bool(v)
        self.changed()

    def set_title_color(self, v):
        self.cfg["settings"]["current-window"]["color"] = v
        self.changed()

    def first_text_color(self):
        for w in self.cfg["widgets"]:
            s = w.get("settings", {})
            if w["type"] in themes.TEXT_TYPES and s.get("color"):
                return s["color"]
            if w["type"] == "TopBar.Widgets.Clock" and s.get("text-color"):
                return s["text-color"]
        return "#BBBBBB"

    def set_text_color(self, v):
        for w in self.cfg["widgets"]:
            s = w.setdefault("settings", {})
            if w["type"] in themes.TEXT_TYPES or w["type"] in (themes.GPU, themes.REMOTE):
                s["color"] = v
            elif w["type"] == "TopBar.Widgets.Clock":
                s["text-color"] = v
        self.changed()

    # -- Widgets ---------------------------------------------------------------

    def rows(self):
        """(config index, widget) for everything the user edits - the engine
        widget stays out of sight."""
        return [(i, w) for i, w in enumerate(self.cfg["widgets"]) if w["type"] != themes.ENGINE]

    def page_widgets(self, p):
        self.banner(p)
        self.preview_block(p)
        rows = self.rows()
        if self.sel is not None and self.sel not in [i for i, _ in rows]:
            self.sel = None
        if self.sel is None and rows:
            self.sel = rows[0][0]
        self.arrange_block(p)

        cols = tk.Frame(p, bg=PANEL)
        cols.pack(fill="both", expand=True, pady=(8, 0))
        cols.columnconfigure(0, weight=0, minsize=340)
        cols.columnconfigure(1, weight=1)

        left = tk.Frame(cols, bg=PANEL)
        left.grid(row=0, column=0, sticky="nw")
        heading(left, "On the bar, left to right", pady=(18, 2))
        tk.Label(left, text="Every widget in order. Drag a row to move just that one.",
                 fg=FAINT, bg=PANEL, font=font(9), anchor="w").pack(fill="x", pady=(0, 8))
        lst = tk.Frame(left, bg=CARD)
        lst.pack(fill="x")
        list_rows = []
        for i, w in rows:
            row = ListRow(lst, w, i == self.sel, lambda i=i: self.select(i))
            row.pack(fill="x")
            list_rows.append((i, row))
        ListDrag(self, lst, list_rows)
        bar = tk.Frame(left, bg=PANEL)
        bar.pack(fill="x", pady=(10, 0))
        Button(bar, "Add \u25be", self.add_menu, height=32, pad=14).pack(side="left")
        Button(bar, "Duplicate", self.duplicate, height=32, pad=12).pack(side="left", padx=(8, 0))
        Button(bar, "Remove", self.remove, height=32, pad=12).pack(side="left", padx=(8, 0))
        Button(bar, "\u2193", lambda: self.move(1), height=32, pad=12).pack(side="right")
        Button(bar, "\u2191", lambda: self.move(-1), height=32, pad=12).pack(side="right", padx=(0, 8))

        right = tk.Frame(cols, bg=PANEL)
        right.grid(row=0, column=1, sticky="nsew", padx=(28, 0))
        self.props(right)

    def arrange_block(self, p):
        head = tk.Frame(p, bg=PANEL)
        head.pack(fill="x", pady=(14, 0))
        tk.Label(head, text="Arrange", fg=TEXT, bg=PANEL, font=font(12), anchor="w").pack(side="left")
        names = order.sides_in_use(self.cfg)
        sides, _ = order.read(self.cfg)
        loc = order.locate(self.cfg, self.sel)
        nudge = {-1: loc is not None and loc != (names[0], 0),
                 1: loc is not None and loc != (names[-1], len(sides[names[-1]].pills) - 1)}
        for d, glyph, pad in ((1, "→", 0), (-1, "←", 8)):
            b = Button(head, glyph, lambda d=d: self.step_pill(d), height=32, pad=12)
            b.pack(side="right", padx=(0, pad))
            b.set_enabled(nudge[d])
        tk.Label(head, text="Move the selected pill", fg=MUTED, bg=PANEL, font=font(9)).pack(
            side="right", padx=(0, 10))
        tip = ("Drag a pill to reorder it or to move it to another side of the bar. "
               "Click one to edit it below.")
        if not order.is_split(self.cfg):
            tip += " Classic layouts line everything up on the right — a split theme " \
                   "(Floating Pills, Frosted Glass) gives the bar left, center and right."
        tk.Label(p, text=tip, fg=MUTED, bg=PANEL, font=font(9), anchor="w", justify="left",
                 wraplength=780).pack(fill="x", pady=(4, 10))
        ArrangeStrip(p, self).pack(fill="x")

    def index_of(self, widget):
        return next((i for i, w in enumerate(self.cfg["widgets"]) if w is widget), None)

    def select_widget(self, widget):
        self.select(self.index_of(widget))

    def move_pill(self, src, dst):
        pill = order.move(self.cfg, src, dst)
        self.sel = self.index_of(pill[0])
        self.widgets_changed()

    def step_pill(self, d):
        loc = order.locate(self.cfg, self.sel)
        if loc is None:
            return
        keep = self.cfg["widgets"][self.sel]
        if order.step(self.cfg, loc, d) is not None:
            self.sel = self.index_of(keep)
            self.widgets_changed()

    def move_row(self, i, slot):
        """Drop the widget at config index i into gap `slot` of the list."""
        rows = [w for _, w in self.rows()]
        ws = self.cfg["widgets"]
        w = ws[i]
        before = rows[slot] if slot < len(rows) else None
        if before is w or (slot > 0 and rows[slot - 1] is w):
            return                          # dropped right beside itself
        del ws[i]
        ws.insert(self.index_of(before) if before is not None else len(ws), w)
        self.sel = self.index_of(w)
        self.widgets_changed()

    def select(self, i):
        self.sel = i
        self.show("widgets", keep_scroll=True)

    def widgets_changed(self, rebuild=True):
        self.changed()
        if rebuild:
            self.show("widgets", keep_scroll=True)

    def add_menu(self):
        m = tk.Menu(self, tearoff=0, bg=CARD, fg=TEXT, activebackground=CARD_HI,
                    activeforeground=TEXT, bd=0, font=font(10))
        for t in SCHEMA:
            if (t.startswith("TopBarExtras") and not self.engine) or t == themes.REMOTE:
                continue                # network PCs are added from the Network page
            m.add_command(label=NAMES.get(t, t), command=lambda t=t: self.add(t))
        if self.engine:
            m.add_separator()
            for a in ("left", "center", "right"):
                m.add_command(label="Group \u2014 %s" % a,
                              command=lambda a=a: self.add(themes.GROUP, {"align": a}))
        m.tk_popup(self.winfo_pointerx(), self.winfo_pointery())
        m.grab_release()

    def add(self, wtype, settings=None):
        if settings is None:
            settings = {}
            for field in SCHEMA[wtype]:
                key, kind, default = field[0], field[1], field[2]
                if kind in ("text", "file") and default == "":
                    continue
                settings[key] = default
        ws = self.cfg["widgets"]
        at = (self.sel + 1) if self.sel is not None else len(ws)
        ws.insert(at, {"type": wtype, "settings": settings})
        if wtype == themes.GROUP:
            themes.extras(self.cfg)["layout"] = "split"
        self.sel = at
        self.widgets_changed()

    def duplicate(self):
        if self.sel is None:
            return
        ws = self.cfg["widgets"]
        ws.insert(self.sel + 1, copy.deepcopy(ws[self.sel]))
        self.sel += 1
        self.widgets_changed()

    def remove(self):
        if self.sel is None:
            return
        ws = self.cfg["widgets"]
        del ws[self.sel]
        rows = [i for i, _ in self.rows()]
        self.sel = min([i for i in rows if i >= self.sel] or rows[-1:] or [None])
        self.widgets_changed()

    def move(self, d):
        if self.sel is None:
            return
        idx = [i for i, _ in self.rows()]
        pos = idx.index(self.sel)
        if not 0 <= pos + d < len(idx):
            return
        a, b = idx[pos], idx[pos + d]
        ws = self.cfg["widgets"]
        ws[a], ws[b] = ws[b], ws[a]
        self.sel = b
        self.widgets_changed()

    def props(self, parent):
        if self.sel is None:
            tk.Label(parent, text="Add a widget to get started.", fg=MUTED, bg=PANEL,
                     font=font(10)).pack(anchor="w", pady=10)
            return
        w = self.cfg["widgets"][self.sel]
        t = w["type"]
        s = w.setdefault("settings", {})
        tk.Label(parent, text=widget_name(w), fg=TEXT, bg=PANEL, font=font(12), anchor="w").pack(
            fill="x", pady=(4, 0))
        tk.Label(parent, text=t, fg=FAINT, bg=PANEL, font=font(9, mono=True), anchor="w").pack(
            fill="x", pady=(2, 6))

        if t == themes.GROUP:
            row = Row(parent, "Position", "Where this group sits on the bar. Everything after it, "
                                          "up to the next group, goes there too.", wrap=250)
            row.pack(fill="x")
            Segmented(row.right, [("left", "Left"), ("center", "Center"), ("right", "Right")],
                      s.get("align", "right"),
                      lambda v: (s.__setitem__("align", v), self.widgets_changed())).pack()
            return
        for field in SCHEMA.get(t, []):
            key, kind, default, label = field[:4]
            tokens = field[4] if len(field) > 4 else []
            self.field(parent, s, key, kind, default, label, tokens)
        if t == "TopBar.Widgets.Shortcut":
            self.shortcut_note(parent, s)

    def field(self, parent, s, key, kind, default, label, tokens):
        row = Row(parent, label)
        row.pack(fill="x")
        if kind == "bool":
            Toggle(row.right, s.get(key, default),
                   lambda v: (s.__setitem__(key, bool(v)), self.widgets_changed(False))).pack()
        elif kind == "choice":
            Segmented(row.right, tokens, s.get(key, default),
                      lambda v: (s.__setitem__(key, v), self.widgets_changed(False))).pack()
        elif kind == "int":
            e = entry(row.right, str(s.get(key, default)), width=8)

            def commit(_e=None):
                try:
                    s[key] = int(float(e.get()))
                    self.widgets_changed(False)
                except ValueError:
                    pass
            e.bind("<KeyRelease>", commit)
        elif kind in ("text", "path", "file"):
            wrap = tk.Frame(row.right, bg=PANEL)
            wrap.pack()
            e = entry(wrap, str(s.get(key, default if kind == "text" else "") or ""), width=34, pack=False)
            e.pack(side="left")
            e.bind("<KeyRelease>", lambda _e: (s.__setitem__(key, e.get()), self.widgets_changed(False)))
            if kind in ("path", "file"):
                def browse(e=e):
                    types = ([("Applications", "*.exe;*.lnk;*.bat;*.cmd"), ("All files", "*.*")]
                             if kind == "path" else
                             [("Images", "*.png;*.ico;*.jpg;*.bmp;*.gif"), ("All files", "*.*")])
                    cur = e.get().strip('"')
                    init = (cur if os.path.isdir(cur) else os.path.dirname(cur)) if cur else \
                        os.environ.get("ProgramFiles", "C:\\")
                    got = filedialog.askopenfilename(parent=self, initialdir=init, filetypes=types)
                    if got:
                        s[key] = os.path.normpath(got)
                        self.widgets_changed()
                Button(wrap, "Browse", browse, height=32, pad=12).pack(side="left", padx=(8, 0))
        elif kind == "color":
            value = s.get(key, default)
            wrap = tk.Frame(row.right, bg=PANEL)
            wrap.pack()
            if tokens:
                mode = value if value in tokens else "custom"
                opts = [(t, t.replace("from-percentage", "by load").capitalize()) for t in tokens]
                Segmented(wrap, opts + [("custom", "Color")], mode,
                          lambda v: (s.__setitem__(key, v if v != "custom" else "#FFFFFF"),
                                     self.widgets_changed())).pack(side="left", padx=(0, 10))
                if mode != "custom":
                    return
            Swatch(wrap, value if isinstance(value, str) and value.startswith("#") else "#FFFFFF",
                   lambda v: (s.__setitem__(key, v), self.widgets_changed(False))).pack(side="left")

    def shortcut_note(self, parent, s):
        cmd = str(s.get("command", "") or "").strip()
        note, fix = None, None
        if os.path.isdir(cmd):
            note = ("That's a folder, not an app, so the shortcut shows as a blank gap and "
                    "opens the folder when clicked.")
            exe = find_main_exe(cmd)
            if exe:
                fix = ("Use %s" % os.path.basename(exe), exe)
        elif cmd and not os.path.isfile(cmd):
            note = "That path doesn't exist, so this shortcut is a blank gap."
        elif cmd and not s.get("icon"):
            note = "Icon taken automatically from %s." % os.path.basename(cmd)
        if note:
            tk.Label(parent, text=note, fg="#e57373" if fix or "exist" in note else MUTED,
                     bg=PANEL, font=font(9), wraplength=420, justify="left", anchor="w").pack(
                fill="x", pady=(12, 6))
        if fix:
            Button(parent, fix[0], lambda: (s.__setitem__("command", fix[1]), self.widgets_changed()),
                   height=32, pad=14).pack(anchor="w")

    # -- Network -----------------------------------------------------------------

    def page_network(self, p):
        ln = themes.extras(self.cfg)["lan"]
        data = lan.read_peers()
        self.net = {"sig": self.net_signature(data), "labels": {}}
        Banner(p, "Every PC with TopBar Extras on this network shares its CPU, RAM and GPU usage "
                  "and can show the others’. They find each other by themselves — there’s "
                  "nothing to type in. The first time a PC turns up, its GPUs join the left of the "
                  "bar; pick what else to show below.").pack(fill="x", pady=(0, 18))
        self.preview_block(p)

        heading(p, "This PC", pady=(14, 4))
        me = os.environ.get("COMPUTERNAME", "this PC")
        row = Row(p, "Share this PC’s usage",
                  "Lets the other PCs show %s’s CPU, RAM and GPUs." % (ln.get("name") or me))
        row.pack(fill="x")
        Toggle(row.right, ln.get("share", True), lambda v: self.set_lan("share", bool(v))).pack()

        row = Row(p, "Name on the other PCs", "Leave it empty to use the computer name, %s." % me)
        row.pack(fill="x")
        e = entry(row.right, ln.get("name", ""), width=24)
        e.bind("<KeyRelease>", lambda _e: self.set_lan("name", e.get().strip()))

        row = Row(p, "Show other PCs", "Listen for the other PCs, for their readings on this bar.")
        row.pack(fill="x")
        Toggle(row.right, ln.get("listen", True), lambda v: self.set_lan("listen", bool(v))).pack()

        row = Row(p, "Put new PCs’ GPUs on the bar",
                  "The first time a PC turns up, its GPUs join the end of the bar’s left side. "
                  "Switch them off below and they stay off.")
        row.pack(fill="x")
        Toggle(row.right, ln.get("auto-add", True), lambda v: self.set_lan("auto-add", bool(v))).pack()

        state, why = lan.firewall()
        row = Row(p, "Windows Firewall", why)
        row.pack(fill="x")
        if state == "missing":
            Button(row.right, "Allow through firewall", self.allow_firewall, height=32, pad=14).pack()
        else:
            tk.Label(row.right, text="Allowed" if state == "ok" else "Unknown",
                     fg=TEXT if state == "ok" else MUTED, bg=PANEL, font=font(10)).pack()

        # Whether the other PCs can wake this one. Asking takes a second
        # (PowerShell), so the answer fills in when it arrives.
        row = Row(p, "Let the other PCs wake this one",
                  "Arms this PC's wired network card for Wake-on-LAN. Waking it from shut down "
                  "also needs Wake-on-LAN (or “power on by PCI-E”) turned on in the BIOS.",
                  divider=False)
        row.pack(fill="x")
        self.wake_slot = tk.Frame(row.right, bg=PANEL)
        self.wake_slot.pack()
        self.show_wake_state()

        heading(p, "PCs on your network")
        peers = self.net_peers(data)
        note = None
        if data is None or not data.get("fresh"):
            note = "TopBar isn’t running, so no PCs are being heard right now."
        elif data.get("problem"):
            note = "The network side reports a problem: %s" % data["problem"]
        elif not data.get("listening"):
            note = "Showing other PCs is off, so none are being heard."
        if note:
            tk.Label(p, text=note, fg="#e5a573", bg=PANEL, font=font(10), anchor="w",
                     justify="left", wraplength=760).pack(fill="x", pady=(0, 10))
        if not peers:
            tk.Label(p, text="No other PCs yet. Run the TopBar Extras installer on them — it sets "
                             "up TopBar too if they don’t have it — and each one appears here "
                             "within a couple of seconds. Nothing showing up? Make sure the installer’s "
                             "firewall option was ticked on that PC.",
                     fg=MUTED, bg=PANEL, font=font(10), anchor="w", justify="left",
                     wraplength=760).pack(fill="x")
        for peer in peers:
            self.peer_card(p, peer)

    def show_wake_state(self):
        slot = getattr(self, "wake_slot", None)
        if slot is None or not slot.winfo_exists():
            return
        for child in slot.winfo_children():
            child.destroy()
        cached = getattr(self, "wake_state", None)
        if cached is None or time.time() - cached[0] > 30:
            tk.Label(slot, text="Checking…", fg=MUTED, bg=PANEL, font=font(10)).pack()
            if not getattr(self, "wake_checking", False):
                self.wake_checking = True

                def work():
                    result = lan.wake_ready()
                    self.after(0, lambda: self.wake_checked(result))
                threading.Thread(target=work, daemon=True).start()
            return
        armed, cards = cached[1]
        if armed:
            tk.Label(slot, text="Ready", fg=TEXT, bg=PANEL, font=font(10)).pack()
        elif armed is None or not cards:
            tk.Label(slot, text="No wired network card found" if armed is not None else "Unknown",
                     fg=MUTED, bg=PANEL, font=font(10)).pack()
        else:
            Button(slot, "Set up", self.set_up_wake, height=32, pad=14).pack()

    def wake_checked(self, result):
        self.wake_checking = False
        self.wake_state = (time.time(), result)
        self.show_wake_state()

    def set_up_wake(self):
        if lan.setup(wake=True):
            self.wake_state = None
            self.after(6000, self.show_wake_state)
        else:
            messagebox.showinfo(TITLE, "Windows didn’t show its admin prompt, so nothing was changed.")

    def net_peers(self, data):
        """The PCs being heard; then PCs heard before that are off now (from
        what the engine remembers, so they can be woken); then any with
        readings on the bar that were never remembered - so those can still
        be taken off."""
        heard = [dict(q) for q in (data or {}).get("peers") or []] if data and data.get("fresh") else []
        known = lan.known_pcs()
        for q in heard:
            q["known"] = known.get(str(q.get("id", "")).upper(), {})
        ids = {str(q.get("id", "")).upper() for q in heard}
        for pc, k in sorted(known.items(), key=lambda kv: str(kv[1].get("name") or kv[0]).lower()):
            if pc in ids or pc == os.environ.get("COMPUTERNAME", "").upper():
                continue
            heard.append({"id": pc, "name": k.get("name") or pc, "online": False,
                          "gpus": [{"name": g} for g in k.get("gpus") or []],
                          "cpu": {"name": k.get("cpu", "")}, "ram": {}, "known": k})
            ids.add(pc)
        for pc, readings in sorted(lan.on_bar(self.cfg).items()):
            if pc in ids:
                continue
            gpus = []
            for s in readings:
                if s.get("metric", "gpu") != "gpu":
                    continue
                n = int(s.get("gpu", 0) or 0)
                while len(gpus) <= n:
                    gpus.append({"name": "GPU %d" % (len(gpus) + 1)})
                if s.get("gpu-name"):
                    gpus[n] = {"name": s["gpu-name"]}
            heard.append({"id": pc, "name": pc, "online": False, "gpus": gpus, "cpu": {}, "ram": {},
                          "bar_only": {s.get("metric", "gpu") for s in readings}})
        return heard

    def net_signature(self, data):
        return (bool(data and data.get("fresh")), bool(data and data.get("listening")),
                (data or {}).get("problem", ""),
                tuple((str(q.get("id", "")).upper(), q.get("name"), bool(q.get("online")),
                       tuple(g.get("name") for g in q.get("gpus") or []),
                       bool((q.get("cpu") or {}).get("name")),
                       bool((q.get("known") or {}).get("macs")))
                      for q in self.net_peers(data)))

    def peer_card(self, parent, peer):
        pid = str(peer.get("id", "")).upper()
        card = tk.Frame(parent, bg=CARD)
        card.pack(fill="x", pady=(0, 12))
        head = tk.Frame(card, bg=CARD)
        head.pack(fill="x", padx=18, pady=(14, 10))
        name = peer.get("name") or pid
        tk.Label(head, text=name, fg=TEXT, bg=CARD, font=font(12)).pack(side="left")
        if name.upper() != pid:
            tk.Label(head, text=pid, fg=FAINT, bg=CARD, font=font(9, mono=True)).pack(
                side="left", padx=(10, 0), pady=(3, 0))
        known = peer.get("known") or {}
        if not peer.get("online") and known.get("macs"):
            Button(head, "Wake", lambda: self.wake_now(pid, known), height=28, pad=14).pack(
                side="right", padx=(12, 0))
        status = tk.Label(head, text=self.net_status(peer), fg=MUTED, bg=CARD, font=font(9))
        status.pack(side="right")
        self.net["labels"][(pid, "status")] = status
        if lan.on_bar(self.cfg).get(pid):
            # While the PC is off, its first reading on the bar becomes a wake
            # button (or everything of it hides).
            waking = all(s.get("offline", "wake") == "wake" for s in lan.on_bar(self.cfg)[pid])
            Chip(head, "Wake button when off", waking,
                 lambda v: self.set_pc_part(pid, "offline", "wake" if v else "hide")).pack(
                side="right", padx=(0, 14))

        rows = [("gpu", i, "GPU", g.get("short") or lan.short_gpu(g.get("name", "")))
                for i, g in enumerate(peer.get("gpus") or [])]
        only = peer.get("bar_only")
        if only is None or "cpu" in only:
            rows.append(("cpu", 0, "CPU", (peer.get("cpu") or {}).get("name") or ""))
        if only is None or "ram" in only:
            rows.append(("ram", 0, "RAM", ""))
        for metric, idx, kind, part in rows:
            tk.Frame(card, bg=LINE, height=1).pack(fill="x", padx=18)
            r = tk.Frame(card, bg=CARD)
            r.pack(fill="x", padx=18, pady=8)
            tk.Label(r, text=kind, fg=MUTED, bg=CARD, font=font(9, "bold"), width=5,
                     anchor="w").pack(side="left")
            tk.Label(r, text=part, fg=TEXT, bg=CARD, font=font(10), anchor="w").pack(side="left")
            on = lan.shown(self.cfg, pid, metric, idx)
            Toggle(r, on, lambda v, m=metric, i=idx: self.set_remote(peer, m, i, v)).pack(side="right")
            # How this reading looks on the bar - only while it's on it.
            parts = [("show-pc", "PC name", True), ("bar", "Bar", True)]
            if metric == "gpu":
                parts.append(("vram", "VRAM", False))
            for key, label, default in reversed(parts):
                Chip(r, label, on and lan.option(self.cfg, pid, metric, idx, key, default),
                     lambda v, m=metric, i=idx, k=key: self.set_part(pid, m, i, k, v),
                     enabled=on).pack(side="right", padx=(0, 6 if key != parts[-1][0] else 16))
            value = tk.Label(r, text=net_value(peer, metric, idx), fg=MUTED, bg=CARD,
                             font=font(10, mono=True))
            value.pack(side="right", padx=(0, 16))
            self.net["labels"][(pid, metric, idx)] = value
        tk.Frame(card, bg=CARD, height=6).pack(fill="x")

    def net_status(self, peer):
        pid = str(peer.get("id", "")).upper()
        sent = getattr(self, "woke", {}).get(pid)
        if not peer.get("online") and sent and time.time() - sent < 120:
            return "wake-up sent · waiting for it to start"
        return net_status(peer)

    def refresh_network(self):
        """Live readings in place; the whole page only when the set of PCs
        (or what they have) changes - and never under the name being typed."""
        net = getattr(self, "net", None)
        if not net:
            return
        data = lan.read_peers()
        if self.net_signature(data) != net["sig"]:
            if not isinstance(self.focus_get(), tk.Entry):
                self.show("network", keep_scroll=True)
            return
        for peer in self.net_peers(data):
            pid = str(peer.get("id", "")).upper()
            for key, label in net["labels"].items():
                if key[0] != pid or not label.winfo_exists():
                    continue
                label.configure(text=self.net_status(peer) if key[1] == "status"
                                else net_value(peer, key[1], key[2]))

    def set_lan(self, key, value):
        themes.extras(self.cfg)["lan"][key] = value
        self.changed(repaint=False)

    def set_remote(self, peer, metric, idx, on):
        if on:
            lan.add(self.cfg, peer, metric, idx)
        else:
            lan.remove(self.cfg, str(peer.get("id", "")), metric, idx)
        self.changed()
        # The row's switches follow whether the reading is on the bar.
        self.show("network", keep_scroll=True)

    def set_part(self, pid, metric, idx, key, value):
        lan.set_option(self.cfg, pid, metric, idx, key, bool(value))
        self.changed()

    def set_pc_part(self, pid, key, value):
        lan.set_pc_option(self.cfg, pid, key, value)
        self.changed()

    def wake_now(self, pid, known):
        if lan.wake(known):
            if not hasattr(self, "woke"):
                self.woke = {}
            self.woke[pid] = time.time()
            self.refresh_network()

    def allow_firewall(self):
        if lan.allow_firewall():
            self.after(3000, lambda: self.page == "network" and self.show("network", keep_scroll=True))
        else:
            messagebox.showinfo(TITLE, "Windows didn’t show its admin prompt. You can allow UDP "
                                       "port %d in Windows Defender Firewall yourself." % lan.PORT)

    # -- Monitors ----------------------------------------------------------------

    def watcher_exe(self):
        exe = os.path.join(install_dir(), "TopBarDisplayWatch.exe")
        return exe if os.path.isfile(exe) else None

    def watcher_on(self):
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
                winreg.QueryValueEx(k, "TopBarDisplayWatch")
                return True
        except OSError:
            return False

    def set_watcher(self, on):
        import winreg
        exe = self.watcher_exe()
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
            if on and exe:
                winreg.SetValueEx(k, "TopBarDisplayWatch", 0, winreg.REG_SZ, '"%s"' % exe)
            else:
                try:
                    winreg.DeleteValue(k, "TopBarDisplayWatch")
                except OSError:
                    pass
        subprocess.run(["taskkill", "/IM", "TopBarDisplayWatch.exe", "/F"], capture_output=True,
                       creationflags=subprocess.CREATE_NO_WINDOW)
        if on and exe:
            subprocess.Popen([exe], creationflags=subprocess.DETACHED_PROCESS)
        self.after(600, lambda: self.show("monitors", keep_scroll=True))

    def page_monitors(self, p):
        exe = self.watcher_exe()
        Banner(p, "TopBar only positions itself when it starts, so a monitor turning off or on "
                  "can leave it out of place. The watcher restarts it once the displays settle "
                  "\u2014 and waits if a fullscreen game has focus.").pack(fill="x", pady=(0, 18))
        row = Row(p, "Restart TopBar when a monitor changes",
                  "Runs quietly at sign-in." if exe else
                  "The watcher isn't installed \u2014 reinstall TopBar Extras to get it.")
        row.pack(fill="x")
        t = Toggle(row.right, self.watcher_on() and bool(exe), self.set_watcher)
        t.pack()
        running = "TopBarDisplayWatch.exe" in subprocess.run(
            ["tasklist", "/NH", "/FI", "IMAGENAME eq TopBarDisplayWatch.exe"],
            capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW).stdout
        row = Row(p, "Status")
        row.pack(fill="x")
        tk.Label(row.right, text="Watching" if running else "Not running",
                 fg=TEXT if running else MUTED, bg=PANEL, font=font(10)).pack()
        heading(p, "Recent activity")
        box = tk.Text(p, height=14, bg=CARD, fg="#cfcfcf", font=font(9, mono=True), relief="flat",
                      bd=0, padx=16, pady=12, wrap="none")
        try:
            with open(WATCH_LOG, encoding="utf-8", errors="replace") as fh:
                lines = fh.read().splitlines()[-40:]
        except OSError:
            lines = ["Nothing logged yet."]
        box.insert("1.0", "\n".join(reversed(lines)))
        box.configure(state="disabled")
        box.pack(fill="x")

    # -- About -------------------------------------------------------------------

    def page_about(self, p):
        heading(p, "TopBar Extras %s" % VERSION, pady=(0, 6))
        tk.Label(p, text="A settings editor and in-process engine for TopBar: themes, fonts, pills, "
                         "live changes, and the extra widgets.",
                 fg=MUTED, bg=PANEL, font=font(10), wraplength=640, justify="left", anchor="w").pack(fill="x")
        items = [("TopBar", os.path.dirname(EXE_PATH), "Open folder"),
                 ("Config file", CONFIG_PATH, "Show"),
                 ("Engine", "installed \u2014 live changes on" if self.engine else "not installed", None),
                 ("Installed to", install_dir(), "Open folder")]
        heading(p, "Locations")
        for title, value, action in items:
            row = Row(p, title, value)
            row.pack(fill="x")
            if action:
                target = value
                Button(row.right, action, lambda t=target: open_in_explorer(t), height=32,
                       pad=14).pack()
        heading(p, "Credits")
        row = Row(p, "JetBrains Mono", "Bundled font. Copyright 2020 The JetBrains Mono Project "
                                       "Authors, SIL Open Font License 1.1.", divider=False)
        row.pack(fill="x")
        lic = resource(os.path.join("fonts", "OFL.txt"))
        if lic:
            Button(row.right, "License", lambda: os.startfile(lic), height=32, pad=14).pack()


# --------------------------------------------------------------- network ---

def net_status(peer):
    if peer.get("online"):
        return "online · %s" % peer.get("address", "")
    if peer.get("bar_only") is not None:
        return "not heard since TopBar started"
    known = peer.get("known") or {}
    ago = since(known.get("seen"))
    text = "off" + (" · last seen %s" % ago if ago else "")
    if not known.get("macs"):
        text += " · can’t be woken until it’s been on once"
    return text


def since(iso):
    """'5 min ago', '3 h ago', '2 days ago' - or '' for no time. The engine
    writes UTC times in .NET's round-trip format (seven-digit fractions)."""
    import datetime
    import re as _re
    m = _re.match(r"(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)", str(iso or ""))
    if not m:
        return ""
    then = datetime.datetime(*map(int, m.groups()), tzinfo=datetime.timezone.utc)
    s = (datetime.datetime.now(datetime.timezone.utc) - then).total_seconds()
    if s < 90:
        return "just now"
    if s < 3600:
        return "%d min ago" % (s // 60)
    if s < 86400 * 2:
        return "%d h ago" % (s // 3600)
    return "%d days ago" % (s // 86400)


def net_value(peer, metric, idx):
    if not peer.get("online"):
        return "—"
    if metric == "cpu":
        return "%.0f%%" % float((peer.get("cpu") or {}).get("load", 0))
    if metric == "ram":
        ram = peer.get("ram") or {}
        return "%.1f/%.0f GB" % (float(ram.get("used", 0)), float(ram.get("total", 0)))
    gpus = peer.get("gpus") or []
    g = gpus[idx] if idx < len(gpus) else {}
    text = "%.0f%%" % float(g.get("load", 0))
    if float(g.get("vram_total", 0)):
        text += "   %.1f/%.0f GB" % (float(g.get("vram_used", 0)), float(g["vram_total"]))
    return text


# ------------------------------------------------------------- list rows ---

def widget_name(w):
    t = w["type"]
    if t == themes.GROUP:
        return "%s group" % w.get("settings", {}).get("align", "right").capitalize()
    return NAMES.get(t, t.rsplit(".", 1)[-1])


def widget_detail(w):
    s = w.get("settings", {})
    t = w["type"]
    if t == "TopBar.Widgets.Shortcut":
        return s.get("title") or os.path.basename(s.get("command", ""))
    if t == "TopBar.Widgets.CustomText":
        return (s.get("text") or "").strip()
    if t == "TopBar.Widgets.Clock":
        bits = [b for b, on in (("date", s.get("date")), ("seconds", s.get("seconds"))) if on]
        return ", ".join(bits) or "time"
    if t == "TopBarExtras.Widgets.RAMUsage":
        return s.get("format", "")
    if t == themes.GPU:
        label = s.get("label", "auto")
        return "auto name" if label == "auto" else label
    if t == themes.REMOTE:
        metric = s.get("metric", "gpu")
        part = {"cpu": "CPU", "ram": "RAM"}.get(metric) or "GPU %d" % (int(s.get("gpu", 0) or 0) + 1)
        return "%s · %s" % (s.get("pc") or "?", part)
    if t in CPU_TYPES and s.get("interval"):
        return "every %gs" % (int(s["interval"]) / 1000.0)
    return ""


class ListDrag:
    """Drag a row of the widget list to a new place; a plain click selects."""

    def __init__(self, app, frame, rows):
        self.app, self.frame, self.rows = app, frame, rows
        self.start, self.moved = None, False
        self.line = tk.Frame(frame, bg=WHITE, height=2)
        for i, row in rows:
            row.bind("<ButtonPress-1>", lambda e, i=i: self._press(i, e))
            row.bind("<B1-Motion>", self._motion)
            row.bind("<ButtonRelease-1>", lambda e, i=i: self._release(i, e))

    def _slot(self, y_root):
        return sum(1 for _, r in self.rows if y_root > r.winfo_rooty() + r.winfo_height() / 2.0)

    def _press(self, i, e):
        self.start, self.moved = (i, e.y_root), False

    def _motion(self, e):
        if self.start is None or (not self.moved and abs(e.y_root - self.start[1]) < 5):
            return
        self.moved = True
        slot = self._slot(e.y_root)
        if slot < len(self.rows):
            y = self.rows[slot][1].winfo_y()
        else:
            last = self.rows[-1][1]
            y = last.winfo_y() + last.winfo_height()
        self.line.place(x=10, y=max(0, y - 1), relwidth=1.0, width=-20)
        self.line.lift()

    def _release(self, i, e):
        start, moved = self.start, self.moved
        self.start, self.moved = None, False
        self.line.place_forget()
        if start is None:
            return
        if moved:
            self.app.move_row(i, self._slot(e.y_root))
        else:
            self.app.select(i)


class ListRow(ui._Canvas):
    def __init__(self, parent, w, selected, command):
        self.w, self.selected, self.hover = w, selected, False
        self.group = w["type"] == themes.GROUP
        super().__init__(parent, 340, 40 if self.group else 34, bg=CARD)
        self.bind("<Enter>", lambda e: self._h(True))
        self.bind("<Leave>", lambda e: self._h(False))
        self.bind("<ButtonRelease-1>", lambda e: command())
        self.configure(cursor="hand2")
        self.draw()

    def _h(self, v):
        self.hover = v
        self.draw()

    def draw(self):
        self.delete("all")
        W, H = int(self["width"]), int(self["height"])
        if self.selected or self.hover:
            rrect(self, 6, 3, W - 6, H - 3, 8, fill="#383838" if self.selected else CARD_HI, outline="")
        if self.group:
            self.create_text(18, H / 2 + 3, anchor="w", fill=MUTED, font=font(9, "bold"),
                             text=widget_name(self.w).upper())
            return
        self.create_text(20, H / 2, anchor="w", fill=TEXT, font=font(10), text=widget_name(self.w))
        detail = widget_detail(self.w)
        if detail:
            f = font(9)
            while f.measure(detail) > 130 and len(detail) > 4:
                detail = detail[:-2]
            self.create_text(W - 18, H / 2, anchor="e", fill=MUTED, font=f, text=detail)


class ThemeCard(ui._Canvas):
    def __init__(self, parent, theme, selected, command):
        self.theme, self.selected, self.hover = theme, selected, False
        super().__init__(parent, 250, 232)
        self.bind("<Enter>", lambda e: self._h(True))
        self.bind("<Leave>", lambda e: self._h(False))
        self.bind("<ButtonRelease-1>", lambda e: command())
        self.bind("<Configure>", lambda e: self.draw())
        self.configure(cursor="hand2")

    def _h(self, v):
        self.hover = v
        self.draw()

    def draw(self):
        self.delete("all")
        W, H = self.winfo_width(), int(self["height"])
        if W < 10:
            return
        rrect(self, 1, 1, W - 2, H - 2, 16, fill="#2a2a2a" if self.hover else CARD,
              outline=WHITE if self.selected else "", width=2 if self.selected else 1)
        theme_mock(self, 14, 14, W - 14, H - 72, self.theme)
        name = self.theme["name"] + ("  \u2713" if self.selected else "")
        self.create_text(16, H - 50, anchor="w", text=name, fill=TEXT, font=font(11, "bold"))
        self.create_text(16, H - 27, anchor="w", text=self.theme["sub"], fill=MUTED, font=font(10))


# ------------------------------------------------------------------- misc ---

def entry(parent, value, width=20, pack=True):
    e = tk.Entry(parent, bg=CARD, fg=TEXT, insertbackground=TEXT, relief="flat", bd=0,
                 font=font(10), width=width, highlightthickness=1,
                 highlightbackground=CARD, highlightcolor="#5a5a5a")
    e.insert(0, value)
    if pack:
        e.pack(ipady=7)
    return e


def open_in_explorer(path):
    if os.path.isfile(path):
        subprocess.Popen(["explorer.exe", "/select,", path])
    elif os.path.isdir(path):
        subprocess.Popen(["explorer.exe", path])


def bring_existing_to_front():
    """One Settings window at a time: the right-click menu can be clicked
    again while it's open."""
    user32 = ctypes.windll.user32
    hwnd = user32.FindWindowW(None, TITLE)
    if not hwnd:
        return False
    user32.ShowWindow(hwnd, 9)          # SW_RESTORE
    user32.SetForegroundWindow(hwnd)
    return True


def init_config():
    """--init, run by the installer before it restarts TopBar: put the engine
    first in config.json, so the right-click Settings entry and live changes
    work from TopBar's very next start - without opening this window."""
    try:
        cfg = load_config()
    except FileNotFoundError:
        cfg = None
    except Exception:
        return 1            # leave a config we can't parse alone
    # A PC TopBar was only just installed on gets the starter bar - stats on
    # the left, where the other PCs' GPUs will join them.
    if cfg is None or themes.is_untouched(cfg):
        cfg = themes.starter(cfg or copy.deepcopy(DEFAULT_CONFIG))
    themes.ensure_engine(cfg)
    themes.extras(cfg)
    lan.migrate(cfg)
    save_config(cfg)
    return 0


def main():
    if "--init" in sys.argv:
        sys.exit(init_config())
    if "--new" not in sys.argv and bring_existing_to_front():
        return
    app = App()
    if "--page" in sys.argv:
        app.show(sys.argv[sys.argv.index("--page") + 1])
    app.mainloop()
