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

    private const int SettingsSlot0 = SlotBytes;
    private const int SettingsSlot1 = 2 * SlotBytes;
    private const uint DefaultTargetLinux = 2;

    private static uint Crc32(ReadOnlySpan<byte> data) => InternalInstallArtifacts.Crc32(data);

    private static bool IsZero(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s) if (b != 0) return false;
        return true;
    }

    private static ulong GetU64(ReadOnlySpan<byte> s, int off) =>
        BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(off, 8));
    private static uint GetU32(ReadOnlySpan<byte> s, int off) =>
        BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4));
    private static void PutU32(Span<byte> s, int off, uint v) =>
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(off, 4), v);
    private static void PutU64(Span<byte> s, int off, ulong v) =>
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(off, 8), v);

    private enum SlotState { Erased, Valid, Malformed }

    private readonly record struct SlotInfo(
        SlotState State,
        ulong Sequence,
        uint DeviceChoice,
        uint DefaultTarget,
        uint Language,
        uint LastTarget,
        uint NextAction)
    {
        public static SlotInfo Erased => new(SlotState.Erased, 0, 0, 0, 0, 0, 0);
        public static SlotInfo Malformed => new(SlotState.Malformed, 0, 0, 0, 0, 0, 0);
    }

    private static bool MagicMatches(ReadOnlySpan<byte> p)
    {
        ReadOnlySpan<byte> magic = stackalloc byte[] { (byte)'K',(byte)'S',(byte)'C',(byte)'F',(byte)'G',(byte)'R',(byte)'1', 0 };
        return p.Slice(0, 8).SequenceEqual(magic);
    }

    private static SlotInfo ParseSlot(ReadOnlySpan<byte> p, Guid partition, Guid installation)
    {
        if (IsZero(p)) return SlotInfo.Erased;
        if (!MagicMatches(p)) return SlotInfo.Malformed;

        uint version = GetU32(p, 8);
        if (version < 1 || version > 3) return SlotInfo.Malformed;
        if (GetU32(p, 12) != SlotBytes) return SlotInfo.Malformed;

        ulong seq = GetU64(p, 16);
        if (seq == 0) return SlotInfo.Malformed;

        if (!p.Slice(24, 16).SequenceEqual(partition.ToByteArray(bigEndian: true)))
            return SlotInfo.Malformed;
        if (!p.Slice(40, 16).SequenceEqual(installation.ToByteArray(bigEndian: true)))
            return SlotInfo.Malformed;

        uint dev = GetU32(p, 56);
        uint def = GetU32(p, 60);
        uint lang = GetU32(p, 64);
        uint last = GetU32(p, 68);
        uint next = GetU32(p, 72);

        if (dev > 2) return SlotInfo.Malformed;
        uint maxTarget = version == 1 ? 3u : 4u;
        if (def > maxTarget) return SlotInfo.Malformed;
        if (lang > 1) return SlotInfo.Malformed;
        if (version >= 2 && last > 3) return SlotInfo.Malformed;
        if (version == 3 && next > 1) return SlotInfo.Malformed;

        int reservedFrom = version == 1 ? 68 : version == 2 ? 72 : 76;
        int reservedLen = version == 1 ? 4024 : version == 2 ? 4020 : 4016;
        if (!IsZero(p.Slice(reservedFrom, reservedLen)))
            return SlotInfo.Malformed;

        if (Crc32(p.Slice(0, 4092)) != GetU32(p, 4092))
            return SlotInfo.Malformed;

        uint lastOut = version >= 2 ? last : 0;
        uint nextOut = version == 3 ? next : 0;
        return new SlotInfo(SlotState.Valid, seq, dev, def, lang, lastOut, nextOut);
    }

    private static byte[] BuildRecord(uint version, ulong seq, Guid partition, Guid installation, uint dev, uint def, uint lang, uint last, uint next)
    {
        var r = new byte[SlotBytes];
        ReadOnlySpan<byte> magic = stackalloc byte[] { (byte)'K',(byte)'S',(byte)'C',(byte)'F',(byte)'G',(byte)'R',(byte)'1', 0 };
        magic.CopyTo(r);
        PutU32(r, 8, version);
        PutU32(r, 12, SlotBytes);
        PutU64(r, 16, seq);
        partition.ToByteArray(bigEndian: true).CopyTo(r, 24);
        installation.ToByteArray(bigEndian: true).CopyTo(r, 40);
        PutU32(r, 56, dev);
        PutU32(r, 60, def);
        PutU32(r, 64, lang);
        PutU32(r, 68, last);
        PutU32(r, 72, next);
        PutU32(r, 4092, Crc32(r.AsSpan(0, 4092)));
        return r;
    }

    /// <summary>
    /// Project a 64 KiB tail whose settings slots pin the default Linux target (2).
    /// No device IO: this is a pure function of the captured region.
    /// </summary>
    internal static byte[] PrepareLinuxDefault(byte[] region, Guid partition, Guid installation, string model, long payloadEnd)
    {
        if (region is null) throw new ArgumentNullException(nameof(region));
        VerifyRegion(region, partition, installation, model, payloadEnd);

        if (partition == Guid.Empty || installation == Guid.Empty)
            throw new InvalidDataException("Launcher settings have no installation identity.");

        var output = (byte[])region.Clone();

        uint descVersion = GetU32(output, 8);
        if (descVersion is 1 or 2)
        {
            var upgraded = CreateTrialDescriptor(partition, installation, model, payloadEnd);
            upgraded.CopyTo(output, 0);
        }
        else if (descVersion != 3)
        {
            throw new InvalidDataException("The launcher settings descriptor version is not supported.");
        }

        var s0 = ParseSlot(output.AsSpan(SettingsSlot0, SlotBytes), partition, installation);
        var s1 = ParseSlot(output.AsSpan(SettingsSlot1, SlotBytes), partition, installation);

        SlotInfo winner;
        int targetIndex;

        if (s0.State == SlotState.Valid && s1.State == SlotState.Valid)
        {
            if (s0.Sequence == s1.Sequence)
            {
                if (!output.AsSpan(SettingsSlot0, SlotBytes).SequenceEqual(output.AsSpan(SettingsSlot1, SlotBytes)))
                    throw new InvalidDataException("Settings slots disagree at equal sequence.");
                winner = s0;
            }
            else if (s0.Sequence > s1.Sequence)
            {
                winner = s0;
            }
            else
            {
                winner = s1;
            }

            bool s0IsWinner = winner.Sequence == s0.Sequence && s0.State == SlotState.Valid;
            targetIndex = s0IsWinner ? 1 : 0;

        }
        else if (s0.State == SlotState.Valid)
        {
            winner = s0;
            targetIndex = 1;
        }
        else if (s1.State == SlotState.Valid)
        {
            winner = s1;
            targetIndex = 0;
        }
        else if (s0.State == SlotState.Erased && s1.State == SlotState.Erased)
        {
            winner = new SlotInfo(SlotState.Erased, 0, 0, DefaultTargetLinux, 0, 0, 0);
            targetIndex = 0;
        }
        else
        {
            throw new InvalidDataException("Settings slots are unavailable.");
        }

        if (winner.State == SlotState.Valid && winner.NextAction != 0)
            throw new InvalidDataException("A pending boot action blocks the default preparation.");

        if (winner.State == SlotState.Valid && winner.DefaultTarget == DefaultTargetLinux)
            return output;

        if (winner.Sequence == ulong.MaxValue)
            throw new InvalidDataException("Settings sequence exhausted.");
        ulong nextSeq = winner.Sequence + 1;

        uint dev = winner.State == SlotState.Valid ? winner.DeviceChoice : 0;
        uint lang = winner.State == SlotState.Valid ? winner.Language : 0;
        uint last = winner.State == SlotState.Valid ? winner.LastTarget : 0;
        var record = BuildRecord(3, nextSeq, partition, installation, dev, DefaultTargetLinux, lang, last, 0);
        int targetOff = targetIndex == 0 ? SettingsSlot0 : SettingsSlot1;
        record.CopyTo(output, targetOff);

        VerifyRegion(output, partition, installation, model, payloadEnd);
        return output;
    }
}
