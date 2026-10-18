// A drop-in replacement for TopBar.Widgets.CPULoad with a configurable
// refresh interval. Stock TopBar hardcodes timer.Interval = 1000 in the
// CPULoad constructor, so the only way to change it without patching the exe
// is a widget of our own - which TopBar supports: Build() does
// Assembly.LoadFile on every extensions\*.dll and SetupWidgets falls back to
// assembly.GetType(name) before giving up.
//
// Settings (all optional):
//   "color"       hex, "rainbow", or "from-percentage"   (default "rainbow")
//   "line-width"  bar width in px                        (default 5)
//   "radius"      corner radius - honoured here, unlike stock CPULoad
//   "interval"    milliseconds between samples           (default 3000)
//
// Visuals mirror the original: one Rectangle per core, Margin 3 all round,
// aligned bottom, height = percent/100 * 12, inside an 18px bar.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Newtonsoft.Json.Linq;
using TopBar;

namespace TopBarExtras.Widgets
{
    public class CPULoad : iWidgetBase
    {
        private const double MaxHeight = 12.0;

        private readonly StackPanel panel = new StackPanel();
        private readonly List<PerformanceCounter> counters =
            new List<PerformanceCounter>();
        private readonly List<CounterSample> previous =
            new List<CounterSample>();
        private readonly List<Rectangle> bars = new List<Rectangle>();
        private readonly string mainColor;
        private readonly Timer timer;
        private bool disposed;

        public CPULoad(object settings)
        {
            JObject s = settings as JObject;
            mainColor = Str(s, "color", "rainbow");
            int lineWidth = Int(s, "line-width", 5);
            int radius = Int(s, "radius", 0);
            double interval = Dbl(s, "interval", 3000.0);
            if (interval < 100.0) interval = 100.0;

            panel.Orientation = Orientation.Horizontal;

            List<string> names = new PerformanceCounterCategory(
                    "Processor Information")
                .GetInstanceNames()
                .Where(n => !n.EndsWith("Total"))
                .OrderBy(CoreNum)
                .ToList();

            for (int i = 0; i < names.Count; i++)
            {
                PerformanceCounter counter = new PerformanceCounter(
                    "Processor Information", "% Processor Time", names[i]);
                counters.Add(counter);
                previous.Add(counter.NextSample());

                Rectangle bar = new Rectangle();
                bar.Width = lineWidth;
                bar.Height = 0;
                bar.RadiusX = radius;
                bar.RadiusY = radius;
                bar.VerticalAlignment = VerticalAlignment.Bottom;
                bar.Margin = new Thickness(3, 3, 3, 3);
                if (mainColor != "from-percentage")
                    bar.Fill = new SolidColorBrush(StaticColor(i, names.Count));

                bars.Add(bar);
                panel.Children.Add(bar);
            }

            timer = new Timer();
            timer.Interval = interval;
            timer.Elapsed += Tick;
            timer.Start();
        }

        public FrameworkElement GetComponent()
        {
            return panel;
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); }
            foreach (PerformanceCounter c in counters) c.Dispose();
        }

        private void Tick(object sender, ElapsedEventArgs e)
        {
            if (disposed) return;
            try
            {
                double[] values = new double[counters.Count];
                for (int i = 0; i < counters.Count; i++)
                {
                    CounterSample now = counters[i].NextSample();
                    double v = CounterSample.Calculate(previous[i], now);
                    previous[i] = now;
                    values[i] = Math.Max(0.0, Math.Min(100.0, v));
                }
                panel.Dispatcher.BeginInvoke(new Action(delegate
                {
                    for (int i = 0; i < bars.Count && i < values.Length; i++)
                    {
                        bars[i].Height = values[i] / 100.0 * MaxHeight;
                        if (mainColor == "from-percentage")
                            bars[i].Fill = new SolidColorBrush(
                                PercentColor(values[i]));
                    }
                }));
            }
            catch (Exception)
            {
                // A dropped perf-counter sample must never take the bar down.
            }
        }

        // -- settings helpers -------------------------------------------------

        private static string Str(JObject s, string key, string fallback)
        {
            if (s == null || s[key] == null) return fallback;
            string v = s[key].ToString();
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static int Int(JObject s, string key, int fallback)
        {
            if (s == null || s[key] == null) return fallback;
            int v;
            return int.TryParse(s[key].ToString(), out v) ? v : fallback;
        }

        private static double Dbl(JObject s, string key, double fallback)
        {
            if (s == null || s[key] == null) return fallback;
            double v;
            return double.TryParse(s[key].ToString(), out v) ? v : fallback;
        }

        // Instance names look like "0,5" (group, core); sort them properly so
        // the bars stay in core order on machines with more than one group.
        private static int CoreNum(string name)
        {
            string[] parts = name.Split(',');
            int group = 0, core = 0;
            if (parts.Length >= 2)
            {
                int.TryParse(parts[0], out group);
                int.TryParse(parts[1], out core);
            }
            else int.TryParse(name, out core);
            return group * 4096 + core;
        }

        // -- colors -----------------------------------------------------------

        private Color StaticColor(int index, int count)
        {
            if (mainColor == "rainbow")
                return HslToRgb(360.0 * index / count, 200.0 / 255.0,
                                200.0 / 255.0);
            try
            {
                return (Color)ColorConverter.ConvertFromString(mainColor);
            }
            catch (Exception)
            {
                return Colors.Silver;
            }
        }

        // Green at idle through yellow to red at 100%, matching the original.
        private static Color PercentColor(double percent)
        {
            double r = percent < 50.0 ? percent * 5.12 : 255.0;
            double g = percent < 50.0 ? 255.0 : 256.0 - (percent - 50.0) * 5.12;
            return Color.FromRgb(Byte(r), Byte(g), 0);
        }

        private static byte Byte(double v)
        {
            return (byte)Math.Max(0.0, Math.Min(255.0, v));
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            if (s <= 0.0)
            {
                byte g = Byte(l * 255.0);
                return Color.FromRgb(g, g, g);
            }
            h = h / 360.0;
            double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            double p = 2.0 * l - q;
            return Color.FromRgb(Byte(Hue(p, q, h + 1.0 / 3.0) * 255.0),
                                 Byte(Hue(p, q, h) * 255.0),
                                 Byte(Hue(p, q, h - 1.0 / 3.0) * 255.0));
        }

        private static double Hue(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }
    }
}
