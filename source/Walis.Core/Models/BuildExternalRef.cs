using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Walis.Core.Models;

/// <summary>
///     An idempotency reference supplied by an external caller (body <c>externalRef</c> or the <c>Idempotency-Key</c> header),
///     together with the hash of what was triggered so a replay with different content can be told apart from a true retry.
/// </summary>
public sealed record BuildExternalReference(string Value, string TriggerHash);

public enum BuildQueueOutcome
{
    Queued,

    /// <summary>The job already has builds for this reference with the same trigger content; nothing new was created.</summary>
    Deduplicated,

    /// <summary>The job already has builds for this reference but they were triggered with different content.</summary>
    ExternalRefConflict,

    CapacityExceeded,

    JobNotFound
}

public sealed record BuildQueueResult(BuildQueueOutcome Outcome, IReadOnlyList<Build> Builds)
{
    public static BuildQueueResult Of(BuildQueueOutcome outcome)
    {
        return new BuildQueueResult(outcome, []);
    }
}

public static class BuildExternalRefs
{
    public const int MaximumLength = 200;

    /// <summary>The header AgentGate's REST adapter sends; treated exactly like the body's externalRef.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Domain separation, so this hash can never be confused with any other SHA-256 Walis stores.</summary>
    private const string HashDomain = "walis.build-trigger.v1\n";

    public static bool IsValid(string? value)
    {
        return value is { Length: > 0 and <= MaximumLength } &&
               !string.IsNullOrWhiteSpace(value) &&
               !value.Any(char.IsControl);
    }

    /// <summary>
    ///     Hashes the parameters exactly as the caller sent them (not after defaults are filled in): keys sorted ordinally,
    ///     null and an empty set are the same. Changing the job's declared defaults later therefore does not turn a retry into a conflict.
    /// </summary>
    private const string SecretPlaceholder = "\u0000walis:secret\u0000";

    /// <param name="secretNames">
    ///     Names of password-type parameters. Their values never enter the hash (a fixed placeholder does): the hash is stored
    ///     unsalted next to the encrypted values, and a low-entropy password could be recovered from it offline. A retry that
    ///     changes only a password value is therefore treated as the same request.
    /// </param>
    public static string ComputeTriggerHash(
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlySet<string>? secretNames = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("parameters");
            writer.WriteStartObject();

            // Values can still be null at runtime when JSON sends "name": null.
            foreach (var (name, value) in (parameters ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if ((string?)value is null)
                    writer.WriteNull(name);
                else if (secretNames?.Contains(name) == true)
                    writer.WriteString(name, SecretPlaceholder);
                else
                    writer.WriteString(name, value);

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var domain = Encoding.UTF8.GetBytes(HashDomain);
        var payload = new byte[domain.Length + buffer.Length];
        domain.CopyTo(payload, 0);
        buffer.ToArray().CopyTo(payload, domain.Length);

        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}
