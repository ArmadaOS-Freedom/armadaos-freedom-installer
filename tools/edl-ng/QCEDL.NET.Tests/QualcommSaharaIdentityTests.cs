using Qualcomm.EmergencyDownload.Layers.PBL.Sahara;
using Qualcomm.EmergencyDownload.Transport;

namespace QCEDL.NET.Tests;

public sealed class QualcommSaharaIdentityTests
{
    private static readonly QualcommSaharaCommand[] AllowedCommands =
    [
        QualcommSaharaCommand.HelloResponse,
        QualcommSaharaCommand.Execute,
        QualcommSaharaCommand.ExecuteData,
        QualcommSaharaCommand.SwitchMode
    ];

    [Fact]
    public void QueryReadsSerialThenHwidOnProtocolV2()
    {
        var responses = new Queue<byte[]>(
        [
            BuildHello(2),
            BuildCommandReady(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.SerialNumRead, 4),
            BuildSerialPayload(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.MsmHwidRead, 8),
            BuildHwidPayload()
        ]);
        using var transport = new SaharaRecordingTransport(responses);

        var identity = QualcommSaharaIdentity.Query(new QualcommSahara(transport));

        Assert.Equal("12345678", identity.SerialNumberHex);
        Assert.Equal("0807060504030201", identity.HwidHex);
        Assert.Equal(
            [
                QualcommSaharaCommand.HelloResponse,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.SwitchMode
            ],
            transport.SentCommands);
        Assert.Equal(
            [
                QualcommSaharaExecuteCommand.SerialNumRead,
                QualcommSaharaExecuteCommand.SerialNumRead,
                QualcommSaharaExecuteCommand.MsmHwidRead,
                QualcommSaharaExecuteCommand.MsmHwidRead
            ],
            GetExecuteClientCommands(transport));
        Assert.Equal(
            (uint)QualcommSaharaMode.ImageTxPending,
            BitConverter.ToUInt32(transport.SentPackets[^1], 0x08));
        AssertIdentityOnlyTraffic(transport);
        Assert.Empty(responses);
    }

    [Fact]
    public void QueryUsesCmd10ForExplicitV3Hello()
    {
        var responses = new Queue<byte[]>(
        [
            BuildHello(3),
            BuildCommandReady(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.SerialNumRead, 8),
            BuildV3SerialPayload(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.ReadChipIdV3, 0x2C),
            BuildFusePayload()
        ]);
        using var transport = new SaharaRecordingTransport(responses);

        var identity = QualcommSaharaIdentity.Query(new QualcommSahara(transport));

        Assert.Equal("12345678", identity.SerialNumberHex);
        Assert.Equal("002B40E100400000", identity.HwidHex);
        Assert.Equal(
            [
                QualcommSaharaCommand.HelloResponse,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.SwitchMode
            ],
            transport.SentCommands);
        Assert.Equal(
            [
                QualcommSaharaExecuteCommand.SerialNumRead,
                QualcommSaharaExecuteCommand.SerialNumRead,
                QualcommSaharaExecuteCommand.ReadChipIdV3,
                QualcommSaharaExecuteCommand.ReadChipIdV3
            ],
            GetExecuteClientCommands(transport));
        Assert.Equal(
            (uint)QualcommSaharaMode.ImageTxPending,
            BitConverter.ToUInt32(transport.SentPackets[^1], 0x08));
        AssertIdentityOnlyTraffic(transport);
        Assert.Empty(responses);
    }

    [Fact]
    public void QueryInfersV3FromEightByteSerialAfterDiscardedHello()
    {
        var responses = new Queue<byte[]>(
        [
            BuildCommandReady(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.SerialNumRead, 8),
            BuildV3SerialPayload(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.ReadChipIdV3, 0x2C),
            BuildFusePayload()
        ]);
        using var transport = new SaharaRecordingTransport(responses, initialReadTimeouts: 1);
        var sahara = new QualcommSahara(transport);

        var identity = QualcommSaharaIdentity.Query(sahara);

        Assert.Equal("12345678", identity.SerialNumberHex);
        Assert.Equal("002B40E100400000", identity.HwidHex);
        Assert.Equal(3u, sahara.DetectedDeviceSaharaVersion);
        Assert.True(sahara.IsDeviceSaharaVersionInferred);
        Assert.Equal(0x00000437u, sahara.DetectedDeviceChipId);
        Assert.Equal(
            [
                QualcommSaharaCommand.HelloResponse,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.SwitchMode
            ],
            transport.SentCommands);
        Assert.Equal(
            [
                QualcommSaharaExecuteCommand.SerialNumRead,
                QualcommSaharaExecuteCommand.SerialNumRead,
                QualcommSaharaExecuteCommand.ReadChipIdV3,
                QualcommSaharaExecuteCommand.ReadChipIdV3
            ],
            GetExecuteClientCommands(transport));
        AssertIdentityOnlyTraffic(transport);
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void QueryRejectsInvalidSerialNumberBeforeHwidQuery(int fillValue)
    {
        var responses = new Queue<byte[]>(
        [
            BuildHello(2),
            BuildCommandReady(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.SerialNumRead, 4),
            BuildFilledPayload(4, (byte)fillValue)
        ]);
        using var transport = new SaharaRecordingTransport(responses);
        var sahara = new QualcommSahara(transport);

        _ = Assert.Throws<BadMessageException>(() => QualcommSaharaIdentity.Query(sahara));

        Assert.Equal(
            [
                QualcommSaharaCommand.HelloResponse,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData
            ],
            transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaExecuteCommand.MsmHwidRead, GetExecuteClientCommands(transport));
        Assert.DoesNotContain(QualcommSaharaCommand.SwitchMode, transport.SentCommands);
        AssertIdentityOnlyTraffic(transport);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void QueryRejectsInvalidHwidBeforeSwitchMode(int fillValue)
    {
        var responses = new Queue<byte[]>(
        [
            BuildHello(2),
            BuildCommandReady(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.SerialNumRead, 4),
            BuildSerialPayload(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.MsmHwidRead, 8),
            BuildFilledPayload(8, (byte)fillValue)
        ]);
        using var transport = new SaharaRecordingTransport(responses);
        var sahara = new QualcommSahara(transport);

        _ = Assert.Throws<BadMessageException>(() => QualcommSaharaIdentity.Query(sahara));

        Assert.Equal(
            [
                QualcommSaharaCommand.HelloResponse,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData
            ],
            transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.SwitchMode, transport.SentCommands);
        AssertIdentityOnlyTraffic(transport);
    }

    [Fact]
    public void QueryRejectsFailedHandshakeWithoutSendingAnyCommand()
    {
        using var transport = new SaharaRecordingTransport(new Queue<byte[]>());
        var sahara = new QualcommSahara(transport);

        _ = Assert.Throws<InvalidOperationException>(() => QualcommSaharaIdentity.Query(sahara));

        Assert.Empty(transport.SentPackets);
        AssertIdentityOnlyTraffic(transport);
    }

    [Fact]
    public void QueryRejectsMalformedSerialLengthWithoutResetOrSwitch()
    {
        var responses = new Queue<byte[]>(
        [
            BuildHello(2),
            BuildCommandReady(),
            BuildExecuteResponse(QualcommSaharaExecuteCommand.SerialNumRead, 3),
            new byte[3]
        ]);
        using var transport = new SaharaRecordingTransport(responses);
        var sahara = new QualcommSahara(transport);

        _ = Assert.Throws<BadMessageException>(() => QualcommSaharaIdentity.Query(sahara));

        Assert.Equal(
            [
                QualcommSaharaCommand.HelloResponse,
                QualcommSaharaCommand.Execute,
                QualcommSaharaCommand.ExecuteData
            ],
            transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.SwitchMode, transport.SentCommands);
        AssertIdentityOnlyTraffic(transport);
    }

    private static void AssertIdentityOnlyTraffic(SaharaRecordingTransport transport)
    {
        Assert.All(transport.SentPackets, static packet =>
        {
            // Only header-only HELLO_RESP (0x30) and 12-byte command packets:
            // never an image payload or a loader upload.
            Assert.InRange(packet.Length, 0x0C, 0x30);
            Assert.False(packet.AsSpan().StartsWith("<?xml"u8));
            Assert.Contains((QualcommSaharaCommand)BitConverter.ToUInt32(packet, 0), AllowedCommands);
        });
        Assert.DoesNotContain(QualcommSaharaCommand.Reset, transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.ResetStateMachine, transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.ReadData, transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.ReadData64Bit, transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.EndImageTx, transport.SentCommands);
        Assert.DoesNotContain(QualcommSaharaCommand.Done, transport.SentCommands);
    }

    private static IEnumerable<QualcommSaharaExecuteCommand> GetExecuteClientCommands(
        SaharaRecordingTransport transport)
    {
        return transport.SentPackets
            .Where(static packet => BitConverter.ToUInt32(packet, 0) is
                (uint)QualcommSaharaCommand.Execute or
                (uint)QualcommSaharaCommand.ExecuteData)
            .Select(static packet =>
                (QualcommSaharaExecuteCommand)BitConverter.ToUInt32(packet, 0x08));
    }

    private static byte[] BuildHello(uint protocolVersion)
    {
        var hello = QualcommSahara.BuildCommandPacket(QualcommSaharaCommand.Hello, new byte[40]);
        ByteOperations.WriteUInt32(hello, 0x08, protocolVersion);
        return hello;
    }

    private static byte[] BuildCommandReady()
    {
        return QualcommSahara.BuildCommandPacket(QualcommSaharaCommand.CommandReady);
    }

    private static byte[] BuildExecuteResponse(QualcommSaharaExecuteCommand command, uint payloadLength)
    {
        var payload = new byte[8];
        ByteOperations.WriteUInt32(payload, 0x00, (uint)command);
        ByteOperations.WriteUInt32(payload, 0x04, payloadLength);
        return QualcommSahara.BuildCommandPacket(QualcommSaharaCommand.ExecuteResponse, payload);
    }

    private static byte[] BuildSerialPayload()
    {
        var payload = new byte[4];
        ByteOperations.WriteUInt32(payload, 0x00, 0x12345678);
        return payload;
    }

    private static byte[] BuildV3SerialPayload()
    {
        var payload = new byte[8];
        ByteOperations.WriteUInt32(payload, 0x00, 0x12345678);
        ByteOperations.WriteUInt32(payload, 0x04, 0x00000437);
        return payload;
    }

    private static byte[] BuildHwidPayload()
    {
        var payload = new byte[8];
        ByteOperations.WriteUInt32(payload, 0x00, 0x04030201);
        ByteOperations.WriteUInt32(payload, 0x04, 0x08070605);
        return payload;
    }

    private static byte[] BuildFusePayload()
    {
        var payload = new byte[0x2C];
        ByteOperations.WriteUInt32(payload, 0x24, 0x002B40E1);
        ByteOperations.WriteUInt32(payload, 0x28, 0x00000040);
        return payload;
    }

    private static byte[] BuildFilledPayload(int length, byte fillValue)
    {
        var payload = new byte[length];
        Array.Fill(payload, fillValue);
        return payload;
    }
}
