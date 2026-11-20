using System;
using System.Buffers.Binary;

namespace FreedomInstaller.Core;

internal static class DiskLinuxEntry
{
    private static ReadOnlySpan<byte> Magic => "ARMDSK1\0"u8;

    internal static byte[] Encode(Guid bootFileSystemUuid, Guid rootFileSystemUuid, bool diagnostic)
    {
        if (bootFileSystemUuid == Guid.Empty)
        {
            throw new ArgumentException("Boot filesystem UUID must not be empty.", nameof(bootFileSystemUuid));
        }

        if (rootFileSystemUuid == Guid.Empty)
        {
            throw new ArgumentException("Root filesystem UUID must not be empty.", nameof(rootFileSystemUuid));
        }

        byte[] result = new byte[64];

        Magic.CopyTo(result.AsSpan(0, Magic.Length));

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), diagnostic ? 1u : 0u);

        bootFileSystemUuid.ToByteArray(bigEndian: true).CopyTo(result, 16);
        rootFileSystemUuid.ToByteArray(bigEndian: true).CopyTo(result, 32);

        // bytes 48..63 remain zero (reserved)

        return result;
    }

    internal static bool IsValid(ReadOnlySpan<byte> entry)
    {
        if (entry.Length != 64)
        {
            return false;
        }

        if (!entry.Slice(0, 8).SequenceEqual(Magic))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4)) != 1u)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)) > 1u)
        {
            return false;
        }

        if (new Guid(entry.Slice(16, 16), bigEndian: true) == Guid.Empty)
        {
            return false;
        }

        if (new Guid(entry.Slice(32, 16), bigEndian: true) == Guid.Empty)
        {
            return false;
        }

        if (entry.Slice(48, 16).IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        return true;
    }
}
