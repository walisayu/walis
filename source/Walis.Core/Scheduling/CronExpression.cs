namespace Walis.Core.Scheduling;

/// <summary>
///     A five-field Cron expression (minute hour day month weekday) supporting *, a, a-b, a,b, */n and a-b/n.
///     Weekdays are 0 (Sunday) to 6 (Saturday); 7 is also treated as Sunday.
///     When both day and weekday are restricted, cron convention applies and they are ORed.
/// </summary>
public sealed class CronExpression
{
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _daysOfWeek;
    private readonly bool[] _hours;
    private readonly bool[] _minutes;
    private readonly bool[] _months;

    private CronExpression(
        bool[] minutes,
        bool[] hours,
        bool[] daysOfMonth,
        bool[] months,
        bool[] daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "請輸入 Cron 運算式。";
            return false;
        }

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "Cron 運算式需要五個欄位：分 時 日 月 週。";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, out var minutes, out error) ||
            !TryParseField(fields[1], 0, 23, out var hours, out error) ||
            !TryParseField(fields[2], 1, 31, out var daysOfMonth, out error) ||
            !TryParseField(fields[3], 1, 12, out var months, out error) ||
            !TryParseField(fields[4], 0, 7, out var rawDaysOfWeek, out error))
            return false;

        var daysOfWeek = new bool[7];
        for (var index = 0; index <= 7; index++)
            if (rawDaysOfWeek![index])
                daysOfWeek[index % 7] = true;

        cron = new CronExpression(
            minutes!,
            hours!,
            daysOfMonth!,
            months!,
            daysOfWeek,
            fields[2] != "*",
            fields[4] != "*");
        return true;
    }

    public static CronExpression Parse(string expression)
    {
        return TryParse(expression, out var cron, out var error)
            ? cron!
            : throw new FormatException(error);
    }

    public bool Matches(DateTime time)
    {
        if (!_minutes[time.Minute] || !_hours[time.Hour] || !_months[time.Month]) return false;

        var dayOfMonth = _daysOfMonth[time.Day];
        var dayOfWeek = _daysOfWeek[(int)time.DayOfWeek];

        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (true, true) => dayOfMonth || dayOfWeek,
            (true, false) => dayOfMonth,
            (false, true) => dayOfWeek,
            _ => true
        };
    }

    /// <summary>Gets the next trigger time after <paramref name="after" /> (exclusive); returns null when none is found.</summary>
    /// <summary>
    /// Lists every trigger time within the window (start inclusive, end exclusive) for the timeline. Scans minute by minute;
    /// seven days is only ten thousand comparisons, and <paramref name="limit"/> keeps an "every minute" schedule from blowing up the response.
    /// </summary>
    public IReadOnlyList<DateTime> GetOccurrences(DateTime fromLocal, DateTime toLocal, int limit = 2_000)
    {
        var results = new List<DateTime>();
        var cursor = new DateTime(fromLocal.Year, fromLocal.Month, fromLocal.Day, fromLocal.Hour, fromLocal.Minute, 0, fromLocal.Kind);

        while (cursor < toLocal && results.Count < limit)
        {
            if (Matches(cursor))
            {
                results.Add(cursor);
            }

            cursor = cursor.AddMinutes(1);
        }

        return results;
    }

    public DateTime? GetNextOccurrence(DateTime after, int searchLimitDays = 366)
    {
        var candidate = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind)
            .AddMinutes(1);
        var limit = candidate.AddDays(searchLimitDays);

        while (candidate < limit)
        {
            if (!MatchesDate(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        return null;
    }

    private bool MatchesDate(DateTime time)
    {
        if (!_months[time.Month]) return false;

        var dayOfMonth = _daysOfMonth[time.Day];
        var dayOfWeek = _daysOfWeek[(int)time.DayOfWeek];

        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (true, true) => dayOfMonth || dayOfWeek,
            (true, false) => dayOfMonth,
            (false, true) => dayOfWeek,
            _ => true
        };
    }

    private static bool TryParseField(string field, int min, int max, out bool[]? values, out string? error)
    {
        values = new bool[max + 1];
        error = null;

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');

            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
                {
                    error = $"無法解析 Cron 欄位「{field}」的間隔。";
                    values = null;
                    return false;
                }
            }

            int from;
            int to;

            if (range is "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (dash >= 0)
                {
                    if (!int.TryParse(range[..dash], out from) || !int.TryParse(range[(dash + 1)..], out to))
                    {
                        error = $"無法解析 Cron 欄位「{field}」的範圍。";
                        values = null;
                        return false;
                    }
                }
                else
                {
                    if (!int.TryParse(range, out from))
                    {
                        error = $"無法解析 Cron 欄位「{field}」。";
                        values = null;
                        return false;
                    }

                    to = slash >= 0 ? max : from;
                }
            }

            if (from < min || to > max || from > to)
            {
                error = $"Cron 欄位「{field}」超出允許範圍 {min}-{max}。";
                values = null;
                return false;
            }

            for (var value = from; value <= to; value += step) values[value] = true;
        }

        if (Array.IndexOf(values, true) < 0)
        {
            error = $"Cron 欄位「{field}」沒有任何有效值。";
            values = null;
            return false;
        }

        return true;
    }
}