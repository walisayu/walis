using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Walis.Core.Backup;

/// <summary>
///     Versioned, streaming authenticated encryption for backup archives.
///     Version 1 uses PBKDF2-SHA256 and independently authenticated AES-256-GCM
///     chunks. The authenticated zero-length final record makes truncation
///     detectable without buffering a potentially very large backup in memory.
///     Legacy OpenSSL <c>Salted__</c> CBC files remain decryptable for offline
///     migration, but callers can distinguish them with <see cref="GetFormatAsync" />.
/// </summary>
public static class BackupCipher
{
    public enum BackupEncryptionFormat
    {
        None,
        LegacyUnauthenticated,
        AuthenticatedV1
    }

    public const int Iterations = 600_000;

    /// <summary>Four times what this version writes: room for a future increase, not for a denial of service on restore.</summary>
    public const int MaximumAcceptedIterations = Iterations * 4;
    public const int ChunkSize = 1024 * 1024;
    public const long MaximumAuthenticatedPlaintextBytes = 10L * 1024 * 1024 * 1024;

    private const byte Version = 1;
    private const byte KdfPbkdf2Sha256 = 1;
    private const int SaltSize = 16;
    private const int NoncePrefixSize = 8;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 8 + 1 + 1 + 4 + 4 + SaltSize + NoncePrefixSize;
    private const int MaximumAcceptedChunkSize = 16 * 1024 * 1024;
    private const int MaximumAcceptedChunks = 20_000;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WALISBKP");
    private static readonly byte[] LegacyMagic = Encoding.ASCII.GetBytes("Salted__");

    public static async Task EncryptAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        var temporaryPath = CreateTemporaryPath(outputPath);

        try
        {
            await EncryptCoreAsync(inputPath, temporaryPath, passphrase, cancellationToken);
            File.Move(temporaryPath, outputPath, true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>
    ///     Decrypts an authenticated backup, or a legacy CBC backup for migration.
    ///     Output is committed atomically only after the entire input validates.
    /// </summary>
    public static async Task DecryptAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        await DecryptCoreAsync(
            inputPath,
            outputPath,
            passphrase,
            true,
            MaximumAuthenticatedPlaintextBytes,
            cancellationToken);
    }

    /// <summary>Decrypts only the authenticated current format.</summary>
    public static async Task DecryptAuthenticatedBackupAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        await DecryptAuthenticatedBackupAsync(
            inputPath,
            outputPath,
            passphrase,
            MaximumAuthenticatedPlaintextBytes,
            cancellationToken);
    }

    /// <summary>Authenticated decryption with a caller-selected lower output ceiling.</summary>
    public static async Task DecryptAuthenticatedBackupAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        long maximumPlaintextBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumPlaintextBytes <= 0 || maximumPlaintextBytes > MaximumAuthenticatedPlaintextBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextBytes));

        await DecryptCoreAsync(
            inputPath,
            outputPath,
            passphrase,
            false,
            maximumPlaintextBytes,
            cancellationToken);
    }

    private static async Task DecryptCoreAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        bool allowLegacy,
        long maximumAuthenticatedPlaintextBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        var temporaryPath = CreateTemporaryPath(outputPath);

        try
        {
            await using var input = OpenRead(inputPath);
            var prefix = new byte[Magic.Length];
            await input.ReadExactlyAsync(prefix, cancellationToken);

            await using (var output = OpenNew(temporaryPath))
            {
                if (prefix.AsSpan().SequenceEqual(Magic))
                    await DecryptAuthenticatedChunksAsync(
                        input,
                        output,
                        prefix,
                        passphrase,
                        maximumAuthenticatedPlaintextBytes,
                        cancellationToken);
                else if (allowLegacy && prefix.AsSpan().SequenceEqual(LegacyMagic))
                    await DecryptLegacyAsync(input, output, passphrase, cancellationToken);
                else
                    throw new BackupTargetException("這個檔案不是 Walis 產生的加密備份。");

                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, outputPath, true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static async Task<BackupEncryptionFormat> GetFormatAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = OpenRead(path);
        var prefix = new byte[Magic.Length];
        var read = 0;

        while (read < prefix.Length)
        {
            var count = await stream.ReadAsync(prefix.AsMemory(read), cancellationToken);

            if (count == 0) return BackupEncryptionFormat.None;

            read += count;
        }

        if (prefix.AsSpan().SequenceEqual(Magic)) return BackupEncryptionFormat.AuthenticatedV1;

        return prefix.AsSpan().SequenceEqual(LegacyMagic)
            ? BackupEncryptionFormat.LegacyUnauthenticated
            : BackupEncryptionFormat.None;
    }

    private static async Task EncryptCoreAsync(
        string inputPath,
        string outputPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var header = CreateHeader(salt, noncePrefix);
        var key = DeriveKey(passphrase, salt, Iterations);
        var plain = new byte[ChunkSize];

        try
        {
            await using var input = OpenRead(inputPath);
            await using var output = OpenNew(outputPath);
            await output.WriteAsync(header, cancellationToken);

            using var aes = new AesGcm(key, TagSize);
            uint counter = 0;

            while (true)
            {
                var length = await input.ReadAsync(plain, cancellationToken);
                var cipher = new byte[length];
                var tag = new byte[TagSize];
                var nonce = CreateNonce(noncePrefix, counter);
                var associatedData = CreateAssociatedData(header, counter, length);

                aes.Encrypt(nonce, plain.AsSpan(0, length), cipher, tag, associatedData);
                await WriteUInt32Async(output, checked((uint)length), cancellationToken);
                await output.WriteAsync(cipher, cancellationToken);
                await output.WriteAsync(tag, cancellationToken);

                if (length == 0) break;

                if (counter == uint.MaxValue) throw new IOException("備份過大，無法在不重複 nonce 的情況下繼續加密。");

                counter++;
            }

            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task DecryptAuthenticatedChunksAsync(
        Stream input,
        Stream output,
        byte[] prefix,
        string passphrase,
        long maximumPlaintextBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        prefix.CopyTo(header, 0);
        await input.ReadExactlyAsync(header.AsMemory(prefix.Length), cancellationToken);

        if (header[8] != Version || header[9] != KdfPbkdf2Sha256) throw new BackupTargetException("這份備份使用尚未支援的加密格式。");

        var iterations = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(10, 4)));
        var chunkSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14, 4)));

        // The header is read before the passphrase is checked, so a crafted file must not be able to demand minutes of KDF work.
        if (iterations is < 100_000 or > MaximumAcceptedIterations || chunkSize is < 4096 or > MaximumAcceptedChunkSize)
            throw new BackupTargetException("備份的加密參數不安全或超出支援範圍。");

        var salt = header.AsSpan(18, SaltSize).ToArray();
        var noncePrefix = header.AsSpan(18 + SaltSize, NoncePrefixSize).ToArray();
        var key = DeriveKey(passphrase, salt, iterations);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            uint counter = 0;
            var chunks = 0;
            long plaintextBytes = 0;

            while (true)
            {
                if (++chunks > MaximumAcceptedChunks) throw new InvalidDataException("備份包含過多加密區塊。");

                var length = checked((int)await ReadUInt32Async(input, cancellationToken));

                if (length > chunkSize) throw new InvalidDataException("備份包含無效的加密區塊長度。");

                if (length > maximumPlaintextBytes - plaintextBytes)
                    throw new InvalidDataException(
                        $"解密後的備份超過 {maximumPlaintextBytes} bytes 的上限。");

                var cipher = new byte[length];
                var tag = new byte[TagSize];
                await input.ReadExactlyAsync(cipher, cancellationToken);
                await input.ReadExactlyAsync(tag, cancellationToken);

                var plain = new byte[length];
                var nonce = CreateNonce(noncePrefix, counter);
                var associatedData = CreateAssociatedData(header, counter, length);
                try
                {
                    aes.Decrypt(nonce, cipher, tag, plain, associatedData);

                    if (length == 0)
                    {
                        if (await input.ReadAsync(new byte[1], cancellationToken) != 0)
                            throw new InvalidDataException("備份的認證結尾後還有多餘資料。");

                        break;
                    }

                    await output.WriteAsync(plain, cancellationToken);
                    plaintextBytes += length;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }

                if (counter == uint.MaxValue) throw new InvalidDataException("備份包含過多加密區塊。");

                counter++;
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new CryptographicException("備份遭截斷，認證失敗。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task DecryptLegacyAsync(
        Stream input,
        Stream output,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var salt = new byte[8];
        await input.ReadExactlyAsync(salt, cancellationToken);
        var password = Encoding.UTF8.GetBytes(passphrase);
        byte[] material;

        try
        {
            material = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, 48);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }

        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = material[..32];
            aes.IV = material[32..48];

            await using var transform = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            await transform.CopyToAsync(output, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] CreateHeader(byte[] salt, byte[] noncePrefix)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[8] = Version;
        header[9] = KdfPbkdf2Sha256;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10, 4), Iterations);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14, 4), ChunkSize);
        salt.CopyTo(header, 18);
        noncePrefix.CopyTo(header, 18 + SaltSize);
        return header;
    }

    private static byte[] CreateNonce(byte[] prefix, uint counter)
    {
        var nonce = new byte[NonceSize];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), counter);
        return nonce;
    }

    private static byte[] CreateAssociatedData(byte[] header, uint counter, int length)
    {
        var result = new byte[header.Length + 8];
        header.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(header.Length, 4), counter);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(header.Length + 4, 4), checked((uint)length));
        return result;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        var password = Encoding.UTF8.GetBytes(passphrase);

        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static async Task WriteUInt32Async(Stream stream, uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static FileStream OpenNew(string path)
    {
        return PrivateFileSystem.CreatePrivateFile(path);
    }

    private static string CreateTemporaryPath(string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var name = Path.GetFileName(fullPath);
        return Path.Combine(directory, $".{name}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup; preserve the original crypto error.
        }
    }
}