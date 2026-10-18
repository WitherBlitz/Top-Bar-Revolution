"""Network PCs, the Settings side: what the engine has heard (peers.json),
putting another PC's readings on the bar or taking them off, and the Windows
Firewall rule that lets the other PCs' broadcasts in.

The engine inside TopBar does the networking itself (extension/Lan.cs); this
window only reads what it writes and edits config.json."""

import ctypes
import os
import re
import subprocess
import time

from . import order, themes
from .common import APP_DIR

REMOTE = "TopBarExtras.Widgets.RemoteStat"
PEERS_PATH = os.path.join(APP_DIR, "peers.json")
WANT_PATH = os.path.join(APP_DIR, "peers.want")
PORT = 51736
RULE = "TopBar Extras LAN"
NO_WINDOW = 0x08000000


def read_peers():
    """What the engine last wrote, plus "fresh": False once TopBar has stopped
    writing it (it rewrites the file at least once a minute). None if there's
    no file yet."""
    import json
    try:
        with open(PEERS_PATH, encoding="utf-8-sig") as fh:
            data = json.load(fh)
        age = time.time() - os.path.getmtime(PEERS_PATH)
    except (OSError, ValueError):
        return None
    data["fresh"] = age < 90
    return data


def want_live():
    """Tell the engine someone is looking, so it rewrites peers.json every
    couple of seconds instead of once a minute."""
    try:
        with open(WANT_PATH, "a"):
            pass
        os.utime(WANT_PATH, None)
    except OSError:
        pass


def short_gpu(name):
    """'NVIDIA GeForce RTX 4060' -> 'RTX 4060', as the engine shortens it."""
    s = re.sub(r"\b(NVIDIA|GeForce|AMD|ATI|Radeon|Intel)\b|\((R|TM)\)", "", name or "", flags=re.I)
    return re.sub(r"\s+", " ", s).strip() or (name or "GPU")


# ------------------------------------------------------------ on the bar ---

def reading_key(w):
    s = w.get("settings", {})
    metric = s.get("metric", "gpu")
    return (str(s.get("pc", "")).upper(), metric, int(s.get("gpu", 0) or 0) if metric == "gpu" else 0)


def shown(cfg, pc, metric, gpu=0):
    key = (pc.upper(), metric, gpu if metric == "gpu" else 0)
    return any(w.get("type") == REMOTE and reading_key(w) == key for w in cfg.get("widgets", []))


def on_bar(cfg):
    """{pc: [settings of each of its readings]} for everything already on the bar."""
    out = {}
    for w in cfg.get("widgets", []):
        if w.get("type") == REMOTE:
            s = w.get("settings", {})
            out.setdefault(str(s.get("pc", "")).upper(), []).append(s)
    return out


def _style(cfg):
    """Look like this bar's own GPU readout, which the theme colors - or, if
    there isn't one, its RAM bar and text."""
    ws = cfg.get("widgets", [])

    def first(t):
        return next((w.get("settings", {}) for w in ws if w.get("type") == t), None)

    gpu = first(themes.GPU)
    if gpu:
        return {k: gpu[k] for k in ("color", "bar-color", "background-color", "width",
                                    "height", "radius", "value-position") if k in gpu}
    style = {}
    ram = first("TopBar.Widgets.RAMUsageBar")
    if ram:
        if "color" in ram:
            style["bar-color"] = ram["color"]
        style.update({k: ram[k] for k in ("background-color", "width", "height", "radius") if k in ram})
    text = first("TopBar.Widgets.CustomText")
    if text and "color" in text:
        style["color"] = text["color"]
    return style


def add(cfg, peer, metric, gpu=0):
    """Put a reading from another PC on the bar, in a pill of its own at the
    end of the left side (the far left of the strip in a classic layout)."""
    s = {"pc": str(peer.get("id", "")).upper(), "metric": metric, "label": "auto",
         "format": "{used:0.0}/{total:0} GB" if metric == "ram" else "{percent:0}%",
         "value-position": "before", "offline": "wake",
         "show-pc": True, "bar": True, "vram": False}
    if metric == "gpu":
        s["gpu"] = gpu
        gpus = peer.get("gpus") or []
        if gpu < len(gpus) and gpus[gpu].get("name"):
            s["gpu-name"] = gpus[gpu]["name"]
    s.update(_style(cfg))
    w = {"type": REMOTE, "settings": s}
    sides, inner = order.read(cfg)
    if order.is_split(cfg):
        sides["left"].pills.append([w])
    else:
        sides["right"].pills.insert(0, [w])
    order.write(cfg, sides, inner)
    remember(cfg, s["pc"])
    return w


def remove(cfg, pc, metric, gpu=0):
    key = (pc.upper(), metric, gpu if metric == "gpu" else 0)
    sides, inner = order.read(cfg)
    for side in sides.values():
        side.pills = [[w for w in p if not (w.get("type") == REMOTE and reading_key(w) == key)]
                      for p in side.pills]
        side.pills = [p for p in side.pills if p]
    order.write(cfg, sides, inner)
    remember(cfg, pc)


def option(cfg, pc, metric, gpu, key, default):
    """A setting of a reading on the bar (the first, if it's there twice)."""
    want = (pc.upper(), metric, gpu if metric == "gpu" else 0)
    for w in cfg.get("widgets", []):
        if w.get("type") == REMOTE and reading_key(w) == want:
            return w.get("settings", {}).get(key, default)
    return default


def set_option(cfg, pc, metric, gpu, key, value):
    want = (pc.upper(), metric, gpu if metric == "gpu" else 0)
    for w in cfg.get("widgets", []):
        if w.get("type") == REMOTE and reading_key(w) == want:
            w.setdefault("settings", {})[key] = value


def set_pc_option(cfg, pc, key, value):
    """The same setting on every reading of one PC - e.g. its wake button."""
    for w in cfg.get("widgets", []):
        if w.get("type") == REMOTE and str(w.get("settings", {}).get("pc", "")).upper() == pc.upper():
            w["settings"][key] = value


def migrate(cfg):
    """Readings added before Wake-on-LAN existed hid while their PC was off;
    from 1.5 the first one turns into a wake button instead. Done once, so a
    reading set back to "hide" stays that way. True if anything changed."""
    ln = themes.extras(cfg).setdefault("lan", {})
    if ln.get("wake-default"):
        return False
    ln["wake-default"] = True
    for w in cfg.get("widgets", []):
        s = w.get("settings", {})
        if w.get("type") == REMOTE and s.get("offline", "hide") == "hide":
            s["offline"] = "wake"
    return True


# ---------------------------------------------------------- Wake-on-LAN ---

KNOWN_PATH = os.path.join(APP_DIR, "known-pcs.json")


def known_pcs():
    """{PC: what the engine remembers of it - name, MACs, last address, parts}."""
    import json
    try:
        with open(KNOWN_PATH, encoding="utf-8-sig") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return {}
    return {str(k).upper(): v for k, v in data.items() if isinstance(v, dict)}


def _mac_bytes(mac):
    h = re.sub(r"[^0-9A-Fa-f]", "", mac or "")
    return bytes.fromhex(h) if len(h) == 12 else None


def wake(known):
    """Send Wake-on-LAN magic packets for a remembered PC: six FF bytes then
    its MAC sixteen times, broadcast to its subnet and to everyone, on ports 9
    and 7 - the same as the bar's wake button. False if no MAC is known."""
    import socket
    macs = [m for m in (_mac_bytes(x) for x in known.get("macs") or []) if m]
    if not macs:
        return False
    targets = ["255.255.255.255"]
    try:
        import ipaddress
        net = ipaddress.IPv4Network("%s/%s" % (known.get("address"), known.get("mask")), strict=False)
        targets.insert(0, str(net.broadcast_address))
    except ValueError:
        pass
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    try:
        for _ in range(3):
            for mac in macs:
                packet = b"\xff" * 6 + mac * 16
                for host in targets:
                    for port in (9, 7):
                        try:
                            s.sendto(packet, (host, port))
                        except OSError:
                            pass
            time.sleep(0.15)
    finally:
        s.close()
    return True


def wake_ready():
    """(this PC's wired network cards that are armed to wake it, all of its
    wired cards). Takes a second - PowerShell - so call it off the UI thread."""
    try:
        cards = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "Get-NetAdapter -Physical | Where-Object MediaType -eq '802.3' | "
             "ForEach-Object InterfaceDescription"],
            capture_output=True, text=True, creationflags=NO_WINDOW, timeout=20).stdout.splitlines()
        armed = subprocess.run(["powercfg", "/devicequery", "wake_armed"], capture_output=True,
                               text=True, creationflags=NO_WINDOW, timeout=10).stdout.splitlines()
    except (OSError, subprocess.SubprocessError):
        return None, []
    cards = [c.strip() for c in cards if c.strip()]
    armed = {a.strip().lower() for a in armed}
    return [c for c in cards if c.lower() in armed], cards


def setup_script():
    """lan-setup.ps1: next to the installed Settings, else bundled with it."""
    from .common import resource
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
                            r"\{FD187DC3-B573-4BD4-A120-BB9C1F4A4283}_is1") as k:
            installed = os.path.join(winreg.QueryValueEx(k, "InstallLocation")[0], "lan-setup.ps1")
            if os.path.isfile(installed):
                return installed
    except OSError:
        pass
    return resource("lan-setup.ps1")


def setup(firewall=False, wake=False):
    """Run lan-setup.ps1 with admin rights - Windows asks first. False if it
    couldn't even ask (no script, or the prompt was refused)."""
    script = setup_script()
    if not script:
        return False
    args = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%s"%s%s' % (
        script, " -Firewall" if firewall else "", " -Wake" if wake else "")
    return ctypes.windll.shell32.ShellExecuteW(None, "runas", "powershell.exe", args, None, 0) > 32


def remember(cfg, pc):
    """Mark a PC as seen, so the engine never auto-adds its GPUs (again)."""
    ln = themes.extras(cfg).setdefault("lan", {})
    known = ln.setdefault("known", [])
    if pc.upper() not in [str(k).upper() for k in known]:
        known.append(pc.upper())


# -------------------------------------------------------------- firewall ---

def _netsh(*args):
    return subprocess.run(["netsh"] + list(args), capture_output=True, text=True,
                          creationflags=NO_WINDOW, timeout=8)


def firewall():
    """("ok" | "missing" | "unknown", a sentence saying why)."""
    try:
        rule = _netsh("advfirewall", "firewall", "show", "rule", "name=" + RULE)
        if rule.returncode == 0 and RULE in rule.stdout:
            return "ok", "A firewall rule lets the other PCs' broadcasts in (UDP %d)." % PORT
        prof = _netsh("advfirewall", "show", "currentprofile").stdout.lower()
    except (OSError, subprocess.SubprocessError):
        return "unknown", "Couldn't ask Windows Firewall."
    if re.search(r"^\s*state\s+off", prof, re.M):
        return "ok", "Windows Firewall is off on this network."
    if "allowinbound" in prof:
        return "ok", "This network's firewall profile already lets incoming traffic in."
    return "missing", ("Windows Firewall will keep the other PCs out until TopBar is allowed "
                       "through. Windows asks for admin approval once.")


def allow_firewall():
    """Add the rule - UDP 51736, from this network only. Windows shows its
    admin prompt; returns False if it couldn't even ask."""
    return setup(firewall=True)
