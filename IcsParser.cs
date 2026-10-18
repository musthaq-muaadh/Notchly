using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Notchly
{
    public sealed class CalendarEvent
    {
        public string Title { get; init; } = "";
        public DateTime Start { get; init; }   // local time
        public DateTime End { get; init; }     // local time (exclusive for all-day)
        public bool AllDay { get; init; }
        public string Location { get; init; } = "";
    }

    /// <summary>
    /// Small iCalendar (.ics) reader: events, time zones, all-day events, and the common
    /// recurrence rules (daily / weekly / monthly / yearly, COUNT, UNTIL, BYDAY, EXDATE, edited instances).
    /// </summary>
    static class IcsParser
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class Prop
        {
            public string Name = "";
            public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
            public string Value = "";
        }

        sealed class Raw
        {
            public string Uid = "", Title = "", Location = "";
            public bool AllDay, Cancelled;
            public DateTime StartWall;
            public TimeZoneInfo? Tz;            // null for all-day
            public TimeSpan Duration;
            public string? RRule;
            public List<DateTime> ExLocal = new();
            public DateTime? RecurIdLocal;
        }

        sealed class Rule
        {
            public string Freq = "";
            public int Interval = 1;
            public int? Count;
            public DateTime? Until;
            public List<(int? Ord, DayOfWeek Dow)> ByDay = new();
            public List<int> ByMonthDay = new();
            public List<int> ByMonth = new();
            public DayOfWeek Wkst = DayOfWeek.Monday;
        }

        // ================= public entry =================

        public static List<CalendarEvent> Parse(string text, DateTime rangeStart, DateTime rangeEnd)
        {
            var raws = ReadRaws(text);
            var overrideKeys = new HashSet<(string, DateTime)>(
                raws.Where(r => r.RecurIdLocal != null).Select(r => (r.Uid, r.RecurIdLocal!.Value)));

            var result = new List<CalendarEvent>();

            void Add(Raw r, DateTime start)
            {
                var end = start + r.Duration;
                if (end <= rangeStart || start >= rangeEnd) return;
                result.Add(new CalendarEvent
                {
                    Title = string.IsNullOrWhiteSpace(r.Title) ? "(No title)" : r.Title,
                    Start = start,
                    End = end,
                    AllDay = r.AllDay,
                    Location = r.Location
                });
            }

            foreach (var r in raws)
            {
                if (r.RRule == null || r.RecurIdLocal != null)
                {
                    Add(r, Local(r.StartWall, r.Tz));
                    continue;
                }

                var rule = ParseRule(r.RRule, r);
                foreach (var occ in Expand(r, rule, rangeEnd.AddDays(2)))
                {
                    var local = Local(occ, r.Tz);
                    if (local >= rangeEnd) break;
                    if (r.ExLocal.Contains(local)) continue;
                    if (overrideKeys.Contains((r.Uid, local))) continue;
                    Add(r, local);
                }
            }

            return result;
        }

        // ================= reading =================

        static List<Raw> ReadRaws(string text)
        {
            var raws = new List<Raw>();
            List<Prop>? cur = null;
            bool inAlarm = false;

            foreach (var line in Unfold(text))
            {
                if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new List<Prop>(); inAlarm = false; continue; }
                if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (cur != null) { var raw = Build(cur); if (raw != null) raws.Add(raw); }
                    cur = null;
                    continue;
                }
                if (cur == null) continue;
                if (line.StartsWith("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase)) { inAlarm = true; continue; }
                if (line.StartsWith("END:VALARM", StringComparison.OrdinalIgnoreCase)) { inAlarm = false; continue; }
                if (inAlarm) continue;

                var p = ParseProp(line);
                if (p != null) cur.Add(p);
            }
            return raws;
        }

        static IEnumerable<string> Unfold(string text)
        {
            var sb = new StringBuilder();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && sb.Length > 0)
                {
                    sb.Append(raw, 1, raw.Length - 1);
                    continue;
                }
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                sb.Append(raw);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        static Prop? ParseProp(string line)
        {
            int colon = -1;
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') quoted = !quoted;
                else if (c == ':' && !quoted) { colon = i; break; }
            }
            if (colon < 1) return null;

            var parts = SplitOutsideQuotes(line.Substring(0, colon), ';');
            var p = new Prop { Name = parts[0].ToUpperInvariant(), Value = line.Substring(colon + 1) };
            for (int i = 1; i < parts.Count; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq > 0) p.Params[parts[i].Substring(0, eq)] = parts[i].Substring(eq + 1).Trim('"');
            }
            return p;
        }

        static List<string> SplitOutsideQuotes(string s, char sep)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (var c in s)
            {
                if (c == '"') { quoted = !quoted; sb.Append(c); }
                else if (c == sep && !quoted) { list.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        static string Unescape(string s) =>
            s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

        static Raw? Build(List<Prop> props)
        {
            var r = new Raw();
            Prop? dtstart = null, dtend = null, dur = null, recId = null;
            var exProps = new List<Prop>();

            foreach (var p in props)
            {
                switch (p.Name)
                {
                    case "UID": r.Uid = p.Value.Trim(); break;
                    case "SUMMARY": r.Title = Unescape(p.Value).Replace("\n", " ").Trim(); break;
                    case "LOCATION": r.Location = Unescape(p.Value).Replace("\n", ", ").Trim(); break;
                    case "STATUS": if (p.Value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) r.Cancelled = true; break;
                    case "DTSTART": dtstart = p; break;
                    case "DTEND": dtend = p; break;
                    case "DURATION": dur = p; break;
                    case "RRULE": r.RRule = p.Value.Trim(); break;
                    case "EXDATE": exProps.Add(p); break;
                    case "RECURRENCE-ID": recId = p; break;
                }
            }

            if (dtstart == null || r.Cancelled) return null;
            if (!TryParseTime(dtstart, out var wall, out var tz, out var allDay)) return null;
            r.StartWall = wall; r.Tz = tz; r.AllDay = allDay;

            if (dtend != null && TryParseTime(dtend, out var ew, out var etz, out _))
                r.Duration = allDay ? ew - wall : Local(ew, etz) - Local(wall, tz);
            else if (dur != null)
                r.Duration = ParseDuration(dur.Value);
            else
                r.Duration = allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;

            if (allDay && r.Duration < TimeSpan.FromDays(1)) r.Duration = TimeSpan.FromDays(1);
            if (!allDay && r.Duration <= TimeSpan.Zero) r.Duration = TimeSpan.FromMinutes(1);

            foreach (var ex in exProps)
                foreach (var part in ex.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tmp = new Prop { Name = ex.Name, Value = part.Trim() };
                    foreach (var kv in ex.Params) tmp.Params[kv.Key] = kv.Value;
                    if (TryParseTime(tmp, out var xw, out var xtz, out _)) r.ExLocal.Add(Local(xw, xtz));
                }

            if (recId != null && TryParseTime(recId, out var rw, out var rtz, out _))
                r.RecurIdLocal = Local(rw, rtz);

            return r;
        }

        // ================= time helpers =================

        static readonly Dictionary<string, TimeZoneInfo?> TzCache = new();

        static TimeZoneInfo? FindTz(string id)
        {
            if (TzCache.TryGetValue(id, out var cached)) return cached;
            TimeZoneInfo? tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { tz = null; }
            TzCache[id] = tz;
            return tz;
        }

        static bool TryParseTime(Prop p, out DateTime wall, out TimeZoneInfo? tz, out bool allDay)
        {
            wall = default; tz = null; allDay = false;
            var v = p.Value.Trim();

            bool dateOnly = (p.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8;
            if (dateOnly)
            {
                allDay = true;
                return v.Length >= 8 && DateTime.TryParseExact(v.Substring(0, 8), "yyyyMMdd", Inv, DateTimeStyles.None, out wall);
            }

            bool utc = v.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
            var core = utc ? v.Substring(0, v.Length - 1) : v;
            if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", Inv, DateTimeStyles.None, out wall) &&
                !DateTime.TryParseExact(core, "yyyyMMdd'T'HHmm", Inv, DateTimeStyles.None, out wall))
                return false;

            if (utc) { tz = TimeZoneInfo.Utc; return true; }
            if (p.Params.TryGetValue("TZID", out var id)) { tz = FindTz(id.Trim('"')) ?? TimeZoneInfo.Local; return true; }
            tz = TimeZoneInfo.Local; // floating time
            return true;
        }

        static DateTime Local(DateTime wall, TimeZoneInfo? tz)
        {
            if (tz == null) return wall;
            try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local); }
            catch { return wall; }
        }

        static readonly Regex DurRx = new(@"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.IgnoreCase);

        static TimeSpan ParseDuration(string s)
        {
            var m = DurRx.Match(s.Trim());
            if (!m.Success) return TimeSpan.Zero;
            int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, Inv) : 0;
            var t = TimeSpan.FromDays(G(2) * 7 + G(3)) + TimeSpan.FromHours(G(4)) + TimeSpan.FromMinutes(G(5)) + TimeSpan.FromSeconds(G(6));
            return m.Groups[1].Value == "-" ? -t : t;
        }

        // ================= recurrence =================

        static bool TryDow(string s, out DayOfWeek d)
        {
            switch (s.ToUpperInvariant())
            {
                case "MO": d = DayOfWeek.Monday; return true;
                case "TU": d = DayOfWeek.Tuesday; return true;
                case "WE": d = DayOfWeek.Wednesday; return true;
                case "TH": d = DayOfWeek.Thursday; return true;
                case "FR": d = DayOfWeek.Friday; return true;
                case "SA": d = DayOfWeek.Saturday; return true;
                case "SU": d = DayOfWeek.Sunday; return true;
                default: d = DayOfWeek.Monday; return false;
            }
        }

        static Rule ParseRule(string s, Raw r)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in s.Split(';'))
            {
                int i = part.IndexOf('=');
                if (i > 0) d[part.Substring(0, i)] = part.Substring(i + 1);
            }

            var rule = new Rule { Freq = d.GetValueOrDefault("FREQ", "").ToUpperInvariant() };
            if (d.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var n) && n > 0) rule.Interval = n;
            if (d.TryGetValue("COUNT", out var cv) && int.TryParse(cv, out var c)) rule.Count = c;
            if (d.TryGetValue("UNTIL", out var uv)) rule.Until = ParseUntil(uv, r);
            if (d.TryGetValue("WKST", out var wk) && TryDow(wk, out var wd)) rule.Wkst = wd;

            if (d.TryGetValue("BYDAY", out var bd))
                foreach (var tok in bd.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = tok.Trim().ToUpperInvariant();
                    if (t.Length < 2 || !TryDow(t.Substring(t.Length - 2), out var dow)) continue;
                    int? ord = null;
                    var os = t.Substring(0, t.Length - 2);
                    if (os.Length > 0 && int.TryParse(os, NumberStyles.AllowLeadingSign, Inv, out var o)) ord = o;
                    rule.ByDay.Add((ord, dow));
                }

            if (d.TryGetValue("BYMONTHDAY", out var bmd))
                foreach (var t in bmd.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(t, NumberStyles.AllowLeadingSign, Inv, out var m)) rule.ByMonthDay.Add(m);

            if (d.TryGetValue("BYMONTH", out var bm))
                foreach (var t in bm.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(t, out var m)) rule.ByMonth.Add(m);

            return rule;
        }

        static DateTime? ParseUntil(string uv, Raw r)
        {
            uv = uv.Trim();
            if (uv.Length == 8 && DateTime.TryParseExact(uv, "yyyyMMdd", Inv, DateTimeStyles.None, out var day))
                return day.Date.AddDays(1).AddTicks(-1);

            bool utc = uv.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
            var core = utc ? uv.Substring(0, uv.Length - 1) : uv;
            if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", Inv, DateTimeStyles.None, out var w)) return null;
            if (!utc) return w;
            try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(w, DateTimeKind.Utc), r.Tz ?? TimeZoneInfo.Local); }
            catch { return w; }
        }

        static DateTime StartOfWeek(DateTime date, DayOfWeek wkst)
        {
            int diff = ((int)date.DayOfWeek - (int)wkst + 7) % 7;
            return date.Date.AddDays(-diff);
        }

        static List<DateTime> MonthCandidates(DateTime first, Rule rule, int defaultDay, TimeSpan tod)
        {
            int dim = DateTime.DaysInMonth(first.Year, first.Month);
            var res = new List<DateTime>();

            if (rule.ByDay.Count > 0)
            {
                foreach (var (ord, dow) in rule.ByDay)
                {
                    var days = Enumerable.Range(0, dim).Select(i => first.AddDays(i)).Where(x => x.DayOfWeek == dow).ToList();
                    if (ord == null) res.AddRange(days.Select(x => x + tod));
                    else
                    {
                        int idx = ord.Value > 0 ? ord.Value - 1 : days.Count + ord.Value;
                        if (idx >= 0 && idx < days.Count) res.Add(days[idx] + tod);
                    }
                }
                if (rule.ByMonthDay.Count > 0)
                    res = res.Where(x => rule.ByMonthDay.Any(m => m > 0 ? x.Day == m : x.Day == dim + m + 1)).ToList();
            }
            else if (rule.ByMonthDay.Count > 0)
            {
                foreach (var m in rule.ByMonthDay)
                {
                    int day = m > 0 ? m : dim + m + 1;
                    if (day >= 1 && day <= dim) res.Add(first.AddDays(day - 1) + tod);
                }
            }
            else if (defaultDay <= dim)
            {
                res.Add(first.AddDays(defaultDay - 1) + tod);
            }
            return res;
        }

        /// <summary>Yields occurrence start times as wall-clock time in the event's own zone.</summary>
        static IEnumerable<DateTime> Expand(Raw r, Rule rule, DateTime limit)
        {
            var start = r.StartWall;
            var tod = start.TimeOfDay;
            int produced = 0;

            for (int k = 0; k < 100000; k++)
            {
                List<DateTime> cands;
                DateTime periodStart;

                switch (rule.Freq)
                {
                    case "DAILY":
                    {
                        var d = start.Date.AddDays((double)k * rule.Interval);
                        periodStart = d;
                        cands = new List<DateTime> { d + tod };
                        if (rule.ByDay.Count > 0)
                            cands = cands.Where(c => rule.ByDay.Any(b => b.Dow == c.DayOfWeek)).ToList();
                        break;
                    }
                    case "WEEKLY":
                    {
                        var weekStart = StartOfWeek(start.Date, rule.Wkst).AddDays(7.0 * k * rule.Interval);
                        periodStart = weekStart;
                        var days = rule.ByDay.Count > 0 ? rule.ByDay.Select(b => b.Dow).Distinct().ToList() : new List<DayOfWeek> { start.DayOfWeek };
                        cands = days.Select(dw => weekStart.AddDays(((int)dw - (int)rule.Wkst + 7) % 7) + tod).ToList();
                        break;
                    }
                    case "MONTHLY":
                    {
                        var first = new DateTime(start.Year, start.Month, 1).AddMonths(k * rule.Interval);
                        periodStart = first;
                        cands = MonthCandidates(first, rule, start.Day, tod);
                        break;
                    }
                    case "YEARLY":
                    {
                        int year = start.Year + k * rule.Interval;
                        if (year > 9990) yield break;
                        periodStart = new DateTime(year, 1, 1);
                        var months = rule.ByMonth.Count > 0 ? rule.ByMonth : new List<int> { start.Month };
                        cands = new List<DateTime>();
                        foreach (var m in months)
                            if (m >= 1 && m <= 12)
                                cands.AddRange(MonthCandidates(new DateTime(year, m, 1), rule, start.Day, tod));
                        break;
                    }
                    default:
                        yield break;
                }

                if (periodStart > limit) yield break;

                foreach (var c in cands.OrderBy(x => x))
                {
                    if (c < start) continue;
                    if (rule.Until != null && c > rule.Until.Value) yield break;
                    if (rule.ByMonth.Count > 0 && rule.Freq != "YEARLY" && !rule.ByMonth.Contains(c.Month)) continue;
                    produced++;
                    if (rule.Count != null && produced > rule.Count.Value) yield break;
                    yield return c;
                }
            }
        }
    }
}
