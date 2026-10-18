using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Notchly
{
    /// <summary>Downloads the user's iCal feeds, keeps an offline copy, and answers "what's on?" questions.</summary>
    public sealed class CalendarService
    {
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Notchly/1.3");
            return c;
        }

        readonly AppSettings _s;
        List<CalendarEvent> _events = new();
        bool _busy, _pending;

        public CalendarService(AppSettings settings) { _s = settings; }

        /// <summary>Raised on the UI thread after each refresh.</summary>
        public event Action? Updated;

        public DateTime? LastUpdated { get; private set; }
        public string Status { get; private set; } = "No calendar added yet.";
        public bool HasCalendars => _s.CalendarSources.Any(x => x.Enabled);
        public bool Loaded => LastUpdated != null;

        // ---------- loading ----------

        static string Normalize(string url)
        {
            url = url.Trim();
            if (url.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) return "https://" + url.Substring(10);
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) return "https://" + url.Substring(9);
            return url;
        }

        /// <summary>A friendly default name for a new calendar.</summary>
        public static string DeriveName(string url)
        {
            url = url.Trim();
            try
            {
                if (File.Exists(url)) return Path.GetFileNameWithoutExtension(url);
                if (Uri.TryCreate(Normalize(url), UriKind.Absolute, out var u))
                {
                    var h = u.Host.ToLowerInvariant();
                    if (h.Contains("google")) return "Google Calendar";
                    if (h.Contains("icloud")) return "iCloud Calendar";
                    if (h.Contains("outlook") || h.Contains("office") || h.Contains("live.com")) return "Outlook Calendar";
                    return h.StartsWith("www.") ? h.Substring(4) : h;
                }
            }
            catch { }
            return "Calendar";
        }

        /// <summary>Deletes the saved offline copy of a calendar that was removed.</summary>
        public void ForgetCache(string url)
        {
            try { var cp = CachePath(url); if (File.Exists(cp)) File.Delete(cp); } catch { }
        }

        static string CachePath(string url)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).Substring(0, 12);
            return Path.Combine(AppSettings.Folder, $"calendar-{hash}.ics");
        }

        async Task<string?> LoadAsync(string url, List<string> errors)
        {
            try
            {
                // a local .ics file
                if (File.Exists(url.Trim()))
                    return await File.ReadAllTextAsync(url.Trim());

                var text = await Http.GetStringAsync(Normalize(url));
                if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("That link is not an iCal (.ics) calendar.");
                try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(CachePath(url), text); } catch { }
                return text;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                var cp = CachePath(url);
                try { if (File.Exists(cp)) return File.ReadAllText(cp); } catch { }
                return null;
            }
        }

        public async Task RefreshAsync()
        {
            if (_busy) { _pending = true; return; }
            _busy = true;
            try
            {
                do
                {
                    _pending = false;
                    await RefreshOnceAsync();
                } while (_pending);
            }
            finally { _busy = false; }
        }

        async Task RefreshOnceAsync()
        {
            var urls = _s.CalendarSources.Where(x => x.Enabled).Select(x => x.Url).ToList();
            if (urls.Count == 0)
            {
                _events = new List<CalendarEvent>();
                LastUpdated = null;
                Status = "No calendar added yet.";
                Updated?.Invoke();
                return;
            }

            var from = DateTime.Today.AddDays(-45);
            var to = DateTime.Today.AddDays(400);
            var all = new List<CalendarEvent>();
            var errors = new List<string>();
            int ok = 0;

            foreach (var url in urls)
            {
                var text = await LoadAsync(url, errors);
                if (text == null) continue;
                try
                {
                    var parsed = await Task.Run(() => IcsParser.Parse(text, from, to));
                    all.AddRange(parsed);
                    ok++;
                }
                catch (Exception ex) { errors.Add("Could not read calendar: " + ex.Message); }
            }

            _events = all.OrderBy(e => e.Start).ToList();
            LastUpdated = DateTime.Now;

            if (ok == 0) Status = "Could not load: " + (errors.FirstOrDefault() ?? "unknown error");
            else
            {
                Status = $"{_events.Count(e => e.End > DateTime.Today)} upcoming events from {ok} calendar{(ok == 1 ? "" : "s")}";
                if (errors.Count > 0) Status += $" ({errors.Count} failed, using saved copy)";
            }
            Updated?.Invoke();
        }

        // ---------- queries ----------

        public List<CalendarEvent> OnDay(DateTime day, bool includeAllDay)
        {
            var s = day.Date;
            var e = s.AddDays(1);
            return _events
                .Where(x => (includeAllDay || !x.AllDay) && x.Start < e && x.End > s)
                .OrderBy(x => !x.AllDay).ThenBy(x => x.Start)
                .ToList();
        }

        public HashSet<DateTime> DaysWithEvents(DateTime from, DateTime to, bool includeAllDay)
        {
            var set = new HashSet<DateTime>();
            foreach (var ev in _events)
            {
                if (!includeAllDay && ev.AllDay) continue;
                if (ev.End <= from || ev.Start >= to) continue;
                var last = ev.End > ev.Start ? ev.End.AddTicks(-1).Date : ev.Start.Date;
                for (var d = ev.Start.Date; d <= last && d < to; d = d.AddDays(1))
                    if (d >= from.Date) set.Add(d);
            }
            return set;
        }

        /// <summary>The event happening now or starting within the next N minutes.</summary>
        public CalendarEvent? NextTimed(DateTime now, int withinMinutes)
        {
            var limit = now.AddMinutes(withinMinutes);
            return _events.Where(x => !x.AllDay && x.End > now && x.Start <= limit).OrderBy(x => x.Start).FirstOrDefault();
        }

        /// <summary>The next timed event that starts within the next N minutes (not already started).</summary>
        public CalendarEvent? NextStarting(DateTime now, int withinMinutes)
        {
            var limit = now.AddMinutes(withinMinutes);
            return _events.Where(x => !x.AllDay && x.Start > now && x.Start <= limit).OrderBy(x => x.Start).FirstOrDefault();
        }
    }
}
