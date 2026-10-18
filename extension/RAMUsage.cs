// RAM readout that can show gigabytes as well as a percentage. Stock
// TopBar.Widgets.RAMUsageText only ever renders "NN %" - the format is baked in.
//
// Settings (all optional):
//   "color"     hex or WPF color name                    (default "#BBBBBB")
//   "format"    template, see below   (default "{percent:0} %  {used:0.0}/{total:0} GB")
//   "interval"  milliseconds between samples             (default 1000)
//
// Format placeholders: {used} {total} {free} {percent}. Each takes an optional
// .NET numeric format after a colon, so "{used:0.00}/{total:0} GB" gives
// "12.45/64 GB". Values are in GiB; percent is Windows' own memory load figure,
// the same number Task Manager shows.
//
// Rendered as a bare TextBlock with only Foreground set, exactly like the stock
// text widgets - SetupWidgets supplies the 4px gap between widgets itself.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using TopBar;

namespace TopBarExtras.Widgets
{
    public class RAMUsage : iWidgetBase
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile,
                         ullAvailPageFile, ullTotalVirtual, ullAvailVirtual,
                         ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        private static readonly Regex Token =
            new Regex(@"\{(\w+)(?::([^}]*))?\}");
        private const double GB = 1024.0 * 1024.0 * 1024.0;

        private readonly TextBlock label = new TextBlock();
        private readonly Timer timer;
        private readonly string format;
        private bool disposed;

        public RAMUsage(object settings)
        {
            JObject s = settings as JObject;
            string color = Str(s, "color", "#BBBBBB");
            format = Str(s, "format", "{percent:0} %  {used:0.0}/{total:0} GB");
            double interval = Dbl(s, "interval", 1000.0);
            if (interval < 100.0) interval = 100.0;

            try
            {
                label.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(color));
            }
            catch (Exception)
            {
                label.Foreground = new SolidColorBrush(Colors.Silver);
            }

            label.Text = Compose();

            timer = new Timer();
            timer.Interval = interval;
            timer.Elapsed += Tick;
            timer.Start();
        }

        public FrameworkElement GetComponent()
        {
            return label;
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); }
        }

        private void Tick(object sender, ElapsedEventArgs e)
        {
            if (disposed) return;
            try
            {
                string text = Compose();
                label.Dispatcher.BeginInvoke(new Action(delegate
                {
                    label.Text = text;
                }));
            }
            catch (Exception)
            {
                // Never let a sampling hiccup take the bar down.
            }
        }

        private string Compose()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) return "";

            double total = m.ullTotalPhys / GB;
            double free = m.ullAvailPhys / GB;
            double used = total - free;
            double percent = m.dwMemoryLoad;

            return Token.Replace(format, delegate(Match match)
            {
                double v;
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "used": v = used; break;
                    case "total": v = total; break;
                    case "free": v = free; break;
                    case "percent": v = percent; break;
                    default: return match.Value;
                }
                string f = match.Groups[2].Success && match.Groups[2].Value.Length > 0
                    ? match.Groups[2].Value : "0.0";
                try
                {
                    return v.ToString(f, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    return v.ToString(CultureInfo.InvariantCulture);
                }
            });
        }

        private static string Str(JObject s, string key, string fallback)
        {
            if (s == null || s[key] == null) return fallback;
            string v = s[key].ToString();
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static double Dbl(JObject s, string key, double fallback)
        {
            if (s == null || s[key] == null) return fallback;
            double v;
            return double.TryParse(s[key].ToString(), NumberStyles.Any,
                                   CultureInfo.InvariantCulture, out v)
                ? v : fallback;
        }
    }
}
