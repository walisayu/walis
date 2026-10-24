using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Walis.Mcp;

// MCP runs over stdio, and anything printed to stdout breaks the protocol, so logs always go to stderr.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var baseUrl = Environment.GetEnvironmentVariable("WALIS_API_URL") ?? "http://127.0.0.1:11120";
var apiKey = Environment.GetEnvironmentVariable("WALIS_API_KEY");

// The key is held in memory from here on; like the runner, drop it from this process's environment so another process of
// the same account (ps -E, /proc/<pid>/environ) cannot read it back.
Environment.SetEnvironmentVariable("WALIS_API_KEY", null);

// A single long-lived process needs only one HttpClient; no reason to pull in Microsoft.Extensions.Http for it.
builder.Services.AddSingleton(_ =>
{
    var api = new WalisApi(WalisApi.CreateSecureClient(), baseUrl, apiKey);

    if (api.UsesPlaintextRemote)
        Console.Error.WriteLine($"警告：{baseUrl} 不是 HTTPS，API 金鑰會以明文傳送；正式環境請改用 HTTPS。");

    return api;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();