using System.Security.Cryptography;
using System.Text;
using Walis.Core.Backup;

namespace Walis.Api.Services;

/// <summary>
///     The one-time code that lets the first administrator be created from another machine. A fresh install is reachable over
///     the network before anyone owns it, so "whoever opens the page first" must not be enough: the code is printed on the
///     server console and saved in the data directory, both of which only the person who installed Walis can read.
///     It exists only until the first administrator is created. Each node keeps its own code; a multi-node deployment sets
///     Security:BootstrapToken instead, which every node shares.
/// </summary>
public sealed class SetupCode(DataDirectory data)
{
    public const string FileName = "setup-code.txt";

    // No I, L, O, U, 0 or 1: the code is read off a terminal and typed into a browser on another machine.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    private const int GroupCount = 4;
    private const int GroupLength = 5;
    private const int MaximumPresentedLength = 256;

    private readonly Lock _gate = new();
    private string? _code;

    public string FilePath => data.Combine(FileName);

    /// <summary>The code in force, or null when none was issued or the first administrator already exists.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                return _code;
            }
        }
    }

    /// <summary>
    ///     Issues the code for this start. A code left by an earlier start is reused, so restarting an install nobody has claimed
    ///     yet does not invalidate what the operator already copied.
    /// </summary>
    public string EnsureCreated()
    {
        lock (_gate)
        {
            if (_code is not null) return _code;

            _code = ReadExisting() ?? Generate();
            TryWrite(_code);
            return _code;
        }
    }

    /// <summary>Accepts the code with or without separators, in either case; compared in constant time.</summary>
    public bool Matches(string? presented)
    {
        var expected = Current;

        if (expected is null || string.IsNullOrWhiteSpace(presented) || presented.Length > MaximumPresentedLength)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(expected))),
            SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(presented))));
    }

    /// <summary>Retires the code once it has served its purpose, and removes the copy on disk.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _code = null;

            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover file is harmless: its code is no longer accepted once an administrator exists.
            }
        }
    }

    private static string Generate()
    {
        var builder = new StringBuilder(GroupCount * (GroupLength + 1));

        for (var group = 0; group < GroupCount; group++)
        {
            if (group != 0) builder.Append('-');

            for (var index = 0; index < GroupLength; index++)
                builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
            if (!char.IsWhiteSpace(character) && character != '-')
                builder.Append(char.ToUpperInvariant(character));

        return builder.ToString();
    }

    private string? ReadExisting()
    {
        try
        {
            var file = new FileInfo(FilePath);

            if (!file.Exists || file.Length > MaximumPresentedLength ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            var normalized = Normalize(File.ReadAllText(FilePath));

            if (normalized.Length != GroupCount * GroupLength || normalized.Any(character => !Alphabet.Contains(character)))
                return null;

            return string.Join('-', normalized.Chunk(GroupLength).Select(group => new string(group)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryWrite(string code)
    {
        // Written beside the target and moved into place, so a reader never sees half a code and a link left at the
        // target path is replaced rather than followed.
        var staging = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = PrivateFileSystem.CreatePrivateFile(staging, FileMode.CreateNew, FileOptions.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(code + Environment.NewLine));
            }

            File.Move(staging, FilePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The console still shows the code; a read-only data directory must not stop the server from starting.
            try
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
