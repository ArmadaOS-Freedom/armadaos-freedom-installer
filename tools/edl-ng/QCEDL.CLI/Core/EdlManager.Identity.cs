using Qualcomm.EmergencyDownload.Layers.PBL.Sahara;

namespace QCEDL.CLI.Core;

internal sealed partial class EdlManager
{
    /// <summary>
    /// Reads the Sahara serial number and HWID without uploading a loader,
    /// probing Firehose, resetting the device, or touching any storage.
    /// </summary>
    public async Task<QualcommSaharaIdentity> IdentifySaharaAsync()
    {
        if (IsDirectMode)
        {
            throw new InvalidOperationException(
                "The identify command requires a Qualcomm USB device and cannot use a direct storage backend.");
        }

        if (!string.IsNullOrEmpty(globalOptions.LoaderPath))
        {
            throw new ArgumentException(
                "The identify command reads Sahara identity only and does not accept --loader.");
        }

        if (!FindDevice())
        {
            throw new IOException("No Qualcomm EDL device was found for Sahara identification.");
        }

        if (_transport is not null)
        {
            _transport.Dispose();
            _transport = null;
            _saharaClient = null;
        }

        var transport = OpenTransport();
        _transport = transport;
        var sahara = new QualcommSahara(transport);
        _saharaClient = sahara;

        return await Task.Run(() => QualcommSaharaIdentity.Query(sahara));
    }
}
