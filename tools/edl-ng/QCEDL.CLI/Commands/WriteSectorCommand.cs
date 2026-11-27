using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;

namespace QCEDL.CLI.Commands;

internal sealed class WriteSectorCommand
{
    private static readonly Argument<ulong> StartSectorArgument = new("start_sector", "The starting sector LBA to write to.");
    private static readonly Argument<FileInfo> FilenameArgument =
        new("filename", "The file containing data to write.")
        {
            Arity = ArgumentArity.ExactlyOne
        };

    private static readonly Option<uint> LunOption = new(
        aliases: ["--lun", "-u"],
        description: "Specify the LUN number to write to.",
        getDefaultValue: () => 0);

    private static readonly Option<long?> GzipExpandedBytesOption = new(
        "--gzip-expanded-bytes", "Stream a gzip input with this exact expanded byte length.");

    private static readonly Option<string?> GzipCompressedSha256Option = new(
        "--gzip-compressed-sha256", "Verify the compressed input SHA-256 before writing.");

    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command("write-sector", "Writes data from a file to a specified number of sectors from a given LUN and start LBA.")
        {
            StartSectorArgument,
            FilenameArgument,
            LunOption,
            GzipExpandedBytesOption,
            GzipCompressedSha256Option
        };

        _ = FilenameArgument.ExistingOnly();

        command.SetHandler(async context =>
        {
            var parsed = context.ParseResult;
            context.ExitCode = await ExecuteAsync(
                globalOptionsBinder.Bind(context.BindingContext),
                parsed.GetValueForArgument(StartSectorArgument),
                parsed.GetValueForArgument(FilenameArgument),
                parsed.GetValueForOption(LunOption),
                parsed.GetValueForOption(GzipExpandedBytesOption),
                parsed.GetValueForOption(GzipCompressedSha256Option));
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionsBinder globalOptions,
        ulong startSector,
        FileInfo inputFile,
        uint lun,
        long? gzipExpandedBytes,
        string? gzipCompressedSha256)
    {
        Logging.Log($"Executing 'write-sector' command: LUN {lun}, Start LBA {startSector}, File '{inputFile.FullName}'...", LogLevel.Trace);

        if (!inputFile.Exists)
        {
            Logging.Log($"Error: Input file '{inputFile.FullName}' not found.", LogLevel.Error);
            return 1;
        }

        if (inputFile.Length == 0)
        {
            Logging.Log("Error: Input file is empty. Nothing to write.", LogLevel.Error);
            return 1;
        }

        if (gzipExpandedBytes.HasValue != (gzipCompressedSha256 is not null)
            || gzipExpandedBytes is <= 0
            || (gzipCompressedSha256 is not null
                && (gzipCompressedSha256.Length != 64 || !gzipCompressedSha256.All(Uri.IsHexDigit))))
        {
            Logging.Log("Error: Gzip input requires a positive --gzip-expanded-bytes and a 64-digit --gzip-compressed-sha256.", LogLevel.Error);
            return 1;
        }

        return await CommandExecutor.RunAsync("write-sector", async () =>
        {
            await using var fileStream = inputFile.OpenRead();
            if (gzipCompressedSha256 is not null)
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(fileStream));
                if (!string.Equals(digest, gzipCompressedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The compressed input SHA-256 does not match.");
                }

                fileStream.Position = 0;
            }

            using var manager = new EdlManager(globalOptions);
            var effectiveLun = manager.IsDirectMode ? 0u : lun;
            if (manager.IsDirectMode && lun != 0)
            {
                if (gzipExpandedBytes.HasValue)
                {
                    throw new InvalidOperationException("Gzip writes only support LUN 0 in direct mode.");
                }

                Logging.Log("Warning: LUN parameter is ignored in direct mode.", LogLevel.Warning);
            }

            var geometry = await manager.GetStorageGeometryAsync(effectiveLun);
            var sectorSize = geometry.SectorSize;
            var sourceLength = gzipExpandedBytes ?? fileStream.Length;
            if (gzipExpandedBytes.HasValue && sourceLength % sectorSize != 0)
            {
                throw new InvalidDataException($"The expanded gzip data is not aligned to the {sectorSize}-byte sector size.");
            }

            var paddedBytes = AlignmentHelper.AlignTo((ulong)sourceLength, sectorSize);
            if (paddedBytes == 0)
            {
                Logging.Log("No data to write after alignment.", LogLevel.Warning);
                return 0;
            }

            var sectorsToWrite = paddedBytes / sectorSize;
            if (!manager.IsDirectMode)
            {
                try
                {
                    checked
                    {
                        var endSector = startSector + sectorsToWrite - 1;
                        Logging.Log($"Calculated end sector: {endSector}", LogLevel.Debug);
                    }
                }
                catch (OverflowException)
                {
                    Logging.Log("Error: Sector range exceeds supported bounds.", LogLevel.Error);
                    return 1;
                }
            }

            var targetDescription = manager.GetTargetDescription(effectiveLun);
            Logging.Log($"Writing {sectorsToWrite} sectors ({paddedBytes} bytes) to {targetDescription}, starting at sector {startSector}...");

            var writeStopwatch = Stopwatch.StartNew();
            var progress = new ProgressReporter(writeStopwatch, "Writing");

            using var rawStream = gzipExpandedBytes.HasValue
                ? new ExactLengthGzipStream(fileStream, sourceLength)
                : null;
            await manager.WriteSectorsFromStreamAsync(
                effectiveLun,
                startSector,
                (Stream?)rawStream ?? fileStream,
                sourceLength,
                padToSector: !gzipExpandedBytes.HasValue,
                inputFile.Name,
                progress.Report);

            writeStopwatch.Stop();
            Console.WriteLine();

            var bytesWrittenReported = progress.BytesReported == 0 && paddedBytes > 0
                ? (long)Math.Min(paddedBytes, long.MaxValue)
                : progress.BytesReported;
            Logging.Log($"Successfully wrote {bytesWrittenReported / (1024.0 * 1024.0):F2} MiB in {writeStopwatch.Elapsed.TotalSeconds:F2}s.");

            return 0;
        });
    }

    private sealed class ExactLengthGzipStream(Stream input, long length) : Stream
    {
        private readonly GZipStream _gzip = new(input, CompressionMode.Decompress, leaveOpen: true);
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position
        {
            get => Length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _remaining == 0)
            {
                return 0;
            }

            var read = _gzip.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            if (read == 0)
            {
                // Firehose permits padding ordinary files; short gzip input must fail instead.
                throw new InvalidDataException("The gzip stream ended before its declared expanded byte length.");
            }

            _remaining -= read;
            return _remaining == 0 && _gzip.ReadByte() != -1
                ? throw new InvalidDataException("The gzip stream exceeds its declared expanded byte length.")
                : read;
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gzip.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
