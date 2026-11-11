#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// One managed connection attempt captures the peer key without a credential,
/// asks for approval only after that handshake is gone, and then trusts nothing
/// but the captured key. These tests drive that decision directly: no socket,
/// no server and no UI thread, so a slow approval can be simulated without a
/// network timeout in the way.
/// </summary>
public sealed class OfflineUpdateSshNetTransportTests
{
    private static byte[] Blob(byte seed)
    {
        var wire = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire.AsSpan(4, 11));
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(15, 4), 32);
        wire.AsSpan(19).Fill(seed);
        return wire;
    }

    private static OfflineUpdateSshHostKey Key(byte seed) => new("ssh-ed25519", Blob(seed));

    [Fact]
    public void CapturedKeyIsNotTrustedBeforeTheApprovalReturns()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(1));

        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(1)));
    }

    [Fact]
    public void AKeyApprovedAfterALongPauseIsTrustedAndLaterPresentationsNeverAskAgain()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(2));
        var calls = 0;
        OfflineUpdateSshHostKey? approved = null;

        gate.Approve(
            key =>
            {
                calls++;
                approved = key;
                // Stands in for a dialog answered long after the discovery
                // handshake was closed; nothing network-facing is waiting.
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
                return true;
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.NotNull(approved);
        Assert.Equal("ssh-ed25519", approved.Value.KeyType);
        Assert.Equal(gate.Captured.KeyBlob, approved.Value.KeyBlob);
        Assert.True(gate.IsApproved);

        // The authenticated handshake and every later rekey ask the same
        // question, and all of them get the recorded answer.
        Assert.True(gate.Accepts("ssh-ed25519", Blob(2)));
        Assert.True(gate.Accepts("ssh-ed25519", Blob(2)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AKeyReplacedBetweenApprovalAndAuthenticationIsRejected()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(4));
        gate.Approve(_ => true, CancellationToken.None);

        Assert.False(gate.Accepts("ssh-ed25519", Blob(5)));
        Assert.False(gate.Accepts("ssh-rsa", Blob(4)));
        Assert.False(gate.Accepts(null, Blob(4)));
        Assert.False(gate.Accepts("ssh-ed25519", new byte[50]));
        Assert.True(gate.Accepts("ssh-ed25519", Blob(4)));
    }

    [Fact]
    public void ADeniedKeyIsNotTrusted()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(3));
        var calls = 0;

        var error = Assert.Throws<OfflineUpdateSshHostKeyRejectedException>(() =>
            gate.Approve(
                _ =>
                {
                    calls++;
                    return false;
                },
                CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Null(error.InnerException);
        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(3)));
    }

    [Fact]
    public void ATrustStoreFailureRejectsTheKeyWithoutTrustingIt()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(6));
        var failure = new IOException("trust store is unavailable");

        var error = Assert.Throws<OfflineUpdateSshHostKeyRejectedException>(() =>
            gate.Approve(_ => throw failure, CancellationToken.None));

        Assert.Same(failure, error.InnerException);
        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(6)));
    }

    [Fact]
    public void ACancellationAfterTheDialogReturnsTrueStillLeavesTheKeyUntrusted()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(7));
        using var cts = new CancellationTokenSource();
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() =>
            gate.Approve(
                _ =>
                {
                    calls++;
                    // The attempt was abandoned while the dialog was open, so a
                    // late "yes" must not authorize any handshake.
                    cts.Cancel();
                    return true;
                },
                cts.Token));

        Assert.Equal(1, calls);
        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(7)));
    }

    [Fact]
    public void ACancellationFromTheDialogPropagatesWithoutApproval()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(8));

        Assert.Throws<OperationCanceledException>(() =>
            gate.Approve(_ => throw new OperationCanceledException(), CancellationToken.None));

        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(8)));
    }

    [Fact]
    public void APreCancelledAttemptNeverAsksForApproval()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(9));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() =>
            gate.Approve(
                _ =>
                {
                    calls++;
                    return true;
                },
                cts.Token));

        Assert.Equal(0, calls);
        Assert.False(gate.IsApproved);
    }
}
