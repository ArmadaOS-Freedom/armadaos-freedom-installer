using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class Sha256TextTests
{
    private const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Hex_KnownVectors_AreLowercase()
    {
        Assert.Equal(Abc, Sha256Text.Hex("abc"u8));
        Assert.Equal(Empty, Sha256Text.Hex(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void HexOfStream_HashesFromCurrentPositionAndLeavesStreamOpenAtEof()
    {
        using var stream = new MemoryStream("xxxabc"u8.ToArray());
        stream.Position = 3;

        Assert.Equal(Abc, Sha256Text.HexOfStream(stream));
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void HexOfStream_PreCancelledToken_ThrowsWithoutReading()
    {
        using var stream = new MemoryStream("abc"u8.ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Sha256Text.HexOfStream(stream, cts.Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void HexOfStream_CancelledBetweenReads_StopsBeforeEof()
    {
        using var cts = new CancellationTokenSource();
        using var stream = new CancelOnFirstRead("abc"u8.ToArray(), cts);

        Assert.ThrowsAny<OperationCanceledException>(() => Sha256Text.HexOfStream(stream, cts.Token));
    }

    [Fact]
    public void IsHex_AcceptsOnlySixtyFourAsciiHexDigits()
    {
        Assert.True(Sha256Text.IsHex(Abc));
        Assert.True(Sha256Text.IsHex(Abc.ToUpperInvariant()));
        Assert.False(Sha256Text.IsHex(null));
        Assert.False(Sha256Text.IsHex(""));
        Assert.False(Sha256Text.IsHex(Abc[..63]));
        Assert.False(Sha256Text.IsHex(Abc + "0"));
        Assert.False(Sha256Text.IsHex(new string('a', 63) + "\u00e9"));
        Assert.False(Sha256Text.IsHex(new string('g', 64)));
    }

    [Fact]
    public void IsHex_LowercaseOnlyRejectsUppercase()
    {
        Assert.True(Sha256Text.IsHex(Abc, lowercaseOnly: true));
        Assert.False(Sha256Text.IsHex(Abc.ToUpperInvariant(), lowercaseOnly: true));
        Assert.False(Sha256Text.IsHex(new string('A', 64), lowercaseOnly: true));
    }

    [Fact]
    public void EqualsHex_IsOrdinalIgnoreCaseAndKeepsNullSemantics()
    {
        Assert.True(Sha256Text.EqualsHex(Abc, Abc.ToUpperInvariant()));
        Assert.True(Sha256Text.EqualsHex(null, null));
        Assert.True(Sha256Text.EqualsHex("", ""));
        Assert.False(Sha256Text.EqualsHex(null, ""));
        Assert.False(Sha256Text.EqualsHex(Abc, null));
        Assert.False(Sha256Text.EqualsHex(Abc, Empty));

        // Format validation stays with the caller: unequal-length or non-hex text only compares ordinally.
        Assert.True(Sha256Text.EqualsHex("not-a-hash", "NOT-A-HASH"));
    }

    private sealed class CancelOnFirstRead(byte[] data, CancellationTokenSource cts) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            cts.Cancel();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            cts.Cancel();
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
