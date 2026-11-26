using System.CommandLine;
using System.Diagnostics;
using System.Security.Cryptography;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;

namespace QCEDL.CLI.Commands;

internal sealed class ReadSectorCommand
{
    private static readonly Argument<ulong> StartSectorArgument = new("start_sector", "The starting sector LBA to read from.");
    private static readonly Argument<ulong> SectorsArgument = new("sectors", "The number of sectors to read.");
    private static readonly Argument<FileInfo> FilenameArgument = new("filename", "The file to save the read data to.") { Arity = ArgumentArity.ExactlyOne };

    private static readonly Argument<string> ReadSectorDestinationArgument = new("filename", "The output filename, or '-' with --sha256-only.")
    {
        Arity = ArgumentArity.ExactlyOne
    };

    private static readonly Option<bool> Sha256OnlyOption = new(
        "--sha256-only", "Compute SHA-256 from the readback without creating an output file.");

    private static readonly Option<uint> LunOption = new(
        aliases: ["--lun", "-u"],
        description: "Specify the LUN number to read from.",
        getDefaultValue: () => 0);

    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command("read-sector", "Reads a specified number of sectors from a given LUN and start LBA, saving to a file.")
        {
            StartSectorArgument,
            SectorsArgument,
            ReadSectorDestinationArgument,
            LunOption,
            Sha256OnlyOption
        };

        command.SetHandler(async context =>
        {
            var parsed = context.ParseResult;
            context.ExitCode = await ExecuteAsync(
                globalOptionsBinder.Bind(context.BindingContext),
                parsed.GetValueForArgument(StartSectorArgument),
                parsed.GetValueForArgument(SectorsArgument),
                parsed.GetValueForArgument(ReadSectorDestinationArgument),
                parsed.GetValueForOption(LunOption),
                parsed.GetValueForOption(Sha256OnlyOption));
        });

        return command;
    }

    public static Command CreateReadLunCommand(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command("read-lun", "Reads the entire LUN (all sectors) from a given LUN, saving to a file.")
        {
            FilenameArgument,
            LunOption // Command-specific LUN
        };

        command.SetHandler(ExecuteReadLunAsync,
            globalOptionsBinder,
            FilenameArgument,
            LunOption);

        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionsBinder globalOptions,
        ulong startSector,
        ulong sectorsToRead,
        string destination,
        uint lun,
        bool sha256Only)
    {
        if (sha256Only && destination != "-")
        {
            Logging.Log("Error: --sha256-only requires '-' as the output filename.", LogLevel.Error);
            return 1;
        }

        var outputFile = sha256Only ? null : new FileInfo(destination);
        Logging.Log($"Executing 'read-sector' command: LUN {lun}, Start LBA {startSector}, Sectors {sectorsToRead}, Destination '{outputFile?.FullName ?? "SHA-256"}'...", LogLevel.Trace);

        return await ExecuteReadSectorsAsync(globalOptions, startSector, sectorsToRead, outputFile, lun, "read-sector");
    }

    private static async Task<int> ExecuteReadLunAsync(
        GlobalOptionsBinder globalOptions,
        FileInfo outputFile,
        uint lun)
    {
        Logging.Log($"Executing 'read-lun' command: LUN {lun}, File '{outputFile.FullName}'...", LogLevel.Trace);

        // For read-lun, we'll determine the total sectors dynamically
        return await ExecuteReadSectorsAsync(globalOptions, 0, 0, outputFile, lun, "read-lun", readEntireLun: true);
    }

    private static async Task<int> ExecuteReadSectorsAsync(
        GlobalOptionsBinder globalOptions,
        ulong startSector,
        ulong sectorsToRead,
        FileInfo? outputFile,
        uint lun,
        string commandName,
        bool readEntireLun = false)
    {
        static void TryDeletePartialFile(FileInfo file, long expectedBytes)
        {
            try
            {
                if (file.Exists && expectedBytes > 0 && file.Length < expectedBytes)
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                Logging.Log($"Could not delete partial file '{file.FullName}': {ex.Message}", LogLevel.Warning);
            }
        }

        return await CommandExecutor.RunAsync(commandName, async () =>
        {
            using var manager = new EdlManager(globalOptions);

            var effectiveLun = lun;
            if (manager.IsDirectMode && lun != 0)
            {
                if (outputFile is null)
                {
                    throw new InvalidOperationException("SHA-256 readback only supports LUN 0 in direct mode.");
                }

                Logging.Log("Warning: LUN parameter is ignored in direct mode.", LogLevel.Warning);
                effectiveLun = 0;
            }

            var geometry = await manager.GetStorageGeometryAsync(effectiveLun);
            var sectorSize = geometry.SectorSize;
            var targetDescription = manager.GetTargetDescription(effectiveLun);
            Logging.Log($"Using sector size: {sectorSize} bytes for {targetDescription}.", LogLevel.Debug);

            if (readEntireLun)
            {
                if (geometry.TotalSectors.HasValue && geometry.TotalSectors.Value > 0)
                {
                    sectorsToRead = geometry.TotalSectors.Value;
                    Logging.Log($"Read-lun: Total blocks from storage info: {sectorsToRead}", LogLevel.Info);
                }
                else
                {
                    Logging.Log("Error: Could not determine total blocks for read-lun command. Storage info unavailable.", LogLevel.Error);
                    return 1;
                }
            }

            if (sectorsToRead == 0)
            {
                Logging.Log("Error: Number of sectors to read must be greater than 0.", LogLevel.Error);
                return 1;
            }

            ulong endSector;
            try
            {
                endSector = checked(startSector + sectorsToRead - 1);
            }
            catch (OverflowException)
            {
                Logging.Log("Error: Sector range exceeds supported bounds.", LogLevel.Error);
                return 1;
            }

            if (!manager.IsDirectMode && (startSector > uint.MaxValue || endSector > uint.MaxValue))
            {
                Logging.Log("Error: Sector range exceeds uint.MaxValue, which is not supported by the current Firehose.Read implementation.", LogLevel.Error);
                return 1;
            }

            decimal totalBytesDecimal;
            try
            {
                totalBytesDecimal = checked((decimal)sectorsToRead * sectorSize);
            }
            catch (OverflowException)
            {
                totalBytesDecimal = decimal.MaxValue;
            }

            if (totalBytesDecimal <= 0)
            {
                Logging.Log($"Warning: Calculated total bytes to read is {totalBytesDecimal}. Nothing to read.", LogLevel.Warning);
                if (outputFile is not null)
                {
                    await File.WriteAllBytesAsync(outputFile.FullName, []);
                }

                return 0;
            }

            var totalBytesToRead = totalBytesDecimal > long.MaxValue ? long.MaxValue : (long)totalBytesDecimal;
            Logging.Log($"Preparing to read {sectorsToRead} sectors (LBA {startSector} to {endSector}, {totalBytesDecimal} bytes) from {targetDescription} into '{outputFile?.FullName ?? "SHA-256"}'...");

            var readStopwatch = new Stopwatch();
            var progress = new ProgressReporter(readStopwatch, "Reading");

            if (outputFile is null)
            {
                if (totalBytesDecimal > long.MaxValue)
                {
                    throw new InvalidOperationException("The readback byte count exceeds supported bounds.");
                }

                using var hashStream = new ReadbackHashStream(totalBytesToRead);

                readStopwatch.Start();
                await manager.ReadSectorsToStreamAsync(
                    effectiveLun, startSector, sectorsToRead, hashStream, progress.Report);
                readStopwatch.Stop();
                Console.WriteLine();

                Logging.Log($"Sector read SHA-256: {hashStream.Length} bytes, {hashStream.Finish()}");
                return 0;
            }

            try
            {
                outputFile.Directory?.Create();
                using var fileStream = outputFile.Open(FileMode.Create, FileAccess.Write, FileShare.None);

                readStopwatch.Start();
                await manager.ReadSectorsToStreamAsync(
                    effectiveLun,
                    startSector,
                    sectorsToRead,
                    fileStream,
                    progress.Report);
                readStopwatch.Stop();
            }
            catch (IOException ioEx)
            {
                Logging.Log($"IO Error creating/writing to file '{outputFile.FullName}': {ioEx.Message}", LogLevel.Error);
                Console.WriteLine();
                return 1;
            }
            catch
            {
                Console.WriteLine();
                TryDeletePartialFile(outputFile, totalBytesToRead);
                throw;
            }

            Console.WriteLine();

            var bytesReadReported = progress.BytesReported == 0 ? totalBytesToRead : progress.BytesReported;
            Logging.Log($"Successfully read {bytesReadReported / (1024.0 * 1024.0):F2} MiB and wrote to '{outputFile.FullName}' in {readStopwatch.Elapsed.TotalSeconds:F2}s.");

            return 0;
        });
    }

    private sealed class ReadbackHashStream(long expectedBytes) : Stream
    {
        private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > expectedBytes - _length)
            {
                throw new InvalidDataException("The streamed readback exceeded the requested byte count.");
            }

            _sha256.AppendData(buffer);
            _length += buffer.Length;
        }

        internal string Finish()
        {
            return _length == expectedBytes
                ? Convert.ToHexString(_sha256.GetHashAndReset())
                : throw new InvalidDataException($"The streamed readback contained {_length} bytes, expected {expectedBytes}.");
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sha256.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
