using System.Text;
using Walis.Core.Backup;

namespace Walis.Api.Services;

/// <summary>
///     Hands pg_dump its password through a private <c>PGPASSFILE</c> instead of <c>PGPASSWORD</c>: the environment of a running
///     process is readable by other processes of the same account on some platforms, a 0600 file is not. libpq ignores a
///     password file that group or others can read, so the file is created with the private mode from the start.
/// </summary>
public static class PgPassFile
{
    /// <summary>One line matching every host, port, database and user; <c>\</c> and <c>:</c> are escaped as libpq requires.</summary>
    public static string Line(string password)
    {
        var escaped = new StringBuilder(password.Length + 8);

        foreach (var character in password)
        {
            if (character is '\\' or ':') escaped.Append('\\');

            escaped.Append(character);
        }

        return $"*:*:*:*:{escaped}";
    }

    /// <summary>Writes the file into <paramref name="privateDirectory" /> (already 0700) and returns its path.</summary>
    public static string Create(string privateDirectory, string password)
    {
        if (password.Any(character => character is '\n' or '\r' or '\0'))
            throw new InvalidOperationException("PostgreSQL 密碼不可包含換行或 NUL 字元。");

        var path = Path.Combine(privateDirectory, $".pgpass-{Guid.NewGuid():N}");

        using var stream = PrivateFileSystem.CreatePrivateFile(path, FileMode.CreateNew, FileOptions.None);
        stream.Write(Encoding.UTF8.GetBytes(Line(password) + "\n"));

        return path;
    }
}
