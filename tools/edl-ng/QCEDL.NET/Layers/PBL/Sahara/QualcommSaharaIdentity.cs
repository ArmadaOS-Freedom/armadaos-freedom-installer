using Qualcomm.EmergencyDownload.Transport;

namespace Qualcomm.EmergencyDownload.Layers.PBL.Sahara;

/// <summary>
/// Loader-free Sahara identity (serial number and HWID) captured over an
/// existing command-mode session without uploading a programmer.
/// </summary>
public sealed record QualcommSaharaIdentity
{
    public const int SerialNumberLength = 4;
    public const int HwidLength = 8;

    private QualcommSaharaIdentity(string serialNumberHex, string hwidHex)
    {
        SerialNumberHex = serialNumberHex;
        HwidHex = hwidHex;
    }

    public string SerialNumberHex { get; }

    public string HwidHex { get; }

    public static QualcommSaharaIdentity Query(QualcommSahara sahara)
    {
        ArgumentNullException.ThrowIfNull(sahara);

        if (!sahara.CommandHandshake())
        {
            throw new InvalidOperationException(
                "Sahara command-mode handshake failed; the device did not enter Sahara command mode.");
        }

        var serialNumber = sahara.GetSerialNumber();
        Validate("serial number", serialNumber, SerialNumberLength);

        var hwid = sahara.GetHwid();
        Validate("HWID", hwid, HwidLength);

        // Leave the device in image-transfer mode so the next invocation receives
        // a fresh HELLO. No RESET/reset/reboot and no programmer upload.
        sahara.SwitchMode(QualcommSaharaMode.ImageTxPending);

        return new(Convert.ToHexString(serialNumber), Convert.ToHexString(hwid));
    }

    private static void Validate(string fieldName, byte[] value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            throw new BadMessageException(
                $"Sahara {fieldName} is {value.Length} bytes; expected exactly {expectedLength} bytes.");
        }

        if (IsAllBytes(value, 0x00) || IsAllBytes(value, 0xFF))
        {
            throw new BadMessageException(
                $"Sahara {fieldName} is not a valid identity value (all zeroes or all 0xFF).");
        }
    }

    private static bool IsAllBytes(ReadOnlySpan<byte> value, byte expected)
    {
        foreach (var valueByte in value)
        {
            if (valueByte != expected)
            {
                return false;
            }
        }

        return true;
    }
}
