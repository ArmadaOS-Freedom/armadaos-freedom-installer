using System;
using Xunit;

namespace FreedomInstaller.Core.Tests
{
    public class DiskLinuxEntryTests
    {
        [Theory]
        [InlineData(false, 0u)]
        [InlineData(true, 1u)]
        public void Encode_ProducesExactAbiBytes(bool diagnostic, uint expectedDiagWord)
        {
            var bootFs = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var rootFs = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

            var actual = DiskLinuxEntry.Encode(bootFs, rootFs, diagnostic);

            var expected = new byte[64];
            // magic "ARMDSK1"+NUL at 0..7
            expected[0] = (byte)'A';
            expected[1] = (byte)'R';
            expected[2] = (byte)'M';
            expected[3] = (byte)'D';
            expected[4] = (byte)'S';
            expected[5] = (byte)'K';
            expected[6] = (byte)'1';
            expected[7] = 0x00;
            // LE32 version = 1 at 8
            expected[8] = 0x01;
            expected[9] = 0x00;
            expected[10] = 0x00;
            expected[11] = 0x00;
            // LE32 diagnostic 0/1 at 12
            expected[12] = (byte)(expectedDiagWord & 0xFF);
            expected[13] = (byte)((expectedDiagWord >> 8) & 0xFF);
            expected[14] = (byte)((expectedDiagWord >> 16) & 0xFF);
            expected[15] = (byte)((expectedDiagWord >> 24) & 0xFF);
            // boot FS UUID RFC4122 canonical bytes at 16
            expected[16] = 0x00; expected[17] = 0x11; expected[18] = 0x22; expected[19] = 0x33;
            expected[20] = 0x44; expected[21] = 0x55;
            expected[22] = 0x66; expected[23] = 0x77;
            expected[24] = 0x88; expected[25] = 0x99;
            expected[26] = 0xaa; expected[27] = 0xbb; expected[28] = 0xcc; expected[29] = 0xdd; expected[30] = 0xee; expected[31] = 0xff;
            // root FS UUID at 32
            expected[32] = 0xff; expected[33] = 0xee; expected[34] = 0xdd; expected[35] = 0xcc;
            expected[36] = 0xbb; expected[37] = 0xaa;
            expected[38] = 0x99; expected[39] = 0x88;
            expected[40] = 0x77; expected[41] = 0x66;
            expected[42] = 0x55; expected[43] = 0x44; expected[44] = 0x33; expected[45] = 0x22; expected[46] = 0x11; expected[47] = 0x00;
            // reserved 48..63 = 0 (already zero-initialized)

            Assert.NotNull(actual);
            Assert.Equal(64, actual.Length);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Encode_Throws_OnEmptyBootFileSystemUuid()
        {
            Assert.Throws<ArgumentException>(() =>
                DiskLinuxEntry.Encode(Guid.Empty, Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"), false));
        }

        [Fact]
        public void Encode_Throws_OnEmptyRootFileSystemUuid()
        {
            Assert.Throws<ArgumentException>(() =>
                DiskLinuxEntry.Encode(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.Empty, false));
        }
    }
}
