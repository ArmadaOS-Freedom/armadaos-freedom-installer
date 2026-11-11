using System.Text;

namespace FreedomInstaller.Core;

/// <summary>
/// Bounded authenticated command and upload operations over an already
/// authenticated SSH connection. Arguments are validated and shell-quoted. The
/// password is never part of a command; sudo uses an explicit stderr prompt
/// handshake, and uploads are staged unprivileged for the device bootstrap to
/// verify and install.
/// </summary>
internal sealed class OfflineUpdateSshManagedSession : IOfflineUpdateManagedSession
{
    private const int MaxArguments = 64;
    private const int MaxArgumentLength = 4096;
    private const int MaxExecutableLength = 1024;
    private const int MaxRemotePathLength = 1024;
    private const int MaxCommandOutputBytes = 64 * 1024;
    private const long MaxUploadBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(30);
    private static readonly string[] PrivilegedRoots =
    {
        "/usr", "/etc", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32",
        "/boot", "/var", "/run", "/root", "/sys", "/proc", "/dev",
    };

    private readonly IOfflineUpdateSshTransport _transport;
    private string? _sudoPassword;

    internal OfflineUpdateSshManagedSession(IOfflineUpdateSshTransport transport, string? sudoPassword = null)
    {
        _transport = transport;
        _sudoPassword = sudoPassword;
    }

    /// <summary>Drops the elevation secret after the preparation callback has finished.</summary>
    internal void ClearSecret() => _sudoPassword = null;

    public async Task<OfflineUpdateSshCommandResult> ExecuteAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool useSudo,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var commandLine = BuildCommandLine(executablePath, arguments);
        try
        {
            if (useSudo)
            {
                return await OfflineUpdateSshSudo
                    .RunAsync(_transport, commandLine, _sudoPassword, CommandTimeout, MaxCommandOutputBytes, token)
                    .ConfigureAwait(false);
            }

            var result = await _transport
                .RunAsync(commandLine, CommandTimeout, MaxCommandOutputBytes, token)
                .ConfigureAwait(false);

            return new OfflineUpdateSshCommandResult(
                result.ExitStatus,
                result.StandardOutput,
                result.StandardError);
        }
        catch (TimeoutException ex)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.Timeout,
                "The authenticated command did not finish in time.",
                ex);
        }
    }

    /// <summary>
    /// Uploads to a caller-created, user-owned staging path. The original
    /// version redirected through an unprivileged shell even with
    /// <c>useSudo</c>, which never produced a root-owned file and could leak a
    /// password into the redirected input. Privileged installation is now the
    /// device bootstrap's job after it verifies the uploaded hash.
    /// </summary>
    public async Task UploadFileAsync(
        string remotePath,
        Stream content,
        bool useSudo,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw new ArgumentException("Upload content must be readable.", nameof(content));

        var target = ValidateRemotePath(remotePath);
        if (useSudo)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.SudoUnavailable,
                "Privileged uploads are not supported. Stage the file in a user-owned temporary directory; the device bootstrap verifies its hash and installs it with root privileges.");
        }

        if (IsPrivilegedUploadPath(target))
        {
            throw new ArgumentException(
                "RemotePath is under a privileged system location. Stage uploads in a user-owned temporary directory and let the device bootstrap install them.",
                nameof(remotePath));
        }

        var command = "cat > '" + target + "'";
        var channel = _transport.Start(command);
        var stdout = new OfflineUpdateBoundedText(4096);
        var stderr = new OfflineUpdateBoundedText(4096);
        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stdoutTask = DrainAsync(channel.StandardOutput, stdout, ioCts.Token);
        var stderrTask = DrainAsync(channel.StandardError, stderr, ioCts.Token);
        var exitTask = Task.Run(() => channel.WaitForExit(UploadTimeout), CancellationToken.None);

        try
        {
            await CopyBoundedAsync(content, channel.StandardInput, MaxUploadBytes, token).ConfigureAwait(false);
            await channel.StandardInput.FlushAsync(token).ConfigureAwait(false);
            try
            {
                channel.StandardInput.Dispose();
            }
            catch
            {
            }

            bool exited;
            try
            {
                exited = await exitTask.WaitAsync(UploadTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.Timeout,
                    "The file upload did not finish in time.");
            }

            if (!exited)
            {
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.Timeout,
                    "The file upload did not finish in time.");
            }

            await DrainTaskAsync(stdoutTask).ConfigureAwait(false);
            await DrainTaskAsync(stderrTask).ConfigureAwait(false);

            if (channel.ExitStatus != 0)
            {
                var tail = stderr.Snapshot();
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                    "The file upload failed with exit code " +
                    channel.ExitStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "." + (tail.Length == 0 ? string.Empty : " " + tail));
            }
        }
        finally
        {
            await ioCts.CancelAsync().ConfigureAwait(false);
            try
            {
                channel.Cancel();
            }
            catch
            {
            }

            await DrainTaskAsync(exitTask).ConfigureAwait(false);
            await DrainTaskAsync(stdoutTask).ConfigureAwait(false);
            await DrainTaskAsync(stderrTask).ConfigureAwait(false);
            if (exitTask.IsCompleted && stdoutTask.IsCompleted && stderrTask.IsCompleted)
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var executable = ValidateExecutable(executablePath);
        if (arguments.Count > MaxArguments)
            throw new ArgumentOutOfRangeException(nameof(arguments), $"At most {MaxArguments} arguments are allowed.");

        var builder = new StringBuilder(executable);
        foreach (var argument in arguments)
        {
            if (argument is null)
                throw new ArgumentException("Arguments must not contain null entries.", nameof(arguments));
            if (argument.Length > MaxArgumentLength)
                throw new ArgumentOutOfRangeException(nameof(arguments), $"An argument exceeds the {MaxArgumentLength}-character limit.");
            if (argument.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Arguments must not contain newline or null characters.", nameof(arguments));

            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string ValidateExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Length > MaxExecutableLength ||
            !executablePath.StartsWith('/') ||
            executablePath.Contains("..", StringComparison.Ordinal) ||
            executablePath.Any(c => !IsSafePathChar(c)))
        {
            throw new ArgumentException(
                "ExecutablePath must be an absolute path made of letters, digits, and /._+- characters.",
                nameof(executablePath));
        }

        return executablePath;
    }

    private static string ValidateRemotePath(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (remotePath.Length > MaxRemotePathLength ||
            !remotePath.StartsWith('/') ||
            remotePath.EndsWith('/') ||
            remotePath.Contains("//", StringComparison.Ordinal) ||
            remotePath[1..].Split('/').Any(segment => segment is "." or ".." || segment.Length == 0) ||
            remotePath.Any(c => !IsSafePathChar(c)))
        {
            throw new ArgumentException(
                "RemotePath must be an absolute file path made of letters, digits, and /._+- characters.",
                nameof(remotePath));
        }

        return remotePath;
    }

    private static bool IsPrivilegedUploadPath(string path)
    {
        foreach (var root in PrivilegedRoots)
        {
            if (string.Equals(path, root, StringComparison.Ordinal) ||
                path.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafePathChar(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '/' or '.' or '_' or '-' or '+';

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "''";

        var needsQuoting = false;
        foreach (var c in value)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or
                '_' or '-' or '.' or '/' or ':' or '=' or '+' or '@' or '%' or ',')
            {
                continue;
            }

            needsQuoting = true;
            break;
        }

        return needsQuoting ? "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'" : value;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
                return;

            total += read;
            if (total > limit)
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                    $"Upload exceeds the {limit} byte limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Stream stream, OfflineUpdateBoundedText target, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    return;
                target.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task DrainTaskAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
