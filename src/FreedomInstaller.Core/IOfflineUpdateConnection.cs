using System.Text.Json;

namespace FreedomInstaller.Core;

/// <summary>
/// Common surface of an authenticated offline-update transport. The existing
/// OpenSSH public-key connection and the managed password connection implement
/// the same contract so the application does not branch on the transport.
/// </summary>
public interface IOfflineUpdateConnection : IAsyncDisposable
{
    OfflineUpdateRpcClient Rpc { get; }

    JsonElement DeviceInfo { get; }

    string Diagnostics { get; }
}
