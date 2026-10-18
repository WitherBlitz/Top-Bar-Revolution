"""The bar's order the way the engine reads it: sides -> pills -> widgets.

TopBar lays its widgets out in config order. The engine cuts that order into
left / center / right sides at each Group marker, and cuts each side into
pills at each separator line (without pills the lines are drawn instead, but
they divide the bar into the same runs). Moving a pill rewrites the widget
list from this model, so every side keeps one Group marker and exactly one
separator stands between neighbouring pills.
"""

import copy

from . import themes

SEP = "TopBar.Widgets.LineSpacer"
SIDES = ("left", "center", "right")


class Side:
    def __init__(self, align):
        self.align = align
        self.marker = None      # its Group widget, kept for anything set on it
        self.lead = []          # separators before the first pill
        self.pills = []         # [[widget, ...], ...]
        self.trail = []         # separators after the last pill


def is_split(cfg):
    return themes.extras(cfg)["layout"] == "split"


def sides_in_use(cfg):
    """Classic layouts put everything on the right, after the window title."""
    return SIDES if is_split(cfg) else ("right",)


def read(cfg):
    """({align: Side}, the separators between pills, in order)."""
    split = is_split(cfg)
    sides = {a: Side(a) for a in SIDES}
    inner = []
    side, pill, pending = sides["right"], None, []
    for w in cfg.get("widgets", []):
        t = w.get("type")
        if t == themes.ENGINE:
            continue
        if t == themes.GROUP:
            side.trail.extend(pending)
            pending, pill = [], None
            if split:
                align = w.get("settings", {}).get("align", "right")
                side = sides[align if align in sides else "right"]
                if side.marker is None:
                    side.marker = w
            continue
        if t == SEP:
            pending.append(w)
            pill = None
            continue
        if pill is None:
            if side.pills:
                inner.extend(side.trail + pending)
                side.trail = []
            else:
                side.lead.extend(pending)
            pending, pill = [], []
            side.pills.append(pill)
        pill.append(w)
    side.trail.extend(pending)
    return sides, inner


def new_separator(cfg):
    theme = themes.THEMES_BY_ID.get(themes.extras(cfg).get("theme"))
    color = theme["widgets"]["separator"] if theme else "#888888"
    return {"type": SEP, "settings": {"color": color, "width": 1, "height": 10}}


def write(cfg, sides, inner):
    """Rebuild the widget list from the model. The separators that stood
    between pills are reused in order, so custom ones survive a move."""
    ws = cfg["widgets"]
    out = [w for w in ws if w.get("type") == themes.ENGINE][:1]
    pool = list(inner)
    spare = next(iter(inner + [s for side in sides.values() for s in side.lead + side.trail]), None)

    def separator():
        if pool:
            return pool.pop(0)
        return copy.deepcopy(spare) if spare else new_separator(cfg)

    split = is_split(cfg)
    for a in sides_in_use(cfg):
        side = sides[a]
        if not side.pills:
            continue
        if split:
            out.append(side.marker or {"type": themes.GROUP, "settings": {"align": a}})
        out.extend(side.lead)
        for i, pill in enumerate(side.pills):
            if i:
                out.append(separator())
            out.extend(pill)
        out.extend(side.trail)
    ws[:] = out


def locate(cfg, index):
    """(side, pill number) holding the widget at a config index, or None for
    the engine, a Group marker or a separator."""
    ws = cfg.get("widgets", [])
    if index is None or not 0 <= index < len(ws):
        return None
    target = ws[index]
    sides, _ = read(cfg)
    for a, side in sides.items():
        for n, pill in enumerate(side.pills):
            if any(w is target for w in pill):
                return a, n
    return None


def is_noop(src, dst):
    """Dropping a pill into either gap beside it changes nothing."""
    return src[0] == dst[0] and dst[1] in (src[1], src[1] + 1)


def move(cfg, src, dst):
    """Move pill src = (side, n) into gap dst = (side, slot), counted as the
    side stands now: 0 is before its first pill, len(pills) after its last.
    Returns the moved pill's widgets."""
    sides, inner = read(cfg)
    a, n = src
    b, slot = dst
    pill = sides[a].pills.pop(n)
    if a == b and slot > n:
        slot -= 1
    sides[b].pills.insert(max(0, min(slot, len(sides[b].pills))), pill)
    write(cfg, sides, inner)
    return pill


def step(cfg, src, d):
    """One place left (d = -1) or right (d = +1) along the bar. Past the end
    of a side a pill carries on to the next side, even an empty one. Returns
    the moved pill, or None at either end of the bar."""
    sides, _ = read(cfg)
    names = sides_in_use(cfg)
    a, n = src
    i = names.index(a)
    if d < 0:
        if n > 0:
            return move(cfg, src, (a, n - 1))
        if i == 0:
            return None
        prev = names[i - 1]
        return move(cfg, src, (prev, len(sides[prev].pills)))
    if n < len(sides[a].pills) - 1:
        return move(cfg, src, (a, n + 2))
    if i == len(names) - 1:
        return None
    return move(cfg, src, (names[i + 1], 0))
