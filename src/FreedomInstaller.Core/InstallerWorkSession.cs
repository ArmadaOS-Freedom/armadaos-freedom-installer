using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FreedomInstaller.Core;

/// <summary>Owns one application's generated work, never the selected backup or source package.</summary>
public sealed class InstallerWorkSession : IDisposable
{
    private const string LeaseMagic = "FWLS1";
    private const string RetiredLeaseMagic = "FWLS0";
    private const string RecoveryMarkerName = ".recovery-pending";
    private const string BackupMarkerName = ".backup-pending";
    private const string LeasesDirectoryName = ".session-leases";

    public InstallerWorkSession(string root)
        : this(Path.GetFullPath(root), Guid.NewGuid().ToString("N"), null)
    {
    }

    private InstallerWorkSession(string fullRoot, string id, FileStream? existingLease)
    {
        _workRoot = fullRoot;
        DirectoryPath = Path.Combine(fullRoot, "sessions", id);
        RecordsDirectory = Path.Combine(fullRoot, "records", id);
        _leasePath = Path.Combine(fullRoot, LeasesDirectoryName, id + ".lease");

        RefuseReparsePointsInAncestors(fullRoot);
        if (existingLease is not null)
        {
            _lease = existingLease;
            return;
        }

        string sessionsRoot = Path.Combine(fullRoot, "sessions");
        string leasesRoot = Path.Combine(fullRoot, LeasesDirectoryName);
        RefuseReparsePoint(leasesRoot);
        Directory.CreateDirectory(leasesRoot);
        RefuseReparsePoint(leasesRoot);

        FileStream? lease = null;
        try
        {
            lease = new FileStream(
                _leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            byte[] magic = Encoding.UTF8.GetBytes(LeaseMagic);
            lease.Write(magic, 0, magic.Length);
            lease.Flush(true);
        }
        catch
        {
            lease?.Dispose();
            if (lease is not null) TryRetireLease(_leasePath);
            throw;
        }

        try
        {
            RefuseReparsePoint(sessionsRoot);
            RefuseReparsePoint(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            RefuseReparsePoint(DirectoryPath);
        }
        catch
        {
            lease.Dispose();
            TryRetireLease(_leasePath);
            throw;
        }

        _lease = lease;
    }

    private readonly HashSet<Guid> _unverifiedRecoveryBackups = new();
    private readonly string _leasePath;
    private FileStream? _lease;
    private readonly string _workRoot;

    private static void RefuseReparsePointsInAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null;
            current = Path.GetDirectoryName(current))
        {
            RefuseReparsePoint(current);
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal)) break;
        }
    }

    private static void RefuseReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing work session path through a reparse point.");
    }

    private static void TryRetireLease(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string DirectoryPath { get; }
    public string RecordsDirectory { get; }

    /// <summary>True while any begun automatic backup attempt has not been confirmed by the execution side.</summary>
    public bool HasUnverifiedRecoveryBackups => _unverifiedRecoveryBackups.Count > 0;

    /// <summary>Registers a distinct unverified backup attempt and returns its token.</summary>
    public Guid BeginRecoveryBackup()
    {
        RefuseReparsePoint(DirectoryPath);
        string marker = Path.Combine(DirectoryPath, BackupMarkerName);
        RefuseReparsePoint(marker);
        using (var stream = new FileStream(
            marker, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            byte[] magic = Encoding.UTF8.GetBytes(LeaseMagic);
            stream.Write(magic, 0, magic.Length);
            stream.Flush(true);
        }
        var token = Guid.NewGuid();
        _unverifiedRecoveryBackups.Add(token);
        return token;
    }

    /// <summary>Ends only the supplied attempt, keeping recovery protection until no attempts remain.</summary>
    public void AbandonRecoveryBackup(Guid token) => CompleteRecoveryToken(token);

    /// <summary>Marks this session as needing recovery even if the process exits unexpectedly.</summary>
    public void ProtectRecovery()
    {
        RefuseReparsePoint(DirectoryPath);
        string marker = Path.Combine(DirectoryPath, RecoveryMarkerName);
        RefuseReparsePoint(marker);
        using var stream = new FileStream(
            marker, FileMode.Create, FileAccess.Write, FileShare.Read);
        byte[] magic = Encoding.UTF8.GetBytes(LeaseMagic);
        stream.Write(magic, 0, magic.Length);
        stream.Flush(true);
    }

    /// <summary>Confirms only the supplied attempt. Unknown or already-confirmed tokens clear nothing.</summary>
    public void ConfirmRecoveryBackup(Guid token) => CompleteRecoveryToken(token);

    private void CompleteRecoveryToken(Guid token)
    {
        if (!_unverifiedRecoveryBackups.Remove(token)) return;
        if (_unverifiedRecoveryBackups.Count > 0) return;
        string marker = Path.Combine(DirectoryPath, BackupMarkerName);
        try
        {
            RefuseReparsePoint(marker);
            File.Delete(marker);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Installer work session kept its backup marker after an attempt completed: " + exception.Message);
        }
    }

    public void DiscardPreparation(string path, bool preserveRecovery)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(DirectoryPath, full);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Refusing preparation cleanup outside this installer session.");
        if (preserveRecovery || !Directory.Exists(full)) return;
        for (DirectoryInfo? item = new(full); item is not null && item.FullName != DirectoryPath; item = item.Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing preparation cleanup through a directory link.");
        CopyReports(new DirectoryInfo(full));
        DeleteOwnedTree(new DirectoryInfo(full));
    }

    public void Complete(bool preserveRecovery)
    {
        if (preserveRecovery)
        {
            if (!Directory.Exists(DirectoryPath)) return;
            try
            {
                ProtectRecovery();
            }
            catch (Exception first)
            {
                Exception? retired = TryRetireLeaseUnderExclusiveHold();
                if (retired is not null)
                    throw new IOException("Could not preserve installer recovery files for the next startup.", new AggregateException(first, retired));
                throw;
            }
            return;
        }

        if (HasUnverifiedRecoveryBackups || !Directory.Exists(DirectoryPath)) return;
        RefuseReparsePointsInAncestors(_workRoot);
        RefuseReparsePoint(DirectoryPath);
        // Save readable receipts before removing images, readbacks and extracted tools.
        CopyReports(new DirectoryInfo(DirectoryPath));
        DeleteOwnedTree(new DirectoryInfo(DirectoryPath));
        Dispose();
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
        if (Directory.Exists(DirectoryPath)) return;
        TryRetireLease(_leasePath);
    }

    private Exception? TryRetireLeaseUnderExclusiveHold()
    {
        FileStream? lease = _lease;
        if (lease is null) return null;
        try
        {
            lease.SetLength(0);
            byte[] magic = Encoding.UTF8.GetBytes(RetiredLeaseMagic);
            lease.Write(magic, 0, magic.Length);
            lease.Flush(true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return exception;
        }
    }

    public static int SweepAbandoned(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        string leasesDir = Path.Combine(fullRoot, LeasesDirectoryName);
        string sessionsDir = Path.Combine(fullRoot, "sessions");
        string recordsDir = Path.Combine(fullRoot, "records");
        int swept = 0;
        try
        {
            RefuseReparsePointsInAncestors(fullRoot);
            RefuseReparsePoint(leasesDir);
            RefuseReparsePoint(sessionsDir);
            RefuseReparsePoint(recordsDir);
            if (!Directory.Exists(leasesDir)) return 0;
            foreach (string leasePath in Directory.GetFiles(leasesDir, "*.lease"))
            {
                try
                {
                    string id = Path.GetFileNameWithoutExtension(leasePath);
                    if (!Guid.TryParseExact(id, "N", out _)) continue;
                    RefuseReparsePoint(leasePath);
                    using FileStream lease = new(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    byte[] magic = new byte[Encoding.UTF8.GetByteCount(LeaseMagic)];
                    if (lease.Length != magic.Length || lease.Read(magic, 0, magic.Length) != magic.Length
                        || !string.Equals(Encoding.UTF8.GetString(magic), LeaseMagic, StringComparison.Ordinal)) continue;
                    string sessionDir = Path.Combine(sessionsDir, id);
                    RefuseReparsePoint(sessionDir);
                    RefuseReparsePoint(Path.Combine(recordsDir, id));
                    if (HasRecoveryMarker(sessionDir)) continue;
                    if (HasBackupMarker(sessionDir)) continue;
                    if (!Directory.Exists(sessionDir)) continue;
                    using var orphan = new InstallerWorkSession(fullRoot, id, lease);
                    orphan.Complete(false);
                    if (!Directory.Exists(sessionDir)) swept++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException or NotSupportedException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
        }
        return swept;
    }

    private static bool HasRecoveryMarker(string sessionDir) => HasMarker(sessionDir, RecoveryMarkerName);

    private static bool HasBackupMarker(string sessionDir) => HasMarker(sessionDir, BackupMarkerName);

    private static bool HasMarker(string sessionDir, string markerName)
    {
        try
        {
            File.GetAttributes(Path.Combine(sessionDir, markerName));
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private void CopyReports(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
        foreach (FileInfo file in directory.GetFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string extension = file.Extension.ToLowerInvariant();
            if (extension is not (".json" or ".jsonl" or ".log" or ".txt")) continue;
            string destination = Path.Combine(RecordsDirectory, Path.GetRelativePath(DirectoryPath, file.FullName));
            RefuseReparsePointsInAncestors(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            file.CopyTo(destination, overwrite: true);
        }
        foreach (DirectoryInfo child in directory.GetDirectories()) CopyReports(child);
    }

    private void DeleteOwnedTree(DirectoryInfo directory)
    {
        string relative = Path.GetRelativePath(DirectoryPath, directory.FullName);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Refusing cleanup outside the owned installer session.");
        if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (FileInfo file in directory.GetFiles()) file.Delete();
            foreach (DirectoryInfo child in directory.GetDirectories()) DeleteOwnedTree(child);
        }
        directory.Delete();
    }
}
