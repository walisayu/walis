namespace Walis.Runner;

public sealed class RunnerOptions
{
    private static readonly HashSet<string> AllowedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "server", "name", "labels", "slots", "token", "token-file", "user", "password", "password-file", "workdir",
        "external-isolation", "allow-insecure-http", "interval"
    };

    /// <summary>Environment variables that carry credentials; removed from the process once parsed.</summary>
    public static readonly string[] SensitiveEnvironmentNames = ["WALIS_TOKEN", "WALIS_PASSWORD"];

    private const int MaximumTokenLength = 4_096;

    public required Uri Server { get; init; }
    public required string Name { get; init; }
    public string? Labels { get; init; }
    public int Slots { get; init; } = 2;
    public string? Token { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool ExternalIsolation { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The server is on another machine and reached without TLS, so the token and job secrets travel in the clear.</summary>
    public bool UsesPlaintextRemote => Server.Scheme == Uri.UriSchemeHttp && !Server.IsLoopback;

    public static string Usage => """
                                  walis-runner — Walis 遠端執行器

                                  用法：
                                    walis-runner --server <網址> --name <執行器名稱> [選項]

                                  選項：
                                    --server    Walis API 位址，預設 http://127.0.0.1:11126（或 WALIS_SERVER）；
                                                 跨機器連線建議用 HTTPS，用 HTTP 時權杖與機密會以明文傳送
                                    --allow-insecure-http true 已不需要，保留是為了相容既有的啟動設定
                                    --name      執行器名稱，預設為本機電腦名稱（或環境變數 WALIS_RUNNER_NAME）
                                    --labels    以逗號分隔的標籤，例如 linux,docker
                                    --slots     同時可執行的建置數，預設 2
                                    --token-file 讀取權杖的檔案（權限必須是 600，內容只有權杖一行）
                                    --token     API 存取權杖；寫在命令列上同機帳號看得到，請改用 --token-file
                                                 或環境變數 WALIS_TOKEN（程式讀完就會從自己的環境移除）
                                    --user      帳號，與 --password 一起使用時會自動登入取得權杖
                                    --password-file 讀取密碼的檔案（權限必須是 600，內容只有密碼一行）
                                    --password  密碼；寫在命令列上同機帳號看得到，請改用 --password-file
                                                 或環境變數 WALIS_PASSWORD（程式讀完就會從自己的環境移除）
                                    --workdir   預設工作目錄，任務沒有指定時使用
                                    --external-isolation true 表示部署者已把整個 runner 放在專用 VM/container 隔離邊界內
                                                 （或環境變數 WALIS_EXTERNAL_ISOLATION=true）；請勿只因在一般主機上執行就開啟
                                    --interval  輪詢間隔秒數，預設 5
                                  """;

    public static bool TryParse(string[] args, out RunnerOptions? options, out string? error)
    {
        options = null;
        error = null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = Environment.GetEnvironmentVariable("WALIS_SERVER") ?? "http://127.0.0.1:11126",
            ["name"] = Environment.GetEnvironmentVariable("WALIS_RUNNER_NAME") ??
                       Environment.MachineName.ToLowerInvariant(),
            ["token"] = Environment.GetEnvironmentVariable("WALIS_TOKEN") ?? string.Empty,
            ["password"] = Environment.GetEnvironmentVariable("WALIS_PASSWORD") ?? string.Empty,
            ["external-isolation"] = Environment.GetEnvironmentVariable("WALIS_EXTERNAL_ISOLATION") ?? string.Empty,
            ["allow-insecure-http"] = Environment.GetEnvironmentVariable("WALIS_ALLOW_INSECURE_HTTP") ?? string.Empty
        };

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"無法辨識的參數：{argument}";
                return false;
            }

            var key = argument[2..];
            var separator = key.IndexOf('=');
            var argumentName = separator >= 0 ? key[..separator] : key;

            if (!AllowedArguments.Contains(argumentName))
            {
                error = $"無法辨識的參數：--{argumentName}";
                return false;
            }

            if (separator >= 0)
            {
                values[key[..separator]] = key[(separator + 1)..];
                continue;
            }

            if (index + 1 >= args.Length)
            {
                error = $"參數 --{key} 缺少值。";
                return false;
            }

            values[key] = args[++index];
        }

        if (values["server"].Length > 2_048 ||
            !Uri.TryCreate(values["server"], UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(server.Host) ||
            !string.IsNullOrEmpty(server.UserInfo) ||
            !string.IsNullOrEmpty(server.Query) ||
            !string.IsNullOrEmpty(server.Fragment))
        {
            error = "伺服器位址必須是不帶帳密、查詢或片段的 HTTP/HTTPS 完整網址。";
            return false;
        }

        // Plain HTTP to another machine is allowed so a runner connects to a fresh server straight away; Program warns about it.
        // --allow-insecure-http is still accepted so service units written for older versions keep starting.

        if (string.IsNullOrWhiteSpace(values["name"]) ||
            values["name"].Trim().Length > 60 ||
            values["name"].Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#' or '%'))
        {
            error = "請以 --name 指定 1 到 60 個字元、且不含路徑保留字元的執行器名稱。";
            return false;
        }

        if (!TryResolveToken(values, out var token, out error)) return false;

        if (!TryResolveSecret(values, "password", "password-file", "密碼", out var password, out error)) return false;

        options = new RunnerOptions
        {
            Server = server,
            Name = values["name"].Trim(),
            Labels = Value(values, "labels"),
            Slots = int.TryParse(Value(values, "slots"), out var slots) ? Math.Clamp(slots, 1, 32) : 2,
            Token = token,
            Username = Value(values, "user"),
            Password = password,
            WorkingDirectory = Value(values, "workdir"),
            ExternalIsolation = bool.TryParse(Value(values, "external-isolation"), out var externalIsolation) &&
                                externalIsolation,
            Interval = int.TryParse(Value(values, "interval"), out var seconds)
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300))
                : TimeSpan.FromSeconds(5)
        };

        return true;
    }

    /// <summary>Removes credential variables from this process so builds sharing the account cannot read them back.</summary>
    public static void ClearSensitiveEnvironment()
    {
        foreach (var name in SensitiveEnvironmentNames) Environment.SetEnvironmentVariable(name, null);
    }

    /// <summary>
    ///     --token-file wins over --token/WALIS_TOKEN only when the latter is absent; giving both is a configuration mistake and is
    ///     refused rather than silently resolved. The file holds the token on its first line and nothing else of use.
    /// </summary>
    private static bool TryResolveToken(
        IReadOnlyDictionary<string, string> values,
        out string? token,
        out string? error)
    {
        return TryResolveSecret(values, "token", "token-file", "權杖", out token, out error) &&
               ValidateToken(token, out error);
    }

    /// <summary>
    ///     Resolves a credential given either directly (argument or environment) or through a file. Giving both is refused.
    ///     The file must hold the value on its first line and, outside Windows, must not be readable by group or others:
    ///     a credential file another local account can read is no better than one on the command line.
    /// </summary>
    private static bool TryResolveSecret(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fileKey,
        string label,
        out string? secret,
        out string? error)
    {
        secret = Value(values, key);
        error = null;

        var file = Value(values, fileKey);

        if (file is null) return true;

        if (secret is not null)
        {
            error = $"--{fileKey} 與 --{key}（或對應的環境變數）只能擇一提供。";
            return false;
        }

        try
        {
            var info = new FileInfo(file);

            if (!info.Exists)
            {
                error = $"找不到{label}檔案：{file}";
                return false;
            }

            if (info.Length > MaximumTokenLength)
            {
                error = $"{label}檔案過大，最多 {MaximumTokenLength} bytes：{file}";
                return false;
            }

            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead |
                                               UnixFileMode.OtherWrite)) != 0)
            {
                error = $"{label}檔案 {file} 可被其他帳號讀寫，請先執行 chmod 600 {file}。";
                return false;
            }

            secret = File.ReadLines(file).FirstOrDefault()?.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"無法讀取{label}檔案 {file}：{exception.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            error = $"{label}檔案是空的：{file}";
            return false;
        }

        return true;
    }

    private static bool ValidateToken(string? token, out string? error)
    {
        error = null;

        if (token is null) return true;

        if (token.Length > MaximumTokenLength || token.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            error = "權杖不可含空白或控制字元，長度最多 4,096 個字元。";
            return false;
        }

        return true;
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}