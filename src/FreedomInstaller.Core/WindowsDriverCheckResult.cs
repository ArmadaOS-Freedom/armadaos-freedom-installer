using System.Collections.Generic;

namespace FreedomInstaller.Core;

/// <summary>
/// Readiness of the Windows Qualcomm 9008 driver, as established by read-only SetupAPI and PnP queries.
/// </summary>
public enum WindowsDriverCheckState
{
    /// <summary>No matching published package and no present 9008 endpoint was proven.</summary>
    Missing,

    /// <summary>A matching NTamd64 store package exists, its signature is accepted, and no endpoint is present.</summary>
    Prepared,

    /// <summary>Every present 9008 endpoint is started, problem-free, and bound to a verified package.</summary>
    Connected,

    /// <summary>Matching evidence exists but is rejected: signature, binding, service, or device state.</summary>
    Problem,

    /// <summary>A relevant OS query failed, so readiness cannot be established either way.</summary>
    Unknown,

    /// <summary>This platform/architecture/OS version cannot be inspected here.</summary>
    Unsupported,
}

/// <summary>
/// Immutable outcome of a driver readiness probe. <see cref="DriverVersion"/> is the INF
/// <c>DriverVer</c> of the selected package (never a signer version), and <see cref="PublishedInf"/> is
/// the published <c>oem*.inf</c> file name of that package.
/// </summary>
public record WindowsDriverCheckResult(
    WindowsDriverCheckState State,
    string? DriverVersion,
    string? PublishedInf,
    string? Detail)
{
    /// <summary>
    /// True only when the driver is actually usable: a verified package is staged or a healthy endpoint is
    /// bound to one. <see cref="WindowsDriverCheckState.Problem"/>, <see cref="WindowsDriverCheckState.Unknown"/>
    /// and <see cref="WindowsDriverCheckState.Missing"/> are never ready.
    /// </summary>
    public bool IsReady => State is WindowsDriverCheckState.Prepared or WindowsDriverCheckState.Connected;

    /// <summary>
    /// Supported USB hardware IDs that no accepted, verified package covers (or that a present endpoint
    /// requires but is not covered for). Diagnostic evidence only; never used to relax readiness.
    /// </summary>
    public IReadOnlyList<string> MissingHardwareIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Published packages whose signature was inspected and not accepted. Diagnostic evidence only;
    /// contains no device instance serial numbers or other device identifiers.
    /// </summary>
    public IReadOnlyList<WindowsRejectedDriverPackage> RejectedPackages { get; init; } =
        Array.Empty<WindowsRejectedDriverPackage>();
}

/// <summary>
/// One published OEM INF candidate that contains an NTamd64 model for USB VID_05C6/PID_9008, together with
/// the evidence gathered about it. <see cref="SignatureAccepted"/> alone is not trusted: the signer and
/// catalog must be present and the store location must be known.
/// </summary>
internal sealed record DriverPackageEvidence(
    string InfName,
    string StoreInfPath,
    string? DriverVersion,
    string? DigitalSigner,
    string? CatalogFile,
    bool SignatureAccepted,
    string? SignatureDetail)
{
    public IReadOnlyList<string> MatchingHardwareIds { get; init; } = Array.Empty<string>();

    /// <summary>The raw native <c>SetupVerifyInfFile</c> error code, when a verdict was reached.</summary>
    public int? SignatureErrorCode { get; init; }
}

/// <summary>
/// A published driver package that was inspected but whose signature is not accepted. Exposed to callers as
/// diagnostic evidence; contains no device instance identifiers.
/// </summary>
public sealed record WindowsRejectedDriverPackage(
    string? PublishedInf,
    string? DriverVersion,
    string? Signer,
    string? Catalog,
    int? SignatureErrorCode,
    string? SignatureDetail);

/// <summary>
/// Read-only state of one present 9008 device node: its bound service, its bound published INF, and the
/// device node status from <c>CM_Get_DevNode_Status</c>. <see cref="Problem"/> is meaningful only when the
/// node carries DN_HAS_PROBLEM and is zeroed otherwise at capture time.
/// </summary>
internal sealed record DriverEndpointEvidence(
    string DeviceInstanceId,
    string? BoundInfName,
    string? Service,
    uint Status,
    uint Problem,
    bool StatusQuerySucceeded,
    string? FailureDetail)
{
    /// <summary>
    /// Authoritative USB serial reported by the physical (composite parent) USB devnode when
    /// <c>CM_DEVCAP_UNIQUEID</c> is set and the instance suffix is a stable, non-generated value.
    /// Null when the endpoint has no verifiable physical USB serial.
    /// </summary>
    public string? UsbSerial { get; init; }

    /// <summary>
    /// Signed package captured for the inbox WinUSB INF when a selected ADB endpoint is bound to it and
    /// exposes the generic ADB-compatible USB class ID. Null otherwise.
    /// </summary>
    public DriverPackageEvidence? InboxAdbPackage { get; init; }

    /// <summary>
    /// True when the endpoint exposes the generic Android ADB compatible USB class ID
    /// <c>USB\Class_FF&amp;SubClass_42&amp;Prot_01</c>. This is the authoritative interface marker for
    /// selected-device ADB checks; the USB VID/PID is not used as a whitelist.
    /// </summary>
    public bool AdbCompatible { get; init; }
}

/// <summary>
/// Everything the pure decision needs, and nothing that requires a live OS. Tests construct this shape
/// through reflection because the assembly has no InternalsVisibleTo.
/// </summary>
internal sealed record DriverEvidenceSnapshot(
    bool WindowsSupported,
    string? UnsupportedReason,
    string? PackageQueryFailure,
    string? EndpointQueryFailure,
    IReadOnlyList<DriverPackageEvidence> Packages,
    IReadOnlyList<DriverEndpointEvidence> Endpoints);
