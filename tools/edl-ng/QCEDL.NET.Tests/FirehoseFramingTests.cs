using System.Text;
using System.Xml;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose.Xml.Elements;
using Qualcomm.EmergencyDownload.Transport;

namespace QCEDL.NET.Tests;

public sealed class FirehoseFramingTests
{
    private const uint SectorSize = 4096;
    private const uint Lun = 2;
    private const uint Slot = 1;
    private const uint FirstSector = 10;

    private static readonly byte[] Ack = "<data><response value=\"ACK\" /></data>"u8.ToArray();
    private static readonly byte[] Nak = "<data><response value=\"NAK\" /></data>"u8.ToArray();

    [Fact]
    public void ReadToStreamPreservesRawPayloadCoalescedAfterInitialAck()
    {
        var payload = CreatePayload((int)SectorSize);
        payload[1] = 0x00;
        payload[^1] = 0xA5;

        using var transport = new ScriptedFirehoseTransport(Concat(AckRawMode(), payload, Ack));
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.True(succeeded);
        Assert.Equal(payload, output.ToArray());
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadToStreamPreservesRawPayloadWhenInitialAckIsFragmented()
    {
        var payload = CreatePayload((int)SectorSize);
        var ack = AckRawMode();
        var splitIndex = ack.Length - 6; // Inside the "</data>" footer.

        using var transport = new ScriptedFirehoseTransport(
            ack[..splitIndex],
            Concat(ack[splitIndex..], payload, Ack));
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.True(succeeded);
        Assert.Equal(payload, output.ToArray());
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadToStreamPreservesLargePayloadSplitAcrossTransportReads()
    {
        const int middleChunkSize = 400_000;
        var payload = CreatePayload(300 * (int)SectorSize);
        var prefixLength = (int)SectorSize;
        var lastRawStart = prefixLength + (2 * middleChunkSize);

        using var transport = new ScriptedFirehoseTransport(
            Concat(AckRawMode(), payload[..prefixLength]),
            payload[prefixLength..(prefixLength + middleChunkSize)],
            payload[(prefixLength + middleChunkSize)..lastRawStart],
            Concat(payload[lastRawStart..], Ack));
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector,
            FirstSector + 299, output);

        Assert.True(succeeded);
        Assert.Equal(payload.Length, output.Length);
        Assert.Equal(payload, output.ToArray());
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadToStreamDoesNotParseRawPayloadContainingXmlText()
    {
        var payload = CreatePayload((int)SectorSize);
        "</data><data><log value=\"</data>\" /></data>"u8.CopyTo(payload.AsSpan(512));
        payload[100] = 0x00;
        payload[101] = 0x1D;
        payload[102] = 0xFF;
        payload[103] = 0x80;

        using var transport = new ScriptedFirehoseTransport(
            Concat(LogDocument("preface"), AckRawMode()),
            payload,
            Ack);
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.True(succeeded);
        Assert.Equal(payload, output.ToArray());
        AssertReadCommandSentOnce(transport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadToStreamFailsOnInitialOrFinalNak(bool finalNak)
    {
        var payload = CreatePayload((int)SectorSize);
        using var transport = finalNak
            ? new ScriptedFirehoseTransport(AckRawMode(), payload, Nak)
            : new ScriptedFirehoseTransport(Nak);
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.False(succeeded);
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadToStreamFailsWhenRawPayloadIsTruncated()
    {
        var payload = CreatePayload((int)SectorSize);
        var truncatedPayload = payload[..100];

        using var transport = new ScriptedFirehoseTransport(
            AckRawMode(),
            truncatedPayload,
            new TimeoutException("Scripted truncation of the raw payload."),
            Ack);
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.False(succeeded);
        Assert.Equal(truncatedPayload, output.ToArray());
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadToStreamSurfacesMalformedXmlFromTheRealParser()
    {
        using var transport = new ScriptedFirehoseTransport(
            "<data><response value=\"ACK\" rawmode=\"true\"></data>"u8.ToArray());
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        Assert.Throws<XmlException>(() =>
        {
            _ = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector, output);
        });
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadToStreamFailsWhenFinalAckIsMissing()
    {
        var payload = CreatePayload((int)SectorSize);
        using var transport = new ScriptedFirehoseTransport(
            AckRawMode(),
            payload,
            LogDocument("one"),
            LogDocument("two"),
            LogDocument("three"),
            LogDocument("four"),
            LogDocument("five"));
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.False(succeeded);
        Assert.Equal(payload, output.ToArray());
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadFailsWhenFinalAckIsMissing()
    {
        var payload = CreatePayload((int)SectorSize);
        using var transport = new ScriptedFirehoseTransport(
            AckRawMode(),
            payload,
            LogDocument("one"),
            LogDocument("two"),
            LogDocument("three"),
            LogDocument("four"),
            LogDocument("five"));
        var firehose = new QualcommFirehose(transport);

        var result = firehose.Read(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector);

        Assert.Null(result);
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadPreservesRawPayloadCoalescedAfterInitialAck()
    {
        var payload = CreatePayload((int)SectorSize);

        using var transport = new ScriptedFirehoseTransport(Concat(AckRawMode(), payload, Ack));
        var firehose = new QualcommFirehose(transport);

        var result = Assert.IsType<byte[]>(firehose.Read(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector,
            FirstSector));

        Assert.Equal(payload, result);
        AssertReadCommandSentOnce(transport);
    }

    [Fact]
    public void ReadReturnsNullOnFinalNak()
    {
        var payload = CreatePayload((int)SectorSize);
        using var transport = new ScriptedFirehoseTransport(AckRawMode(), payload, Nak);
        var firehose = new QualcommFirehose(transport);

        var result = firehose.Read(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector);

        Assert.Null(result);
        AssertReadCommandSentOnce(transport);
    }

    private static byte[] AckRawMode()
    {
        return "<data><response value=\"ACK\" rawmode=\"true\" /></data>"u8.ToArray();
    }

    private static byte[] LogDocument(string message)
    {
        return Encoding.UTF8.GetBytes($"<data><log value=\"{message}\" /></data>");
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        var state = 0x2545F491u;
        for (var i = 0; i < payload.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            payload[i] = (byte)(state >> 24);
        }

        payload[0] = 0x1D;
        payload[1] = 0x00;
        payload[^1] = 0xA5;
        return payload;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    private static void AssertReadCommandSentOnce(ScriptedFirehoseTransport transport)
    {
        var command = Assert.Single(transport.Commands);
        Assert.Contains("<read", Encoding.UTF8.GetString(command));
    }
}

internal sealed class ScriptedFirehoseTransport(params object[] script) : IQualcommTransport
{
    private readonly Queue<object> _script = new(script);
    private byte[]? _chunk;
    private int _chunkOffset;

    internal List<byte[]> Commands { get; } = [];

    public TransportBackend Backend => TransportBackend.WindowsQud;
    public int TimeoutMilliseconds { get; set; } = 1000;

    public int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_chunk != null)
            {
                var available = Math.Min(count, _chunk.Length - _chunkOffset);
                Buffer.BlockCopy(_chunk, _chunkOffset, buffer, offset, available);
                _chunkOffset += available;
                if (_chunkOffset == _chunk.Length)
                {
                    _chunk = null;
                    _chunkOffset = 0;
                }

                return available;
            }

            if (!_script.TryDequeue(out var next))
            {
                throw new TimeoutException("Scripted Firehose transport is exhausted.");
            }

            if (next is TimeoutException timeout)
            {
                throw timeout;
            }

            if (next is not byte[] chunk)
            {
                throw new InvalidOperationException("Unsupported scripted transport item.");
            }

            _chunk = chunk;
            _chunkOffset = 0;
        }
    }

    public int Write(byte[] buffer, int offset, int count)
    {
        Commands.Add(buffer.AsSpan(offset, count).ToArray());
        return count;
    }

    public void SendZeroLengthPacket()
    {
    }

    public void Dispose()
    {
    }
}
