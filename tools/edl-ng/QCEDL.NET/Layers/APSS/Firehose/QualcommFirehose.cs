// Copyright (c) 2018, Rene Lergner - @Heathcliff74xda
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Text;
using QCEDL.NET.Logging;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose.Xml;
using Qualcomm.EmergencyDownload.Layers.APSS.Firehose.Xml.Elements;
using Qualcomm.EmergencyDownload.Transport;

namespace Qualcomm.EmergencyDownload.Layers.APSS.Firehose;

public class QualcommFirehose(IQualcommTransport transport)
{
    private const int MaxXmlFrameLength = 8192;
    private const int XmlReadChunkSize = 8192;

    // Bytes received from the transport but not yet returned to a caller. The same instance owns
    // pending bytes so that an XML frame and any raw bytes that follow it in the same transport
    // read survive across separate calls.
    private readonly List<byte> _pending = [];

    public IQualcommTransport Transport { get; } = transport;

    private static ReadOnlySpan<byte> FrameFooter => "</data>"u8;

    public byte[] GetFirehoseXmlResponseBuffer(bool waitTilFooter = false)
    {
        // The bool parameter is kept for source compatibility. Both values now read exactly one
        // complete XML document, because a single transport read may contain raw payload or
        // several documents and must never be handed to the XML parser as a whole.
        _ = waitTilFooter;

        while (true)
        {
            if (TryExtractCompleteFrame(out var frame))
            {
                EnsureFrameWithinLimit(frame.Length);
                return frame;
            }

            if (_pending.Count >= MaxXmlFrameLength)
            {
                LibraryLogger.Error(
                    $"XML response footer not found within the {MaxXmlFrameLength}-byte frame limit. Buffer size: {_pending.Count}");
                throw new BadMessageException(
                    $"Firehose XML frame did not complete within {MaxXmlFrameLength} bytes.");
            }

            var chunk = Transport.GetResponse(null, length: XmlReadChunkSize);
            if (chunk == null || chunk.Length == 0)
            {
                // Timeout or no data from device. An incomplete frame cannot be treated as success.
                LibraryLogger.Warning("Timeout or no data received while waiting for XML footer.");
                throw new BadMessageException(
                    "The device returned no data while an XML frame was still incomplete.");
            }

            _pending.AddRange(chunk);
        }
    }

    public Data[] GetFirehoseResponseDataPayloads(bool waitTilFooter = false)
    {
        var responseBuffer = GetFirehoseXmlResponseBuffer(waitTilFooter);
        var datas = ParsePayloads(responseBuffer);

        if (ContainsResponse(datas))
        {
            // Never look at pending bytes after a response: in raw mode the next bytes are
            // arbitrary payload and may start with "<data>" or "</data>".
            return datas;
        }

        // Preserve the previous "one transport receive, many documents" behavior for log-only
        // frames by consuming frames that are already complete in the buffer. No extra transport
        // read is issued here: if no complete frame is pending, the next ordinary call reads more.
        var aggregated = new List<Data>(datas);
        while (TryExtractCompleteFrame(out var nextFrame))
        {
            EnsureFrameWithinLimit(nextFrame.Length);
            var nextDatas = ParsePayloads(nextFrame);
            aggregated.AddRange(nextDatas);
            if (ContainsResponse(nextDatas))
            {
                break;
            }
        }

        return [.. aggregated];
    }

    internal byte[] ReadRawResponse(int requestedLength)
    {
        if (requestedLength <= 0)
        {
            return [];
        }

        if (_pending.Count == 0)
        {
            // One existing transport read, exactly like the direct calls this replaces.
            return Transport.GetResponse(null, length: requestedLength);
        }

        // A short return from pending is valid; callers already accumulate until their length.
        var count = Math.Min(requestedLength, _pending.Count);
        var response = _pending.GetRange(0, count).ToArray();
        _pending.RemoveRange(0, count);
        return response;
    }

    private bool TryExtractCompleteFrame(out byte[] frame)
    {
        var footer = FrameFooter;
        for (var index = 0; index <= _pending.Count - footer.Length; index++)
        {
            var matches = true;
            for (var footerIndex = 0; footerIndex < footer.Length; footerIndex++)
            {
                if (_pending[index + footerIndex] != footer[footerIndex])
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            var frameLength = index + footer.Length;
            frame = [.. _pending.GetRange(0, frameLength)];
            _pending.RemoveRange(0, frameLength);
            return true;
        }

        frame = [];
        return false;
    }

    private static void EnsureFrameWithinLimit(int frameLength)
    {
        if (frameLength > MaxXmlFrameLength)
        {
            LibraryLogger.Error(
                $"XML response frame exceeded the {MaxXmlFrameLength}-byte limit ({frameLength} bytes).");
            throw new BadMessageException(
                $"Firehose XML frame exceeded {MaxXmlFrameLength} bytes.");
        }
    }

    private static bool ContainsResponse(Data[] datas)
    {
        foreach (var data in datas)
        {
            if (data.Response != null)
            {
                return true;
            }
        }

        return false;
    }

    private static Data[] ParsePayloads(byte[] frame)
    {
        var incoming = Encoding.UTF8.GetString(frame);

        try
        {
            return QualcommFirehoseXml.GetDataPayloads(incoming);
        }
        catch (Exception ex) // Catch specific XML parsing exceptions if possible
        {
            LibraryLogger.Error("UNEXPECTED PARSING FAILURE. ABOUT TO CRASH. PAYLOAD BYTE RAW AS FOLLOW:");
            LibraryLogger.Error(Convert.ToHexString(frame));
            LibraryLogger.Error($"Exception: {ex.Message}");
            throw;
        }
    }
}
