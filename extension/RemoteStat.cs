// A reading from another PC on the network - one of its GPUs, its CPU or its
// RAM - drawn like the GPU widget: name, reading and load bar.
//
// Clicking it drops down what's using the most of that part on that PC (from
// the lists each PC shares). While the PC is off, the first of its readings on
// the bar turns into a wake button - a dimmed power icon and the PC's name;
// clicking it sends the PC a Wake-on-LAN packet - and the rest of its readings
// hide (the engine hides their pills with them).
//
// Settings (only "pc" is needed):
//   "pc"                the PC's computer name, as it announces itself
//   "metric"            "gpu" | "cpu" | "ram"                          (gpu)
//   "gpu", "gpu-name"   which GPU: by name while the PC still has one by that
//                       name (the n-th of several alike), else by number from 0
//   "label"             "auto" = "PC - part"; other text is shown with {pc} and
//                       {name} filled in; "" hides it
//   "show-pc"           the PC's name in the auto label                     (true)
//   "bar"               the load bar                                        (true)
//   "vram"              a GPU's memory in use / total after its load        (false)
//   "format"            "{percent:0}%" - a GPU also has {vram_used} {vram_total},
//                       RAM {used} {total} {free} (GB)
//   "offline"           while the PC is off: "wake" | "hide" | "show"       (wake)
//   "interval"          ms between refreshes                                (1000)
//   plus the GPU widget's look: color, bar-color, background-color,
//   width, height, radius, value-position

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using TopBar;

namespace TopBarExtras.Widgets
{
    public class RemoteStat : iWidgetBase
    {
        private static readonly string Dot = " " + (char)0x00B7 + " ";
        private static readonly string Ellipsis = ((char)0x2026).ToString();
        private const double Dim = 0.55;

        // Every live instance, in bar order, so a PC's first reading can be
        // the one that turns into its wake button.
        private static readonly List<RemoteStat> live = new List<RemoteStat>();

        private readonly Meter meter;
        private readonly Button host = new Button();
        private readonly DispatcherTimer timer;
        private readonly string pc, metric, gpuName, label, format, offline;
        private readonly int gpuIndex;
        private readonly bool showPc;
        private bool wakeButton;

        public RemoteStat(object settings)
        {
            JObject s = settings as JObject;
            pc = Cfg.Str(s, "pc", "");
            metric = Cfg.Str(s, "metric", "gpu").ToLowerInvariant();
            gpuIndex = (int)Cfg.Num(s, "gpu", 0);
            gpuName = Cfg.Str(s, "gpu-name", "");
            label = s != null && s["label"] != null ? s["label"].ToString() : "auto";
            showPc = Cfg.Bool(s, "show-pc", true);
            offline = Cfg.Str(s, "offline", "wake").ToLowerInvariant();
            string fmt = Cfg.Str(s, "format", metric == "ram" ? "{used:0.0}/{total:0} GB" : "{percent:0}%");
            if (metric == "gpu" && Cfg.Bool(s, "vram", false)
                && fmt.IndexOf("vram", StringComparison.OrdinalIgnoreCase) < 0)
                fmt += "  {vram_used:0.0}/{vram_total:0} GB";
            format = fmt;

            meter = new Meter(s);
            meter.Panel.Background = Brushes.Transparent;   // clickable between the letters too

            // A plain button around the row: no chrome of its own, but it can be
            // clicked, and screen readers / UI Automation can press it.
            ControlTemplate bare = new ControlTemplate(typeof(Button));
            bare.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            host.Template = bare;
            host.Content = meter.Panel;
            host.Focusable = false;
            host.Cursor = Cursors.Hand;
            // Click: wake the PC while it's off; otherwise drop down what's
            // using the most of this part on it.
            host.Click += delegate
            {
                if (wakeButton)
                {
                    if (Lan.Wake(pc)) Refresh();
                    return;
                }
                FrameworkElement anchor = host;
                StackPanel row = host.Parent as StackPanel;
                if (row != null && row.Parent is Border) anchor = (Border)row.Parent;
                TopPanel.Toggle(anchor, new RemoteSource(pc, metric, Gpu, "remote:" + GetHashCode()));
            };
            host.MouseEnter += delegate { if (wakeButton) host.Opacity = 1; };
            host.MouseLeave += delegate { if (wakeButton) host.Opacity = Dim; };

            lock (live) live.Add(this);
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, Cfg.Num(s, "interval", 1000)));
            timer.Tick += delegate { Refresh(); };
            Refresh();
            timer.Start();
        }

        public FrameworkElement GetComponent()
        {
            return host;
        }

        public void Dispose()
        {
            timer.Stop();
            lock (live) live.Remove(this);
        }

        private void Refresh()
        {
            Peer p = Lan.Find(pc);
            if (p == null || !p.Online)
            {
                Offline();
                return;
            }
            Wakeable(false);
            host.Visibility = Visibility.Visible;
            host.Opacity = 1;

            Dictionary<string, double> now = new Dictionary<string, double>();
            Dictionary<string, double> most = new Dictionary<string, double>();
            double load;
            if (metric == "cpu")
            {
                load = p.CpuLoad;
                now["percent"] = load;
                most["percent"] = 100;
            }
            else if (metric == "ram")
            {
                double total = p.RamTotal, used = p.RamUsed;
                load = total > 0 ? 100.0 * used / total : 0;
                now["percent"] = load;
                now["used"] = used;
                now["total"] = total;
                now["free"] = total - used;
                most["percent"] = 100;
                most["used"] = most["total"] = most["free"] = total;
            }
            else
            {
                RemoteGpu g = Gpu(p);
                load = g == null ? 0 : g.Load;
                now["percent"] = load;
                now["vram_used"] = g == null ? 0 : g.VramUsed;
                now["vram_total"] = g == null ? 0 : g.VramTotal;
                most["percent"] = 100;
                most["vram_used"] = most["vram_total"] = g == null ? 0 : g.VramTotal;
            }
            meter.Name = Label(p.Name, Part(p));
            meter.Show(Cfg.Compose(format, now), load, Meter.Widest(Cfg.Compose(format, most)));
            AutomationProperties.SetName(host, meter.Name);
        }

        private void Offline()
        {
            string name = Lan.NameOf(pc);
            if (offline == "show")
            {
                Wakeable(false);
                host.Visibility = Visibility.Visible;
                host.Opacity = 0.45;
                meter.Name = Label(name, Part(null));
                meter.Show("offline", 0, "offline");
                return;
            }
            if (offline == "wake" && First() && Lan.CanWake(pc))
            {
                bool waking = Lan.Waking(pc);
                Wakeable(true);
                host.Visibility = Visibility.Visible;
                if (!host.IsMouseOver) host.Opacity = Dim;
                meter.Name = name;
                meter.Show(waking ? "waking" + Ellipsis : "", 0, "waking" + Ellipsis);
                host.ToolTip = waking ? "Waking " + name + Ellipsis : "Wake " + name + " (Wake-on-LAN)";
                AutomationProperties.SetName(host, "Wake " + name);
                return;
            }
            Wakeable(false);
            host.Visibility = Visibility.Collapsed;
        }

        private void Wakeable(bool on)
        {
            if (wakeButton == on) return;
            wakeButton = on;
            meter.Icon = on;
            meter.Bar = !on;
            if (!on) host.ToolTip = null;
        }

        // The first of this PC's readings on the bar.
        private bool First()
        {
            lock (live)
                foreach (RemoteStat r in live)
                    if (string.Equals(r.pc, pc, StringComparison.OrdinalIgnoreCase)) return r == this;
            return false;
        }

        // "RTX 2080 Ti", "Athlon 200GE", "RAM" - from the live PC, or from what
        // was last heard of it.
        private string Part(Peer p)
        {
            if (metric == "cpu")
            {
                string cpu = p != null ? p.CpuName : Lan.CpuOf(pc);
                return string.IsNullOrEmpty(cpu) ? "CPU" : cpu;
            }
            if (metric == "ram") return "RAM";
            RemoteGpu g = p == null ? null : Gpu(p);
            if (g != null) return g.Short;
            return gpuName.Length > 0 ? GpuAdapter.Short(gpuName) : "GPU";
        }

        private RemoteGpu Gpu(Peer p)
        {
            if (gpuName.Length > 0)
            {
                // Several of the same card: the n-th one by that name, counted
                // the way it was when this reading was set up.
                int nth = 0, wanted = 0;
                for (int i = 0; i < Math.Min(gpuIndex, p.Gpus.Count); i++)
                    if (string.Equals(p.Gpus[i].Name, gpuName, StringComparison.OrdinalIgnoreCase)) wanted++;
                for (int i = 0; i < p.Gpus.Count; i++)
                {
                    if (!string.Equals(p.Gpus[i].Name, gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (nth == wanted) return p.Gpus[i];
                    nth++;
                }
            }
            return gpuIndex >= 0 && gpuIndex < p.Gpus.Count ? p.Gpus[gpuIndex] : null;
        }

        private string Label(string pcName, string part)
        {
            if (label == "auto") return showPc ? pcName + Dot + part : part;
            return label.Replace("{pc}", pcName).Replace("{name}", part);
        }
    }
}
