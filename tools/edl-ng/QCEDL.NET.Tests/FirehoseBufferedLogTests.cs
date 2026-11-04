using System.Text;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose.Xml.Elements;

namespace QCEDL.NET.Tests;

public sealed class FirehoseBufferedLogTests
{
    private const uint SectorSize = 4096;
    private const uint Lun = 2;
    private const uint Slot = 1;
    private const uint FirstSector = 10;

    private static readonly byte[] FinalAck = "<data><response value=\"ACK\" /></data>"u8.ToArray();

    [Fact]
    public void ReadToStreamKeepsBufferedLogBatchingWithoutExtraTransportRead()
    {
        var payload = CreateLiteralPayload();

        // Exactly one transport receive holds five complete log documents followed by the final
        // ACK. Splitting the initial ACK away from the payload must not change the previous
        // same-receive log batching or the bounded final-ACK loop.
        var batchedLogsAndFinalAck = Concat(
            LogDocument(new string('x', 300)),
            LogDocument(new string('x', 300)),
            LogDocument(new string('x', 300)),
            LogDocument(new string('x', 300)),
            LogDocument(new string('x', 300)),
            FinalAck);

        using var transport = new ScriptedFirehoseTransport(AckRawMode(), payload, batchedLogsAndFinalAck);
        var firehose = new QualcommFirehose(transport);
        using var output = new MemoryStream();

        var succeeded = firehose.ReadToStream(StorageType.Ufs, Lun, Slot, SectorSize, FirstSector, FirstSector,
            output);

        Assert.True(succeeded);

        var written = output.ToArray();
        Assert.Equal(payload, written);
        Assert.Equal("<data>"u8.ToArray(), written[..6]);
        Assert.Equal("</data>"u8.ToArray(), written[64..71]);
        Assert.Equal((byte)0x1D, written[128]);
        Assert.Equal((byte)0x00, written[129]);
        Assert.Equal((byte)0xFF, written[130]);
        Assert.Equal((byte)0x80, written[131]);

        var command = Assert.Single(transport.Commands);
        Assert.Contains("<read", Encoding.UTF8.GetString(command));
    }

    private static byte[] AckRawMode()
    {
        return "<data><response value=\"ACK\" rawmode=\"true\" /></data>"u8.ToArray();
    }

    private static byte[] LogDocument(string message)
    {
        return Encoding.UTF8.GetBytes($"<data><log value=\"{message}\" /></data>");
    }

    // Literal bytes chosen by the test, independent of the production parser or framing logic.
    private static byte[] CreateLiteralPayload()
    {
        var payload = new byte[(int)SectorSize];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(((index * 37) + 11) & 0xFF);
        }

        "<data>"u8.CopyTo(payload.AsSpan(0));
        "</data>"u8.CopyTo(payload.AsSpan(64));
        payload[128] = 0x1D;
        payload[129] = 0x00;
        payload[130] = 0xFF;
        payload[131] = 0x80;
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
}
