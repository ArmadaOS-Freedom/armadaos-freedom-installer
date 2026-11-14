using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreedomInstaller.Core;

public static class UpdateSignatureVerifier
{
    private const int MaxDocumentBytes = 2 * 1024 * 1024;
    private const int MaxSignatureBytes = 4096;
    private const string TrustedKeyResource = "FreedomInstaller.UpdateTrust.PublicKey.pem";

    public static JsonDocument VerifyAndParse(byte[] document, byte[] signatureBase64)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signatureBase64);
        return VerifyAndParseWithKey(document, signatureBase64, LoadTrustedPublicKeyPem());
    }

    internal static JsonDocument VerifyAndParseWithKey(
        byte[] document,
        byte[] signatureBase64,
        string trustedPublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signatureBase64);
        ArgumentNullException.ThrowIfNull(trustedPublicKeyPem);

        if (document.Length == 0)
            throw new InvalidDataException("Document must not be empty.");
        if (document.Length > MaxDocumentBytes)
            throw new InvalidDataException("Document exceeds 2 MiB.");
        if (signatureBase64.Length == 0)
            throw new InvalidDataException("Signature must not be empty.");
        if (signatureBase64.Length > MaxSignatureBytes)
            throw new InvalidDataException("Signature exceeds 4096 bytes.");

        byte[] snapshot = (byte[])document.Clone();
        byte[] signature = DecodeSignature(signatureBase64);
        VerifySignature(snapshot, signature, trustedPublicKeyPem);
        return ParseDocument(snapshot);
    }

    private static string LoadTrustedPublicKeyPem()
    {
        Assembly assembly = typeof(UpdateSignatureVerifier).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(TrustedKeyResource);
        if (stream is null)
            throw new InvalidDataException("Trusted update public key resource is missing.");

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Trusted update public key is not valid UTF-8.", ex);
        }
    }

    private static byte[] DecodeSignature(byte[] encoded)
    {
        int start = 0;
        int end = encoded.Length;
        while (start < end && IsAsciiWhitespace(encoded[start]))
            start++;
        while (end > start && IsAsciiWhitespace(encoded[end - 1]))
            end--;
        if (start == end)
            throw new InvalidDataException("Signature must not be empty.");

        for (int i = start; i < end; i++)
        {
            byte value = encoded[i];
            bool valid = value >= (byte)'A' && value <= (byte)'Z'
                || value >= (byte)'a' && value <= (byte)'z'
                || value >= (byte)'0' && value <= (byte)'9'
                || value == (byte)'+'
                || value == (byte)'/'
                || value == (byte)'=';
            if (!valid)
                throw new InvalidDataException("Signature is not strict ASCII base64.");
        }

        string text = Encoding.ASCII.GetString(encoded, start, end - start);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Signature is not valid base64.", ex);
        }

        if (decoded.Length == 0)
            throw new InvalidDataException("Decoded signature must not be empty.");
        if (decoded.Length > MaxSignatureBytes)
            throw new InvalidDataException("Decoded signature exceeds 4096 bytes.");
        return decoded;
    }

    private static bool IsAsciiWhitespace(byte value)
    {
        return value == 0x20 || value >= 0x09 && value <= 0x0d;
    }

    private static void VerifySignature(byte[] document, byte[] signature, string publicKeyPem)
    {
        using ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Trusted update public key is invalid.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Trusted update public key is invalid.", ex);
        }

        bool verified;
        try
        {
            verified = key.VerifyData(
                document,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Signature verification failed.", ex);
        }

        if (!verified)
            throw new InvalidDataException("Signature verification failed.");
    }

    private static JsonDocument ParseDocument(byte[] document)
    {
        if (document.Length >= 3 && document[0] == 0xef && document[1] == 0xbb && document[2] == 0xbf)
            throw new InvalidDataException("UTF-8 BOM is not allowed.");

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(document);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Document is not valid UTF-8.", ex);
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Document is not valid JSON.", ex);
        }

        try
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("JSON root must be an object.");
            ValidateElement(parsed.RootElement);
            return parsed;
        }
        catch
        {
            parsed.Dispose();
            throw;
        }
    }

    private static void ValidateElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Duplicate JSON object property.");
                    ValidateElement(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    ValidateElement(item);
                break;
            case JsonValueKind.Number:
                ValidateNumber(element);
                break;
        }
    }

    private static void ValidateNumber(JsonElement number)
    {
        ReadOnlySpan<char> raw = number.GetRawText().AsSpan();
        bool requiresFiniteDouble = false;
        foreach (char c in raw)
        {
            if (c is '.' or 'e' or 'E')
            {
                requiresFiniteDouble = true;
                break;
            }
        }

        if (requiresFiniteDouble &&
            (!number.TryGetDouble(out double value) || !double.IsFinite(value)))
            throw new InvalidDataException("JSON number is non-finite or overflows.");
    }
}
