"""Forge-style controls for tkinter.

Plain ttk can't do rounded pills, cards or switches, so these draw themselves
on canvases. They share one palette and a few fonts so the whole window reads
as a single system: near-black panels, soft grey cards, white primary actions.
"""

import os
import tkinter as tk
import tkinter.font as tkfont
from tkinter import colorchooser

from .common import format_color, hexs, parse_color, blend

# ---------------------------------------------------------------- palette ---

WIN = "#111111"
PANEL = "#1b1b1b"
CARD = "#262626"
CARD_HI = "#2f2f2f"
LINE = "#2b2b2b"
TEXT = "#ececec"
MUTED = "#8d8d8d"
FAINT = "#5e5e5e"
WHITE = "#f4f4f4"
INK = "#121212"
KNOB = "#f4f4f4"

UI_FAMILY = "Segoe UI"
MONO_FAMILY = "Cascadia Mono"      # replaced by JetBrains Mono once loaded
_fonts = {}


def load_private_fonts(folder):
    """Make bundled .ttf files usable by Tk without installing them."""
    global MONO_FAMILY
    if not folder or not os.path.isdir(folder):
        return
    import ctypes
    loaded = 0
    for name in os.listdir(folder):
        if name.lower().endswith(".ttf"):
            loaded += ctypes.windll.gdi32.AddFontResourceExW(
                os.path.join(folder, name), 0x10, 0)   # FR_PRIVATE
    if loaded:
        MONO_FAMILY = "JetBrains Mono"


def font(size=10, weight="normal", family=None, mono=False):
    fam = family or (MONO_FAMILY if mono else UI_FAMILY)
    key = (fam, size, weight)
    if key not in _fonts:
        _fonts[key] = tkfont.Font(family=fam, size=size, weight=weight)
    return _fonts[key]


def rrect(cv, x0, y0, x1, y1, r, **kw):
    r = max(0, min(r, (x1 - x0) / 2.0, (y1 - y0) / 2.0))
    pts = [x0 + r, y0, x1 - r, y0, x1, y0, x1, y0 + r, x1, y1 - r, x1, y1,
           x1 - r, y1, x0 + r, y1, x0, y1, x0, y1 - r, x0, y0 + r, x0, y0]
    return cv.create_polygon(pts, smooth=True, **kw)


def _bg_of(widget):
    try:
        return widget.cget("bg")
    except tk.TclError:
        return PANEL


class _Canvas(tk.Canvas):
    def __init__(self, parent, width, height, **kw):
        super().__init__(parent, width=width, height=height,
                         bg=kw.pop("bg", _bg_of(parent)),
                         highlightthickness=0, bd=0, **kw)


# ---------------------------------------------------------------- buttons ---

class Button(_Canvas):
    """Pill button. kind: primary (white), secondary (grey), ghost."""

    def __init__(self, parent, text, command=None, kind="secondary",
                 size=10, pad=18, height=34):
        f = font(size, "bold" if kind == "primary" else "normal")
        w = f.measure(text) + pad * 2
        super().__init__(parent, w, height)
        self.text, self.kind, self.f, self.command = text, kind, f, command
        self.hover = False
        self.enabled = True
        self.bind("<Enter>", lambda e: self._set(True))
        self.bind("<Leave>", lambda e: self._set(False))
        self.bind("<ButtonRelease-1>", self._click)
        self.configure(cursor="hand2")
        self.draw()

    def _set(self, hover):
        self.hover = hover
        self.draw()

    def _click(self, _e):
        if self.enabled and self.command:
            self.command()

    def set_enabled(self, on):
        self.enabled = on
        self.configure(cursor="hand2" if on else "arrow")
        self.draw()

    def draw(self):
        self.delete("all")
        w, h = int(self["width"]), int(self["height"])
        if self.kind == "primary":
            fill = "#ffffff" if self.hover else WHITE
            ink = INK
        elif self.kind == "ghost":
            fill = CARD if self.hover else self["bg"]
            ink = TEXT
        else:
            fill = "#383838" if self.hover else CARD_HI
            ink = TEXT
        if not self.enabled:
            fill, ink = CARD, FAINT
        rrect(self, 0, 0, w - 1, h - 1, h / 2, fill=fill, outline="")
        self.create_text(w / 2, h / 2, text=self.text, fill=ink, font=self.f)


class NavItem(_Canvas):
    """Sidebar entry with a rounded highlight when selected or hovered."""

    def __init__(self, parent, text, command, width=196):
        super().__init__(parent, width, 38)
        self.text, self.command = text, command
        self.selected = self.hover = False
        self.bind("<Enter>", lambda e: self._h(True))
        self.bind("<Leave>", lambda e: self._h(False))
        self.bind("<ButtonRelease-1>", lambda e: command())
        self.configure(cursor="hand2")
        self.draw()

    def _h(self, v):
        self.hover = v
        self.draw()

    def select(self, on):
        self.selected = on
        self.draw()

    def draw(self):
        self.delete("all")
        w, h = int(self["width"]), int(self["height"])
        if self.selected or self.hover:
            rrect(self, 0, 1, w - 1, h - 2, 8,
                  fill=CARD_HI if self.selected else CARD, outline="")
        self.create_text(12, h / 2, text=self.text, anchor="w",
                         fill=TEXT if self.selected else "#d5d5d5",
                         font=font(11))


# ---------------------------------------------------------------- inputs ---

class Toggle(_Canvas):
    def __init__(self, parent, value=False, command=None):
        super().__init__(parent, 44, 24)
        self.value, self.command = bool(value), command
        self.bind("<ButtonRelease-1>", self._flip)
        self.configure(cursor="hand2")
        self.draw()

    def _flip(self, _e=None):
        self.value = not self.value
        self.draw()
        if self.command:
            self.command(self.value)

    def set(self, v):
        self.value = bool(v)
        self.draw()

    def draw(self):
        self.delete("all")
        on = self.value
        rrect(self, 1, 1, 43, 23, 11, fill=WHITE if on else "#3a3a3a", outline="")
        x = 32 if on else 12
        self.create_oval(x - 8, 4, x + 8, 20, fill=INK if on else "#bdbdbd", outline="")


class Chip(_Canvas):
    """A small labelled on/off switch - a word in a pill that lights up when
    it's on. For rows with several of them, where full toggles won't fit."""

    def __init__(self, parent, text, value=False, command=None, enabled=True):
        self.text, self.value, self.command, self.enabled = text, bool(value), command, enabled
        self.f = font(9)
        self.hover = False
        super().__init__(parent, self.f.measure(text) + 24, 24)
        self.bind("<Enter>", lambda e: self._h(True))
        self.bind("<Leave>", lambda e: self._h(False))
        self.bind("<ButtonRelease-1>", self._flip)
        self.configure(cursor="hand2" if enabled else "arrow")
        self.draw()

    def _h(self, v):
        self.hover = v
        self.draw()

    def _flip(self, _e=None):
        if not self.enabled:
            return
        self.value = not self.value
        self.draw()
        if self.command:
            self.command(self.value)

    def draw(self):
        self.delete("all")
        W, H = int(self["width"]), int(self["height"])
        if not self.enabled:
            fill, edge, ink = "", "#333333", "#4f4f4f"
        elif self.value:
            fill, edge, ink = ("#f4f4f4" if self.hover else "#dedede"), "", INK
        else:
            fill, edge, ink = ("#333333" if self.hover else ""), "#3d3d3d", MUTED
        rrect(self, 1, 1, W - 2, H - 2, (H - 3) / 2.0, fill=fill or self["bg"], outline=edge)
        self.create_text(W / 2.0, H / 2.0, text=self.text, fill=ink, font=self.f)


class Slider(tk.Frame):
    """Thin track, white fill and knob, a reset arrow and a mono value label -
    the Forge 'List spacing' control."""

    def __init__(self, parent, lo, hi, value, default=None, step=1,
                 fmt=lambda v: str(v), command=None, width=184):
        super().__init__(parent, bg=_bg_of(parent))
        self.lo, self.hi, self.step = lo, hi, step
        self.value = value
        self.default = default
        self.fmt, self.command = fmt, command
        self.w = width
        self.cv = _Canvas(self, width, 24)
        self.cv.pack(side="left")
        self.reset = tk.Label(self, text="↺", fg=FAINT, bg=self["bg"],
                              font=font(11), cursor="hand2")
        self.reset.pack(side="left", padx=(10, 6))
        self.reset.bind("<Button-1>", lambda e: self._reset())
        self.label = tk.Label(self, text="", fg=MUTED, bg=self["bg"],
                              font=font(9, mono=True), width=16, anchor="w")
        self.label.pack(side="left")
        for ev in ("<Button-1>", "<B1-Motion>"):
            self.cv.bind(ev, self._drag)
        self.cv.bind("<ButtonRelease-1>", lambda e: self._commit())
        self.cv.configure(cursor="hand2")
        self.draw()

    def _x_for(self, v):
        span = float(self.hi - self.lo) or 1.0
        return 10 + (self.w - 20) * (v - self.lo) / span

    def _drag(self, e):
        span = self.hi - self.lo
        v = self.lo + span * (e.x - 10) / float(self.w - 20)
        v = round(v / self.step) * self.step
        v = max(self.lo, min(self.hi, v))
        if v != self.value:
            self.value = v
            self.draw()
            if self.command:
                self.command(v, final=False)

    def _commit(self):
        if self.command:
            self.command(self.value, final=True)

    def _reset(self):
        if self.default is not None and self.value != self.default:
            self.value = self.default
            self.draw()
            self._commit()

    def set(self, v, default=None):
        self.value = v
        if default is not None:
            self.default = default
        self.draw()

    def draw(self):
        cv = self.cv
        cv.delete("all")
        x = self._x_for(self.value)
        rrect(cv, 10, 9, self.w - 10, 15, 3, fill="#3a3a3a", outline="")
        rrect(cv, 10, 9, max(16, x), 15, 3, fill="#e4e4e4", outline="")
        cv.create_oval(x - 9, 3, x + 9, 21, fill=KNOB, outline="")
        text = self.fmt(self.value)
        if self.default is not None and self.value == self.default:
            text += " · default"
        self.label.configure(text=text)
        self.reset.configure(fg=FAINT if self.value == self.default else MUTED)


class Segmented(_Canvas):
    def __init__(self, parent, options, value, command=None):
        """options: list of (value, label)."""
        self.options, self.value, self.command = options, value, command
        f = font(10)
        self.widths = [f.measure(lbl) + 26 for _, lbl in options]
        super().__init__(parent, sum(self.widths) + 8, 34)
        self.bind("<ButtonRelease-1>", self._click)
        self.configure(cursor="hand2")
        self.draw()

    def _click(self, e):
        x = 4
        for (val, _), w in zip(self.options, self.widths):
            if x <= e.x < x + w:
                if val != self.value:
                    self.value = val
                    self.draw()
                    if self.command:
                        self.command(val)
                return
            x += w

    def set(self, v):
        self.value = v
        self.draw()

    def draw(self):
        self.delete("all")
        W, H = int(self["width"]), int(self["height"])
        rrect(self, 0, 0, W - 1, H - 1, H / 2, fill=CARD, outline="")
        x = 4
        for (val, lbl), w in zip(self.options, self.widths):
            on = val == self.value
            if on:
                rrect(self, x, 4, x + w, H - 5, (H - 9) / 2, fill=WHITE, outline="")
            self.create_text(x + w / 2, H / 2, text=lbl, font=font(10),
                             fill=INK if on else "#cfcfcf")
            x += w


class Dropdown(_Canvas):
    def __init__(self, parent, options, value, command=None, width=240):
        """options: list of (value, label)."""
        self.options, self.value, self.command = options, value, command
        super().__init__(parent, width, 34)
        self.bind("<ButtonRelease-1>", self._open)
        self.configure(cursor="hand2")
        self.draw()

    def _label(self):
        for v, lbl in self.options:
            if v == self.value:
                return lbl
        return str(self.value) or "Default"

    def _open(self, _e):
        m = tk.Menu(self, tearoff=0, bg=CARD, fg=TEXT, activebackground=CARD_HI,
                    activeforeground=TEXT, bd=0, relief="flat", font=font(10))
        for v, lbl in self.options:
            m.add_command(label=("✓  " if v == self.value else "     ") + lbl,
                          command=lambda v=v: self._pick(v))
        try:
            m.tk_popup(self.winfo_rootx(), self.winfo_rooty() + int(self["height"]) + 4)
        finally:
            m.grab_release()

    def _pick(self, v):
        self.value = v
        self.draw()
        if self.command:
            self.command(v)

    def set(self, v):
        self.value = v
        self.draw()

    def draw(self):
        self.delete("all")
        W, H = int(self["width"]), int(self["height"])
        rrect(self, 0, 0, W - 1, H - 1, 10, fill=CARD, outline="")
        self.create_text(14, H / 2, text=self._label(), anchor="w", fill=TEXT,
                         font=font(10))
        self.create_text(W - 16, H / 2 + 1, text="▾", fill=MUTED, font=font(10))


class Swatch(_Canvas):
    """Color well; click to pick. Keeps the alpha of the current value."""

    def __init__(self, parent, value, command=None, alpha=True):
        super().__init__(parent, 46, 28)
        self.value, self.command, self.alpha = value, command, alpha
        self.bind("<ButtonRelease-1>", self._pick)
        self.configure(cursor="hand2")
        self.draw()

    def _pick(self, _e):
        a, r, g, b = parse_color(self.value, (255, 255, 255, 255))
        rgb, _ = colorchooser.askcolor(color=hexs((r, g, b)), parent=self)
        if rgb:
            self.value = format_color(a if self.alpha else 255,
                                      *[int(c) for c in rgb])
            self.draw()
            if self.command:
                self.command(self.value)

    def set(self, v):
        self.value = v
        self.draw()

    def draw(self):
        self.delete("all")
        a, r, g, b = parse_color(self.value, (255, 255, 255, 255))
        rrect(self, 0, 0, 45, 27, 8, fill="#4a4a4a", outline="")
        for x in range(4, 42, 6):
            for y in range(4, 24, 6):
                if (x // 6 + y // 6) % 2:
                    self.create_rectangle(x, y, x + 6, y + 6, fill="#2e2e2e", width=0)
        rrect(self, 3, 3, 42, 24, 6,
              fill=hexs(blend((a, r, g, b), (58, 58, 58))), outline="")


# ---------------------------------------------------------------- surfaces ---

class Banner(tk.Frame):
    """Rounded note with a light left edge: 'Changes apply immediately...'"""

    def __init__(self, parent, text):
        super().__init__(parent, bg=_bg_of(parent))
        self.cv = _Canvas(self, 100, 58)
        self.cv.pack(fill="x")
        self.text = text
        self.cv.bind("<Configure>", lambda e: self.draw())

    def set(self, text):
        self.text = text
        self.draw()

    def draw(self):
        cv = self.cv
        cv.delete("all")
        W = cv.winfo_width()
        if W < 40:
            return
        # Lay the text out first so the card can grow to fit it.
        t = cv.create_text(22, 0, text=self.text, anchor="nw", fill="#cfcfcf",
                           font=font(10), width=W - 44)
        x0, y0, x1, y1 = cv.bbox(t)
        H = max(58, (y1 - y0) + 36)
        if int(cv["height"]) != H:
            cv.configure(height=H)
        cv.coords(t, 22, (H - (y1 - y0)) / 2.0)
        back = [rrect(cv, 0, 0, W - 1, H - 1, 12, fill=CARD, outline=""),
                rrect(cv, 0, 0, 16, H - 1, 12, fill="#d0d0d0", outline=""),
                rrect(cv, 3, 0, 18, H - 1, 10, fill=CARD, outline="")]
        for item in reversed(back):
            cv.tag_lower(item)


class Scroll(tk.Frame):
    """Vertical scroll area with a slim rounded thumb."""

    def __init__(self, parent, bg=PANEL):
        super().__init__(parent, bg=bg)
        self.cv = tk.Canvas(self, bg=bg, highlightthickness=0, bd=0)
        self.bar = _Canvas(self, 10, 100, bg=bg)
        self.bar.pack(side="right", fill="y", padx=(0, 6), pady=6)
        self.cv.pack(side="left", fill="both", expand=True)
        self.body = tk.Frame(self.cv, bg=bg)
        self.win = self.cv.create_window(0, 0, window=self.body, anchor="nw")
        self.body.bind("<Configure>", lambda e: self._sync())
        self.cv.bind("<Configure>", self._fit)
        self.bar.bind("<B1-Motion>", self._drag)
        self.bar.bind("<Button-1>", self._drag)
        # The window has one scroll area. Binding per-widget Enter/Leave would
        # drop the wheel every time the pointer crossed into a child, so route
        # the wheel app-wide instead.
        self.cv.bind_all("<MouseWheel>", self._on_wheel)

    def _fit(self, e):
        self.cv.itemconfigure(self.win, width=e.width)
        self._sync()

    def _on_wheel(self, e):
        if self.body.winfo_height() > self.cv.winfo_height():
            self.cv.yview_scroll(int(-e.delta / 40), "units")
            self._draw_bar()

    def _drag(self, e):
        h = self.bar.winfo_height() or 1
        self.cv.yview_moveto(max(0.0, min(1.0, e.y / float(h))))
        self._draw_bar()

    def _sync(self):
        self.cv.configure(scrollregion=(0, 0, self.body.winfo_width(),
                                        self.body.winfo_height()))
        self._draw_bar()

    def top(self):
        self.cv.yview_moveto(0)
        self._draw_bar()

    def _draw_bar(self):
        b = self.bar
        b.delete("all")
        lo, hi = self.cv.yview()
        if hi - lo >= 0.999:
            return
        H = b.winfo_height()
        rrect(b, 2, lo * H, 8, max(lo * H + 24, hi * H), 3, fill="#3c3c3c", outline="")


class Row(tk.Frame):
    """Title and description on the left, a control on the right, a hairline
    under it - one setting in the Forge layout."""

    def __init__(self, parent, title, desc="", divider=True, wrap=420):
        super().__init__(parent, bg=_bg_of(parent))
        self.columnconfigure(0, weight=1)
        left = tk.Frame(self, bg=self["bg"])
        left.grid(row=0, column=0, sticky="nw", pady=(14, 14))
        tk.Label(left, text=title, fg=TEXT, bg=self["bg"], font=font(11),
                 anchor="w").pack(anchor="w")
        if desc:
            self.desc = tk.Label(left, text=desc, fg=MUTED, bg=self["bg"],
                                 font=font(9), justify="left", anchor="w",
                                 wraplength=wrap)
            self.desc.pack(anchor="w", pady=(3, 0))
        self.right = tk.Frame(self, bg=self["bg"])
        self.right.grid(row=0, column=1, sticky="e", padx=(24, 0))
        if divider:
            tk.Frame(self, bg=LINE, height=1).grid(row=1, column=0, columnspan=2,
                                                   sticky="ew")


def heading(parent, text, pady=(22, 10)):
    lbl = tk.Label(parent, text=text, fg=TEXT, bg=_bg_of(parent), font=font(12),
                   anchor="w")
    lbl.pack(fill="x", pady=pady)
    return lbl
