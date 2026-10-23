using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;

namespace Walis.Core.Plugins;

public sealed record PluginRenderRequest(
    Plugin Plugin,
    IReadOnlyDictionary<string, string?> Inputs,
    IReadOnlyDictionary<string, string> Variables);

public sealed record PluginRenderResult(string Script, IReadOnlyList<string> Logs);

public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     Runs a plugin's JavaScript.
///     Uses Jint (a pure C# interpreter) rather than V8 or Node: the deployment is a self-contained executable,
///     and remote runners must be able to run plugins too; one more native dependency or runtime would break both.
///     The price is no npm, so plugins can only use the standard library plus the host API provided here.
///     The sandbox works by "not giving" rather than "blocking": Jint itself has no file system or network,
///     so the only things a plugin can reach are the few functions attached below.
/// </summary>
public sealed class PluginHost(IPluginCapabilities capabilities)
{
    public const int CurrentApiVersion = 1;
    public const int MaximumHttpUrlLength = 2048;
    public const int MaximumCodeLength = 100_000;
    public const int MaximumRenderedScriptLength = 256_000;
    private const int MaximumInputCharacters = 256_000;
    private const int MaximumInputs = 128;
    private const int MaximumHttpRequests = 8;
    private const int MaximumLogEntries = 100;
    private const int MaximumLogCharacters = 64 * 1024;
    private const int MemoryLimitBytes = 8 * 1024 * 1024;
    private const int StatementLimit = 500_000;
    private const int RecursionLimit = 64;

    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(10);

    public PluginRenderResult Render(PluginRenderRequest request)
    {
        if (request.Plugin.Code.Length > MaximumCodeLength)
            throw new PluginException(
                $"外掛 {request.Plugin.PluginId} 的程式碼超過 {MaximumCodeLength:N0} 個字元上限。");

        if (request.Inputs.Count > MaximumInputs ||
            request.Inputs.Sum(pair => pair.Key.Length + (pair.Value?.Length ?? 0)) > MaximumInputCharacters ||
            request.Variables.Sum(pair => pair.Key.Length + pair.Value.Length) > MaximumInputCharacters)
            throw new PluginException($"外掛 {request.Plugin.PluginId} 的輸入超過資源上限。");

        if (request.Plugin.Manifest.ApiVersion != CurrentApiVersion)
            throw new PluginException(
                $"外掛 {request.Plugin.PluginId} 需要 host API {request.Plugin.Manifest.ApiVersion}，" +
                $"這個版本的 Walis 提供 {CurrentApiVersion}。");

        var logs = new List<string>();
        var engine = CreateEngine(request.Plugin, logs);

        try
        {
            engine.Execute(request.Plugin.Code);

            var render = engine.GetValue("render");

            if (render is not Function) throw new PluginException($"外掛 {request.Plugin.PluginId} 沒有定義 render 函式。");

            var result = engine.Invoke(render, ToJsObject(engine, request.Inputs), BuildContext(engine, request, logs));

            if (result.IsNull() || result.IsUndefined()) return new PluginRenderResult(string.Empty, logs);

            var script = result.ToString();
            if (script.Length > MaximumRenderedScriptLength)
                throw new PluginException(
                    $"外掛 {request.Plugin.PluginId} 產生的腳本超過 {MaximumRenderedScriptLength:N0} 個字元上限。");

            return new PluginRenderResult(script, logs);
        }
        catch (TimeoutException)
        {
            throw new PluginException($"外掛 {request.Plugin.PluginId} 執行超過 {ExecutionTimeout.TotalSeconds} 秒。");
        }
        catch (StatementsCountOverflowException)
        {
            throw new PluginException(
                $"外掛 {request.Plugin.PluginId} 執行的指令數超過上限（{StatementLimit:N0}），可能有無窮迴圈。");
        }
        catch (MemoryLimitExceededException)
        {
            throw new PluginException(
                $"外掛 {request.Plugin.PluginId} 用掉的記憶體超過上限（{MemoryLimitBytes / 1024 / 1024} MB）。");
        }
        catch (RecursionDepthOverflowException)
        {
            throw new PluginException($"外掛 {request.Plugin.PluginId} 的遞迴超過 {RecursionLimit} 層。");
        }
        catch (JavaScriptException exception)
        {
            throw new PluginException(
                $"外掛 {request.Plugin.PluginId} 執行失敗：{exception.Message}（第 {exception.Location.Start.Line} 行）",
                exception);
        }
        catch (Exception exception) when (exception is not PluginException)
        {
            throw new PluginException($"外掛 {request.Plugin.PluginId} 執行失敗：{exception.Message}", exception);
        }
    }

    private static Engine CreateEngine(Plugin plugin, List<string> logs)
    {
        return new Engine(options => options
            .TimeoutInterval(ExecutionTimeout)
            .LimitMemory(MemoryLimitBytes)
            .MaxStatements(StatementLimit)
            .LimitRecursion(RecursionLimit)

            // No CLR access: a plugin can only reach the functions explicitly attached below.
            .Strict());
    }

    private JsValue BuildContext(Engine engine, PluginRenderRequest request, List<string> logs)
    {
        var permissions = request.Plugin.Manifest.Permissions ?? new PluginPermissions();
        permissions.Http ??= [];
        permissions.Secrets ??= [];
        var plugin = request.Plugin;
        var httpRequests = 0;
        var logCharacters = 0;

        var shell = new JsObject(engine);
        shell.Set("quote",
            JsValue.FromObject(engine, (Func<string?, string>)(value => ShellQuote(value ?? string.Empty))));

        var http = new JsObject(engine);
        http.Set("fetch", JsValue.FromObject(engine, (Func<string, JsValue?, JsValue>)((url, options) =>
        {
            // The manifest is written by the plugin author, so whatever it declares does not count; outbound access needs an admin to mark the plugin trusted.
            if (!plugin.IsTrusted)
                throw new PluginException(
                    $"外掛 {plugin.PluginId} 尚未標記為受信任，不能連外。請管理員審閱程式碼後在安裝時勾選「信任」。");

            if (!IsAllowed(url, permissions.Http))
                throw new PluginException(
                    $"外掛沒有宣告可以連線到 {url}；請在 manifest 的 permissions.http 加入該網域。");

            if (++httpRequests > MaximumHttpRequests)
                throw new PluginException($"外掛每次產生腳本最多發出 {MaximumHttpRequests} 個 HTTP 請求。");

            // The second argument may be omitted; Jint then passes null.
            var parsed = options is not null && options.IsObject()
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(options.ToObject()))
                : null;

            return JsValue.FromObject(engine, capabilities.Fetch(plugin, url, parsed));
        })));

        var context = new JsObject(engine);
        context.Set("log", JsValue.FromObject(engine, (Action<string?>)(message =>
        {
            var value = message ?? string.Empty;
            logCharacters += value.Length;
            if (logs.Count >= MaximumLogEntries || logCharacters > MaximumLogCharacters)
                throw new PluginException("外掛記錄超過數量或長度上限。");

            logs.Add(value);
            capabilities.Log(plugin, value);
        })));
        context.Set("shell", shell);
        context.Set("http", http);
        context.Set("variables", ToJsObject(
            engine,
            request.Variables.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)));

        return context;
    }

    /// <summary>Matches only the host and ignores the path: allowing a host allows every endpoint under it.</summary>
    public static bool IsAllowed(string url, IReadOnlyList<string> allowed)
    {
        if (allowed.Count > 64 ||
            url.Length > MaximumHttpUrlLength ||
            !Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(target.Host) ||
            !string.IsNullOrEmpty(target.UserInfo) ||
            !string.IsNullOrEmpty(target.Fragment))
            return false;

        return allowed.Any(entry =>
            !string.IsNullOrWhiteSpace(entry) &&
            entry.Length <= MaximumHttpUrlLength &&
            Uri.TryCreate(entry, UriKind.Absolute, out var permitted) &&
            permitted.Scheme is "http" or "https" &&
            !string.IsNullOrEmpty(permitted.Host) &&
            string.IsNullOrEmpty(permitted.UserInfo) &&
            string.IsNullOrEmpty(permitted.Fragment) &&
            string.Equals(permitted.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(permitted.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
            permitted.Port == target.Port);
    }

    /// <summary>Same rules as {{name}} in action templates, so plugin authors need not invent their own escaping.</summary>
    public static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\\''")}'";
    }

    private static JsValue ToJsObject(Engine engine, IReadOnlyDictionary<string, string?> values)
    {
        var instance = new JsObject(engine);

        foreach (var (key, value) in values) instance.Set(key, value is null ? JsValue.Null : value);

        return instance;
    }
}

/// <summary>The capabilities the host provides to plugins. Implemented in the API layer; Core only defines the shape.</summary>
public interface IPluginCapabilities
{
    void Log(Plugin plugin, string message);

    /// <summary>Synchronous calls: plugins are glue code, and synchronous is easier to write and read than Promises.</summary>
    IReadOnlyDictionary<string, object?> Fetch(
        Plugin plugin,
        string url,
        IReadOnlyDictionary<string, JsonElement>? options);
}