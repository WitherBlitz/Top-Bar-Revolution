// The little drop-down a usage pill opens: what's using the most CPU, memory
// or GPU right now - on this PC, or on a network PC (from what it shares in
// its broadcasts). One window, reused, refreshed every second while it's open;
// it closes when it loses focus, like the clock's panel.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TopBarExtras.Widgets;

namespace TopBarExtras
{
    internal abstract class TopSource
    {
        public static readonly string Ellipsis = ((char)0x2026).ToString();
        public static readonly string Dot = " " + (char)0x00B7 + " ";

        // Clicking the pill that opened the panel closes it again.
        public abstract string Key { get; }
        public abstract string Title { get; }
        // Biggest first; null while there's nothing to show (Note says why).
        public abstract List<TopRow> Rows();
        public virtual string Note { get { return "Measuring" + Ellipsis; } }
        public virtual string Idle { get { return "Nothing is using it right now"; } }
        public abstract string Format(double value);

        public static string Percent(double v)
        {
            return v < 9.95 ? v.ToString("0.0") + "%" : v.ToString("0") + "%";
        }

        public static string Gigabytes(double gb)
        {
            return gb < 1 ? (gb * 1024).ToString("0") + " MB" : gb.ToString("0.0") + " GB";
        }
    }

    internal sealed class LocalCpuSource : TopSource
    {
        private readonly ProcessSampler procs = new ProcessSampler();
        public override string Key { get { return "local:cpu"; } }
        public override string Title { get { return "CPU" + Name(); } }
        public override List<TopRow> Rows()
        {
            procs.Sample();
            return procs.Ready ? ProcessSampler.Top(procs.Cpu, procs.Names, 8, 0.05) : null;
        }
        public override string Format(double v) { return Percent(v); }
        private static string name;
        private static string Name()
        {
            if (name == null) name = LocalStats.CpuName();
            return name.Length > 0 ? Dot + name : "";
        }
    }

    internal sealed class LocalRamSource : TopSource
    {
        private const double GB = 1024.0 * 1024.0 * 1024.0;
        private readonly ProcessSampler procs = new ProcessSampler();
        public override string Key { get { return "local:ram"; } }
        public override string Title { get { return "Memory"; } }
        public override List<TopRow> Rows()
        {
            procs.Sample();
            Dictionary<int, double> gb = procs.Ram.ToDictionary(kv => kv.Key, kv => kv.Value / GB);
            return ProcessSampler.Top(gb, procs.Names, 8);
        }
        public override string Format(double v) { return Gigabytes(v); }
    }

    internal sealed class LocalGpuSource : TopSource
    {
        private readonly GPUUsage gpu;
        private readonly ProcessSampler procs = new ProcessSampler();
        public LocalGpuSource(GPUUsage gpu) { this.gpu = gpu; }
        public override string Key { get { return "local:gpu:" + gpu.GetHashCode(); } }
        public override string Title { get { return gpu.Title; } }
        public override List<TopRow> Rows()
        {
            Dictionary<int, double> users = gpu.Users;
            if (users == null) return null;
            procs.Sample();
            return ProcessSampler.Top(users, procs.Names, 8, 0.05);
        }
        public override string Format(double v) { return Percent(v); }
    }

    // A network PC's, as it last shared them.
    internal sealed class RemoteSource : TopSource
    {
        private readonly string pc, metric, key;
        private readonly Func<Peer, RemoteGpu> pick;
        private string note = "";

        // key: the same for every click on one pill, so a second click closes it.
        public RemoteSource(string pc, string metric, Func<Peer, RemoteGpu> pick, string key)
        {
            this.pc = pc;
            this.metric = metric;
            this.pick = pick;
            this.key = key;
        }

        public override string Key { get { return key; } }

        public override string Title
        {
            get
            {
                string name = Lan.NameOf(pc);
                Peer p = Lan.Find(pc);
                if (metric == "cpu") return name + Dot + (p != null && p.CpuName.Length > 0 ? p.CpuName : "CPU");
                if (metric == "ram") return name + Dot + "Memory";
                RemoteGpu g = p == null ? null : pick(p);
                return name + Dot + (g != null ? g.Short : "GPU");
            }
        }

        public override List<TopRow> Rows()
        {
            Peer p = Lan.Find(pc);
            if (p == null || !p.Online)
            {
                note = Lan.NameOf(pc) + " is off";
                return null;
            }
            List<TopRow> rows;
            if (metric == "cpu") rows = p.TopCpu;
            else if (metric == "ram") rows = p.TopRam;
            else
            {
                RemoteGpu g = pick(p);
                rows = g == null ? null : g.Top;
            }
            if (rows == null)
            {
                note = p.Name + " needs TopBar Extras 1.5 or newer to share this";
                return null;
            }
            return rows;
        }

        public override string Note { get { return note; } }
        public override string Format(double v) { return metric == "ram" ? Gigabytes(v) : Percent(v); }
    }

    internal sealed class TopPanel : Window
    {
        private static TopPanel instance;
        private static DateTime lastHidden;
        private static string lastKey;

        private readonly Border root = new Border();
        private readonly TextBlock title = new TextBlock();
        private readonly Grid table = new Grid();
        private readonly TextBlock note = new TextBlock();
        private readonly DispatcherTimer ticker = new DispatcherTimer(DispatcherPriority.Background);
        private TopSource source;
        private FrameworkElement anchor;
        private PanelTheme theme = new PanelTheme();

        // The click that opens a panel also deactivates an open one; without
        // the grace period, clicking the same pill would close it and reopen it.
        public static void Toggle(FrameworkElement anchor, TopSource source)
        {
            if (instance != null && instance.IsVisible)
            {
                bool same = instance.source != null && instance.source.Key == source.Key;
                instance.Dismiss();
                if (same) return;
            }
            else if (lastKey == source.Key && (DateTime.UtcNow - lastHidden).TotalMilliseconds < 250)
                return;
            if (instance == null) instance = new TopPanel();
            instance.Open(anchor, source);
        }

        private TopPanel()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;

            root.CornerRadius = new CornerRadius(12);
            root.BorderThickness = new Thickness(1);
            root.Padding = new Thickness(14, 10, 14, 12);
            StackPanel stack = new StackPanel { MinWidth = 200 };
            title.FontSize = 11;
            title.Margin = new Thickness(0, 0, 0, 6);
            note.FontSize = 12;
            stack.Children.Add(title);
            stack.Children.Add(table);
            stack.Children.Add(note);
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.Child = stack;
            Content = root;

            ticker.Tick += delegate
            {
                ticker.Interval = TimeSpan.FromSeconds(1);
                Fill();
            };
            Deactivated += delegate { Dismiss(); };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Dismiss();
            };
        }

        private void Open(FrameworkElement anchor, TopSource source)
        {
            this.anchor = anchor;
            this.source = source;
            Restyle(Host.Theme);
            Fill();
            Show();
            Place();
            Activate();
            // CPU use needs two samples; the first comes quickly.
            ticker.Interval = TimeSpan.FromMilliseconds(500);
            ticker.Start();
        }

        private void Dismiss()
        {
            ticker.Stop();
            lastHidden = DateTime.UtcNow;
            lastKey = source == null ? null : source.Key;
            Hide();
        }

        private void Restyle(PanelTheme t)
        {
            theme = t ?? new PanelTheme();
            root.Background = new SolidColorBrush(Color.FromArgb(0xFA, theme.Back.R, theme.Back.G, theme.Back.B));
            root.BorderBrush = new SolidColorBrush(theme.Edge);
            FontFamily = theme.Font;
            Foreground = new SolidColorBrush(theme.Text);
            title.Foreground = Ink(0.55);
            note.Foreground = Ink(0.6);
        }

        private SolidColorBrush Ink(double opacity)
        {
            Color c = theme.Text;
            return new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B));
        }

        private void Fill()
        {
            if (source == null) return;
            List<TopRow> rows;
            try { rows = source.Rows(); }
            catch (Exception) { rows = null; }
            title.Text = source.Title;
            table.Children.Clear();
            table.RowDefinitions.Clear();
            if (rows == null || rows.Count == 0)
            {
                note.Text = rows == null ? source.Note : source.Idle;
                note.Visibility = Visibility.Visible;
            }
            else
            {
                note.Visibility = Visibility.Collapsed;
                for (int i = 0; i < rows.Count; i++)
                {
                    table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    TopRow r = rows[i];
                    TextBlock name = new TextBlock
                    {
                        Text = r.Count > 1 ? r.Name + " (" + r.Count + ")" : r.Name,
                        MaxWidth = 210,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 2, 0, 2),
                        FontSize = 12,
                    };
                    TextBlock value = new TextBlock
                    {
                        Text = source.Format(r.Value),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(18, 2, 0, 2),
                        FontSize = 12,
                        Foreground = Ink(0.85),
                    };
                    Grid.SetRow(name, i);
                    Grid.SetRow(value, i);
                    Grid.SetColumn(value, 1);
                    table.Children.Add(name);
                    table.Children.Add(value);
                }
            }
            if (IsVisible) Place();
        }

        // Centred under the pill, kept on screen.
        private void Place()
        {
            if (anchor == null || PresentationSource.FromVisual(anchor) == null) return;
            UpdateLayout();
            PresentationSource src = PresentationSource.FromVisual(anchor);
            double sx = src.CompositionTarget.TransformToDevice.M11;
            double sy = src.CompositionTarget.TransformToDevice.M22;
            Point p = anchor.PointToScreen(new Point(0, 0));
            double centre = (p.X / sx) + anchor.ActualWidth / 2.0;
            Left = Math.Max(8, Math.Min(SystemParameters.PrimaryScreenWidth - ActualWidth - 8,
                                        centre - ActualWidth / 2.0));
            Top = (p.Y / sy) + anchor.ActualHeight + 6;
        }
    }
}
