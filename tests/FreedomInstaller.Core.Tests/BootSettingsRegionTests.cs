using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class BootSettingsRegionTests
{
    private static readonly Guid Partition = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Installation = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private const long Offset = (2L << 30) - 65536;
    private static object? Call(string name, params object[] args)
    {
        var type = typeof(InternalInstallArtifacts).Assembly.GetType("FreedomInstaller.Core.BootSettingsRegion");
        Assert.NotNull(type);
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static byte[] Descriptor(string model = "TB322FC", long end = 1048576) =>
        (byte[])Call("CreateDescriptor", Partition, Installation, model, end)!;
    private static byte[] TrialDescriptor(string model = "TB322FC", long end = 1048576) =>
        (byte[])Call("CreateTrialDescriptor", Partition, Installation, model, end)!;
    private static void Verify(byte[] region, string model = "TB322FC", long end = 1048576) =>
        Call("VerifyRegion", region, Partition, Installation, model, end);

    [Theory]
    [InlineData("TB322FC", 1u)]
    [InlineData("TB323FU", 2u)]
    [InlineData("TB710FU", 3u)]
    public void Descriptor_matches_the_bootloader_wire_format(string model, uint board)
    {
        var d = Descriptor(model);
        Assert.Equal(4096, d.Length);
        Assert.Equal("KSCFGV3\0"u8.ToArray(), d[..8]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12)));
        Assert.Equal(2L << 30, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(16)));
        Assert.Equal(1048576, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(24)));
        Assert.Equal(Offset, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(32)));
        Assert.Equal(65536u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(40)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(44)));
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), d[48..64]);
        Assert.Equal(Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"), d[64..80]);
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80)));
        Assert.Equal(8192u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(84)));
        Assert.Equal(board, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(88)));
        Assert.All(d[92..4092], b => Assert.Equal((byte)0, b));
        uint crc = uint.MaxValue;
        foreach (byte b in d[..4092]) {
            crc ^= b;
            for (int i=0; i<8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        Assert.Equal(~crc, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4092)));
        var export = Environment.GetEnvironmentVariable("KSHIM_CONFIG_FIXTURE");
        if (model == "TB322FC" && !string.IsNullOrEmpty(export))
            File.WriteAllBytes(export, d);
    }

    [Fact]
    public void Settings_writes_or_torn_slots_do_not_invalidate_recovery()
    {
        var region = new byte[65536];
        Descriptor().CopyTo(region, 0);
        Verify(region);
        Array.Fill(region, (byte)0xa5, 4096, 8192);
        Verify(region);
        region[12288] = 1;
        Assert.Throws<InvalidDataException>(() => Verify(region));
    }

    [Fact]
    public void Descriptor_identity_bounds_and_crc_are_bound_to_the_archive()
    {
        var region = new byte[65536];
        Descriptor().CopyTo(region, 0);
        Assert.Throws<InvalidDataException>(() => Verify(region, "TB323FU"));
        Assert.Throws<InvalidDataException>(() => Verify(region, end: 1048577));
        region[48] ^= 1;
        Assert.Throws<InvalidDataException>(() => Verify(region));
        Descriptor().CopyTo(region, 0);
        region[4092] ^= 1;
        Assert.Throws<InvalidDataException>(() => Verify(region));
        Assert.Throws<InvalidDataException>(() => Verify(new byte[65536]));
        Assert.Throws<InvalidDataException>(() => Verify(new byte[4096]));
    }

    private static void RewriteDescriptorCrc(byte[] region)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in region.AsSpan(0, 4092)) {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(4092), ~crc);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    public void Version2_settings_and_diagnostic_log_writes_do_not_invalidate_recovery(string model)
    {
        var region = new byte[65536];
        Descriptor(model).CopyTo(region, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(8), 2);
        RewriteDescriptorCrc(region);
        Verify(region, model);
        // Two settings slots followed by two 8 KiB log slots may be partially written.
        Array.Fill(region, (byte)0xa5, 4096, 28672 - 4096);
        Verify(region, model);
        region[28672] = 1;
        Assert.Throws<InvalidDataException>(() => Verify(region, model));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(12, 1)]
    [InlineData(24, 1)]
    [InlineData(32, 1)]
    [InlineData(48, 1)]
    [InlineData(64, 1)]
    [InlineData(80, 1)]
    [InlineData(88, 1)]
    [InlineData(92, 1)]
    public void Version2_rejects_unknown_format_and_descriptor_changes_even_with_valid_crc(int offset, byte value)
    {
        var region = new byte[65536];
        Descriptor().CopyTo(region, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(8), 2);
        RewriteDescriptorCrc(region);
        Verify(region);
        region[offset] ^= value;
        RewriteDescriptorCrc(region);
        Assert.Throws<InvalidDataException>(() => Verify(region));
    }

    [Fact]
    public void Version2_still_requires_a_valid_descriptor_crc()
    {
        var region = new byte[65536];
        Descriptor().CopyTo(region, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(8), 2);
        Assert.Throws<InvalidDataException>(() => Verify(region));
    }

    [Fact]
    public void Only_supported_boards_and_nonoverlapping_payloads_can_be_provisioned()
    {
        Assert.Throws<InvalidDataException>(() => Descriptor("unknown"));
        Assert.Throws<InvalidDataException>(() => Descriptor(end: 65535));
        Assert.Throws<InvalidDataException>(() => Descriptor(end: Offset+1));
        Assert.Throws<InvalidDataException>(() =>
            Call("CreateDescriptor", Guid.Empty, Installation, "TB322FC", 1048576L));
        Assert.Throws<InvalidDataException>(() =>
            Call("CreateDescriptor", Partition, Guid.Empty, "TB322FC", 1048576L));
        Assert.Equal(4096, Descriptor(end: Offset).Length);
    }

    [Theory]
    [InlineData("TB322FC", 1u)]
    [InlineData("TB323FU", 2u)]
    [InlineData("TB710FU", 3u)]
    public void Trial_descriptor_matches_version3_wire_format_and_preserves_v1_default(string model, uint board)
    {
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(Descriptor(model).AsSpan(8)));
        var d = TrialDescriptor(model);
        Assert.Equal(4096, d.Length);
        Assert.Equal("KSCFGV3\0"u8.ToArray(), d[..8]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12)));
        Assert.Equal(2L << 30, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(16)));
        Assert.Equal(1048576, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(24)));
        Assert.Equal(Offset, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(32)));
        Assert.Equal(65536u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(40)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(44)));
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), d[48..64]);
        Assert.Equal(Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"), d[64..80]);
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80)));
        Assert.Equal(8192u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(84)));
        Assert.Equal(board, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(88)));
        Assert.Equal(28672u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(92)));
        Assert.Equal(32768u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(96)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(100)));
        Assert.All(d[104..4092], b => Assert.Equal((byte)0, b));
        uint crc = uint.MaxValue;
        foreach (byte b in d[..4092]) {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        Assert.Equal(~crc, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4092)));
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public void Version3_settings_logs_and_trial_writes_do_not_invalidate_recovery(string model)
    {
        var region = new byte[65536];
        TrialDescriptor(model).CopyTo(region, 0);
        Verify(region, model);
        Array.Fill(region, (byte)0xa5, 4096, 36864 - 4096);
        Verify(region, model);
        region[36864] = 1;
        Assert.Throws<InvalidDataException>(() => Verify(region, model));
    }

    [Theory]
    [InlineData(92)]
    [InlineData(96)]
    [InlineData(100)]
    [InlineData(104)]
    [InlineData(4091)]
    public void Version3_rejects_trial_field_and_reserved_descriptor_mutations_even_with_valid_crc(int offset)
    {
        var region = new byte[65536];
        TrialDescriptor().CopyTo(region, 0);
        Verify(region);
        region[offset] ^= 1;
        RewriteDescriptorCrc(region);
        Assert.Throws<InvalidDataException>(() => Verify(region));
    }

    [Fact]
    public void Version4_and_truncated_regions_are_rejected()
    {
        var region = new byte[65536];
        TrialDescriptor().CopyTo(region, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(8), 4);
        RewriteDescriptorCrc(region);
        Assert.Throws<InvalidDataException>(() => Verify(region));
        Assert.Throws<InvalidDataException>(() => Verify(region[..36864]));
    }

    private const int Slot0Off = 4096;
    private const int Slot1Off = 8192;

    private static void PutSettings(byte[] region, int slotOff, uint version, ulong seq,
        uint dev, uint def, uint lang, uint last, uint next)
    {
        var s = region.AsSpan(slotOff, 4096);
        s.Clear();
        ReadOnlySpan<byte> magic = stackalloc byte[] { (byte)'K',(byte)'S',(byte)'C',(byte)'F',(byte)'G',(byte)'R',(byte)'1', 0 };
        magic.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(16), seq);
        Partition.ToByteArray(bigEndian: true).CopyTo(s.Slice(24, 16));
        Installation.ToByteArray(bigEndian: true).CopyTo(s.Slice(40, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(56), dev);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(60), def);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(64), lang);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(68), last);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(72), next);
        uint crc = uint.MaxValue;
        foreach (byte b in s[..4092]) { crc ^= b; for (int i=0;i<8;i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4092), ~crc);
    }

    [Fact]
    public void Prepare_android_default_linux_preserves_winner_and_writes_inactive_slot()
    {
        var src = new byte[65536];
        Descriptor().CopyTo(src, 0);
        // Winner slot 0: seq=5, device_choice=1, default=Android(1), lang=1, last=2, next=0.
        PutSettings(src, Slot0Off, 3, 5, 1, 1, 1, 2, 0);
        // Older slot 1: seq=4, default=Android(1).
        PutSettings(src, Slot1Off, 3, 4, 0, 1, 0, 0, 0);
        var original = (byte[])src.Clone();

        var outp = (byte[])Call("PrepareLinuxDefault", src, Partition, Installation, "TB322FC", 1048576L)!;

        // Input untouched.
        Assert.Equal(original, src);
        // Descriptor upgraded to v3, VerifyRegion (via helper) accepts the projection.
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(outp.AsSpan(8)));
        Verify(outp);
        // Winner slot 0 preserved byte-for-byte.
        Assert.True(src.AsSpan(Slot0Off, 4096).SequenceEqual(outp.AsSpan(Slot0Off, 4096)));
        // Inactive slot 1 rewritten as v3 record seq=6, default_target=Linux, preserves others.
        var s1 = outp.AsSpan(Slot1Off, 4096);
        Assert.Equal("KSCFGR1\0"u8.ToArray(), s1[..8].ToArray());
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(s1.Slice(8)));
        Assert.Equal(6ul, BinaryPrimitives.ReadUInt64LittleEndian(s1.Slice(16)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(s1.Slice(56))); // winning device choice retained
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(s1.Slice(60))); // default_target = Linux
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(s1.Slice(64))); // winning language retained
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(s1.Slice(72))); // next_action cleared
    }

    [Fact]
    public void Prepare_is_noop_for_settings_when_already_target_linux()
    {
        var src = new byte[65536];
        Descriptor().CopyTo(src, 0);
        PutSettings(src, Slot0Off, 3, ulong.MaxValue, 2, 2, 1, 3, 0); // winning slot already Linux default
        PutSettings(src, Slot1Off, 3, 6, 0, 1, 0, 0, 0);

        var outp = (byte[])Call("PrepareLinuxDefault", src, Partition, Installation, "TB322FC", 1048576L)!;
        // Settings bytes unchanged (only descriptor may be up-converted; here already v3).
        Assert.True(src.AsSpan(Slot0Off, 8192).SequenceEqual(outp.AsSpan(Slot0Off, 8192)));
        Verify(outp);
    }

    [Fact]
    public void Prepare_initializes_first_settings_from_blank_tail()
    {
        var src = new byte[65536];
        Descriptor().CopyTo(src, 0);

        var outp = (byte[])Call("PrepareLinuxDefault", src, Partition, Installation, "TB322FC", 1048576L)!;
        Verify(outp);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(outp.AsSpan(8)));
        // First record lands at index 0 with seq=1, Auto device, lang=0, last=0, next=0, default=Linux.
        var s0 = outp.AsSpan(Slot0Off, 4096);
        Assert.Equal("KSCFGR1\0"u8.ToArray(), s0[..8].ToArray());
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(s0.Slice(8)));
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(s0.Slice(16)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(s0.Slice(56)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(s0.Slice(60)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(s0.Slice(64)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(s0.Slice(72)));
        // Slot 1 remains erased.
        Assert.True(outp.AsSpan(Slot1Off, 4096).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void Prepare_refuses_pending_next_action()
    {
        var src = new byte[65536];
        Descriptor().CopyTo(src, 0);
        PutSettings(src, Slot0Off, 3, 9, 1, 1, 0, 0, 1); // next_action pending on the winner
        PutSettings(src, Slot1Off, 3, 8, 0, 1, 0, 0, 0);

        Assert.Throws<InvalidDataException>(() =>
            Call("PrepareLinuxDefault", src, Partition, Installation, "TB322FC", 1048576L));
    }

    [Fact]
    public void Prepare_refuses_when_both_slots_malformed_and_equal_sequence_conflict()
    {
        // Two valid records with equal sequence but different bytes -> conflict.
        var src = new byte[65536];
        Descriptor().CopyTo(src, 0);
        PutSettings(src, Slot0Off, 3, 3, 0, 1, 0, 0, 0);
        PutSettings(src, Slot1Off, 3, 3, 0, 2, 0, 0, 0);
        Assert.Throws<InvalidDataException>(() =>
            Call("PrepareLinuxDefault", src, Partition, Installation, "TB322FC", 1048576L));

        // Malformed winner (bad CRC) alone with erased peer: refuse.
        var src2 = new byte[65536];
        Descriptor().CopyTo(src2, 0);
        PutSettings(src2, Slot0Off, 3, 3, 0, 1, 0, 0, 0);
        src2[Slot0Off + 4092] ^= 1; // destroy CRC
        Assert.Throws<InvalidDataException>(() =>
            Call("PrepareLinuxDefault", src2, Partition, Installation, "TB322FC", 1048576L));
    }
}
