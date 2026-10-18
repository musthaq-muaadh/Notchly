using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Notchly
{
    enum AppTab { Home, Apps, Calendar, System }

    public partial class MainWindow : Window
    {
        const double BaseW = 440, BaseH = 180, CollapsedH = 34;
        const int HotkeyId = 0x4E4E;

        readonly AppSettings _settings = AppSettings.Load();
        readonly MediaService _media = new();
        readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
        readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        readonly CalendarService _calendar;
        readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(30) };
        readonly HashSet<string> _reminded = new();
        DateTime _calSelected = DateTime.Today;
        SolidColorBrush _accentBrush = new(Color.FromRgb(0x8B, 0x7C, 0xFF));

        static readonly SolidColorBrush HeaderBrush = new(Color.FromRgb(0x77, 0x77, 0x77));
        static readonly SolidColorBrush DayBrush = new(Color.FromRgb(0xDD, 0xDD, 0xDD));
        static readonly SolidColorBrush SelectedBrush = new(Color.FromRgb(0x3A, 0x3A, 0x3A));
        static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x8A, 0x8A, 0x8A));

        readonly DispatcherTimer _hoverTimer = new();
        SettingsWindow? _settingsWindow;
        IntPtr _hwnd;
        bool _hotkeyRegistered;
        readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        readonly WinForms.NotifyIcon _tray;
        readonly BitmapImage _logo = new(new Uri("pack://application:,,,/Assets/logo.png"));

        static readonly SolidColorBrush BatNormal = new(Color.FromRgb(0xCF, 0xCF, 0xCF));
        static readonly SolidColorBrush BatCharging = new(Color.FromRgb(0x34, 0xC7, 0x59));
        static readonly SolidColorBrush BatLow = new(Color.FromRgb(0xFF, 0x45, 0x3A));

        static readonly string[] Hellos = { "Hello", "Hola", "Bonjour", "こんにちは", "नमस्ते" };
        bool _welcomeActive;
        int _welcomeStep;
        DispatcherTimer? _welcomeTimer;

        bool _expanded, _pinned, _draggingOut;
        Point _dragStart;
        AppTab _tab = AppTab.Home;
        DateTime _calMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

        public ObservableCollection<ShelfItem> Shelf { get; } = new();
        public ObservableCollection<ShelfItem> Apps { get; } = new();

        double CollapsedW => _media.IsPlaying ? 330 : 270;

        double Scale => _settings.SizePreset switch { 0 => 0.88, 2 => 1.15, _ => 1.0 };
        double ExpandedW => BaseW * Scale;
        double ExpandedH => BaseH * Scale;
        double ExpandedRadius => _settings.Roundness switch { 0 => 24, 2 => 44, _ => 34 };
        double CollapsedRadius => _settings.Roundness switch { 0 => 14, 2 => 26, _ => 20 };
        TimeSpan Dur(double ms) => TimeSpan.FromMilliseconds(ms * _settings.AnimSpeed);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _calendar = new CalendarService(_settings);
            _calendar.Updated += () => Dispatcher.BeginInvoke(() =>
            {
                if (_tab == AppTab.Calendar) BuildCalendar();
                UpdateCompact();
            });
            _calendarTimer.Tick += (_, _) => _ = _calendar.RefreshAsync();
            _calendarTimer.Start();

            // restore saved shelf + pinned apps
            foreach (var p in _settings.Shelf.Where(Exists)) Shelf.Add(ShelfItem.FromPath(p, false));
            foreach (var p in _settings.Apps.Where(Exists)) Apps.Add(ShelfItem.FromPath(p, true));
            RefreshHints();
            Shelf.CollectionChanged += (_, _) => { RefreshHints(); SaveLists(); };
            Apps.CollectionChanged += (_, _) => { RefreshHints(); SaveLists(); };

            PositionWindow();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(PositionWindow);

            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (!Notch.IsMouseOver && !_draggingOut && !_pinned) Collapse();
            };

            _tickTimer.Tick += (_, _) => OnTick();
            _tickTimer.Start();

            _fullscreenTimer.Tick += (_, _) =>
            {
                bool hide = _settings.HideInFullscreen && SystemStats.FullscreenAppActive();
                Visibility = hide ? Visibility.Hidden : Visibility.Visible;
            };
            _fullscreenTimer.Start();

            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (Notch.IsMouseOver) Expand();
            };

            _clockTimer.Tick += (_, _) => { UpdateCompact(); CheckReminder(); };
            _clockTimer.Start();

            _media.Changed += () => Dispatcher.BeginInvoke(UpdateMediaUi);
            Loaded += async (_, _) =>
            {
                await _media.InitAsync();
                _ = _calendar.RefreshAsync();
                UpdateCompact();
                if (_settings.ShowWelcome)
                {
                    bool atStartup = Environment.GetCommandLineArgs().Contains("--startup");
                    await Task.Delay(atStartup ? 3000 : 700); // give the desktop a moment when Windows boots
                    ShowWelcome(atStartup);
                }
            };

            SelectTab(AppTab.Home);
            _tray = BuildTray();
            ApplySettings();
        }

        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        void RefreshHints()
        {
            EmptyHint.Visibility = Shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AppsHint.Visibility = Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void SaveLists()
        {
            _settings.Shelf = Shelf.Select(s => s.Path).ToList();
            _settings.Apps = Apps.Select(s => s.Path).ToList();
            _settings.Save();
        }

        // ---------- window setup ----------

        void PositionWindow()
        {
            var screens = WinForms.Screen.AllScreens;
            var screen = _settings.ScreenIndex >= 0 && _settings.ScreenIndex < screens.Length
                ? screens[_settings.ScreenIndex]
                : (WinForms.Screen.PrimaryScreen ?? screens[0]);

            var dpi = VisualTreeHelper.GetDpi(this);
            var b = screen.Bounds;
            Left = b.Left / dpi.DpiScaleX + (b.Width / dpi.DpiScaleX - Width) / 2;
            Top = b.Top / dpi.DpiScaleY;
        }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;

            // Tool window (hidden from Alt+Tab) + no-activate (clicking never steals focus)
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            // Global hotkey: Ctrl+Alt+N pins / unpins the notch open
            _hwnd = h;
            ApplyHotkey();
            HwndSource.FromHwnd(h)?.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                TogglePin();
                handled = true;
            }
            return IntPtr.Zero;
        }

        void TogglePin()
        {
            _pinned = !_pinned;
            if (_pinned) Expand();
            else if (!Notch.IsMouseOver) Collapse();
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            base.OnClosed(e);
        }

        // ---------- tray ----------

        WinForms.NotifyIcon BuildTray()
        {
            var tray = new WinForms.NotifyIcon
            {
                Icon = LoadLogoIcon(),
                Text = "Notchly (Ctrl+Alt+N)",
                Visible = true
            };
            var menu = new WinForms.ContextMenuStrip();

            menu.Items.Add("Settings…", null, (_, _) => Dispatcher.Invoke(OpenSettings));
            menu.Items.Add("Say hello", null, (_, _) => Dispatcher.Invoke(() => ShowWelcome(false)));
            menu.Items.Add("Clear shelf", null, (_, _) => Dispatcher.Invoke(() => Shelf.Clear()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) =>
            {
                tray.Visible = false;
                tray.Dispose();
                Application.Current.Shutdown();
            });

            tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
            tray.ContextMenuStrip = menu;
            return tray;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static bool IsStartupEnabled()
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Notchly") != null;
        }
        internal static void SetStartup(bool on)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on) k.SetValue("Notchly", "\"" + Environment.ProcessPath + "\" --startup");
            else k.DeleteValue("Notchly", false);
        }

        // ---------- expand / collapse ----------

        void Expand()
        {
            _collapseTimer.Stop();
            if (_welcomeActive) CancelWelcome();
            if (_expanded) return;
            _expanded = true;
            ApplyDefaultTab();
            Animate(ExpandedW, ExpandedH, ExpandedRadius, true);
            OnTick();
        }

        void Collapse()
        {
            if (!_expanded) return;
            _expanded = false;
            Animate(CollapsedW, CollapsedH, CollapsedRadius, false);
        }

        void Animate(double w, double h, double radius, bool expanding)
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = expanding ? 0.25 : 0.1 };
            var dur = Dur(expanding ? 420 : 300);

            Notch.CornerRadius = new CornerRadius(0, 0, radius, radius);
            Notch.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
            Notch.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });

            ExpandedView.IsHitTestVisible = expanding;
            ExpandedView.BeginAnimation(OpacityProperty, new DoubleAnimation(expanding ? 1 : 0, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(expanding ? 140 : 0)
            });
            CompactView.BeginAnimation(OpacityProperty, new DoubleAnimation(expanding ? 0 : 1, TimeSpan.FromMilliseconds(180))
            {
                BeginTime = TimeSpan.FromMilliseconds(expanding ? 0 : 160)
            });
        }

        void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_settings.OpenMode == 1) { _collapseTimer.Stop(); return; }   // click mode
            if (_expanded || _settings.HoverDelayMs <= 0) { Expand(); return; }
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverDelayMs);
            _hoverTimer.Start();
        }

        void Notch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_settings.OpenMode == 1 && !_expanded) Expand();
        }

        void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (!_draggingOut) _collapseTimer.Start();
        }

        // ---------- tabs ----------

        void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } && Enum.TryParse<AppTab>(tag, out var t)) SelectTab(t);
        }

        void SelectTab(AppTab t)
        {
            _tab = t;
            HomePanel.Visibility = t == AppTab.Home ? Visibility.Visible : Visibility.Collapsed;
            AppsPanel.Visibility = t == AppTab.Apps ? Visibility.Visible : Visibility.Collapsed;
            CalendarPanel.Visibility = t == AppTab.Calendar ? Visibility.Visible : Visibility.Collapsed;
            SystemPanel.Visibility = t == AppTab.System ? Visibility.Visible : Visibility.Collapsed;

            void Style(Button b, AppTab me)
            {
                bool on = me == t;
                b.Background = on ? new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)) : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                b.Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
            }
            Style(TabHome, AppTab.Home);
            Style(TabApps, AppTab.Apps);
            Style(TabCalendar, AppTab.Calendar);
            Style(TabSystem, AppTab.System);

            if (t == AppTab.Calendar) { _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); _calSelected = DateTime.Today; BuildCalendar(); }
            if (t == AppTab.System) UpdateStats();
        }

        // ---------- drop in (shelf or apps depending on tab) ----------

        void Notch_DragEnter(object sender, DragEventArgs e)
        {
            Expand();
            if (_tab != AppTab.Apps && _tab != AppTab.Home) SelectTab(AppTab.Home);
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Notch_DragOver(object sender, DragEventArgs e)
        {
            _collapseTimer.Stop();
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Notch_DragLeave(object sender, DragEventArgs e)
        {
            if (!_draggingOut) _collapseTimer.Start();
        }

        void Notch_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                bool toApps = _tab == AppTab.Apps;
                var target = toApps ? Apps : Shelf;
                foreach (var p in paths)
                {
                    if (target.Any(s => s.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
                    target.Add(ShelfItem.FromPath(p, toApps));
                }
            }
            e.Handled = true;
        }

        // ---------- shelf: drag out / open / remove ----------

        static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

        void ShelfItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _dragStart = e.GetPosition(null);

        void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && ItemOf(sender) is { } item) OpenPath(item.Path);
        }

        void ShelfItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || ItemOf(sender) is not { } item) return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _draggingOut = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            finally
            {
                _draggingOut = false;
                _collapseTimer.Start();
            }
        }

        void ShelfItem_Open(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) OpenPath(item.Path);
        }

        void ShelfItem_Remove(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) Shelf.Remove(item);
        }

        // ---------- apps launcher ----------

        void App_Click(object sender, MouseButtonEventArgs e)
        {
            if (ItemOf(sender) is { } item) OpenPath(item.Path);
        }

        void App_Remove(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) Apps.Remove(item);
        }

        static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        // ---------- calendar ----------

        void PrevMonth_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
        void NextMonth_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }

        void BuildCalendar()
        {
            var ci = CultureInfo.CurrentCulture;
            var today = DateTime.Today;

            MonthText.Text = _calMonth.ToString("MMMM yyyy", ci);
            CalGrid.Children.Clear();

            var first = ci.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)first + i) % 7);
                CalGrid.Children.Add(MakeCell(ci.DateTimeFormat.GetShortestDayName(day), false, true, false, false, null));
            }

            var eventDays = _calendar.DaysWithEvents(_calMonth.AddDays(-7), _calMonth.AddMonths(1).AddDays(7), _settings.ShowAllDayEvents);

            int offset = ((int)_calMonth.DayOfWeek - (int)first + 7) % 7;
            int days = DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month);
            for (int cell = 0; cell < 42; cell++)
            {
                int d = cell - offset + 1;
                if (d < 1 || d > days) { CalGrid.Children.Add(new Border { Width = 19, Height = 15 }); continue; }
                var date = new DateTime(_calMonth.Year, _calMonth.Month, d);
                CalGrid.Children.Add(MakeCell(d.ToString(), date == today, false, eventDays.Contains(date), date == _calSelected.Date, date));
            }

            BuildAgenda();
        }

        UIElement MakeCell(string text, bool isToday, bool header, bool hasEvents, bool selected, DateTime? date)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                FontWeight = hasEvents && !isToday ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? Brushes.Black : header ? HeaderBrush : hasEvents ? _accentBrush : DayBrush
            };
            var b = new Border
            {
                Width = 19,
                Height = 15,
                CornerRadius = new CornerRadius(7.5),
                Background = isToday ? Brushes.White : selected ? SelectedBrush : Brushes.Transparent,
                Child = tb
            };
            if (date is { } picked)
            {
                b.Cursor = Cursors.Hand;
                b.MouseLeftButtonUp += (_, _) => { _calSelected = picked; BuildCalendar(); };
            }
            return b;
        }

        TextBlock Hint(string text) => new()
        {
            Text = text,
            Foreground = MutedBrush,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        void BuildAgenda()
        {
            var today = DateTime.Today;
            var day = _calSelected.Date;
            var ci = CultureInfo.CurrentCulture;

            AgendaHeader.Text = day == today ? "Today · " + day.ToString("ddd d MMM", ci)
                : day == today.AddDays(1) ? "Tomorrow · " + day.ToString("ddd d MMM", ci)
                : day.ToString("dddd d MMM", ci);

            AgendaList.Children.Clear();

            if (!_calendar.HasCalendars)
            {
                AgendaList.Children.Add(Hint("No calendar connected yet."));
                var link = new TextBlock
                {
                    Text = "Add your calendar in Settings",
                    Foreground = _accentBrush,
                    FontSize = 11.5,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                link.MouseLeftButtonUp += (_, _) => OpenSettings("Calendar");
                AgendaList.Children.Add(link);
                return;
            }

            if (!_calendar.Loaded || _calendar.Status.StartsWith("Could not"))
            {
                AgendaList.Children.Add(Hint(_calendar.Status.StartsWith("Could not") ? _calendar.Status : "Loading your events…"));
                return;
            }

            var events = _calendar.OnDay(day, _settings.ShowAllDayEvents);
            if (events.Count == 0)
            {
                AgendaList.Children.Add(Hint(day == today ? "Nothing on today." : "No events."));
                return;
            }

            var now = DateTime.Now;
            foreach (var ev in events)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = _accentBrush, Margin = new Thickness(0, 1, 7, 1) };
                string time = ev.AllDay ? "All day" : ev.Start.Date < day ? "…" : FormatTime(ev.Start);
                var timeText = new TextBlock { Text = time, Foreground = MutedBrush, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { Text = ev.Title, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

                Grid.SetColumn(timeText, 1);
                Grid.SetColumn(title, 2);
                row.Children.Add(bar);
                row.Children.Add(timeText);
                row.Children.Add(title);

                row.ToolTip = ev.Title + (ev.Location.Length > 0 ? "\n" + ev.Location : "")
                              + (ev.AllDay ? "" : "\n" + FormatTime(ev.Start) + " – " + FormatTime(ev.End));
                if (!ev.AllDay && day == today && ev.End <= now) row.Opacity = 0.45;
                AgendaList.Children.Add(row);
            }
        }

        // ---------- event reminders ----------

        void CheckReminder()
        {
            int mins = _settings.ReminderMinutes;
            if (mins <= 0 || _expanded || _welcomeActive || Visibility != Visibility.Visible) return;

            var now = DateTime.Now;
            var ev = _calendar.NextStarting(now, mins);
            if (ev == null) return;

            var key = ev.Title + "|" + ev.Start.Ticks;
            if (_reminded.Contains(key)) return;

            int left = (int)Math.Ceiling((ev.Start - now).TotalMinutes);
            string sub = (left <= 1 ? "Starting now" : $"In {left} min") + " · " + FormatTime(ev.Start)
                         + (ev.Location.Length > 0 ? " · " + ev.Location : "");

            if (ShowBanner(ev.Title, sub, 7000)) _reminded.Add(key);
        }

        // ---------- system tab ----------

        void UpdateStats()
        {
            var cpu = SystemStats.CpuPercent();
            CpuBar.Value = cpu; CpuText.Text = $"{cpu:0}%";

            var ram = SystemStats.MemoryPercent();
            RamBar.Value = ram; RamText.Text = $"{ram:0}%";

            var ps = WinForms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery))
            {
                BatBar.Value = 100; BatText.Text = "AC";
            }
            else
            {
                double pct = ps.BatteryLifePercent <= 1 ? ps.BatteryLifePercent * 100 : 0;
                BatBar.Value = pct;
                BatText.Text = $"{pct:0}%" + (ps.PowerLineStatus == WinForms.PowerLineStatus.Online ? " ⚡" : "");
            }
        }

        void VolDown_Click(object sender, RoutedEventArgs e) => SystemStats.VolumeDown();
        void VolUp_Click(object sender, RoutedEventArgs e) => SystemStats.VolumeUp();
        void Mute_Click(object sender, RoutedEventArgs e) => SystemStats.VolumeMute();
        void Snip_Click(object sender, RoutedEventArgs e) => SystemStats.Snip();
        void Lock_Click(object sender, RoutedEventArgs e) => SystemStats.Lock();

        // ---------- media ----------

        void OnTick()
        {
            if (!_expanded) return;
            if (_tab == AppTab.Home) UpdateProgress();
            else if (_tab == AppTab.System) UpdateStats();
        }

        void UpdateMediaUi()
        {
            var has = _media.HasSession;
            TitleText.Text = has ? _media.Title : "Nothing playing";
            ArtistText.Text = has ? _media.Artist : "";
            ArtImage.Source = _media.Art;
            CompactArtBrush.ImageSource = (ImageSource?)_media.Art ?? _logo;
            PlayBtn.Content = _media.IsPlaying ? "\uE769" : "\uE768";
            SetBars(_media.IsPlaying);
            Bars.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;

            if (!_expanded && !_welcomeActive)
                Notch.BeginAnimation(WidthProperty, new DoubleAnimation(CollapsedW, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            UpdateProgress();
            UpdateCompact();
        }

        void SetBars(bool on)
        {
            var bars = new[] { Bar1, Bar2, Bar3 };
            var ms = new[] { 420, 560, 480 };
            for (int i = 0; i < bars.Length; i++)
            {
                if (on)
                    bars[i].BeginAnimation(HeightProperty, new DoubleAnimation(4, 14, TimeSpan.FromMilliseconds(ms[i]))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                else
                {
                    bars[i].BeginAnimation(HeightProperty, null);
                    bars[i].Height = 4;
                }
            }
        }

        void UpdateProgress()
        {
            var dur = _media.Duration;
            var pos = _media.EstimatedPosition;
            Progress.Value = dur.TotalSeconds > 0 ? Math.Min(1, pos.TotalSeconds / dur.TotalSeconds) : 0;
            TimeText.Text = dur.TotalSeconds > 0 ? $"{Fmt(pos)} / {Fmt(dur)}" : "";
        }

        static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";


        // ---------- collapsed bar info ----------

        string FormatTime(DateTime t)
        {
            var ci = CultureInfo.CurrentCulture;
            return _settings.ClockFormat switch
            {
                1 => t.ToString("h:mm tt", ci),
                2 => t.ToString("HH:mm", ci),
                _ => t.ToString("t", ci)
            };
        }

        static string RelativeTime(CalendarEvent ev, DateTime now)
        {
            if (ev.Start <= now) return "now";
            var d = ev.Start - now;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalMinutes < 60) return $"in {(int)Math.Ceiling(d.TotalMinutes)} min";
            return $"in {(int)d.TotalHours}h {d.Minutes}m";
        }

        void UpdateCompact()
        {
            var now = DateTime.Now;
            var ci = CultureInfo.CurrentCulture;

            if (_media.IsPlaying && _media.HasSession)
            {
                CompactText.Text = !_settings.ShowCompactText ? ""
                    : string.IsNullOrEmpty(_media.Artist) ? _media.Title : $"{_media.Title} · {_media.Artist}";
            }
            else
            {
                var next = _settings.NextEventMinutes > 0 ? _calendar.NextTimed(now, _settings.NextEventMinutes) : null;
                if (next != null)
                    CompactText.Text = $"{next.Title} · {RelativeTime(next, now)}";
                else
                    CompactText.Text = _settings.ShowCompactClock
                        ? $"{FormatTime(now)}  ·  {now.ToString("ddd d MMM", ci)}"
                        : "";
            }

            if (CompactArtBrush.ImageSource == null) CompactArtBrush.ImageSource = _logo;
            CompactArt.Visibility = _settings.ShowCompactArt ? Visibility.Visible : Visibility.Collapsed;

            var ps = WinForms.SystemInformation.PowerStatus;
            if (!_settings.ShowCompactBattery || ps.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery))
            {
                BatteryCompact.Text = "";
            }
            else
            {
                int pct = ps.BatteryLifePercent <= 1 ? (int)Math.Round(ps.BatteryLifePercent * 100) : 0;
                bool charging = ps.PowerLineStatus == WinForms.PowerLineStatus.Online;
                BatteryCompact.Text = $"{pct}%" + (charging ? " ⚡" : "");
                BatteryCompact.Foreground = charging ? BatCharging : pct <= 20 ? BatLow : BatNormal;
            }
        }

        // ---------- settings ----------

        void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

        void OpenSettings(string? page = null)
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_settings, ApplySettings, () => Dispatcher.BeginInvoke(() => ShowWelcome(false)), _calendar);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            if (page != null) _settingsWindow.ShowPage(page);
            _settingsWindow.Activate();
        }

        bool TabAllowed(AppTab t) => t switch
        {
            AppTab.Apps => _settings.ShowAppsTab,
            AppTab.Calendar => _settings.ShowCalendarTab,
            AppTab.System => _settings.ShowSystemTab,
            _ => true
        };

        void ApplyDefaultTab()
        {
            if (_settings.DefaultTab != "Last" && Enum.TryParse<AppTab>(_settings.DefaultTab, out var t) && TabAllowed(t))
                SelectTab(t);
        }

        void ApplyHotkey()
        {
            if (_hwnd == IntPtr.Zero) return;
            const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
            if (_settings.HotkeyEnabled && !_hotkeyRegistered)
                _hotkeyRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4E);
            else if (!_settings.HotkeyEnabled && _hotkeyRegistered)
            {
                UnregisterHotKey(_hwnd, HotkeyId);
                _hotkeyRegistered = false;
                if (_pinned) { _pinned = false; if (!Notch.IsMouseOver) Collapse(); }
            }
        }

        Color AccentColor()
        {
            try { return (Color)ColorConverter.ConvertFromString(_settings.Accent); }
            catch { return Color.FromRgb(0x8B, 0x7C, 0xFF); }
        }

        /// <summary>Applies every option live (called on startup and whenever a setting changes).</summary>
        void ApplySettings()
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, _settings.CollapseDelayMs));

            // size
            ExpandedView.LayoutTransform = new ScaleTransform(Scale, Scale);

            // accent
            var accent = new SolidColorBrush(AccentColor());
            accent.Freeze();
            _accentBrush = accent;
            AgendaHeader.Foreground = accent;
            Progress.Foreground = accent;
            CpuBar.Foreground = accent;
            RamBar.Foreground = accent;

            // tabs
            TabApps.Visibility = _settings.ShowAppsTab ? Visibility.Visible : Visibility.Collapsed;
            TabCalendar.Visibility = _settings.ShowCalendarTab ? Visibility.Visible : Visibility.Collapsed;
            TabSystem.Visibility = _settings.ShowSystemTab ? Visibility.Visible : Visibility.Collapsed;
            if (!TabAllowed(_tab)) SelectTab(AppTab.Home);

            _calendarTimer.Interval = TimeSpan.FromMinutes(Math.Max(5, _settings.RefreshMinutes));
            if (_tab == AppTab.Calendar) BuildCalendar();

            ApplyHotkey();
            PositionWindow();

            // resize / reshape the notch right now
            if (!_welcomeActive)
            {
                double w = _expanded ? ExpandedW : CollapsedW;
                double h = _expanded ? ExpandedH : CollapsedH;
                Notch.CornerRadius = new CornerRadius(0, 0, _expanded ? ExpandedRadius : CollapsedRadius, _expanded ? ExpandedRadius : CollapsedRadius);
                Notch.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(250)));
                Notch.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(250)));
            }

            UpdateCompact();
        }

        // ---------- welcome animation ----------

        static System.Drawing.Icon LoadLogoIcon()
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.ico"));
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream);
            }
            catch { return System.Drawing.SystemIcons.Application; }
        }

        static string GreetingText()
        {
            int h = DateTime.Now.Hour;
            string g = h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
            var name = (Environment.UserName ?? "").Split(' ', '.', '_')[0];
            if (name.Length > 0) name = char.ToUpper(name[0]) + name.Substring(1);
            return name.Length > 0 ? $"{g}, {name}" : g;
        }

        bool OpenBanner(string title, string sub)
        {
            if (_expanded || _welcomeActive) return false;
            _welcomeActive = true;
            _welcomeStep = 0;

            WelcomeTitle.Text = title;
            WelcomeTitle.Opacity = 1;
            WelcomeSub.Text = sub;
            WelcomeView.Visibility = Visibility.Visible;
            WelcomeView.Opacity = 0;

            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            var dur = TimeSpan.FromMilliseconds(420);
            Notch.CornerRadius = new CornerRadius(0, 0, 30, 30);
            Notch.BeginAnimation(WidthProperty, new DoubleAnimation(360, dur) { EasingFunction = ease });
            Notch.BeginAnimation(HeightProperty, new DoubleAnimation(88, dur) { EasingFunction = ease });

            CompactView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
            WelcomeView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)) { BeginTime = TimeSpan.FromMilliseconds(150) });

            var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
            var popDur = TimeSpan.FromMilliseconds(520);
            LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, popDur) { EasingFunction = pop });
            LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, popDur) { EasingFunction = pop });

            return true;
        }

        void ShowWelcome(bool atStartup)
        {
            if (!OpenBanner(Hellos[0], "")) return;
            _welcomeTimer?.Stop();
            _welcomeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            _welcomeTimer.Tick += (_, _) => WelcomeTick(atStartup);
            _welcomeTimer.Start();
        }

        /// <summary>Opens the notch briefly with a message (used for event reminders).</summary>
        bool ShowBanner(string title, string sub, int holdMs)
        {
            if (!OpenBanner(title, sub)) return false;
            _welcomeTimer?.Stop();
            _welcomeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
            _welcomeTimer.Tick += (_, _) => EndWelcome();
            _welcomeTimer.Start();
            return true;
        }

        void WelcomeTick(bool atStartup)
        {
            _welcomeStep++;
            if (_welcomeStep < Hellos.Length)
            {
                SwapWelcomeText(Hellos[_welcomeStep], "");
            }
            else if (_welcomeStep == Hellos.Length)
            {
                SwapWelcomeText(GreetingText(), atStartup ? "Welcome back - Notchly is ready" : "Notchly is ready");
                if (_welcomeTimer != null) _welcomeTimer.Interval = TimeSpan.FromMilliseconds(1900);
            }
            else
            {
                EndWelcome();
            }
        }

        void SwapWelcomeText(string title, string sub)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
            fadeOut.Completed += (_, _) =>
            {
                WelcomeTitle.Text = title;
                WelcomeSub.Text = sub;
                WelcomeTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            };
            WelcomeTitle.BeginAnimation(OpacityProperty, fadeOut);
        }

        void EndWelcome()
        {
            _welcomeTimer?.Stop();
            _welcomeTimer = null;
            if (!_welcomeActive) return;
            _welcomeActive = false;

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => { if (!_welcomeActive) WelcomeView.Visibility = Visibility.Collapsed; };
            WelcomeView.BeginAnimation(OpacityProperty, fade);

            if (!_expanded)
            {
                var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.1 };
                var dur = TimeSpan.FromMilliseconds(320);
                Notch.CornerRadius = new CornerRadius(0, 0, CollapsedRadius, CollapsedRadius);
                Notch.BeginAnimation(WidthProperty, new DoubleAnimation(CollapsedW, dur) { EasingFunction = ease });
                Notch.BeginAnimation(HeightProperty, new DoubleAnimation(CollapsedH, dur) { EasingFunction = ease });
                CompactView.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(150) });
            }
        }

        void CancelWelcome()
        {
            _welcomeTimer?.Stop();
            _welcomeTimer = null;
            _welcomeActive = false;
            WelcomeView.BeginAnimation(OpacityProperty, null);
            WelcomeView.Opacity = 0;
            WelcomeView.Visibility = Visibility.Collapsed;
        }

        async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
        async void Play_Click(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();
        async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    }
}
