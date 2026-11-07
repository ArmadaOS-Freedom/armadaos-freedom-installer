// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FreedomInstaller.Core.Gbl;

internal static partial class EfispLoadDetector
{
    internal readonly record struct AvbKeyCandidate(int RawOffset, byte[] Blob, int XrefCount);

    internal static List<AvbKeyCandidate> ReadAvbKeyCandidates(byte[] data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data);
        token.ThrowIfCancellationRequested();
        if (data.Length < 64 || data.Length > 4 * 1024 * 1024
            || !TryParseImage(data, out PeImage? image, out _) || image is null)
            throw new InvalidDataException("ABL payload is not a bounded PE image.");
        uint peHeader = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3c));
        if (peHeader > data.Length - 6
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)peHeader + 4)) != 0xAA64)
            throw new InvalidDataException("ABL payload is not AArch64.");

        // Key data can live in .rdata; only instruction references must be executable.
        var code = new PeImage(image.Length, image.Sections.Where(s => s.Executable)
            .Select(s => new Section("", s.VirtualAddress, s.RawOffset, s.RawSize, true)).ToArray());
        var candidates = new List<AvbKeyCandidate>();
        foreach (Section section in image.Sections)
        {
            long end = Math.Min(section.RawOffset + section.RawSize, data.Length);
            for (long raw = section.RawOffset; raw + 8 <= end; raw++)
            {
                token.ThrowIfCancellationRequested();
                uint bits = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)raw, 4));
                if (bits is not (2048 or 4096 or 8192)) continue;
                int size = 8 + 2 * (int)(bits / 8);
                if (raw + size > end) continue;
                long rva = section.VirtualAddress + raw - section.RawOffset;
                List<long> refs = CodeXrefsTo(code, data, rva, size)
                    ?? throw new InvalidDataException("ABL code references could not be read.");
                token.ThrowIfCancellationRequested();
                refs.RemoveAll(pc => pc >= rva && pc < rva + size);
                candidates.Add(new AvbKeyCandidate((int)raw, data.AsSpan((int)raw, size).ToArray(), refs.Count));
            }
        }
        return candidates;
    }
}
