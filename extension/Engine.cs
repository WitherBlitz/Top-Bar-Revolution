// The TopBar Extras engine: runs inside the TopBar process and adds what
// config.json alone can't express.
//
// TopBar loads every extensions\*.dll and builds widgets from config.json, but
// its chrome is fixed: an 18px bar, the WPF default font, the window title on
// the left and one right-aligned StackPanel of widgets separated by 4px Grid
// spacers. Because extension code runs in-process, an (invisible) widget can
// reach the live window and change all of that. This file:
//
//   * adds "Settings..." to TopBar's right-click menu (the ContextMenu resource)
//   * applies the "extras" block of config.json: font, bar height (re-reserving
//     the appbar strip), backdrop (stock / clear / blur / acrylic), rounded
//     "pill" containers and left / center / right widget groups
//   * watches config.json: extras and bar settings are applied live, widget
//     changes rebuild the strip in place through TopBar's own private
//     ReadConfig() + SetupWidgets(), falling back to TopBar's Reload (a process
//     restart) if that ever fails
//   * runs the network side (Lan.cs): sharing this PC's usage on the LAN and
//     hearing the other PCs' for the RemoteStat widget
//
// Config (all optional; the defaults reproduce stock TopBar exactly):
//   "extras": {
//     "font":     { "family": "JetBrains Mono", "size": 11, "weight": "Medium" },
//     "height":   28,
//     "title":    { "show": false },
//     "backdrop": { "mode": "stock|clear|blur|acrylic", "tint": "#101010", "opacity": 55 },
//     "layout":   "classic|split",
//     "pills":    { "enabled": true, "fill": "#B31C1C1C", "border": "#1FFFFFFF",
//                   "radius": 99, "height": 22, "padding": 12, "gap": 8,
//                   "spacing": 14, "edge": 6 }
//   }
// Widget order decides grouping: a TopBarExtras.Widgets.Group widget
// ({"align": "left|center|right"}) starts a new group. With pills on, every
// LineSpacer ends one pill and starts the next instead of drawing a line.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TopBar;

namespace TopBarExtras.Widgets
{
    // Hosts the engine. Always the first widget in config.json; it draws
    // nothing and the engine tidies its spacer away.
    public class Engine : iWidgetBase
    {
        private readonly FrameworkElement marker = Markers.Make(Markers.EngineTag);

        public Engine(object settings)
        {
            try { Host.Attach(); }
            catch (Exception ex) { Host.Log("attach failed: " + ex.Message); }
        }

        public FrameworkElement GetComponent() { return marker; }

        public void Dispose() { }
    }

    // Starts a new group of widgets: {"align": "left" | "center" | "right"}.
    public class Group : iWidgetBase
    {
        private readonly FrameworkElement marker;

        public Group(object settings)
        {
            JObject s = settings as JObject;
            string align = Host.Str(s, "align", "right").ToLowerInvariant();
            if (align != "left" && align != "center") align = "right";
            marker = Markers.Make(Markers.GroupPrefix + align);
        }

        public FrameworkElement GetComponent() { return marker; }

        public void Dispose() { }
    }
}

namespace TopBarExtras
{
    // The left, center and right groups across the bar, in that order. The
    // middle group sits in the middle of the bar, but slides aside rather than
    // run under a long left or right group (a few network PCs' readings make
    // the left one long quickly).
    internal sealed class SplitPanel : Panel
    {
        public double Gap = 24;

        protected override Size MeasureOverride(Size available)
        {
            double w = 0, h = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(double.PositiveInfinity, available.Height));
                w += c.DesiredSize.Width;
                h = Math.Max(h, c.DesiredSize.Height);
            }
            return new Size(double.IsInfinity(available.Width) ? w : available.Width,
                            double.IsInfinity(available.Height) ? h : available.Height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            UIElement left = InternalChildren.Count > 0 ? InternalChildren[0] : null;
            UIElement mid = InternalChildren.Count > 1 ? InternalChildren[1] : null;
            UIElement right = InternalChildren.Count > 2 ? InternalChildren[2] : null;
            double lw = left == null ? 0 : left.DesiredSize.Width;
            double cw = mid == null ? 0 : mid.DesiredSize.Width;
            double rw = right == null ? 0 : right.DesiredSize.Width;
            if (left != null) left.Arrange(new Rect(0, 0, lw, final.Height));
            if (right != null) right.Arrange(new Rect(Math.Max(0, final.Width - rw), 0, rw, final.Height));
            if (mid != null)
            {
                double x = (final.Width - cw) / 2;
                if (rw > 0) x = Math.Min(x, final.Width - rw - Gap - cw);
                if (lw > 0) x = Math.Max(x, lw + Gap);  // if both can't be kept clear, the left wins
                mid.Arrange(new Rect(x, 0, cw, final.Height));
            }
            return final;
        }
    }

    // A pill on the bar. The clickable ones (the clock, CPU / RAM / GPU) can
    // also be pressed through UI Automation - screen readers, and tests that
    // shouldn't have to move the real mouse.
    internal sealed class Pill : Border
    {
        public Action Invoked;
        public string Label = "";

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new PillPeer(this);
        }

        private sealed class PillPeer : FrameworkElementAutomationPeer, IInvokeProvider
        {
            private readonly Pill pill;

            public PillPeer(Pill pill) : base(pill) { this.pill = pill; }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return pill.Invoked != null ? AutomationControlType.Button : AutomationControlType.Group;
            }

            protected override string GetClassNameCore() { return "Pill"; }
            protected override string GetNameCore() { return pill.Label; }

            public override object GetPattern(PatternInterface pattern)
            {
                return pattern == PatternInterface.Invoke && pill.Invoked != null ? this : base.GetPattern(pattern);
            }

            public void Invoke()
            {
                if (pill.Invoked != null) pill.Invoked();
            }
        }
    }

    internal static class Markers
    {
        public const string EngineTag = "TopBarExtras:engine";
        public const string GroupPrefix = "TopBarExtras:group:";

        public static FrameworkElement Make(string tag)
        {
            Border b = new Border();
            b.Width = 0;
            b.Tag = tag;
            b.Visibility = Visibility.Collapsed;
            return b;
        }

        public static string TagOf(UIElement e)
        {
            FrameworkElement fe = e as FrameworkElement;
            return fe == null ? null : fe.Tag as string;
        }
    }

    internal static class Host
    {
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const string MenuTag = "TopBarExtras:settings";
        private const string WakeTag = "TopBarExtras:wake";
        private const string SettingsAppId =
            "{FD187DC3-B573-4BD4-A120-BB9C1F4A4283}_is1";
        private const double StockHeight = 18;

        private static Window win;
        private static bool attached;
        private static FileSystemWatcher watcher;
        private static DispatcherTimer debounce;
        private static int readRetries;
        private static string lastWidgets;
        private static double reservedHeight = StockHeight;

        // The stock strip as SetupWidgets built it, so any restyle can be undone.
        private static readonly List<UIElement> original = new List<UIElement>();
        private static bool haveOriginal;
        private static readonly List<Panel> hosts = new List<Panel>();
        private static readonly List<KeyValuePair<DependencyObject, DependencyProperty>> touched =
            new List<KeyValuePair<DependencyObject, DependencyProperty>>();
        private static Panel titleHome;
        private static int titleIndex;

        private static readonly string topBarDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TopBar");
        private static readonly string configPath = Path.Combine(topBarDir, "config.json");
        private static readonly string logPath = Path.Combine(topBarDir, "extras.log");

        // -- lifecycle -----------------------------------------------------

        public static void Attach()
        {
            if (win == null) win = FindWindow();
            if (win == null) { Log("TopBar's window wasn't found"); return; }
            if (!attached)
            {
                attached = true;
                EnsureMenu();
                StartWatcher();
                Log("engine attached");
            }
            // SetupWidgets is mid-flight: the rest of the strip isn't built yet.
            // Anything posted now runs only after it (and Build) have returned.
            haveOriginal = false;
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                                       new Action(ApplyFromDisk));
        }

        private static Window FindWindow()
        {
            if (Application.Current == null) return null;
            foreach (Window w in Application.Current.Windows)
                if (w.GetType().FullName == "TopBar.TopBarWindow") return w;
            return Application.Current.MainWindow;
        }

        // -- right-click menu ------------------------------------------------

        private static void EnsureMenu()
        {
            ContextMenu cm = win.TryFindResource("ContextMenu") as ContextMenu;
            if (cm == null) { Log("TopBar's context menu wasn't found"); return; }
            foreach (object o in cm.Items)
            {
                MenuItem existing = o as MenuItem;
                if (existing != null && Equals(existing.Tag, MenuTag)) return;
            }
            MenuItem item = new MenuItem();
            item.Header = "Settings\u2026";
            item.Tag = MenuTag;
            item.FontWeight = FontWeights.SemiBold;
            item.Click += delegate { OpenSettings(); };
            cm.Items.Insert(0, item);
            // Wake up > every PC on the network that can be woken, filled in
            // each time the menu opens (and hidden while there are none).
            MenuItem wake = new MenuItem();
            wake.Header = "Wake up";
            wake.Tag = WakeTag;
            cm.Items.Insert(1, wake);
            cm.Items.Insert(2, new Separator());
            cm.Opened += delegate { FillWakeMenu(wake); };
            FillWakeMenu(wake);
            Log("menu: " + string.Join(" | ", cm.Items.Cast<object>().Select(
                o => o is MenuItem ? Convert.ToString(((MenuItem)o).Header) : "---")));
        }

        private static void FillWakeMenu(MenuItem wake)
        {
            wake.Items.Clear();
            List<Known> pcs = Lan.KnownPcs().Where(k => k.Macs.Count > 0).ToList();
            foreach (Known k in pcs)
            {
                bool on = Lan.IsOnline(k.Id);
                MenuItem mi = new MenuItem();
                // A TextBlock header, so an underscore in a PC's name stays an underscore.
                mi.Header = new TextBlock { Text = k.Name + (on ? "  (on)" : Lan.Waking(k.Id) ? "  (waking...)" : "") };
                mi.IsEnabled = !on;
                string id = k.Id;
                mi.Click += delegate { Lan.Wake(id); };
                wake.Items.Add(mi);
            }
            wake.Visibility = pcs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void OpenSettings()
        {
            string exe = SettingsExe();
            if (exe == null)
            {
                MessageBox.Show("TopBar Settings isn't installed. Reinstall TopBar Extras to get it back.",
                                "TopBar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Log("couldn't open settings: " + ex.Message); }
        }

        private static string SettingsExe()
        {
            List<string> candidates = new List<string>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SettingsAppId))
                {
                    string dir = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(dir))
                        candidates.Add(Path.Combine(dir, "TopBar Settings.exe"));
                }
            }
            catch (Exception) { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                @"Programs\TopBar Extras\TopBar Settings.exe"));
            foreach (string c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        // -- live config -----------------------------------------------------

        private static void StartWatcher()
        {
            debounce = new DispatcherTimer(DispatcherPriority.Background, win.Dispatcher);
            debounce.Interval = TimeSpan.FromMilliseconds(250);
            debounce.Tick += delegate { debounce.Stop(); OnConfigChanged(); };
            try
            {
                watcher = new FileSystemWatcher(topBarDir, "config.json");
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                     | NotifyFilters.Size | NotifyFilters.CreationTime;
                FileSystemEventHandler changed = delegate { Kick(); };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Renamed += delegate { Kick(); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log("can't watch config.json: " + ex.Message); }
        }

        private static void Kick()
        {
            win.Dispatcher.BeginInvoke(new Action(delegate
            {
                debounce.Stop();
                debounce.Start();
            }));
        }

        private static void OnConfigChanged()
        {
            JObject cfg = ReadConfigFile();
            if (cfg == null)
            {
                // Caught it mid-write; the writer's next event will kick us too.
                if (++readRetries <= 5) debounce.Start();
                return;
            }
            readRetries = 0;
            if (Canon(cfg["widgets"]) != lastWidgets) Rebuild();
            else Apply(cfg);
        }

        private static JObject ReadConfigFile()
        {
            try
            {
                using (FileStream fs = new FileStream(configPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs))
                    return JObject.Parse(r.ReadToEnd());
            }
            catch (Exception) { return null; }
        }

        private static void ApplyFromDisk()
        {
            JObject cfg = ReadConfigFile();
            if (cfg != null) Apply(cfg);
        }

        // Widgets changed: rebuild the strip in place with TopBar's own code
        // rather than restarting the process (which makes every maximized
        // window jump as the appbar is released and re-reserved).
        private static void Rebuild()
        {
            try
            {
                MethodInfo read = win.GetType().GetMethod("ReadConfig", Any);
                MethodInfo setup = win.GetType().GetMethod("SetupWidgets", Any);
                object cfg = read.Invoke(win, null);

                Restore();
                IList widgets = Field<IList>("Widgets");
                foreach (object w in widgets.Cast<object>().ToList())
                {
                    try { ((iWidgetBase)w).Dispose(); } catch (Exception) { }
                }
                widgets.Clear();
                StripPanel.Children.Clear();
                original.Clear();
                haveOriginal = false;

                setup.Invoke(win, new object[] { cfg });
                FieldInfo cf = win.GetType().GetField("Config", Any);
                if (cf != null) cf.SetValue(win, cfg);
                Log("widgets rebuilt in place");
                // The new Engine instance schedules the restyle; this covers a
                // config that dropped the engine widget.
                win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                                           new Action(ApplyFromDisk));
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Log("in-place rebuild failed (" + inner.Message + ") - restarting TopBar");
                MethodInfo reload = win.GetType().GetMethod("ContextMenu_ReloadTB", Any);
                if (reload != null) reload.Invoke(win, new object[] { null, null });
            }
        }

        // -- applying extras ---------------------------------------------------

        private static void Apply(JObject cfg)
        {
            if (win == null) return;
            if (new WindowInteropHelper(win).Handle == IntPtr.Zero)
            {
                // TopBar's window gets its handle when its own Background-
                // priority timer first shows it. Re-posting at a higher
                // priority would starve that timer and spin forever, so wait
                // on a timer instead.
                Later(250, delegate { Apply(cfg); });
                return;
            }
            lastWidgets = Canon(cfg["widgets"]);
            JObject x = cfg["extras"] as JObject ?? new JObject();
            JObject st = cfg["settings"] as JObject ?? new JObject();
            try
            {
                double h = Math.Max(16, Math.Min(48, Num(x, "height", StockHeight)));
                ApplyFont(x["font"] as JObject);
                ApplyHeight(h);
                ApplyBackdrop(st, x["backdrop"] as JObject);
                ApplyLayout(x, h);
                ApplyTitle(st, x["title"] as JObject, h);
                ApplyClockPanel(cfg, x);
                ApplyUsageClicks();
                try { Lan.Configure(cfg); }
                catch (Exception ex) { Log("network: " + ex.Message); }
                if (Bool(x, "debug", false))
                    Later(500, delegate { DumpTree(); });
                Log(string.Format("applied: font={0} height={1} backdrop={2} layout={3} pills={4}",
                    Str(x["font"] as JObject, "family", "default"), h,
                    Str(x["backdrop"] as JObject, "mode", "stock"),
                    Str(x, "layout", "classic"),
                    Bool(x["pills"] as JObject, "enabled", false)));
            }
            catch (Exception ex) { Log("apply failed: " + ex); }
        }

        private static void ApplyFont(JObject font)
        {
            string family = Str(font, "family", "");
            double size = Num(font, "size", 0);
            string weight = Str(font, "weight", "");

            if (family.Length == 0) win.ClearValue(Control.FontFamilyProperty);
            else win.FontFamily = ResolveFont(family);

            if (size > 0) win.FontSize = size;
            else win.ClearValue(Control.FontSizeProperty);

            if (weight.Length == 0) win.ClearValue(Control.FontWeightProperty);
            else
            {
                try { win.FontWeight = (FontWeight)new FontWeightConverter().ConvertFromString(weight); }
                catch (Exception) { win.ClearValue(Control.FontWeightProperty); }
            }
        }

        // Fonts shipped in extensions\fonts are used privately, without being
        // installed; anything else falls back to the system's fonts.
        private static FontFamily ResolveFont(string family)
        {
            string dir = Path.Combine(Path.GetDirectoryName(typeof(Host).Assembly.Location), "fonts");
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.ttf").Length > 0)
                return new FontFamily(new Uri(dir + Path.DirectorySeparatorChar),
                                      "./#" + family + ", " + family + ", Segoe UI");
            return new FontFamily(family + ", Segoe UI");
        }

        private static void ApplyTitle(JObject st, JObject title, double h)
        {
            TextBlock t = Field<TextBlock>("WindowName");
            if (t == null) return;
            t.Visibility = Bool(title, "show", true) ? Visibility.Visible : Visibility.Collapsed;
            JObject cw = st["current-window"] as JObject;
            Color c;
            if (TryColor(Str(cw, "color", ""), out c)) t.Foreground = new SolidColorBrush(c);
            if (h > StockHeight) Set(t, FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        }

        // TopBar reserves an 18px strip in Build(); resize the window and
        // re-reserve the strip for the same appbar handle.
        private static void ApplyHeight(double h)
        {
            if (Math.Abs(win.Height - h) < 0.5 && Math.Abs(reservedHeight - h) < 0.5) return;
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            PresentationSource src = PresentationSource.FromVisual(win);
            double sy = src != null && src.CompositionTarget != null
                ? src.CompositionTarget.TransformToDevice.M22 : 1.0;

            RECT wr;
            GetWindowRect(hwnd, out wr);
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            // Build() registers the strip under its private `handle` field,
            // which it never fills in - so TopBar's appbar is keyed to a null
            // hwnd. Resizing it means using that same key.
            FieldInfo hf = win.GetType().GetField("handle", Any);
            abd.hWnd = hf != null ? (IntPtr)hf.GetValue(win) : hwnd;
            abd.uEdge = ABE_TOP;
            abd.rc.left = wr.left;
            abd.rc.right = wr.right;
            abd.rc.top = wr.top < 0 ? 0 : wr.top;
            abd.rc.bottom = abd.rc.top + (int)Math.Round(h * sy);
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc.bottom = abd.rc.top + (int)Math.Round(h * sy);
            SHAppBarMessage(ABM_SETPOS, ref abd);

            win.Top = abd.rc.top / sy;
            win.Height = h;
            reservedHeight = h;
        }

        private static void ApplyBackdrop(JObject st, JObject bd)
        {
            string mode = Str(bd, "mode", "stock");
            Color tint;
            if (!TryColor(Str(bd, "tint", "#000000"), out tint)) tint = Colors.Black;
            byte alpha = (byte)Math.Round(Math.Max(0, Math.Min(100, Num(bd, "opacity", 60))) * 2.55);
            // Never fully transparent: a 1/255 alpha keeps the empty parts of
            // the bar hit-testable, so right-click still reaches it.
            byte hit = 1;

            if (mode == "clear")
            {
                win.Background = Brush(Color.FromArgb(Math.Max(alpha, hit), tint.R, tint.G, tint.B));
                SetAccent(ACCENT_DISABLED, 0);
            }
            else if (mode == "blur")
            {
                win.Background = Brush(Color.FromArgb(Math.Max(alpha, hit), tint.R, tint.G, tint.B));
                SetAccent(ACCENT_ENABLE_BLURBEHIND, 0);
            }
            else if (mode == "acrylic")
            {
                win.Background = Brush(Color.FromArgb(hit, 0, 0, 0));
                uint abgr = ((uint)Math.Max(alpha, (byte)1) << 24) | ((uint)tint.B << 16)
                          | ((uint)tint.G << 8) | tint.R;
                SetAccent(ACCENT_ENABLE_ACRYLICBLURBEHIND, abgr);
            }
            else
            {
                // Stock: TopBar's own settings, re-applied so edits show live.
                JObject c = st["color"] as JObject;
                win.Background = Brush(Color.FromArgb(
                    (byte)Num(c, "alpha", 100), (byte)Num(c, "red", 0),
                    (byte)Num(c, "green", 0), (byte)Num(c, "blue", 0)));
                SetAccent(Bool(st, "use-blur", true) ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED, 0);
            }
        }

        // -- calendar / stopwatch / timer ---------------------------------------

        private static readonly MouseButtonEventHandler clockClick =
            delegate(object sender, MouseButtonEventArgs e)
            {
                ClockPanel.Toggle((FrameworkElement)sender, panelTheme);
                e.Handled = true;
            };
        private static PanelTheme panelTheme = new PanelTheme();
        // The clock element the panel hangs under, so a ringing alarm can open
        // it even when nobody has clicked the clock this session.
        internal static FrameworkElement ClockTarget;

        // Clicking the clock drops down a calendar with a stopwatch and timer.
        // The handler goes on the pill when there is one, so the whole pill is
        // clickable, not just the text.
        private static void ApplyClockPanel(JObject cfg, JObject x)
        {
            panelTheme = BuildPanelTheme(cfg, x);
            ClockPanel.Retheme(panelTheme);
            bool on = Bool(x["clock-panel"] as JObject, "enabled", true);
            // Build it now (hidden) so a saved alarm rings even if the panel is
            // never opened this session.
            if (on) ClockPanel.Ensure(panelTheme);
            foreach (KeyValuePair<UIElement, string> pair in WidgetKinds())
            {
                if (!pair.Value.EndsWith(".Clock")) continue;
                FrameworkElement target = pair.Key as FrameworkElement;
                if (target == null) continue;
                Border pill = target.Parent is StackPanel
                    ? ((StackPanel)target.Parent).Parent as Border : null;
                if (pill != null) target = pill;
                ClockTarget = target;
                target.RemoveHandler(UIElement.MouseLeftButtonUpEvent, clockClick);
                Set(target, FrameworkElement.CursorProperty,
                    on ? Cursors.Hand : Cursors.Arrow);
                if (on) target.AddHandler(UIElement.MouseLeftButtonUpEvent, clockClick, true);
                Pill p = target as Pill;
                if (p != null)
                {
                    FrameworkElement anchor = target;
                    p.Label = "Calendar";
                    p.Invoked = on ? (Action)delegate { ClockPanel.Toggle(anchor, panelTheme); } : null;
                }
            }
        }

        internal static PanelTheme Theme { get { return panelTheme; } }

        // -- what's using it: CPU / RAM / GPU drop-downs ---------------------------

        private static readonly Dictionary<FrameworkElement, Func<TopSource>> usageTargets =
            new Dictionary<FrameworkElement, Func<TopSource>>();
        private static readonly MouseButtonEventHandler usageClick =
            delegate(object sender, MouseButtonEventArgs e)
            {
                FrameworkElement target = sender as FrameworkElement;
                Func<TopSource> make;
                if (target == null || !usageTargets.TryGetValue(target, out make)) return;
                TopPanel.Toggle(target, make());
                e.Handled = true;
            };

        // Clicking a CPU, RAM or GPU pill - anywhere on it, so its label too -
        // drops down the processes using the most of it. (Network PCs'
        // readings handle their own clicks; see RemoteStat.)
        private static void ApplyUsageClicks()
        {
            foreach (FrameworkElement t in usageTargets.Keys)
                t.RemoveHandler(UIElement.MouseLeftButtonUpEvent, usageClick);
            usageTargets.Clear();
            IList widgets = Field<IList>("Widgets");
            if (widgets == null) return;
            foreach (object w in widgets)
            {
                Func<TopSource> make = SourceFor(w);
                if (make == null) continue;
                FrameworkElement target;
                try { target = ((iWidgetBase)w).GetComponent(); }
                catch (Exception) { continue; }
                if (target == null) continue;
                Border pill = target.Parent is StackPanel ? ((StackPanel)target.Parent).Parent as Border : null;
                if (pill != null) target = pill;
                if (usageTargets.ContainsKey(target)) continue;     // RAM text and bar share a pill
                usageTargets[target] = make;
                Set(target, FrameworkElement.CursorProperty, Cursors.Hand);
                target.AddHandler(UIElement.MouseLeftButtonUpEvent, usageClick, true);
                Pill p = target as Pill;
                if (p != null)
                {
                    FrameworkElement anchor = target;
                    p.Label = LabelFor(w);
                    p.Invoked = delegate { TopPanel.Toggle(anchor, make()); };
                }
            }
            string wired = string.Join(", ", usageTargets.Keys.Select(k => k is Pill ? ((Pill)k).Label : k.GetType().Name).ToArray());
            if (wired != lastWired) { Log("drop-downs on: " + (wired.Length > 0 ? wired : "nothing")); lastWired = wired; }
        }

        private static string lastWired;

        private static Func<TopSource> SourceFor(object w)
        {
            string t = w.GetType().FullName;
            if (t == "TopBarExtras.Widgets.CPULoad" || t == "TopBar.Widgets.CPULoad")
                return delegate { return new LocalCpuSource(); };
            if (t == "TopBarExtras.Widgets.RAMUsage" || t == "TopBar.Widgets.RAMUsageBar"
                || t == "TopBar.Widgets.RAMUsageText")
                return delegate { return new LocalRamSource(); };
            TopBarExtras.Widgets.GPUUsage gpu = w as TopBarExtras.Widgets.GPUUsage;
            if (gpu != null) return delegate { return new LocalGpuSource(gpu); };
            return null;
        }

        private static string LabelFor(object w)
        {
            string t = w.GetType().FullName;
            if (t.EndsWith(".CPULoad")) return "CPU usage";
            if (t.EndsWith(".GPUUsage")) return "GPU usage";
            return "Memory usage";
        }

        private static PanelTheme BuildPanelTheme(JObject cfg, JObject x)
        {
            PanelTheme t = new PanelTheme();
            JObject font = x["font"] as JObject;
            string family = Str(font, "family", "");
            if (family.Length > 0) t.Font = ResolveFont(family);

            Color c;
            // The panel takes the clock's own text color, so it always matches.
            foreach (JToken w in cfg["widgets"] ?? new JArray())
            {
                if (Str(w as JObject, "type", "").EndsWith(".Clock")
                    && TryColor(Str(w["settings"] as JObject, "text-color", ""), out c))
                {
                    t.Text = Color.FromRgb(c.R, c.G, c.B);
                    break;
                }
            }
            JObject bd = x["backdrop"] as JObject;
            if (Str(bd, "mode", "stock") != "stock" && TryColor(Str(bd, "tint", ""), out c))
                t.Back = Color.FromRgb(c.R, c.G, c.B);
            else
            {
                JObject col = (cfg["settings"] as JObject ?? new JObject())["color"] as JObject;
                if (col != null)
                    t.Back = Color.FromRgb((byte)Num(col, "red", 22), (byte)Num(col, "green", 22),
                                           (byte)Num(col, "blue", 26));
            }
            JObject pills = x["pills"] as JObject;
            if (TryColor(Str(pills, "fill", ""), out c)) t.Card = c;
            if (TryColor(Str(pills, "border", ""), out c)) t.Edge = c;
            string week = Str(x["clock-panel"] as JObject, "week-starts", "");
            if (week.Length > 0)
                t.FirstDay = week.ToLowerInvariant() == "sunday" ? DayOfWeek.Sunday : DayOfWeek.Monday;
            return t;
        }

        // -- layout ------------------------------------------------------------

        private sealed class Segment
        {
            public string Align;
            public readonly List<List<UIElement>> Pills = new List<List<UIElement>>();
            public Segment(string align) { Align = align; Pills.Add(new List<UIElement>()); }
            public List<UIElement> Current { get { return Pills[Pills.Count - 1]; } }
            public bool Empty { get { return Pills.All(p => p.Count == 0); } }
        }

        private static void ApplyLayout(JObject x, double h)
        {
            StackPanel panel = StripPanel;
            if (panel == null) return;
            if (!haveOriginal)
            {
                original.Clear();
                haveOriginal = true;
            }
            // SetupWidgets may still be running: some stock constructors (perf
            // counters, shell icon extraction) pump messages, which runs our
            // posted apply mid-build. Whatever it has added since belongs at
            // the end of the stock order - never drop it.
            foreach (UIElement e in panel.Children.Cast<UIElement>().ToList())
                if (!original.Contains(e)) original.Add(e);
            Restore();

            Dictionary<UIElement, string> kinds = WidgetKinds();
            string layout = Str(x, "layout", "classic");
            JObject pills = x["pills"] as JObject;
            bool pillsOn = Bool(pills, "enabled", false);

            if (layout != "split" && !pillsOn)
            {
                // Stock layout. Just hide the spacers that follow our markers.
                for (int i = 0; i < original.Count - 1; i++)
                    if (IsMarker(original[i]) && IsSpacer(original[i + 1], kinds))
                        Set(original[i + 1], UIElement.VisibilityProperty, Visibility.Collapsed);
                return;
            }

            // Split the stock order into groups (Group markers) and, with pills
            // on, into pills (LineSpacers).
            List<Segment> segments = new List<Segment>();
            Segment cur = new Segment("right");
            foreach (UIElement e in original)
            {
                if (IsSpacer(e, kinds)) continue;
                string tag = Markers.TagOf(e);
                if (tag == Markers.EngineTag) continue;
                if (tag != null && tag.StartsWith(Markers.GroupPrefix))
                {
                    if (!cur.Empty) segments.Add(cur);
                    cur = new Segment(layout == "split" ? tag.Substring(Markers.GroupPrefix.Length) : "right");
                    continue;
                }
                string kind;
                kinds.TryGetValue(e, out kind);
                if (pillsOn && kind == "TopBar.Widgets.LineSpacer")
                {
                    if (cur.Current.Count > 0) cur.Pills.Add(new List<UIElement>());
                    continue;
                }
                cur.Current.Add(e);
            }
            if (!cur.Empty) segments.Add(cur);

            double pillH = Math.Min(h, Num(pills, "height", Math.Max(16, h - 6)));
            double radius = Math.Min(Num(pills, "radius", 99), pillH / 2);
            double pad = Num(pills, "padding", 12);
            double gap = Num(pills, "gap", 8);
            double spacing = pillsOn ? Num(pills, "spacing", 14) : 4;
            double edge = Num(pills, "edge", pillsOn ? 6 : 0);
            Color fill, border;
            if (!TryColor(Str(pills, "fill", "#B31C1C1C"), out fill)) fill = Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1C);
            if (!TryColor(Str(pills, "border", "#1FFFFFFF"), out border)) border = Colors.Transparent;

            // WidgetsPanel sits in a right-aligned Grid sized to its content;
            // the groups need the full-width root Grid to spread out in.
            Panel grid = win.Content as Panel ?? panel.Parent as Panel;
            if (grid == null) return;
            SplitPanel across = new SplitPanel();
            across.Gap = Math.Max(16, (pillsOn ? gap : spacing) * 3);
            Grid rootGrid = grid as Grid;
            if (rootGrid != null && rootGrid.ColumnDefinitions.Count > 1)
                Grid.SetColumnSpan(across, rootGrid.ColumnDefinitions.Count);
            Dictionary<string, StackPanel> byAlign = new Dictionary<string, StackPanel>();
            foreach (string a in new[] { "left", "center", "right" })
            {
                StackPanel host = new StackPanel();
                host.Orientation = Orientation.Horizontal;
                host.VerticalAlignment = VerticalAlignment.Center;
                host.Margin = new Thickness(a == "left" ? edge : 0, 0, a == "right" ? edge : 0, 0);
                across.Children.Add(host);
                hosts.Add(host);
                byAlign[a] = host;
            }
            grid.Children.Add(across);
            hosts.Add(across);

            panel.Children.Clear();
            Set(panel, UIElement.VisibilityProperty, Visibility.Collapsed);

            // In a split layout the window title leads the left group.
            TextBlock title = Field<TextBlock>("WindowName");
            if (layout == "split" && title != null && title.Visibility == Visibility.Visible
                && segments.Any(s => s.Align == "left"))
            {
                titleHome = title.Parent as Panel;
                if (titleHome != null)
                {
                    titleIndex = titleHome.Children.IndexOf(title);
                    titleHome.Children.Remove(title);
                    byAlign["left"].Children.Add(title);
                    Set(title, FrameworkElement.MarginProperty, new Thickness(3, 0, spacing, 0));
                }
            }

            foreach (Segment seg in segments)
            {
                foreach (List<UIElement> items in seg.Pills)
                {
                    if (items.Count == 0) continue;
                    StackPanel row = new StackPanel();
                    row.Orientation = Orientation.Horizontal;
                    row.VerticalAlignment = VerticalAlignment.Center;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) row.Children.Add(Spacer(spacing));
                        UIElement e = items[i];
                        Set(e, FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                        // Stock widgets are drawn for the 18px strip they
                        // normally stretch to: the RAM bar insets itself with
                        // (18 - height) / 2 margins, CPU bars bottom-align. Give
                        // anything without its own height that 18px back, or it
                        // collapses to nothing once centered. Text just centers.
                        FrameworkElement fe = e as FrameworkElement;
                        if (fe != null && !(fe is TextBlock) && double.IsNaN(fe.Height))
                            Set(e, FrameworkElement.HeightProperty, StockHeight);
                        row.Children.Add(e);
                    }

                    FrameworkElement box;
                    if (pillsOn)
                    {
                        Pill pill = new Pill();
                        pill.Background = Brush(fill);
                        pill.BorderBrush = Brush(border);
                        pill.BorderThickness = new Thickness(border.A > 0 ? 1 : 0);
                        pill.CornerRadius = new CornerRadius(radius);
                        pill.Padding = new Thickness(pad, 0, pad, 0);
                        pill.Height = pillH;
                        pill.VerticalAlignment = VerticalAlignment.Center;
                        pill.Child = row;
                        box = pill;
                    }
                    else box = row;

                    StackPanel target = byAlign[seg.Align];
                    FrameworkElement gapBefore = null;
                    if (target.Children.Count > 0 && !(target.Children[target.Children.Count - 1] is TextBlock))
                    {
                        gapBefore = Spacer(pillsOn ? gap : spacing);
                        target.Children.Add(gapBefore);
                    }
                    target.Children.Add(box);
                    Follow(box, gapBefore, items);
                }
            }
        }

        // A pill whose widgets have all hidden themselves - a network PC that
        // is switched off - hides too, along with the gap in front of it, and
        // comes back with them.
        private static readonly List<KeyValuePair<UIElement, EventHandler>> followers =
            new List<KeyValuePair<UIElement, EventHandler>>();
        private static readonly DependencyPropertyDescriptor visibilityOf =
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));

        private static void Follow(FrameworkElement box, FrameworkElement gapBefore, List<UIElement> items)
        {
            EventHandler update = delegate
            {
                Visibility v = items.Any(i => i.Visibility == Visibility.Visible)
                    ? Visibility.Visible : Visibility.Collapsed;
                box.Visibility = v;
                if (gapBefore != null) gapBefore.Visibility = v;
            };
            foreach (UIElement i in items)
            {
                visibilityOf.AddValueChanged(i, update);
                followers.Add(new KeyValuePair<UIElement, EventHandler>(i, update));
            }
            update(null, EventArgs.Empty);
        }

        // Puts the stock strip back exactly as SetupWidgets built it.
        private static void Restore()
        {
            StackPanel panel = StripPanel;
            foreach (KeyValuePair<UIElement, EventHandler> f in followers)
                visibilityOf.RemoveValueChanged(f.Key, f.Value);
            followers.Clear();
            foreach (UIElement e in original) Detach(e);
            TextBlock title = Field<TextBlock>("WindowName");
            if (titleHome != null && title != null)
            {
                Detach(title);
                titleHome.Children.Insert(Math.Min(titleIndex, titleHome.Children.Count), title);
                titleHome = null;
            }
            foreach (Panel host in hosts)
            {
                host.Children.Clear();
                Panel parent = host.Parent as Panel;
                if (parent != null) parent.Children.Remove(host);
            }
            hosts.Clear();
            foreach (KeyValuePair<DependencyObject, DependencyProperty> t in touched)
                t.Key.ClearValue(t.Value);
            touched.Clear();
            if (panel == null) return;
            panel.Children.Clear();
            foreach (UIElement e in original) panel.Children.Add(e);
        }

        private static void Detach(UIElement e)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(e) ?? LogicalTreeHelper.GetParent(e);
            Panel p = parent as Panel;
            if (p != null) { p.Children.Remove(e); return; }
            Decorator d = parent as Decorator;
            if (d != null && d.Child == e) d.Child = null;
        }

        private static Dictionary<UIElement, string> WidgetKinds()
        {
            Dictionary<UIElement, string> kinds = new Dictionary<UIElement, string>();
            IList widgets = Field<IList>("Widgets");
            if (widgets == null) return kinds;
            foreach (object w in widgets)
            {
                try
                {
                    UIElement c = ((iWidgetBase)w).GetComponent();
                    if (c != null && !kinds.ContainsKey(c)) kinds[c] = w.GetType().FullName;
                }
                catch (Exception) { }
            }
            return kinds;
        }

        private static bool IsMarker(UIElement e)
        {
            string tag = Markers.TagOf(e);
            return tag != null && tag.StartsWith("TopBarExtras:");
        }

        // SetupWidgets follows every widget with a bare 4px Grid.
        private static bool IsSpacer(UIElement e, Dictionary<UIElement, string> kinds)
        {
            Grid g = e as Grid;
            return g != null && !kinds.ContainsKey(e) && g.Children.Count == 0
                && Math.Abs(g.Width - 4) < 0.01;
        }

        private static FrameworkElement Spacer(double width)
        {
            Border b = new Border();
            b.Width = width;
            return b;
        }

        // Only ever set properties through here, so Restore() can clear them.
        private static void Set(DependencyObject o, DependencyProperty p, object value)
        {
            o.SetValue(p, value);
            touched.Add(new KeyValuePair<DependencyObject, DependencyProperty>(o, p));
        }

        private static StackPanel StripPanel { get { return Field<StackPanel>("WidgetsPanel"); } }

        private static T Field<T>(string name) where T : class
        {
            if (win == null) return null;
            FieldInfo f = win.GetType().GetField(name, Any);
            return f == null ? null : f.GetValue(win) as T;
        }

        // -- helpers -----------------------------------------------------------

        public static string Str(JObject o, string key, string fallback)
        {
            if (o == null || o[key] == null || o[key].Type == JTokenType.Null) return fallback;
            string v = o[key].ToString();
            return v.Length == 0 && fallback.Length > 0 ? fallback : v;
        }

        private static double Num(JObject o, string key, double fallback)
        {
            if (o == null || o[key] == null) return fallback;
            double v;
            return double.TryParse(o[key].ToString(), NumberStyles.Any,
                                   CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static bool Bool(JObject o, string key, bool fallback)
        {
            if (o == null || o[key] == null) return fallback;
            bool v;
            return bool.TryParse(o[key].ToString(), out v) ? v : fallback;
        }

        // "debug": true in extras logs the live layout tree and handles.
        private static void DumpTree()
        {
            IntPtr own = new WindowInteropHelper(win).Handle;
            FieldInfo hf = win.GetType().GetField("handle", Any);
            object reg = hf == null ? null : hf.GetValue(win);
            Log(string.Format("debug: window hwnd=0x{0:X} registered handle field={1} size={2}x{3} at {4},{5}",
                own.ToInt64(), reg, win.ActualWidth, win.ActualHeight, win.Left, win.Top));
            DumpNode(win, 0);
        }

        private static void DumpNode(DependencyObject d, int depth)
        {
            if (depth > 8) return;
            FrameworkElement fe = d as FrameworkElement;
            string desc = d.GetType().Name;
            if (fe != null)
            {
                Point p = new Point();
                try { p = fe.TransformToAncestor(win).Transform(new Point(0, 0)); } catch (Exception) { }
                desc += string.Format(" name={0} x={1:0} w={2:0} h={3:0} ha={4} col={5} vis={6} tag={7}",
                    fe.Name, p.X, fe.ActualWidth, fe.ActualHeight, fe.HorizontalAlignment,
                    Grid.GetColumn(fe), fe.Visibility, fe.Tag);
                Grid g = fe as Grid;
                if (g != null)
                    desc += " cols=" + string.Join(",", g.ColumnDefinitions.Select(c => c.Width.ToString()));
            }
            Log("debug: " + new string(' ', depth * 2) + desc);
            int n = VisualTreeHelper.GetChildrenCount(d);
            // Widget internals aren't interesting; stop at our hosts' contents.
            if (n > 12 && depth >= 3) { Log("debug: " + new string(' ', depth * 2 + 2) + "(" + n + " children)"); return; }
            for (int i = 0; i < n; i++) DumpNode(VisualTreeHelper.GetChild(d, i), depth + 1);
        }

        private static void Later(int ms, Action action)
        {
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background, win.Dispatcher);
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate { t.Stop(); action(); };
            t.Start();
        }

        private static string Canon(JToken t)
        {
            return t == null ? "" : t.ToString(Formatting.None);
        }

        private static bool TryColor(string s, out Color c)
        {
            c = Colors.Transparent;
            if (string.IsNullOrEmpty(s)) return false;
            try { c = (Color)ColorConverter.ConvertFromString(s); return true; }
            catch (Exception) { return false; }
        }

        private static SolidColorBrush Brush(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(topBarDir);
                FileInfo fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 64 * 1024)
                {
                    string[] all = File.ReadAllLines(logPath);
                    File.WriteAllLines(logPath, all.Skip(all.Length / 2).ToArray());
                }
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                                   + "  " + line + Environment.NewLine);
            }
            catch (Exception) { }
        }

        // -- Win32 -------------------------------------------------------------

        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const int WCA_ACCENT_POLICY = 19;
        private const uint ABM_QUERYPOS = 2;
        private const uint ABM_SETPOS = 3;
        private const uint ABE_TOP = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd,
            ref WindowCompositionAttributeData data);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        private static void SetAccent(int state, uint gradient)
        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero) return;
            AccentPolicy policy = new AccentPolicy();
            policy.AccentState = state;
            policy.AccentFlags = state == ACCENT_ENABLE_ACRYLICBLURBEHIND ? 2 : 0;
            policy.GradientColor = gradient;
            int size = Marshal.SizeOf(policy);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                data.Attribute = WCA_ACCENT_POLICY;
                data.Data = ptr;
                data.SizeOfData = size;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }
}
