using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Notchly
{
    public sealed class AppSettings
    {
        // ---- saved lists ----
        public List<string> Shelf { get; set; } = new();
        public List<string> Apps { get; set; } = new();

        // ---- general ----
        public bool HideInFullscreen { get; set; } = true;
        public bool ShowWelcome { get; set; } = true;
        /// <summary>-1 = primary display.</summary>
        public int ScreenIndex { get; set; } = -1;
        public bool HotkeyEnabled { get; set; } = true;

        // ---- appearance ----
        /// <summary>0 small, 1 default, 2 large.</summary>
        public int SizePreset { get; set; } = 1;
        /// <summary>0 subtle, 1 normal, 2 round.</summary>
        public int Roundness { get; set; } = 1;
        public string Accent { get; set; } = "#8B7CFF";
        /// <summary>Animation duration multiplier (0.7 fast, 1 normal, 1.5 slow).</summary>
        public double AnimSpeed { get; set; } = 1.0;
        /// <summary>0 system, 1 12-hour, 2 24-hour.</summary>
        public int ClockFormat { get; set; } = 0;

        // ---- behavior ----
        /// <summary>0 = open on hover, 1 = open on click.</summary>
        public int OpenMode { get; set; } = 0;
        public int HoverDelayMs { get; set; } = 0;
        public int CollapseDelayMs { get; set; } = 450;
        /// <summary>"Last", "Home", "Apps", "Calendar" or "System".</summary>
        public string DefaultTab { get; set; } = "Last";

        // ---- collapsed bar ----
        public bool ShowCompactArt { get; set; } = true;
        public bool ShowCompactText { get; set; } = true;
        public bool ShowCompactClock { get; set; } = true;
        public bool ShowCompactBattery { get; set; } = true;

        // ---- calendar ----
        public List<CalendarSource> CalendarSources { get; set; } = new();
        /// <summary>Re-download the calendars every N minutes.</summary>
        public int RefreshMinutes { get; set; } = 15;
        /// <summary>Show an upcoming event in the collapsed bar when it starts within N minutes (0 = off).</summary>
        public int NextEventMinutes { get; set; } = 60;
        /// <summary>Pop a reminder from the notch N minutes before an event (0 = off).</summary>
        public int ReminderMinutes { get; set; } = 10;
        public bool ShowAllDayEvents { get; set; } = true;

        // ---- tabs ----
        public bool ShowAppsTab { get; set; } = true;
        public bool ShowCalendarTab { get; set; } = true;
        public bool ShowSystemTab { get; set; } = true;

        /// <summary>Back to defaults for every option (keeps the shelf, pinned apps and connected calendars).</summary>
        public void ResetOptions()
        {
            var d = new AppSettings();
            HideInFullscreen = d.HideInFullscreen; ShowWelcome = d.ShowWelcome; ScreenIndex = d.ScreenIndex;
            HotkeyEnabled = d.HotkeyEnabled; SizePreset = d.SizePreset; Roundness = d.Roundness;
            Accent = d.Accent; AnimSpeed = d.AnimSpeed; ClockFormat = d.ClockFormat;
            OpenMode = d.OpenMode; HoverDelayMs = d.HoverDelayMs; CollapseDelayMs = d.CollapseDelayMs;
            DefaultTab = d.DefaultTab; ShowCompactArt = d.ShowCompactArt; ShowCompactText = d.ShowCompactText;
            ShowCompactClock = d.ShowCompactClock; ShowCompactBattery = d.ShowCompactBattery;
            RefreshMinutes = d.RefreshMinutes; NextEventMinutes = d.NextEventMinutes;
            ReminderMinutes = d.ReminderMinutes; ShowAllDayEvents = d.ShowAllDayEvents;
            ShowAppsTab = d.ShowAppsTab; ShowCalendarTab = d.ShowCalendarTab; ShowSystemTab = d.ShowSystemTab;
        }

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notchly", "settings.json");

        public static string Folder => Path.GetDirectoryName(FilePath)!;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>One connected calendar: a private iCal link or a local .ics file.</summary>
    public sealed class CalendarSource
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }
}
