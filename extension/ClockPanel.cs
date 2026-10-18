// The drop-down behind the bar's clock: a month calendar with a stopwatch and
// a timer beside it.
//
// Built in code rather than XAML because the engine is a plain library loaded
// into TopBar - there is no BAML to load. Controls are Borders and TextBlocks
// with their own mouse handling, which also keeps them matching the bar's theme
// (same font, pill fill and text color) instead of WPF's light default styles.
//
// The panel is created once and hidden rather than closed, so the stopwatch and
// timer keep running while it's out of sight. A finished timer chimes and pops
// the panel back open.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBarExtras
{
    internal sealed class PanelTheme
    {
        public FontFamily Font = new FontFamily("Segoe UI");
        public Color Text = Color.FromRgb(0xEC, 0xEC, 0xEC);
        public Color Back = Color.FromRgb(0x16, 0x16, 0x1A);
        public Color Card = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        public Color Edge = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        public DayOfWeek FirstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    }

    internal sealed class ClockPanel : Window
    {
        private static ClockPanel instance;
        private static DateTime lastHidden;

        private PanelTheme theme;
        private DateTime month = DateTime.Today;
        private readonly UniformGrid days = new UniformGrid();
        private readonly TextBlock monthLabel = new TextBlock();
        private readonly Border root = new Border();
        private readonly Grid content = new Grid();

        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        private readonly TextBlock watchLabel = new TextBlock();
        private TextBlock watchButton;

        private TimeSpan timerSet = TimeSpan.FromMinutes(5);
        private DateTime timerEnds;
        private bool timerRunning;
        private int timerFlash, alarmFlash;
        private readonly TextBlock timerLabel = new TextBlock();
        private TextBlock timerButton;

        // Alarm: a typed time, the days it repeats on, and whether it's armed.
        // Kept in its own file - config.json already has the settings app as a
        // writer, and two writers would race.
        private static readonly string AlarmPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"TopBar\alarm.json");
        private TimeSpan alarmAt = new TimeSpan(7, 30, 0);
        private readonly bool[] alarmDays = new bool[7];
        private bool alarmOn;
        private DateTime alarmNext = DateTime.MaxValue;
        private TextBox alarmBox;
        private TextBlock alarmButton, alarmNote;
        private readonly Border[] dayChips = new Border[7];

        private int ticks;
        private readonly DispatcherTimer ticker = new DispatcherTimer(DispatcherPriority.Render);

        // -- opening ---------------------------------------------------------

        // Built (hidden) as soon as TopBar starts, so an alarm set days ago
        // still rings without anyone opening the panel first.
        public static void Ensure(PanelTheme theme)
        {
            if (instance == null)
            {
                instance = new ClockPanel();
                instance.Restyle(theme);
            }
        }

        public static void Toggle(FrameworkElement anchor, PanelTheme theme)
        {
            // The click that opens the panel also deactivates it; without this
            // the panel would close and immediately reopen.
            if (instance != null && instance.IsVisible)
            {
                instance.Dismiss();
                return;
            }
            lastAnchor = anchor;
            if ((DateTime.UtcNow - lastHidden).TotalMilliseconds < 250) return;
            if (instance == null) instance = new ClockPanel();
            instance.Open(anchor, theme);
        }

        public static void Retheme(PanelTheme theme)
        {
            if (instance != null) instance.Restyle(theme);
        }

        private ClockPanel()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            theme = new PanelTheme();

            root.CornerRadius = new CornerRadius(14);
            root.BorderThickness = new Thickness(1);
            root.Padding = new Thickness(16);
            root.Child = content;
            Content = root;
            Build();

            ticker.Interval = TimeSpan.FromMilliseconds(60);
            ticker.Tick += delegate { Tick(); };
            ticker.Start();

            Deactivated += delegate { Dismiss(); };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Dismiss();
            };
        }

        private void Open(FrameworkElement anchor, PanelTheme t)
        {
            Restyle(t);
            month = DateTime.Today;
            FillDays();
            Show();
            UpdateLayout();

            // Centre under the clock, kept on screen.
            PresentationSource src = PresentationSource.FromVisual(anchor);
            double sx = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
            double sy = src != null ? src.CompositionTarget.TransformToDevice.M22 : 1.0;
            Point p = anchor.PointToScreen(new Point(0, 0));
            double centre = (p.X / sx) + anchor.ActualWidth / 2.0;
            Left = Math.Max(8, Math.Min(SystemParameters.PrimaryScreenWidth - ActualWidth - 8,
                                        centre - ActualWidth / 2.0));
            Top = (p.Y / sy) + anchor.ActualHeight + 6;
            Activate();
            Host.Log("panel opened");
        }

        // Show the panel on its own (a finished timer or a ringing alarm). The
        // anchor may be one nobody has clicked yet, so fall back to whichever
        // clock element the engine last wired up.
        private void PopOpen()
        {
            if (IsVisible) return;
            FrameworkElement anchor = lastAnchor ?? Host.ClockTarget;
            if (anchor == null || !anchor.IsVisible) return;
            Open(anchor, theme);
        }

        private void Dismiss()
        {
            lastHidden = DateTime.UtcNow;
            Hide();
            Host.Log("panel hidden");
        }

        // -- theme -------------------------------------------------------------

        private void Restyle(PanelTheme t)
        {
            theme = t;
            root.Background = new SolidColorBrush(Color.FromArgb(0xFA, t.Back.R, t.Back.G, t.Back.B));
            root.BorderBrush = new SolidColorBrush(t.Edge);
            FontFamily = t.Font;
            Foreground = new SolidColorBrush(t.Text);
            foreach (object o in cards) ((Border)o).Background = new SolidColorBrush(t.Card);
            FillDays();
        }

        private readonly System.Collections.Generic.List<Border> cards =
            new System.Collections.Generic.List<Border>();

        private static SolidColorBrush Alert()
        {
            return new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
        }

        private SolidColorBrush Ink(double opacity)
        {
            Color c = theme.Text;
            return new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B));
        }

        // -- building ----------------------------------------------------------

        private void Build()
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The tools column is taller than six week rows, so centre the
            // calendar against it rather than leaving a gap underneath.
            StackPanel calendar = new StackPanel { Width = 286,
                                                   VerticalAlignment = VerticalAlignment.Center };
            Grid head = new Grid { Margin = new Thickness(2, 0, 2, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock prev = Arrow("\u2039", delegate { month = month.AddMonths(-1); FillDays(); });
            Grid.SetColumn(prev, 0);
            monthLabel.FontSize = 14;
            monthLabel.VerticalAlignment = VerticalAlignment.Center;
            monthLabel.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(monthLabel, 1);
            TextBlock next = Arrow("\u203a", delegate { month = month.AddMonths(1); FillDays(); });
            Grid.SetColumn(next, 2);
            TextBlock today = Arrow("Today", delegate { month = DateTime.Today; FillDays(); });
            today.FontSize = 11;
            today.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(today, 3);
            head.Children.Add(prev);
            head.Children.Add(monthLabel);
            head.Children.Add(next);
            head.Children.Add(today);
            calendar.Children.Add(head);

            UniformGrid names = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
            calendar.Children.Add(names);
            weekNames = names;

            days.Columns = 7;
            days.Rows = 6;
            calendar.Children.Add(days);
            Grid.SetColumn(calendar, 0);
            content.Children.Add(calendar);

            Border split = new Border
            {
                Width = 1,
                Margin = new Thickness(16, 4, 16, 4),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumn(split, 1);
            content.Children.Add(split);

            StackPanel right = new StackPanel { Width = 236 };
            right.Children.Add(BuildStopwatch());
            right.Children.Add(BuildTimer());
            right.Children.Add(BuildAlarm());
            LoadAlarm();
            Grid.SetColumn(right, 2);
            content.Children.Add(right);
        }

        private UniformGrid weekNames;

        private Border BuildStopwatch()
        {
            StackPanel box = new StackPanel();
            box.Children.Add(Caption("STOPWATCH"));
            watchLabel.FontSize = 24;
            watchLabel.Margin = new Thickness(0, 6, 0, 10);
            watchLabel.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(watchLabel);
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal,
                                              HorizontalAlignment = HorizontalAlignment.Center };
            watchButton = Chip("Start", delegate
            {
                if (watch.IsRunning) watch.Stop(); else watch.Start();
            });
            row.Children.Add(Holder(watchButton));
            row.Children.Add(Holder(Chip("Reset", delegate { watch.Reset(); })));
            box.Children.Add(row);
            return Card(box, new Thickness(0, 0, 0, 12));
        }

        private Border BuildTimer()
        {
            StackPanel box = new StackPanel();
            box.Children.Add(Caption("TIMER"));
            timerLabel.FontSize = 24;
            timerLabel.Margin = new Thickness(0, 6, 0, 8);
            timerLabel.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(timerLabel);

            StackPanel presets = new StackPanel { Orientation = Orientation.Horizontal,
                                                  HorizontalAlignment = HorizontalAlignment.Center,
                                                  Margin = new Thickness(0, 0, 0, 8) };
            foreach (int m in new[] { 1, 5, 10, 25 })
            {
                int mins = m;
                presets.Children.Add(Holder(Chip(mins + "m", delegate { SetTimer(TimeSpan.FromMinutes(mins)); }, 8)));
            }
            box.Children.Add(presets);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal,
                                              HorizontalAlignment = HorizontalAlignment.Center };
            timerButton = Chip("Start", delegate
            {
                if (timerRunning) { timerSet = timerEnds - DateTime.Now; timerRunning = false; }
                else if (timerSet > TimeSpan.Zero) { timerEnds = DateTime.Now + timerSet; timerRunning = true; }
                timerFlash = 0;
            });
            row.Children.Add(Holder(timerButton));
            row.Children.Add(Holder(Chip("Reset", delegate
            {
                timerRunning = false;
                timerFlash = 0;
                SetTimer(TimeSpan.FromMinutes(5));
            })));
            // +1m adds to whatever is on the clock: a running countdown keeps
            // going with a minute more, a paused or finished one gains a minute.
            row.Children.Add(Holder(Chip("+1m", delegate
            {
                if (timerRunning) timerEnds = timerEnds.AddMinutes(1);
                else timerSet = timerSet.Add(TimeSpan.FromMinutes(1));
                timerFlash = 0;
            })));
            box.Children.Add(row);
            return Card(box, new Thickness(0));
        }

        private Border BuildAlarm()
        {
            StackPanel box = new StackPanel();
            box.Children.Add(Caption("ALARM"));

            alarmBox = new TextBox
            {
                FontSize = 22,
                Width = 110,
                Margin = new Thickness(0, 4, 0, 6),
                TextAlignment = TextAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            alarmBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { ReadAlarmTime(); Keyboard.ClearFocus(); }
            };
            alarmBox.LostFocus += delegate { ReadAlarmTime(); };
            box.Children.Add(alarmBox);

            // S M T W T F S - no days means "once, at the next such time".
            UniformGrid week = new UniformGrid { Columns = 7, Margin = new Thickness(4, 0, 4, 8) };
            string[] letters = { "S", "M", "T", "W", "T", "F", "S" };
            for (int i = 0; i < 7; i++)
            {
                int day = i;
                TextBlock t = new TextBlock
                {
                    Text = letters[i],
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Padding = new Thickness(0, 3, 0, 4),
                    Cursor = Cursors.Hand,
                };
                Border chip = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(2),
                    Child = t,
                };
                chip.MouseLeftButtonUp += delegate
                {
                    alarmDays[day] = !alarmDays[day];
                    ArmAlarm(alarmOn);
                };
                dayChips[i] = chip;
                week.Children.Add(chip);
            }
            box.Children.Add(week);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal,
                                              HorizontalAlignment = HorizontalAlignment.Center };
            alarmButton = Chip("Set", delegate { ReadAlarmTime(); ArmAlarm(!alarmOn); });
            row.Children.Add(Holder(alarmButton));
            box.Children.Add(row);

            alarmNote = new TextBlock
            {
                FontSize = 10,
                Opacity = 0.6,
                Margin = new Thickness(0, 6, 0, 0),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            box.Children.Add(alarmNote);
            return Card(box, new Thickness(0, 12, 0, 0));
        }

        // Accepts "7:30", "07:30", "7:30 pm", "19:30" - whatever gets typed.
        private void ReadAlarmTime()
        {
            string text = (alarmBox.Text ?? "").Trim();
            DateTime parsed;
            string[] formats = { "H:mm", "HH:mm", "h:mm tt", "hh:mm tt", "h:mmtt", "htt", "H", "h tt" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.CurrentCulture,
                                       DateTimeStyles.None, out parsed)
                || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
                alarmAt = parsed.TimeOfDay;
            Host.Log("alarm time typed: '" + text + "' -> " + alarmAt);
            ShowAlarmTime();
            if (alarmOn) ArmAlarm(true);
        }

        private void ShowAlarmTime()
        {
            alarmBox.Text = new DateTime(2000, 1, 1).Add(alarmAt).ToString("h:mm tt", CultureInfo.CurrentCulture);
        }

        private void ArmAlarm(bool on)
        {
            bool was = alarmOn;
            alarmOn = on;
            alarmFlash = 0;
            alarmNext = on ? NextRing() : DateTime.MaxValue;
            SaveAlarm();
            StyleAlarm();
            // Day chips re-arm to recompute the next ring; only say something
            // when the alarm actually goes on or off.
            if (on) Host.Log("alarm set for " + alarmNext.ToString("ddd HH:mm") + " (days: " + DayList() + ")");
            else if (was) Host.Log("alarm cancelled");
        }

        private string DayList()
        {
            string[] names = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
            string o = "";
            for (int i = 0; i < 7; i++) if (alarmDays[i]) o += (o.Length > 0 ? "," : "") + names[i];
            return o.Length > 0 ? o : "once";
        }

        private DateTime NextRing()
        {
            return NextRingFrom(DateTime.Today.Add(alarmAt));
        }

        private DateTime NextRingFrom(DateTime from)
        {
            DateTime when = from;
            bool repeats = false;
            foreach (bool d in alarmDays) repeats |= d;
            for (int i = 0; i < 8; i++)
            {
                if (when > DateTime.Now && (!repeats || alarmDays[(int)when.DayOfWeek]))
                    return when;
                when = when.AddDays(1);
            }
            return DateTime.MaxValue;
        }

        private void StyleAlarm()
        {
            if (alarmButton == null) return;
            alarmButton.Text = alarmOn ? "Cancel" : "Set";
            alarmBox.Foreground = Ink(alarmOn ? 1 : 0.8);
            alarmBox.CaretBrush = Ink(1);
            alarmBox.BorderBrush = Ink(0.25);
            for (int i = 0; i < 7; i++)
            {
                dayChips[i].Background = alarmDays[i] ? new SolidColorBrush(theme.Text)
                                                      : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                ((TextBlock)dayChips[i].Child).Foreground = alarmDays[i]
                    ? new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)) : Ink(0.75);
            }
            if (!alarmOn) { alarmNote.Text = "off"; return; }
            TimeSpan away = alarmNext - DateTime.Now;
            alarmNote.Text = away.TotalHours >= 1
                ? string.Format(CultureInfo.CurrentCulture, "rings in {0}h {1}m",
                                (int)away.TotalHours, away.Minutes)
                : string.Format(CultureInfo.CurrentCulture, "rings in {0}m", Math.Max(1, away.Minutes));
        }

        private void LoadAlarm()
        {
            try
            {
                if (!System.IO.File.Exists(AlarmPath)) { ShowAlarmTime(); StyleAlarm(); return; }
                Newtonsoft.Json.Linq.JObject j = Newtonsoft.Json.Linq.JObject.Parse(
                    System.IO.File.ReadAllText(AlarmPath));
                TimeSpan at;
                if (j["time"] != null && TimeSpan.TryParse(j["time"].ToString(), out at)) alarmAt = at;
                if (j["days"] != null)
                {
                    int i = 0;
                    foreach (Newtonsoft.Json.Linq.JToken d in j["days"])
                        if (i < 7) alarmDays[i++] = (bool)d;
                }
                alarmOn = j["enabled"] != null && (bool)j["enabled"];
            }
            catch (Exception) { }
            ShowAlarmTime();
            alarmNext = alarmOn ? NextRing() : DateTime.MaxValue;
            StyleAlarm();
        }

        private void SaveAlarm()
        {
            try
            {
                Newtonsoft.Json.Linq.JObject j = new Newtonsoft.Json.Linq.JObject();
                j["time"] = alarmAt.ToString();
                j["days"] = new Newtonsoft.Json.Linq.JArray(alarmDays);
                j["enabled"] = alarmOn;
                System.IO.File.WriteAllText(AlarmPath, j.ToString());
            }
            catch (Exception) { }
        }

        private void SetTimer(TimeSpan span)
        {
            timerSet = span;
            timerRunning = false;
            timerFlash = 0;
        }

        // -- pieces ------------------------------------------------------------

        private Border Card(UIElement child, Thickness margin)
        {
            Border b = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 12),
                Margin = margin,
                Child = child,
            };
            cards.Add(b);
            return b;
        }

        private TextBlock Caption(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        private TextBlock Arrow(string glyph, Action click)
        {
            TextBlock t = new TextBlock
            {
                Text = glyph,
                FontSize = 15,
                Padding = new Thickness(8, 0, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Opacity = 0.75,
            };
            t.MouseEnter += delegate { t.Opacity = 1; };
            t.MouseLeave += delegate { t.Opacity = 0.75; };
            t.MouseLeftButtonUp += delegate { click(); };
            return t;
        }

        private TextBlock Chip(string text, Action click, double pad = 12)
        {
            TextBlock t = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Padding = new Thickness(pad, 5, pad, 6),
                Margin = new Thickness(5, 0, 5, 0),
                Cursor = Cursors.Hand,
                TextAlignment = TextAlignment.Center,
            };
            Border b = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                Child = t,
            };
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); };
            b.MouseLeave += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)); };
            b.MouseLeftButtonUp += delegate { click(); };
            // The chip is what gets laid out; the caller keeps the TextBlock to
            // change its label.
            t.Tag = b;
            return t;
        }

        private static UIElement Holder(TextBlock chip)
        {
            return (UIElement)chip.Tag ?? chip;
        }

        // -- calendar ------------------------------------------------------------

        private void FillDays()
        {
            if (weekNames == null) return;
            monthLabel.Text = month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            monthLabel.Foreground = Ink(1);

            weekNames.Children.Clear();
            string[] shortNames = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
            for (int i = 0; i < 7; i++)
            {
                DayOfWeek d = (DayOfWeek)(((int)theme.FirstDay + i) % 7);
                weekNames.Children.Add(new TextBlock
                {
                    Text = shortNames[(int)d].Substring(0, Math.Min(2, shortNames[(int)d].Length)),
                    FontSize = 10,
                    Opacity = 0.5,
                    Foreground = Ink(1),
                    TextAlignment = TextAlignment.Center,
                });
            }

            days.Children.Clear();
            DateTime first = new DateTime(month.Year, month.Month, 1);
            int lead = ((int)first.DayOfWeek - (int)theme.FirstDay + 7) % 7;
            DateTime start = first.AddDays(-lead);
            for (int i = 0; i < 42; i++)
            {
                DateTime day = start.AddDays(i);
                bool inMonth = day.Month == month.Month;
                bool isToday = day.Date == DateTime.Today;
                TextBlock t = new TextBlock
                {
                    Text = day.Day.ToString(CultureInfo.CurrentCulture),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    Padding = new Thickness(0, 5, 0, 6),
                    Foreground = isToday ? new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12))
                                         : Ink(inMonth ? 0.92 : 0.32),
                };
                Border cell = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(1),
                    Child = t,
                    Background = isToday ? new SolidColorBrush(theme.Text) : Brushes.Transparent,
                };
                days.Children.Add(cell);
            }
        }

        // -- ticking -------------------------------------------------------------

        private void Tick()
        {
            TimeSpan e = watch.Elapsed;
            watchLabel.Text = e.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3}",
                                (int)e.TotalHours, e.Minutes, e.Seconds, e.Milliseconds / 100)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2}",
                                e.Minutes, e.Seconds, e.Milliseconds / 100);
            watchLabel.Foreground = Ink(watch.IsRunning ? 1 : 0.8);
            watchButton.Text = watch.IsRunning ? "Pause" : (e == TimeSpan.Zero ? "Start" : "Resume");

            TimeSpan left = timerRunning ? timerEnds - DateTime.Now : timerSet;
            if (timerRunning && left <= TimeSpan.Zero)
            {
                timerRunning = false;
                timerSet = TimeSpan.Zero;
                left = TimeSpan.Zero;
                timerFlash = 16;
                try { System.Media.SystemSounds.Exclamation.Play(); } catch (Exception) { }
                Host.Log("timer finished");
                PopOpen();
            }
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            timerLabel.Text = left.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                                (int)left.TotalHours, left.Minutes, left.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}",
                                (int)left.TotalMinutes, left.Seconds);
            timerButton.Text = timerRunning ? "Pause" : "Start";
            if (alarmOn && DateTime.Now >= alarmNext)
            {
                bool repeats = false;
                foreach (bool d in alarmDays) repeats |= d;
                alarmFlash = 24;
                try { System.Media.SystemSounds.Exclamation.Play(); } catch (Exception) { }
                if (repeats)
                {
                    alarmNext = DateTime.Today.Add(alarmAt).AddDays(1);
                    alarmNext = NextRingFrom(alarmNext);
                }
                else ArmAlarm(false);
                Host.Log("alarm rang");
                PopOpen();
                StyleAlarm();
            }
            if (alarmOn && ticks % 25 == 0) StyleAlarm();   // refresh "rings in ..."
            ticks++;

            // Each readout flashes for its own event - a ringing alarm must not
            // light up the timer.
            if (timerFlash > 0)
            {
                timerFlash--;
                timerLabel.Foreground = (timerFlash / 3) % 2 == 0 ? Alert() : Ink(0.25);
            }
            else timerLabel.Foreground = Ink(timerRunning ? 1 : 0.8);
            if (alarmFlash > 0)
            {
                alarmFlash--;
                alarmBox.Foreground = (alarmFlash / 3) % 2 == 0 ? Alert() : Ink(0.25);
            }
        }

        private static FrameworkElement lastAnchor;
    }
}
