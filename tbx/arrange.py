"""Arrange: the bar's pills as chips on one row per side - left, center,
right - drawn with their real contents. Drag a chip to reorder it or to move
it to another side; click one to edit it."""

import tkinter as tk

from . import order, themes
from .common import blend, hexs, parse_color
from .preview import _Measure
from .ui import CARD, FAINT, MUTED, PANEL, TEXT, WHITE, font, rrect

ROW_PAD = 8             # above and below the chips in a side's row
ROW_GAP = 8
LINE_GAP = 8            # between wrapped lines of chips
LABEL_W = 88
CHIP_H = 30
CHIP_GAP = 10
PAD = 12
EDGE = 14
MIN_SCALE = 0.75        # shrink this far to fit a row; wrap past that

CHIP = "#333333"
CHIP_HOVER = "#3b3b3b"
HOLE = "#1e1e1e"
INK = parse_color(TEXT)[1:]


def _inked(chip_rgb):
    """The widgets' own colors are chosen for the bar, not for a grey chip:
    keep the ones that still read on it and swap the rest for the UI's ink,
    at the same transparency."""
    def solid(value, default="#BBBBBB", over=None):
        a, r, g, b = parse_color(value if isinstance(value, str) else default)
        if 0.2126 * r + 0.7152 * g + 0.0722 * b < 110:
            r, g, b = INK
        return hexs(blend((a, r, g, b), over or chip_rgb))
    return solid


class ArrangeStrip(tk.Canvas):
    def __init__(self, parent, app):
        super().__init__(parent, height=CHIP_H + 2 * ROW_PAD, bg=PANEL, highlightthickness=0, bd=0)
        self.app = app
        self.chips = []         # {side, n, widgets, x0, y0, x1, y1}
        self.rows = {}          # side -> (y0, y1)
        self.hover = None       # (side, n)
        self.press = None       # (chip, x, y)
        self.drag = None        # {chip, dx, dy, x, y, target}
        self.bind("<Configure>", lambda e: self.draw())
        self.bind("<Motion>", self._motion)
        self.bind("<Leave>", self._leave)
        self.bind("<ButtonPress-1>", self._press)
        self.bind("<B1-Motion>", self._drag)
        self.bind("<ButtonRelease-1>", self._release)

    # -- layout ----------------------------------------------------------------

    def _measure(self, pill, f, scale, spacing):
        pv = self.app.preview
        solid = _inked(parse_color(CHIP)[1:])
        widths = [pv.widget(_Measure(), w, 0, 0, False, f, scale, parse_color(CHIP)[1:], solid)
                  for w in pill]
        return widths, sum(widths) + spacing * max(0, len(widths) - 1) + PAD * 2

    def _layout(self, W):
        cfg = self.app.cfg
        self.app.preview.use(cfg)
        x = themes.extras(cfg)
        sides, _ = order.read(cfg)
        names = order.sides_in_use(cfg)
        avail = W - LABEL_W - EDGE

        def measure(scale):
            f = self.app.preview.font_for(x, scale)
            spacing = 10 * scale
            rows = {a: [self._measure(p, f, scale, spacing) for p in sides[a].pills] for a in names}
            widest = max([sum(m[1] for m in ms) + CHIP_GAP * max(0, len(ms) - 1)
                          for ms in rows.values()] or [0])
            return f, spacing, rows, widest

        scale = 1.0
        f, spacing, rows, widest = measure(scale)
        if widest > avail > 0:
            scale = max(MIN_SCALE, avail / float(widest))
            f, spacing, rows, widest = measure(scale)

        # Chips that still don't fit wrap onto another line of the same row.
        self.chips, self.rows = [], {}
        y0 = 0
        for a in names:
            lines, line, used = [], [], 0
            for n, (widths, cw) in enumerate(rows[a]):
                if line and used + cw > avail:
                    lines.append(line)
                    line, used = [], 0
                line.append((n, widths, cw))
                used += cw + CHIP_GAP
            lines.append(line)
            h = 2 * ROW_PAD + len(lines) * CHIP_H + (len(lines) - 1) * LINE_GAP
            self.rows[a] = (y0, y0 + h)
            for li, line in enumerate(lines):
                cy = y0 + ROW_PAD + li * (CHIP_H + LINE_GAP) + CHIP_H / 2.0
                cx = LABEL_W
                for n, widths, cw in line:
                    self.chips.append({"side": a, "n": n, "line": li, "widgets": sides[a].pills[n],
                                       "widths": widths, "x0": cx, "y0": cy - CHIP_H / 2.0,
                                       "x1": cx + cw, "y1": cy + CHIP_H / 2.0})
                    cx += cw + CHIP_GAP
            y0 += h + ROW_GAP
        return names, f, scale, spacing

    # -- drawing ---------------------------------------------------------------

    def draw(self):
        self.delete("all")
        W = self.winfo_width()
        if W < 100:
            return
        names, f, scale, spacing = self._layout(W)
        H = max(y1 for _, y1 in self.rows.values())
        if int(self["height"]) != H:
            self.configure(height=H)
        split = order.is_split(self.app.cfg)
        target = self.drag["target"] if self.drag else None
        sel = order.locate(self.app.cfg, self.app.sel)

        for a in names:
            y0, y1 = self.rows[a]
            on = target is not None and target[0] == a
            first = y0 + ROW_PAD + CHIP_H / 2.0
            rrect(self, 0, y0, W - 1, y1, 12, fill="#2b2b2b" if on else CARD,
                  outline="#4a4a4a" if on else "")
            self.create_text(18, first, anchor="w", fill=MUTED, font=font(9, "bold"),
                             text=a.upper() if split else "BAR")
            if not any(c["side"] == a for c in self.chips):
                self.create_text(LABEL_W, first, anchor="w", fill=FAINT, font=font(9),
                                 text="Nothing on this side — drop a pill here")

        dragged = self.drag["chip"] if self.drag else None
        for c in self.chips:
            key = (c["side"], c["n"])
            if dragged is not None and key == (dragged["side"], dragged["n"]):
                rrect(self, c["x0"], c["y0"], c["x1"], c["y1"], CHIP_H / 2.0, fill=HOLE,
                      outline="#3a3a3a")
                continue
            hot = key == self.hover and self.drag is None
            picked = key == sel
            self._chip(c, c["x0"], c["y0"], f, scale, spacing,
                       CHIP_HOVER if hot or picked else CHIP, WHITE if picked else "")

        if target is not None and not order.is_noop((dragged["side"], dragged["n"]), target):
            self._caret(target)
        if self.drag is not None:
            d = self.drag
            self._chip(dragged, d["x"] - d["dx"], d["y"] - d["dy"], f, scale, spacing,
                       "#3f3f3f", WHITE)

    def _chip(self, c, x0, y0, f, scale, spacing, fill, outline):
        w = c["x1"] - c["x0"]
        rrect(self, x0, y0, x0 + w, y0 + CHIP_H, CHIP_H / 2.0, fill=fill, outline=outline,
              width=1.5 if outline else 1)
        rgb = parse_color(fill)[1:]
        solid = _inked(rgb)
        x, cy = x0 + PAD, y0 + CHIP_H / 2.0
        for widget, width in zip(c["widgets"], c["widths"]):
            self.app.preview.widget(self, widget, x, cy, True, f, scale, rgb,
                                    lambda v, d="#BBBBBB", o=None: solid(v, d, o))
            x += width + spacing

    def _caret(self, target):
        a, slot = target
        line = self.drag.get("line", 0) if self.drag else 0
        chips = [c for c in self.chips if c["side"] == a and c["line"] == line]
        here = [c for c in chips if c["n"] == slot]
        if here:
            x, c = here[0]["x0"] - CHIP_GAP / 2.0, here[0]
        elif chips:
            x, c = chips[-1]["x1"] + CHIP_GAP / 2.0, chips[-1]
        else:
            y0 = self.rows[a][0]
            x, c = LABEL_W - 4, {"y0": y0 + ROW_PAD, "y1": y0 + ROW_PAD + CHIP_H}
        rrect(self, x - 1.5, c["y0"] - 1, x + 1.5, c["y1"] + 1, 1.5, fill=WHITE, outline="")

    # -- mouse -------------------------------------------------------------------

    def chip_at(self, x, y):
        for c in self.chips:
            if c["x0"] <= x <= c["x1"] and c["y0"] <= y <= c["y1"]:
                return c
        return None

    def target_at(self, x, y):
        """The gap a chip dropped at (x, y) would land in - or None when the
        pointer has wandered well off the strip, which cancels the drop."""
        if not self.rows or not (-30 <= y <= self.winfo_height() + 30) \
                or not (-30 <= x <= self.winfo_width() + 30):
            return None

        def dist(span):
            y0, y1 = span
            return 0 if y0 <= y <= y1 else min(abs(y - y0), abs(y - y1))

        a = min(self.rows, key=lambda k: dist(self.rows[k]))
        chips = [c for c in self.chips if c["side"] == a]
        if not chips:
            return a, 0
        # the wrapped line nearest the pointer, then the gap in it
        line = min({c["line"] for c in chips},
                   key=lambda li: min(abs(y - (c["y0"] + c["y1"]) / 2.0) for c in chips if c["line"] == li))
        if self.drag is not None:
            self.drag["line"] = line
        on_line = [c for c in chips if c["line"] == line]
        after = [c for c in on_line if (c["x0"] + c["x1"]) / 2.0 >= x]
        return a, (after[0]["n"] if after else on_line[-1]["n"] + 1)

    def _motion(self, e):
        if self.drag is not None:
            return
        c = self.chip_at(e.x, e.y)
        key = (c["side"], c["n"]) if c else None
        self.configure(cursor="hand2" if c else "")
        if key != self.hover:
            self.hover = key
            self.draw()

    def _leave(self, _e):
        if self.drag is None and self.hover is not None:
            self.hover = None
            self.draw()

    def _press(self, e):
        c = self.chip_at(e.x, e.y)
        self.press = (c, e.x, e.y) if c else None

    def _drag(self, e):
        if self.press is None:
            return
        c, px, py = self.press
        if self.drag is None:
            if abs(e.x - px) + abs(e.y - py) < 5:
                return
            self.drag = {"chip": c, "dx": px - c["x0"], "dy": py - c["y0"]}
            self.configure(cursor="fleur")
        self.drag.update(x=e.x, y=e.y, target=self.target_at(e.x, e.y))
        self.draw()

    def _release(self, e):
        press, drag = self.press, self.drag
        self.press = self.drag = None
        if drag is not None:
            c, t = drag["chip"], self.target_at(e.x, e.y)
            src = (c["side"], c["n"])
            if t is not None and not order.is_noop(src, t):
                self.app.move_pill(src, t)      # rebuilds the page - this strip included
                return
            self.configure(cursor="hand2" if self.chip_at(e.x, e.y) else "")
            self.draw()
        elif press is not None:
            self.app.select_widget(press[0]["widgets"][0])
