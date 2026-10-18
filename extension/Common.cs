// Bits the extension widgets share: reading settings, colors, the {name:0.0}
// reading templates, and the name / reading / load-bar row the GPU widgets
// (local and network) draw.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Newtonsoft.Json.Linq;

namespace TopBarExtras
{
    internal static class Cfg
    {
        private static readonly Regex Token = new Regex(@"\{(\w+)(?::([^}]*))?\}");

        public static string Str(JObject s, string key, string fallback)
        {
            if (s == null || s[key] == null || s[key].Type == JTokenType.Null) return fallback;
            string v = s[key].ToString();
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        public static double Num(JObject s, string key, double fallback)
        {
            return s == null ? fallback : Num(s[key], fallback);
        }

        // Numbers straight from the JSON token, so a PC set to a comma-decimal
        // language reads "12.5" the same as everyone else.
        public static double Num(JToken t, double fallback)
        {
            if (t == null) return fallback;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (double)t;
            double v;
            return double.TryParse(t.ToString(), NumberStyles.Any,
                                   CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static bool Bool(JObject s, string key, bool fallback)
        {
            if (s == null || s[key] == null) return fallback;
            bool v;
            return bool.TryParse(s[key].ToString(), out v) ? v : fallback;
        }

        public static Brush Paint(string color, Color fallback)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
            catch (Exception) { return new SolidColorBrush(fallback); }
        }

        // Green when idle, through yellow, to red at full load.
        public static Color PercentColor(double percent)
        {
            double r = percent < 50.0 ? percent * 5.12 : 255.0;
            double g = percent < 50.0 ? 255.0 : 256.0 - (percent - 50.0) * 5.12;
            return Color.FromRgb(Byte(r), Byte(g), 0);
        }

        private static byte Byte(double v)
        {
            return (byte)Math.Max(0.0, Math.Min(255.0, v));
        }

        // "{percent:0}%  {used:0.0}/{total:0} GB" with .NET number formats;
        // tokens it has no value for are left as they are.
        public static string Compose(string format, IDictionary<string, double> values)
        {
            return Token.Replace(format, delegate(Match m)
            {
                double v;
                if (!values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out v)) return m.Value;
                string f = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : "0";
                try { return v.ToString(f, CultureInfo.InvariantCulture); }
                catch (FormatException) { return v.ToString("0", CultureInfo.InvariantCulture); }
            });
        }
    }

    // A name, a reading and a load bar filling left to right. The reading goes
    // before the bar unless "value-position" is "after", and keeps room for its
    // widest value, so a number changing width ("9%" -> "10%") never shoves the
    // bar - or anything after it - sideways. The bar can be switched off, and a
    // power icon can lead the row (a network PC's wake button).
    //
    // Settings it reads: color, bar-color (hex or "from-percentage"),
    // background-color, width, height, radius, value-position, bar.
    internal sealed class Meter
    {
        public readonly StackPanel Panel = new StackPanel();
        private readonly Path icon = new Path();
        private readonly TextBlock name = new TextBlock();
        private readonly TextBlock reading = new TextBlock();
        private readonly Grid bar = new Grid();
        private readonly Rectangle fill = new Rectangle();
        private readonly string barColor;
        private readonly double barWidth;
        private readonly double barInset;
        private readonly bool barWanted;
        private string widest = "";

        public Meter(JObject s)
        {
            barColor = Cfg.Str(s, "bar-color", "from-percentage");
            barWidth = Cfg.Num(s, "width", 60);
            barWanted = Cfg.Bool(s, "bar", true);
            double h = Cfg.Num(s, "height", 10);
            double radius = Cfg.Num(s, "radius", 2);
            bool readingFirst = !Cfg.Str(s, "value-position", "before")
                .Equals("after", StringComparison.OrdinalIgnoreCase);

            Brush ink = Cfg.Paint(Cfg.Str(s, "color", "#BBBBBB"), Colors.Silver);
            foreach (TextBlock t in new[] { name, reading })
            {
                t.Foreground = ink;
                t.VerticalAlignment = VerticalAlignment.Center;
            }

            // A power symbol: a ring open at the top, and a stroke through the gap.
            icon.Data = Geometry.Parse("M 2.03,2.63 A 4.2,4.2 0 1 0 7.97,2.63 M 5,0.6 L 5,5.2");
            icon.Stroke = ink;
            icon.StrokeThickness = 1.3;
            icon.StrokeStartLineCap = icon.StrokeEndLineCap = PenLineCap.Round;
            icon.Width = 10;
            icon.Height = 11;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Visibility = Visibility.Collapsed;

            // Drawn like the stock RAM bar: sized for the 18px strip and inset
            // with margins, so it lines up whether it's stretched or centred.
            barInset = (18 - h) / 2;
            bar.Width = barWidth;
            bar.Visibility = barWanted ? Visibility.Visible : Visibility.Collapsed;
            Rectangle track = new Rectangle();
            track.Fill = Cfg.Paint(Cfg.Str(s, "background-color", "#33BBBBBB"),
                                   Color.FromArgb(0x33, 0xBB, 0xBB, 0xBB));
            track.RadiusX = track.RadiusY = radius;
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.Width = 0;
            fill.RadiusX = fill.RadiusY = radius;
            if (barColor != "from-percentage") fill.Fill = Cfg.Paint(barColor, Colors.Silver);
            bar.Children.Add(track);
            bar.Children.Add(fill);

            Panel.Orientation = Orientation.Horizontal;
            Panel.Children.Add(icon);
            Panel.Children.Add(name);
            if (readingFirst)
            {
                // Right-aligned, so the digits sit against the bar.
                reading.TextAlignment = TextAlignment.Right;
                Panel.Children.Add(reading);
                Panel.Children.Add(bar);
            }
            else
            {
                Panel.Children.Add(bar);
                Panel.Children.Add(reading);
            }
            reading.Loaded += delegate { Fit(); };
            Space();
        }

        // "" hides the name.
        public string Name
        {
            get { return name.Text; }
            set
            {
                name.Text = value ?? "";
                name.Visibility = name.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                Space();
            }
        }

        // The bar shows when the settings want one and this is true.
        public bool Bar
        {
            set
            {
                bar.Visibility = value && barWanted ? Visibility.Visible : Visibility.Collapsed;
                Space();
            }
        }

        public bool Icon
        {
            set
            {
                icon.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                Space();
            }
        }

        // UI thread only. widestText is the reading at its widest - see
        // Widest(). An empty reading takes no room at all.
        public void Show(string text, double load, string widestText)
        {
            load = Math.Max(0, Math.Min(100, load));
            reading.Text = text;
            reading.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            widest = widestText ?? text;
            fill.Width = barWidth * load / 100.0;
            if (barColor == "from-percentage") fill.Fill = new SolidColorBrush(Cfg.PercentColor(load));
            Space();
            Fit();
        }

        // 8px between whichever parts are showing (6 after the icon).
        private void Space()
        {
            FrameworkElement before = null;
            foreach (UIElement u in Panel.Children)
            {
                FrameworkElement e = u as FrameworkElement;
                if (e == null || e.Visibility != Visibility.Visible) continue;
                double left = before == null ? 0 : before == icon ? 6 : 8;
                double inset = e == bar ? barInset : 0;
                e.Margin = new Thickness(left, inset, 0, inset);
                before = e;
            }
        }

        // The widest a reading shaped like this gets: every digit an 8.
        public static string Widest(string text)
        {
            return Regex.Replace(text, "[0-9]", "8");
        }

        // Measured each time, in whatever font the engine has applied.
        private void Fit()
        {
            try
            {
                FormattedText ft = new FormattedText(widest, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(reading.FontFamily, reading.FontStyle, reading.FontWeight, reading.FontStretch),
                    reading.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(reading).PixelsPerDip);
                reading.MinWidth = Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
            }
            catch (Exception) { }
        }
    }
}
