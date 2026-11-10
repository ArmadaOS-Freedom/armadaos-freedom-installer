using System.Buffers.Binary;

namespace FreedomInstaller.Core;

/// <summary>The explicit mutable tail of a v3 ARMADA_BACKUP image.
/// UUID bytes are RFC 4122 order, matching kShimC after GPT UUID conversion.
/// Recovery validates the immutable descriptor and reserved bytes. Settings
/// slots, v2 diagnostic log slots, and v3 trial slots remain independent of
/// recovery validation, including interrupted writes.</summary>
internal static class BootSettingsRegion
{
    internal const int Bytes = 65536;
    internal const int SlotBytes = 4096;
    internal const long Offset = (2L << 30) - Bytes;
    internal const string Format = "armada-gzip-ustar-v3";
    internal static uint BoardId(string model) => model switch
    {
        "TB322FC" => 1,
        "TB323FU" => 2,
        // Free value in kShimC's device_board enum; kShimC without TB710FU board support treats it as
        // unknown and boots the manifest defaults.
        "TB710FU" => 3,
        _ => throw new InvalidDataException("No launcher settings profile for this model.")
    };

    internal static byte[] CreateDescriptor(Guid partition, Guid installation, string model, long payloadEnd)
    {
        if (partition == Guid.Empty || installation == Guid.Empty || payloadEnd < 65536 || payloadEnd > Offset)
            throw new InvalidDataException("Launcher settings overlap the archive or have no installation identity.");
        uint board = BoardId(model);
        var d = new byte[SlotBytes];
        "KSCFGV3\0"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), SlotBytes);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), 2L << 30);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), payloadEnd);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(32), Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(40), Bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(44), SlotBytes);
        partition.ToByteArray(bigEndian: true).CopyTo(d, 48);
        installation.ToByteArray(bigEndian: true).CopyTo(d, 64);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(80), SlotBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(84), 2 * SlotBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(88), board);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4092),
            InternalInstallArtifacts.Crc32(d.AsSpan(0, 4092)));
        return d;
    }

    internal static byte[] CreateTrialDescriptor(Guid partition, Guid installation, string model, long payloadEnd)
    {
        var d = CreateDescriptor(partition, installation, model, payloadEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(92), 28672);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(96), 32768);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(100), SlotBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4092),
            InternalInstallArtifacts.Crc32(d.AsSpan(0, 4092)));
        return d;
    }

    internal static void VerifyRegion(byte[] region, Guid partition, Guid installation, string model, long payloadEnd)
    {
        if (region.Length != Bytes)
            throw new InvalidDataException("The launcher settings descriptor does not match this archive.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(8));
        if (version is not (1 or 2 or 3))
            throw new InvalidDataException("The launcher settings descriptor version is not supported.");
        byte[] expected;
        if (version == 3)
        {
            expected = CreateTrialDescriptor(partition, installation, model, payloadEnd);
        }
        else
        {
            expected = CreateDescriptor(partition, installation, model, payloadEnd);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8), version);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(4092),
                InternalInstallArtifacts.Crc32(expected.AsSpan(0, 4092)));
        }
        if (!region.AsSpan(0, SlotBytes).SequenceEqual(expected))
            throw new InvalidDataException("The launcher settings descriptor does not match this archive.");
        // kShimC v2 assigns 12288..28672 to two mutable 8 KiB diagnostic log slots;
        // v3 adds two mutable 4 KiB trial slots at 28672..36864.
        int reservedFrom = version switch
        {
            1 => 3 * SlotBytes,
            2 => 28672,
            _ => 36864
        };
        foreach (byte value in region.AsSpan(reservedFrom))
            if (value != 0)
                throw new InvalidDataException("The reserved launcher settings tail is not zero filled.");
    }
}
