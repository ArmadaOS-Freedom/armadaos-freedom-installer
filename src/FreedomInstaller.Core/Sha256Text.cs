using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace FreedomInstaller.Core;

/// <summary>
/// The one place for the SHA-256 text rules this installer shares: the lowercase 64-hex digest of some
/// bytes, the exact shape a digest string must have, and the case-insensitive comparison of two digest
/// strings.
/// <para>
/// Stateless and internal: it reads no environment, creates no file and keeps no buffer after returning.
/// <see cref="Hex(ReadOnlySpan{byte})"/> and <see cref="HexOfStream"/> always produce lowercase text; a
/// caller that must keep its own uppercase output applies <c>ToUpperInvariant</c> at that boundary.
/// </para>
/// </summary>
internal static class Sha256Text
{
    /// <summary>Exactly 64 ASCII hex characters: the only accepted digest text length.</summary>
    internal const int HexLength = 64;

    private const int BufferSize = 81920;

    /// <summary>SHA-256 of <paramref name="data"/>, lowercase 64-hex.</summary>
    internal static string Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// SHA-256 of the bytes from the stream's current position to its end, lowercase 64-hex, computed
    /// incrementally with the BCL hash. The stream stays open, is left at its end and stays owned by the
    /// caller. Cancellation is observed before the first read and after every chunk, so a token cancelled
    /// during the first read stops before the end of the stream.
    /// </summary>
    internal static string HexOfStream(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = stream.Read(buffer, 0, BufferSize);
                if (read == 0)
                {
                    break;
                }

                // A token cancelled by that read itself stops here, before this chunk is hashed.
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// True only for exactly 64 ASCII hex digits. With <paramref name="lowercaseOnly"/> the uppercase
    /// A-F digits are rejected too. Null and every other length are false; the text is never trimmed.
    /// </summary>
    internal static bool IsHex(string? value, bool lowercaseOnly = false)
    {
        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isHex = lowercaseOnly
                ? character is >= '0' and <= '9' or >= 'a' and <= 'f'
                : character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Ordinal, case-insensitive comparison of two digest strings. The shape is not validated: null equals
    /// null, empty equals empty, and any other pair only compares its text.
    /// </summary>
    internal static bool EqualsHex(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
