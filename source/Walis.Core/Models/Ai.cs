namespace Walis.Core.Models;

public static class AiProviderKind
{
    public const string Gemini = "gemini";
    public const string Claude = "claude";
    public const string Codex = "codex";

    public static bool IsValid(string? value)
    {
        return value is Gemini or Claude or Codex;
    }

    /// <summary>Each provider's default model; users can change it on the settings page to a model they have access to.</summary>
    public static string DefaultModel(string kind)
    {
        return kind switch
        {
            Gemini => "gemini-2.5-pro",
            Claude => "claude-opus-5",
            Codex => "gpt-5-codex",
            _ => string.Empty
        };
    }

    public static string DisplayName(string kind)
    {
        return kind switch
        {
            Gemini => "Gemini",
            Claude => "Claude",
            Codex => "Codex",
            _ => kind
        };
    }
}

public static class AiPurpose
{
    public const string AnalyzeBuild = "analyze-build";
    public const string ReviewScript = "review-script";
    public const string GeneratePlugin = "generate-plugin";
    public const string Test = "test";
}

public sealed class AiProvider
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    public string Model { get; init; } = string.Empty;
    public string? BaseUrl { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsDefault { get; init; }
    public DateTime UpdatedAt { get; init; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool IsUsable => IsEnabled && HasApiKey && !string.IsNullOrWhiteSpace(Model);
}

public sealed record AiProviderDraft(
    string? ApiKey,
    string? Model,
    string? BaseUrl,
    bool IsEnabled,
    bool IsDefault);

public sealed class AiRequestLogEntry
{
    public Guid Id { get; init; }
    public DateTime StartedAt { get; init; }
    public string ProviderKind { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public Guid? BuildId { get; init; }
    public Guid? JobId { get; init; }
    public string Actor { get; init; } = "system";
    public int PromptChars { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int DurationMs { get; init; }
    public bool Succeeded { get; init; }
    public string? Error { get; init; }

    /// <summary>The job name filled in at query time, so it is recognizable on screen.</summary>
    public string? JobName { get; set; }
}

public sealed record AiCompletion(string Text, int? InputTokens, int? OutputTokens, string? StopReason);