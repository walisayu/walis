using System.Security.Cryptography;
using System.Text;
using Walis.Core.Scheduling;

namespace Walis.Core.Jenkins;

/// <summary>
///     Converts Jenkins cron syntax to Walis's five fields: <c>H</c> becomes a fixed value derived from a hash of the job name (Jenkins spreads load the same way),
///     aliases like <c>@daily</c> are expanded, and for <c>TZ=</c> and multi-line schedules only the first entry is taken with a warning.
/// </summary>
public static class JenkinsCron
{
    public static string? Convert(string spec, string seed, List<JenkinsWarning> warnings)
    {
        var lines = spec.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (lines.Count == 0) return null;

        if (lines[0].StartsWith("TZ=", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, $"排程的時區設定「{lines[0]}」已略過，Walis 用系統設定的時區。"));
            lines.RemoveAt(0);
        }

        if (lines.Count == 0) return null;

        if (lines.Count > 1)
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Warning,
                $"Jenkins 排程有 {lines.Count} 條，Walis 一個任務只有一條，只取第一條「{lines[0]}」。"));

        var expression = lines[0] switch
        {
            "@yearly" or "@annually" => "H H H H *",
            "@monthly" => "H H H * *",
            "@weekly" => "H H * * H",
            "@daily" or "@midnight" => "H H * * *",
            "@hourly" => "H * * * *",
            var other => other
        };

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported,
                $"看不懂 Jenkins 排程「{lines[0]}」，請手動設定 Cron。"));
            return null;
        }

        var hash = Hash(seed);
        var ranges = new (int Min, int Max)[] { (0, 59), (0, 23), (1, 28), (1, 12), (0, 6) };
        var usedHash = false;
        var converted = new string[5];

        for (var index = 0; index < 5; index++)
            converted[index] = string.Join(',', fields[index].Split(',').Select(part =>
            {
                var result = ConvertPart(part, ranges[index].Min, ranges[index].Max, hash + index, out var hashed);
                usedHash |= hashed;
                return result;
            }));

        var cron = string.Join(' ', converted);
        if (!CronExpression.TryParse(cron, out _, out var error))
        {
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Unsupported,
                $"排程「{lines[0]}」轉成「{cron}」後仍無法解析（{error}），請手動設定。"));
            return null;
        }

        if (usedHash)
            warnings.Add(new JenkinsWarning(JenkinsWarningSeverity.Info, $"排程「{lines[0]}」的 H 已換成固定值：「{cron}」。"));

        return cron;
    }

    private static string ConvertPart(string part, int min, int max, int hash, out bool hashed)
    {
        hashed = false;

        if (!part.Contains('H')) return part;

        hashed = true;
        var step = string.Empty;
        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            step = part[slash..];
            part = part[..slash];
        }

        if (part == "H")
            // H/n: Jenkins means "every n starting from some offset"; Walis has no offset, so use */n.
            return step.Length == 0 ? Pick(min, max, hash).ToString() : $"*{step}";

        if (part.StartsWith("H(") && part.EndsWith(')'))
        {
            var range = part[2..^1].Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high) &&
                high >= low) return step.Length == 0 ? Pick(low, high, hash).ToString() : $"{low}-{high}{step}";
        }

        return step.Length == 0 ? Pick(min, max, hash).ToString() : $"*{step}";
    }

    private static int Pick(int min, int max, int hash)
    {
        return min + Math.Abs(hash) % (max - min + 1);
    }

    private static int Hash(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
    }
}