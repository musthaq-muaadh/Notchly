using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace Notchly
{
    public partial class SettingsWindow : Window
    {
        static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x98));

        readonly AppSettings _s;
        readonly Action _apply;
        readonly Action _replayWelcome;
        readonly CalendarService _cal;
        StackPanel? _sourceHost;
        TextBlock? _statusText;
        readonly Dictionary<string, StackPanel> _pages = new();
        int _groupCounter;
        string _current = "General";

        public SettingsWindow(AppSettings settings, Action apply, Action replayWelcome, CalendarService calendar)
        {
            InitializeComponent();
            _s = settings;
            _apply = apply;
            _replayWelcome = replayWelcome;
            _cal = calendar;
            _cal.Updated += OnCalendarUpdated;
            Closed += (_, _) => _cal.Updated -= OnCalendarUpdated;
            VersionText.Text = "Version " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.0");
            BuildPages();
        }

        // ---------- pages ----------

        void BuildPages()
        {
            Nav.Children.Clear();
            PagesHost.Children.Clear();
            _pages.Clear();

            BuildGeneral();
            BuildAppearance();
            BuildBehavior();
            BuildCalendarPage();
            BuildCollapsed();
            BuildTabs();
            BuildAbout();

            if (!_pages.ContainsKey(_current)) _current = "General";
            Select(_current);
        }

        public void ShowPage(string name)
        {
            foreach (var child in Nav.Children)
                if (child is RadioButton rb && (rb.Content as string) == name) { rb.IsChecked = true; return; }
        }

        StackPanel NewPage(string name)
        {
            var page = new StackPanel { Visibility = Visibility.Collapsed };
            _pages[name] = page;
            PagesHost.Children.Add(page);

            var rb = new RadioButton
            {
                Content = name,
                GroupName = "nav",
                Style = (Style)FindResource("NavBtn"),
                IsChecked = name == _current
            };
            rb.Checked += (_, _) => Select(name);
            Nav.Children.Add(rb);
            return page;
        }

        void Select(string name)
        {
            _current = name;
            foreach (var kv in _pages)
                kv.Value.Visibility = kv.Key == name ? Visibility.Visible : Visibility.Collapsed;
            PageTitle.Text = name;
            Scroller.ScrollToTop();
        }

        void BuildGeneral()
        {
            var p = NewPage("General");

            Toggle(p, "Launch at startup", "Start Notchly automatically when you sign in to Windows.",
                MainWindow.IsStartupEnabled, MainWindow.SetStartup, saveSettings: false);

            Toggle(p, "Welcome animation", "Show the animated hello when Notchly starts.",
                () => _s.ShowWelcome, v => _s.ShowWelcome = v);

            var replay = new Button { Content = "Replay", Style = (Style)FindResource("ActionBtn") };
            replay.Click += (_, _) => _replayWelcome();
            Row(p, "Preview welcome", "Play the hello animation now.", replay, false);

            Toggle(p, "Hide in fullscreen apps", "Tuck the notch away while a game or fullscreen app is in front.",
                () => _s.HideInFullscreen, v => _s.HideInFullscreen = v);

            Toggle(p, "Global hotkey (Ctrl+Alt+N)", "Pin the notch open or closed from anywhere.",
                () => _s.HotkeyEnabled, v => _s.HotkeyEnabled = v);

            var screens = WinForms.Screen.AllScreens;
            var opts = new List<(string, int)> { ("Primary display", -1) };
            for (int i = 0; i < screens.Length; i++)
                opts.Add(($"Display {i + 1}" + (screens[i].Primary ? " (primary)" : ""), i));
            Choice(p, "Show on display", "Choose which screen the notch lives on.", opts.ToArray(),
                () => _s.ScreenIndex, v => _s.ScreenIndex = v, below: true);
        }

        void BuildAppearance()
        {
            var p = NewPage("Appearance");

            Choice(p, "Size", "How big the expanded notch is.",
                new[] { ("Small", 0), ("Default", 1), ("Large", 2) },
                () => _s.SizePreset, v => _s.SizePreset = v);

            Choice(p, "Corner roundness", "Shape of the notch corners.",
                new[] { ("Subtle", 0), ("Normal", 1), ("Round", 2) },
                () => _s.Roundness, v => _s.Roundness = v);

            AccentRow(p);

            Choice(p, "Animation speed", "How fast the notch opens and closes.",
                new[] { ("Fast", 0.7), ("Normal", 1.0), ("Slow", 1.5) },
                () => _s.AnimSpeed, v => _s.AnimSpeed = v);

            Choice(p, "Clock format", "Used in the collapsed bar.",
                new[] { ("System", 0), ("12-hour", 1), ("24-hour", 2) },
                () => _s.ClockFormat, v => _s.ClockFormat = v);
        }

        void BuildBehavior()
        {
            var p = NewPage("Behavior");

            Choice(p, "Open the notch with", "Hover is quick; click avoids accidental opens.",
                new[] { ("Hover", 0), ("Click", 1) },
                () => _s.OpenMode, v => _s.OpenMode = v);

            Choice(p, "Hover delay", "How long to hover before it opens (hover mode).",
                new[] { ("Instant", 0), ("Short", 150), ("Normal", 350), ("Long", 600) },
                () => _s.HoverDelayMs, v => _s.HoverDelayMs = v, below: true);

            Choice(p, "Close after leaving", "How long it waits before collapsing.",
                new[] { ("Fast", 250), ("Normal", 450), ("Slow", 900) },
                () => _s.CollapseDelayMs, v => _s.CollapseDelayMs = v);

            Choice(p, "Open on tab", "Which tab shows when the notch opens.",
                new[] { ("Last used", "Last"), ("Home", "Home"), ("Apps", "Apps"), ("Calendar", "Calendar"), ("System", "System") },
                () => _s.DefaultTab, v => _s.DefaultTab = v, below: true);
        }


        // ---------- calendar page ----------

        void OnCalendarUpdated() =>
            Dispatcher.BeginInvoke(() => { if (_statusText != null) _statusText.Text = _cal.Status; });

        TextBox StyledBox()
        {
            return new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x34)),
                Padding = new Thickness(10, 7, 10, 7),
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        void BuildCalendarPage()
        {
            var p = NewPage("Calendar");

            p.Children.Add(new TextBlock
            {
                Text = "Show your real events in the notch. Paste your calendar's private iCal link (it ends in .ics), or pick an .ics file. "
                     + "Works with Google Calendar, Outlook / Microsoft 365 and iCloud.",
                Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            foreach (var line in new[]
            {
                "Google: Settings and sharing → Integrate calendar → Secret address in iCal format",
                "Outlook.com: Settings → Calendar → Shared calendars → Publish a calendar → ICS link",
                "iCloud: Calendar → Share → Public Calendar → copy the link"
            })
                p.Children.Add(new TextBlock { Text = "•  " + line, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 3) });
            p.Children.Add(new TextBlock
            {
                Text = "Treat the link like a password: anyone who has it can read that calendar.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), FontSize = 11.5, Margin = new Thickness(0, 6, 0, 14)
            });

            // add row
            var box = StyledBox();
            var add = new Button { Content = "Add", Style = (Style)FindResource("ActionBtn"), Margin = new Thickness(8, 0, 0, 0) };
            var browse = new Button { Content = "Browse .ics…", Style = (Style)FindResource("ActionBtn"), Margin = new Thickness(8, 0, 0, 0) };
            var addRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(add, 1);
            Grid.SetColumn(browse, 2);
            addRow.Children.Add(box);
            addRow.Children.Add(add);
            addRow.Children.Add(browse);
            p.Children.Add(addRow);

            add.Click += (_, _) => { if (AddSource(box.Text)) box.Clear(); };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && AddSource(box.Text)) box.Clear(); };
            browse.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Calendar files (*.ics)|*.ics|All files|*.*" };
                if (dlg.ShowDialog(this) == true) AddSource(dlg.FileName);
            };

            // connected calendars
            _sourceHost = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            p.Children.Add(_sourceHost);
            RebuildSources();

            // status + refresh
            _statusText = new TextBlock { Text = _cal.Status, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var refresh = new Button { Content = "Refresh now", Style = (Style)FindResource("ActionBtn") };
            refresh.Click += (_, _) => { _statusText.Text = "Refreshing…"; _ = _cal.RefreshAsync(); };
            var statusRow = new Grid { Margin = new Thickness(0, 0, 0, 24) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _statusText.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(refresh, 1);
            statusRow.Children.Add(_statusText);
            statusRow.Children.Add(refresh);
            p.Children.Add(statusRow);

            // options
            Choice(p, "Refresh every", "How often the calendars are re-downloaded.",
                new[] { ("5 min", 5), ("15 min", 15), ("30 min", 30), ("1 hour", 60) },
                () => _s.RefreshMinutes, v => _s.RefreshMinutes = v);

            Choice(p, "Upcoming event in the bar", "Show the next event in the collapsed bar when it starts within…",
                new[] { ("Off", 0), ("30 min", 30), ("1 hour", 60), ("3 hours", 180) },
                () => _s.NextEventMinutes, v => _s.NextEventMinutes = v);

            Choice(p, "Reminder", "Pop a reminder from the notch before an event starts.",
                new[] { ("Off", 0), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30) },
                () => _s.ReminderMinutes, v => _s.ReminderMinutes = v);

            Toggle(p, "All-day events", "Include all-day events in the Calendar tab.",
                () => _s.ShowAllDayEvents, v => _s.ShowAllDayEvents = v);
        }

        bool AddSource(string input)
        {
            var url = (input ?? "").Trim().Trim('"');
            if (url.Length == 0) return false;

            bool looksOk = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                           || url.StartsWith("webcal", StringComparison.OrdinalIgnoreCase)
                           || File.Exists(url);
            if (!looksOk)
            {
                if (_statusText != null) _statusText.Text = "That doesn't look like a calendar link or an existing .ics file.";
                return false;
            }
            if (_s.CalendarSources.Any(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                if (_statusText != null) _statusText.Text = "That calendar is already added.";
                return false;
            }

            _s.CalendarSources.Add(new CalendarSource { Name = CalendarService.DeriveName(url), Url = url });
            _s.Save();
            RebuildSources();
            if (_statusText != null) _statusText.Text = "Loading…";
            _ = _cal.RefreshAsync();
            return true;
        }

        void RebuildSources()
        {
            if (_sourceHost == null) return;
            _sourceHost.Children.Clear();

            if (_s.CalendarSources.Count == 0)
            {
                _sourceHost.Children.Add(new TextBlock { Text = "No calendars connected yet.", Foreground = Dim, FontSize = 12 });
                return;
            }

            foreach (var src in _s.CalendarSources.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = src.Enabled, VerticalAlignment = VerticalAlignment.Center };
                sw.Checked += (_, _) => { src.Enabled = true; _s.Save(); _ = _cal.RefreshAsync(); };
                sw.Unchecked += (_, _) => { src.Enabled = false; _s.Save(); _ = _cal.RefreshAsync(); };

                var name = new TextBlock
                {
                    Text = src.Name,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    Margin = new Thickness(12, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 1);

                var remove = new Button { Content = "Remove", Style = (Style)FindResource("ActionBtn") };
                remove.Click += (_, _) =>
                {
                    _s.CalendarSources.Remove(src);
                    _s.Save();
                    _cal.ForgetCache(src.Url);
                    RebuildSources();
                    _ = _cal.RefreshAsync();
                };
                Grid.SetColumn(remove, 2);

                row.Children.Add(sw);
                row.Children.Add(name);
                row.Children.Add(remove);
                _sourceHost.Children.Add(row);
            }
        }

        void BuildCollapsed()
        {
            var p = NewPage("Collapsed bar");

            Toggle(p, "Icon / album art", "Show the logo, or album art while music plays.",
                () => _s.ShowCompactArt, v => _s.ShowCompactArt = v);
            Toggle(p, "Now playing", "Show the song and artist in the middle while music plays.",
                () => _s.ShowCompactText, v => _s.ShowCompactText = v);
            Toggle(p, "Time and date", "Show the clock when nothing is playing.",
                () => _s.ShowCompactClock, v => _s.ShowCompactClock = v);
            Toggle(p, "Battery", "Show the battery percentage (laptops).",
                () => _s.ShowCompactBattery, v => _s.ShowCompactBattery = v);
        }

        void BuildTabs()
        {
            var p = NewPage("Tabs");

            Row(p, "Home", "Media player and file shelf (always on).", new TextBlock { Text = "Always on", Foreground = Dim, FontSize = 12 }, false);
            Toggle(p, "Apps", "Pinned app launcher.", () => _s.ShowAppsTab, v => _s.ShowAppsTab = v);
            Toggle(p, "Calendar", "Month view and today's date.", () => _s.ShowCalendarTab, v => _s.ShowCalendarTab = v);
            Toggle(p, "System", "CPU, RAM, battery and quick actions.", () => _s.ShowSystemTab, v => _s.ShowSystemTab = v);
        }

        void BuildAbout()
        {
            var p = NewPage("About");

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
            header.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 72, Height = 72 });
            var t = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = "Notchly", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = VersionText.Text, Foreground = Dim, FontSize = 12 });
            t.Children.Add(new TextBlock { Text = "A dynamic notch bar for Windows.", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            header.Children.Add(t);
            p.Children.Add(header);

            var open = new Button { Content = "Open folder", Style = (Style)FindResource("ActionBtn") };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppSettings.Folder + "\"") { UseShellExecute = true }); } catch { }
            };
            Row(p, "Settings file", "Where Notchly keeps your settings, shelf and pinned apps.", open, false);

            var reset = new Button { Content = "Reset", Style = (Style)FindResource("ActionBtn") };
            reset.Click += (_, _) =>
            {
                var r = MessageBox.Show(this, "Reset all options to their defaults? Your shelf and pinned apps are kept.",
                    "Notchly", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK) return;
                _s.ResetOptions();
                _s.Save();
                _apply();
                BuildPages();
            };
            Row(p, "Reset options", "Restore every option on these pages to its default.", reset, false);
        }

        // ---------- row builders ----------

        void Row(StackPanel page, string title, string? desc, FrameworkElement control, bool below)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(desc))
                text.Children.Add(new TextBlock { Text = desc, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

            if (below)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                control.Margin = new Thickness(0, 10, 0, 0);
                control.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRow(control, 1);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                text.Margin = new Thickness(0, 0, 18, 0);
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
            }

            grid.Children.Add(text);
            grid.Children.Add(control);
            page.Children.Add(grid);
        }

        void Changed(Action set, bool save = true)
        {
            set();
            if (save) _s.Save();
            _apply();
        }

        void Toggle(StackPanel page, string title, string? desc, Func<bool> get, Action<bool> set, bool saveSettings = true)
        {
            var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
            cb.Checked += (_, _) => Changed(() => set(true), saveSettings);
            cb.Unchecked += (_, _) => Changed(() => set(false), saveSettings);
            Row(page, title, desc, cb, false);
        }

        void Choice<T>(StackPanel page, string title, string? desc, (string label, T value)[] options,
                       Func<T> get, Action<T> set, bool below = false)
        {
            var group = "g" + _groupCounter++;
            var panel = new WrapPanel();
            var current = get();
            foreach (var (label, value) in options)
            {
                var v = value;
                var rb = new RadioButton
                {
                    Content = label,
                    GroupName = group,
                    Style = (Style)FindResource("SegBtn"),
                    IsChecked = EqualityComparer<T>.Default.Equals(current, v)
                };
                rb.Checked += (_, _) => Changed(() => set(v));
                panel.Children.Add(rb);
            }
            Row(page, title, desc, panel, below || options.Length >= 4);
        }

        void AccentRow(StackPanel page)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var hex in new[] { "#8B7CFF", "#0A84FF", "#34C759", "#FF9F0A", "#FF5A5F", "#FF6BD6", "#FFFFFF" })
            {
                var h = hex;
                var rb = new RadioButton
                {
                    GroupName = "accent",
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)),
                    Margin = new Thickness(0, 0, 8, 0),
                    IsChecked = string.Equals(_s.Accent, h, StringComparison.OrdinalIgnoreCase)
                };
                rb.Checked += (_, _) => Changed(() => _s.Accent = h);
                panel.Children.Add(rb);
            }
            Row(page, "Accent color", "Used for the calendar, progress and system bars.", panel, true);
        }
    }
}
