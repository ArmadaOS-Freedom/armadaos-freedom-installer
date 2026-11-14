using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class OfflineUpdateSshManagedConnectionTests : IDisposable
{
    private const string SudoPromptPrefix = "ARMADA-SUDO-PROMPT-";

    private readonly string _root;
    private readonly string _trustDirectory;

    public OfflineUpdateSshManagedConnectionTests()
    {
        var rootEnv = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(rootEnv))
            throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be explicitly set.");

        _root = Path.Combine(rootEnv, "ManagedSsh_" + Guid.NewGuid().ToString("N"));
        _trustDirectory = Path.Combine(_root, "trust");
        Directory.CreateDirectory(_trustDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static OfflineUpdateSshHostKey HostKey(byte seed)
    {
        var wire = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire.AsSpan(4, 11));
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(15, 4), 32);
        for (var i = 19; i < wire.Length; i++)
            wire[i] = seed;

        return new OfflineUpdateSshHostKey("ssh-ed25519", wire);
    }

    private static OfflineUpdateSshKey PublicKeyOf(OfflineUpdateSshHostKey key)
    {
        var text = "ssh-ed25519 " + Convert.ToBase64String(key.KeyBlob);
        return OfflineUpdateSshEnrollment.ParsePublicKey(text);
    }

    private static string InfoFrame(int id, string model = "TB323FU") =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id,
            ok = true,
            result = new
            {
                schemaVersion = 1,
                updaterVersion = "0.8.0",
                model,
                actualSlot = "a",
                inactiveSlot = "b",
                deployment = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.0",
                recoveryPartUuid = "01234567-89ab-cdef-0123-456789abcdef",
            },
        });

    private static string ReadyOutput(int requestId = 1) =>
        OfflineUpdateSshManagedConnection.ReadyMarker + "\n" + InfoFrame(requestId) + "\n";

    private static string InfoErrorOutput(string code, string message, int requestId = 1) =>
        OfflineUpdateSshManagedConnection.ReadyMarker + "\n" + JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id = requestId,
            ok = false,
            error = new { code, message },
        }) + "\n";

    private OfflineUpdateSshPasswordOptions Options(
        OfflineUpdateSshHostKey hostKey,
        string user = "armada",
        Func<OfflineUpdateSshHostKeyPrompt, CancellationToken, Task<bool>>? approve = null) =>
        new("device.example", 2222, user, "topsecret", _trustDirectory)
        {
            ApproveHostKey = approve ?? ((_, _) => Task.FromResult(true)),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

    private static FakeFactory Factory(FakeTransport transport) => new(transport);

    private static async Task<OfflineUpdateSshManagedException> ConnectFailsAsync(
        OfflineUpdateSshPasswordOptions options,
        FakeTransport transport)
    {
        return await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            OfflineUpdateSshManagedConnection.ConnectCoreAsync(
                options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport)));
    }

    /// <summary>
    /// Both teardown events must actually be present, and in that order.
    /// Ordering alone is not enough: a missing event yields -1 from IndexOf,
    /// so an absent socket-release would satisfy "released before cancelled"
    /// whenever the channel cancel was recorded.
    /// </summary>
    private static void AssertSocketReleasedBeforeChannelCancel(List<string> events)
    {
        var released = events.IndexOf("socket-release");
        var cancelled = events.IndexOf("channel-cancel");
        Assert.True(
            released >= 0,
            "The teardown never released the socket: " + string.Join(",", events));
        Assert.True(
            cancelled >= 0,
            "The teardown never cancelled the channel: " + string.Join(",", events));
        Assert.True(
            released < cancelled,
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    private static string PromptMarker(string command)
    {
        var index = command.IndexOf(SudoPromptPrefix, StringComparison.Ordinal);
        Assert.True(index >= 0, "The started command did not contain a sudo prompt marker: " + command);
        var end = index;
        while (end < command.Length && (char.IsAsciiLetterOrDigit(command[end]) || command[end] == '-'))
            end++;
        return command[index..end];
    }

    private static FakeChannel PasswordSudoChannel(
        string command,
        bool secondPrompt = false,
        string? readyOutput = null,
        int exitStatus = 0,
        string errorTail = "",
        bool commandMode = false,
        bool blockOutputForever = false)
    {
        var marker = PromptMarker(command);
        var prompt = Encoding.UTF8.GetBytes(marker);
        var segments = new List<ScriptedStream.Segment> { new(prompt, Task.CompletedTask) };
        if (secondPrompt)
            segments.Add(new ScriptedStream.Segment(prompt, null));

        return new FakeChannel(
            readyOutput ?? ReadyOutput(),
            gaiteOutputOnPassword: true,
            errorSegments: segments,
            exited: false,
            exitStatus: exitStatus,
            errorTail: errorTail,
            exitOnFirstLine: commandMode,
            outputGate: blockOutputForever ? Never : null);
    }

    private static readonly Task Never = new TaskCompletionSource<bool>().Task;

    /// <summary>
    /// A sudo channel that prompts immediately and takes its standard input
    /// from the supplied stream, so a fixture can stall exactly one password
    /// send without touching a real transport.
    /// </summary>
    private static FakeChannel PromptedSudoChannel(
        string command,
        Stream standardInput,
        List<string>? events = null,
        Task? outputGate = null) =>
        new(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: false,
            errorSegments: new[]
            {
                new ScriptedStream.Segment(Encoding.UTF8.GetBytes(PromptMarker(command)), Task.CompletedTask),
            },
            outputGate: outputGate,
            standardInput: standardInput,
            events: events);

    private static byte[] Payload(int length, byte seed)
    {
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)((seed * 131 + index * 17 + 1) & 0xff);
        }

        return data;
    }
}
