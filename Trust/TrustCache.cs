using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReplayGlitchGTA;

/// <summary>
/// Short-lived memory of Authenticode verdicts, keyed by path and by a fingerprint of the
/// opened file.
/// </summary>
/// <remarks>
/// Signature verification costs tens of milliseconds and the detector runs on a timer, so an
/// uncached verdict would be paid on every tick. The fingerprint is what makes the cache safe:
/// volume, file index, size, and both timestamps come from the handle the caller already holds,
/// so a file swapped underneath a known-good path no longer matches its entry. Verdicts also
/// expire, and a rejection expires sooner than an acceptance so a repaired install is picked up
/// quickly.
/// </remarks>
internal static class TrustCache
{
    private const int MaximumCacheEntries = 16;
    private const int TrustedCacheLifetimeSeconds = 30;
    private const int RejectedCacheLifetimeSeconds = 15;
    private const int FileBasicInfoClass = 0;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the identity of the opened file. Returns <c>false</c> when it cannot be
    /// established, in which case the caller must verify without consulting the cache.
    /// </summary>
    internal static bool TryGetFingerprint(
        SafeFileHandle fileHandle, out FileFingerprint fingerprint)
    {
        fingerprint = default;
        if (!GetFileInformationByHandle(fileHandle, out var standardInfo) ||
            !GetFileInformationByHandleEx(fileHandle, FileBasicInfoClass,
                out var basicInfo, (uint)Marshal.SizeOf(typeof(FileBasicInfo))))
        {
            return false;
        }

        fingerprint = new FileFingerprint(
            standardInfo.VolumeSerialNumber,
            ((ulong)standardInfo.FileIndexHigh << 32) | standardInfo.FileIndexLow,
            ((ulong)standardInfo.FileSizeHigh << 32) | standardInfo.FileSizeLow,
            basicInfo.LastWriteTime,
            basicInfo.ChangeTime);
        return true;
    }

    internal static bool TryGet(
        string path, FileFingerprint fingerprint, long now, out bool trusted)
    {
        lock (CacheLock)
        {
            if (Entries.TryGetValue(path, out var entry) &&
                entry.Fingerprint.Equals(fingerprint) &&
                now < entry.ExpiresAt)
            {
                trusted = entry.Trusted;
                return true;
            }
        }

        trusted = false;
        return false;
    }

    internal static void Store(
        string path, FileFingerprint fingerprint, bool trusted, long now)
    {
        var lifetime = trusted
            ? TrustedCacheLifetimeSeconds
            : RejectedCacheLifetimeSeconds;
        var expiresAt = now + Stopwatch.Frequency * lifetime;
        lock (CacheLock)
        {
            if (Entries.Count >= MaximumCacheEntries && !Entries.ContainsKey(path))
            {
                Entries.Clear();
            }
            Entries[path] = new CacheEntry(fingerprint, trusted, expiresAt);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle, int fileInformationClass,
        out FileBasicInfo fileInformation, uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    private sealed class CacheEntry
    {
        internal CacheEntry(FileFingerprint fingerprint, bool trusted, long expiresAt)
        {
            Fingerprint = fingerprint;
            Trusted = trusted;
            ExpiresAt = expiresAt;
        }

        internal FileFingerprint Fingerprint { get; }
        internal bool Trusted { get; }
        internal long ExpiresAt { get; }
    }

    internal readonly struct FileFingerprint : IEquatable<FileFingerprint>
    {
        private readonly uint _volumeSerialNumber;
        private readonly ulong _fileIndex;
        private readonly ulong _fileSize;
        private readonly long _lastWriteTime;
        private readonly long _changeTime;

        internal FileFingerprint(
            uint volumeSerialNumber, ulong fileIndex, ulong fileSize,
            long lastWriteTime, long changeTime)
        {
            _volumeSerialNumber = volumeSerialNumber;
            _fileIndex = fileIndex;
            _fileSize = fileSize;
            _lastWriteTime = lastWriteTime;
            _changeTime = changeTime;
        }

        public bool Equals(FileFingerprint other) =>
            _volumeSerialNumber == other._volumeSerialNumber &&
            _fileIndex == other._fileIndex &&
            _fileSize == other._fileSize &&
            _lastWriteTime == other._lastWriteTime &&
            _changeTime == other._changeTime;
    }
}
