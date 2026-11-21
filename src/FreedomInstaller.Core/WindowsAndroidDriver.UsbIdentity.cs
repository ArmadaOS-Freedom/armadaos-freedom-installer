using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace FreedomInstaller.Core;

public sealed partial class WindowsQualcommDriver
{
    private const uint CM_DRP_CAPABILITIES = 0x10;
    private const uint CM_DEVCAP_UNIQUEID = 0x10;
    private const uint REG_DWORD = 4;

    [SupportedOSPlatform("windows")]
    private static string? ReadAdbUsbSerial(uint endpointDevInst, string endpointInstanceId)
    {
        if (string.IsNullOrEmpty(endpointInstanceId))
            return null;

        string? parentInstanceId = null;
        uint physicalDevInst = endpointDevInst;

        if (IsMiInterface(endpointInstanceId))
        {
            uint cr = CM_Get_Parent(out uint physical, endpointDevInst, 0);
            if (cr != CrSuccess)
                return null;

            physicalDevInst = physical;
            parentInstanceId = GetDeviceId(physical);
            if (parentInstanceId is null)
                return null;
        }

        uint uniqueId = ReadUniqueIdCapability(physicalDevInst);
        bool hasUniqueId = (uniqueId & CM_DEVCAP_UNIQUEID) != 0;

        return ResolveAdbUsbSerial(endpointInstanceId, parentInstanceId, hasUniqueId);
    }

    internal static string? ResolveAdbUsbSerial(string endpointInstanceId, string? parentInstanceId, bool uniqueId)
    {
        if (!uniqueId)
            return null;

        if (string.IsNullOrEmpty(endpointInstanceId))
            return null;

        string endpointPrefix = HardwarePrefix(endpointInstanceId);
        if (endpointPrefix.Length == 0)
            return null;

        Match em = AndroidUsbId.Match(endpointPrefix);
        if (!em.Success)
            return null;

        bool isMi = em.Groups[4].Success && em.Groups[4].Value.Length > 0;

        string physicalInstanceId;
        if (isMi)
        {
            if (string.IsNullOrEmpty(parentInstanceId))
                return null;

            string parentPrefix = HardwarePrefix(parentInstanceId);
            if (parentPrefix.Length == 0)
                return null;

            Match pm = AndroidUsbId.Match(parentPrefix);
            if (!pm.Success)
                return null;

            if (!string.Equals(pm.Groups[1].Value, em.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!string.Equals(pm.Groups[2].Value, em.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                return null;
            if (pm.Groups[4].Success && pm.Groups[4].Value.Length > 0)
                return null;

            physicalInstanceId = parentInstanceId;
        }
        else
        {
            if (!string.IsNullOrEmpty(parentInstanceId))
                return null;

            physicalInstanceId = endpointInstanceId;
        }

        string serial = ExtractPhysicalSerial(physicalInstanceId);
        return serial;
    }

    private static bool IsMiInterface(string instanceId)
    {
        Match m = AndroidUsbId.Match(HardwarePrefix(instanceId));
        return m.Success && m.Groups[4].Success && m.Groups[4].Value.Length > 0;
    }

    private static string? ExtractPhysicalSerial(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return null;

        int lastBackslash = instanceId.LastIndexOf('\\');
        if (lastBackslash < 0 || lastBackslash == instanceId.Length - 1)
            return null;

        string serial = instanceId.Substring(lastBackslash + 1);
        if (serial.Length < 1 || serial.Length > 64)
            return null;

        foreach (char c in serial)
        {
            bool ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-';
            if (!ok)
                return null;
        }

        return serial;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetDeviceId(uint devInst)
    {
        const int bufferSize = 512;
        StringBuilder buffer = new(bufferSize);
        uint cr = CM_Get_Device_IDW(devInst, buffer, (uint)bufferSize, 0);
        if (cr != CrSuccess)
            return null;

        string id = buffer.ToString();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    [SupportedOSPlatform("windows")]
    private static uint ReadUniqueIdCapability(uint devInst)
    {
        uint registryType = 0;
        uint dwordValue = 0;
        uint length = 4;
        uint cr = CM_Get_DevNode_Registry_PropertyW(
            devInst,
            CM_DRP_CAPABILITIES,
            out registryType,
            out dwordValue,
            ref length,
            0);

        if (cr != CrSuccess)
            return 0;
        if (length != 4)
            return 0;
        if (registryType != REG_DWORD)
            return 0;

        return dwordValue;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint CM_Get_Device_IDW(
        uint dnDevInst,
        [Out] StringBuilder buffer,
        uint bufferLen,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(
        uint dnDevInst,
        uint ulProperty,
        out uint pulRegDataType,
        out uint buffer,
        ref uint pulLength,
        uint ulFlags);

    private const uint SPDRP_COMPATIBLEIDS = 0x00000002;
    private const uint REG_MULTI_SZ = 7;
    private const uint MaxCompatibleIdBytes = 65536;

    [SupportedOSPlatform("windows")]
    private static bool HasAdbCompatibleId(IntPtr set, ref SpDevInfoData info)
    {
        try
        {
            if (!SetupDiGetDeviceRegistryProperty(
                    set,
                    ref info,
                    SPDRP_COMPATIBLEIDS,
                    out _,
                    null,
                    0,
                    out uint requiredSize))
            {
                int err = Marshal.GetLastWin32Error();
                if ((uint)err != ErrorInsufficientBuffer)
                    return false;
            }

            if (requiredSize == 0 || requiredSize > MaxCompatibleIdBytes)
                return false;
            if ((requiredSize & 1u) != 0)
                return false;

            byte[] buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryProperty(
                    set,
                    ref info,
                    SPDRP_COMPATIBLEIDS,
                    out uint regType,
                    buffer,
                    (uint)buffer.Length,
                    out uint returnedSize))
                return false;

            if (regType != REG_MULTI_SZ)
                return false;
            if (returnedSize == 0 || returnedSize > buffer.Length)
                return false;
            if ((returnedSize & 1u) != 0)
                return false;

            string multiSz = Encoding.Unicode.GetString(buffer, 0, (int)returnedSize);
            foreach (string entry in multiSz.Split('\0'))
            {
                if (entry.Length == 0)
                    continue;
                if (string.Equals(entry, AdbCompatibleUsbId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
