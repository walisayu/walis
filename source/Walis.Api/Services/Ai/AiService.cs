using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Services.Ai;

public sealed record AiAnswer(
    string Text,
    string ProviderKind,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    int DurationMs);

/// <summary>
///     The single entry point for the three providers. Claude uses the official Anthropic SDK,
///     Gemini and Codex (OpenAI-compatible) use their own REST endpoints. Every call leaves a record.
/// </summary>
public sealed class AiService(
    IAiRepository providers,
    IHttpClientFactory httpClientFactory,
    AuditService audit,
    ILogger<AiService> logger)
{
    private const int MaxTokens = 16_000;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumConcurrentRequests = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _concurrency = new(MaximumConcurrentRequests, MaximumConcurrentRequests);

    public async Task<AiAnswer> CompleteAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        string purpose,
        Guid? buildId,
        Guid? jobId,
        CancellationToken cancellationToken)
    {
        if (!await _concurrency.WaitAsync(TimeSpan.Zero, cancellationToken))
            throw new InvalidOperationException("AI 服務目前忙碌中，請稍後再試。");

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var startedAt = DateTime.UtcNow;

            try
            {
                var completion = provider.Kind switch
                {
                    AiProviderKind.Claude => await CallClaudeAsync(httpClientFactory, provider, systemPrompt, userPrompt,
                        cancellationToken),
                    AiProviderKind.Gemini => await CallGeminiAsync(provider, systemPrompt, userPrompt,
                        cancellationToken),
                    AiProviderKind.Codex => await CallCodexAsync(provider, systemPrompt, userPrompt, cancellationToken),
                    _ => throw new InvalidOperationException($"不支援的 AI 供應商：{provider.Kind}")
                };

                stopwatch.Stop();

                await LogAsync(provider, purpose, buildId, jobId, userPrompt, startedAt, stopwatch, completion, null,
                    cancellationToken);

                return new AiAnswer(
                    completion.Text,
                    provider.Kind,
                    provider.Model,
                    completion.InputTokens,
                    completion.OutputTokens,
                    (int)stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                var safeError = SanitizeError(provider.ApiKey, exception.Message);
                logger.LogWarning("呼叫 {Provider} 失敗：{Error}", provider.Kind, safeError);

                await LogAsync(provider, purpose, buildId, jobId, userPrompt, startedAt, stopwatch, null, safeError,
                    CancellationToken.None);
                throw new InvalidOperationException(safeError);
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task LogAsync(
        AiProvider provider,
        string purpose,
        Guid? buildId,
        Guid? jobId,
        string prompt,
        DateTime startedAt,
        Stopwatch stopwatch,
        AiCompletion? completion,
        string? error,
        CancellationToken cancellationToken)
    {
        await providers.AppendRequestAsync(
            new AiRequestLogEntry
            {
                StartedAt = startedAt,
                ProviderKind = provider.Kind,
                Model = provider.Model,
                Purpose = purpose,
                BuildId = buildId,
                JobId = jobId,
                Actor = audit.Actor,
                PromptChars = prompt.Length,
                InputTokens = completion?.InputTokens,
                OutputTokens = completion?.OutputTokens,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Succeeded = error is null,
                Error = error
            },
            cancellationToken);
    }

    private static async Task<AiCompletion> CallClaudeAsync(
        IHttpClientFactory httpClientFactory,
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        // The SDK builds its own HttpClient unless given one; pass the "ai" client so the same guarded handler applies as for the
        // other providers (no private networks unless Security:OutboundAllowPrivateNetwork, no redirects). A configured base
        // URL (an LLM gateway in front of the API) goes through that same client.
        using var httpClient = httpClientFactory.CreateClient("ai");
        AnthropicClient client = string.IsNullOrWhiteSpace(provider.BaseUrl)
            ? new() { ApiKey = provider.ApiKey, HttpClient = httpClient }
            : new()
            {
                ApiKey = provider.ApiKey,
                HttpClient = httpClient,
                BaseUrl = ResolveBaseUri(provider.BaseUrl, "https://api.anthropic.com").ToString().TrimEnd('/')
            };

        var response = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = provider.Model,
                MaxTokens = MaxTokens,
                System = systemPrompt,
                Thinking = new ThinkingConfigAdaptive(),
                Messages = [new MessageParam { Role = Role.User, Content = userPrompt }]
            },
            cancellationToken);

        if (response.StopReason == "refusal")
        {
            var explanation = response.StopDetails?.Explanation;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(explanation) ? "模型拒絕回答這個請求。" : $"模型拒絕回答：{explanation}");
        }

        var text = string.Join(
            Environment.NewLine,
            response.Content.Select(block => block.Value).OfType<TextBlock>().Select(block => block.Text));

        return new AiCompletion(
            text,
            (int?)response.Usage?.InputTokens,
            (int?)response.Usage?.OutputTokens,
            response.StopReason?.ToString());
    }

    private async Task<AiCompletion> CallGeminiAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(provider.BaseUrl, "https://generativelanguage.googleapis.com");
        var url = new Uri(baseUri, $"v1beta/models/{Uri.EscapeDataString(provider.Model)}:generateContent");

        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            generationConfig = new { maxOutputTokens = MaxTokens }
        };

        using var client = httpClientFactory.CreateClient("ai");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", provider.ApiKey);

        var root = await SendAsync(client, request, cancellationToken);

        var text = new StringBuilder();

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var value))
                    text.Append(value.GetString());

        int? input = null;
        int? output = null;

        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            input = ReadInt(usage, "promptTokenCount");
            output = ReadInt(usage, "candidatesTokenCount");
        }

        return new AiCompletion(text.ToString(), input, output, null);
    }

    private async Task<AiCompletion> CallCodexAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(provider.BaseUrl, "https://api.openai.com");

        var payload = new
        {
            model = provider.Model,
            max_completion_tokens = MaxTokens,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        using var client = httpClientFactory.CreateClient("ai");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        var root = await SendAsync(client, request, cancellationToken);

        var text = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                   choices[0].TryGetProperty("message", out var message) &&
                   message.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;

        int? input = null;
        int? output = null;

        if (root.TryGetProperty("usage", out var usage))
        {
            input = ReadInt(usage, "prompt_tokens");
            output = ReadInt(usage, "completion_tokens");
        }

        return new AiCompletion(text, input, output, null);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await ReadBoundedAsync(response.Content, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{Summarize(body)}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    }

    private static string Summarize(string body)
    {
        return body.Length <= 400 ? body : $"{body[..400]}…";
    }

    private static Uri ResolveBaseUri(string? configured, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        if (candidate.Length > 2048 ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("AI 供應商 Base URL 無效。");

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("AI 供應商回應超過 4 MiB 上限。");

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;

        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("AI 供應商回應超過 4 MiB 上限。");

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static string SanitizeError(string? secret, string message)
    {
        var result = SecretRedactor.Redact(message, secret);
        result = new string(result.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return result.Length <= 1000 ? result : $"{result[..1000]}…";
    }
}