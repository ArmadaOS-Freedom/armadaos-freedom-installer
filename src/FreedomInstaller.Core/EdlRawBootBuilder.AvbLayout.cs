using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;

namespace FreedomInstaller.Core;

public static partial class EdlRawBootBuilder
{
    private static (int ModulusBytes, int HashBytes, HashAlgorithmName Hash) AvbNodeAlgorithm(uint id) => id switch
    {
        1 => (256, 32, HashAlgorithmName.SHA256),
        2 => (512, 32, HashAlgorithmName.SHA256),
        3 => (1024, 32, HashAlgorithmName.SHA256),
        4 => (256, 64, HashAlgorithmName.SHA512),
        5 => (512, 64, HashAlgorithmName.SHA512),
        6 => (1024, 64, HashAlgorithmName.SHA512),
        _ => throw new InvalidDataException("Unsupported or unsigned AVB algorithm: " + id + ".")
    };

    private static void AvbNodeValidatePublicKey(byte[] key)
    {
        if (key.Length < 8) throw new InvalidDataException("AVB public key is truncated.");
        uint bits = ReadBeU32(key, 0);
        if (bits is not (2048 or 4096 or 8192) || key.Length != 8 + 2 * (int)(bits / 8))
            throw new InvalidDataException("AVB public key length and bit count disagree.");
        int width = (int)bits / 8;
        if ((key[8] & 128) == 0 || (key[7 + width] & 1) == 0)
            throw new InvalidDataException("AVB RSA modulus is invalid.");
        uint low = ReadBeU32(key, 8 + width - 4), inverse = 1;
        for (int i = 0; i < 5; i++) inverse = unchecked(inverse * (2u - low * inverse));
        if (ReadBeU32(key, 4) != unchecked(0u - inverse))
            throw new InvalidDataException("AVB RSA Montgomery inverse is invalid.");
        var modulus = new BigInteger(key.AsSpan(8, width), isUnsigned: true, isBigEndian: true);
        var rr = new BigInteger(key.AsSpan(8 + width, width), isUnsigned: true, isBigEndian: true);
        if (rr != BigInteger.ModPow(2, 2 * (int)bits, modulus))
            throw new InvalidDataException("AVB RSA Montgomery remainder is invalid.");
    }

    private static byte[] AvbNodeRebuild(AvbNodeMetadata metadata, byte[] targetPublicKey)
    {
        byte[] original = metadata.Blob;
        int start = VbmetaHeaderBytes + metadata.AuthSize + AvbNodeReadBeU64AsInt(original, 96);
        int end = start + AvbNodeReadBeU64AsInt(original, 104);
        using var descriptors = new MemoryStream();
        for (int position = start; position < end;)
        {
            int size = 16 + AvbNodeReadBeU64AsInt(original, position + 8);
            if (ReadBeU64(original, position) != 4)
            {
                descriptors.Write(original, position, size);
            }
            else
            {
                int nameLength = checked((int)ReadBeU32(original, position + 20));
                int bytes = (92 + nameLength + targetPublicKey.Length + 7) & ~7;
                byte[] chain = new byte[bytes];
                Array.Copy(original, position, chain, 0, 92 + nameLength);
                WriteBeU64(chain, 8, (ulong)bytes - 16);
                WriteBeU32(chain, 24, (uint)targetPublicKey.Length);
                targetPublicKey.CopyTo(chain, 92 + nameLength);
                descriptors.Write(chain);
            }
            position += size;
        }
        byte[] descriptorBytes = descriptors.ToArray();
        int auxBytes = (descriptorBytes.Length + targetPublicKey.Length + 63) & ~63;
        int sizeTotal = VbmetaHeaderBytes + AvbAuthBlockBytes + auxBytes;
        if (sizeTotal > VbmetaReserveBytes)
            throw new InvalidDataException("Re-signed AVB metadata exceeds its bounded reserve.");
        byte[] result = new byte[sizeTotal];
        Array.Copy(original, result, VbmetaHeaderBytes);
        WriteBeU64(result, 12, AvbAuthBlockBytes);
        WriteBeU64(result, 20, (ulong)auxBytes);
        WriteBeU32(result, 28, AvbAlgorithmRsa4096);
        WriteBeU64(result, 32, 0); WriteBeU64(result, 40, AvbHashBytes);
        WriteBeU64(result, 48, AvbHashBytes); WriteBeU64(result, 56, AvbSignatureBytes);
        WriteBeU64(result, 64, (ulong)descriptorBytes.Length);
        WriteBeU64(result, 72, (ulong)targetPublicKey.Length);
        WriteBeU64(result, 80, (ulong)(descriptorBytes.Length + targetPublicKey.Length));
        WriteBeU64(result, 88, 0); WriteBeU64(result, 96, 0);
        WriteBeU64(result, 104, (ulong)descriptorBytes.Length);
        descriptorBytes.CopyTo(result, VbmetaHeaderBytes + AvbAuthBlockBytes);
        targetPublicKey.CopyTo(result, VbmetaHeaderBytes + AvbAuthBlockBytes + descriptorBytes.Length);
        return result;
    }

    private static void AvbNodeCheckGrowth(FileStream source, AvbNodeMetadata metadata, int newSize,
        EdlRawAvbNodeKind kind, CancellationToken cancellationToken)
    {
        long limit = source.Length - (kind == EdlRawAvbNodeKind.Recovery ? AvbFooterBytes : 0);
        if (newSize > VbmetaReserveBytes || metadata.VbmetaOffset > limit - newSize)
            throw new InvalidDataException("Partition has insufficient space for re-signed AVB metadata.");
        if (newSize <= metadata.VbmetaSize) return;
        byte[] growth = new byte[newSize - metadata.VbmetaSize];
        cancellationToken.ThrowIfCancellationRequested();
        ReadExactly(source, growth, metadata.VbmetaOffset + metadata.VbmetaSize, growth.Length);
        foreach (byte value in growth)
            if (value != 0)
                throw new InvalidDataException("AVB metadata growth would overwrite non-empty partition data.");
    }
}
