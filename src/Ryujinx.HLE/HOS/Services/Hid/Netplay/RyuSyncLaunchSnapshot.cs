using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    public sealed record RyuSyncLaunchManifest(
        ulong TitleId, string GameVersion, string Settings, string Seed, long UnixTime, string SaveHash, int SaveLength);

    public sealed class RyuSyncLaunchSnapshot
    {
        public const ulong SmashTitleId = 0x01006a800016e000;
        public const int MaximumSaveBytes = 256 * 1024 * 1024;
        private const int MaximumEntries = 10000;

        public RyuSyncLaunchManifest Manifest { get; }
        public byte[] SaveArchive { get; }

        public RyuSyncLaunchSnapshot(RyuSyncLaunchManifest manifest, byte[] saveArchive)
        {
            if (manifest.TitleId != SmashTitleId || manifest.SaveLength <= 0 ||
                manifest.SaveLength > MaximumSaveBytes || manifest.SaveLength != saveArchive.Length ||
                manifest.GameVersion == null || manifest.GameVersion.Length > 128 ||
                manifest.Settings == null || manifest.Settings.Length > 1024 ||
                manifest.Seed == null || manifest.Seed.Length != 64 ||
                Convert.FromHexString(manifest.Seed).Length != 32 ||
                Convert.ToHexString(SHA256.HashData(saveArchive)) != manifest.SaveHash)
            {
                throw new InvalidDataException("Invalid or incomplete RyuSync launch snapshot.");
            }

            _ = DateTimeOffset.FromUnixTimeSeconds(manifest.UnixTime);
            Manifest = manifest;
            SaveArchive = saveArchive;
        }

        public static RyuSyncLaunchSnapshot Create(string gameVersion, string settings, long unixTime,
            IReadOnlyDictionary<string, string> saveDirectories)
        {
            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                long total = 0;
                int count = 0;
                foreach (string kind in new[] { "account", "device" })
                {
                    archive.CreateEntry(kind + "/");
                    if (!saveDirectories.TryGetValue(kind, out string directory) || directory == null)
                    {
                        continue; // A missing host save means an empty session save, never the guest's save.
                    }

                    RejectLink(directory);
                    foreach (string path in EnumerateTree(directory))
                    {
                        if (++count > MaximumEntries)
                        {
                            throw new InvalidDataException("Too many files in the host save.");
                        }

                        string relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                        string entryName = kind + "/" + relative;
                        ValidateEntryName(entryName);
                        if (Directory.Exists(path))
                        {
                            archive.CreateEntry(entryName + "/");
                            continue;
                        }

                        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        total = checked(total + input.Length);
                        if (total > MaximumSaveBytes)
                        {
                            throw new InvalidDataException("Host save exceeds the 256 MiB transfer limit.");
                        }

                        using Stream destination = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                        input.CopyTo(destination);
                    }
                }
            }

            byte[] bytes = output.ToArray();
            RyuSyncLaunchManifest manifest = new(SmashTitleId, gameVersion, settings,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), unixTime,
                Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            return new RyuSyncLaunchSnapshot(manifest, bytes);
        }

        // The caller supplies a newly created session directory, never a user's save directory.
        public void Extract(string sessionDirectory)
        {
            if (!Directory.Exists(sessionDirectory) || Directory.EnumerateFileSystemEntries(sessionDirectory).Any())
            {
                throw new IOException("RyuSync save destination must be a new, empty directory.");
            }

            RejectLink(sessionDirectory);
            using ZipArchive archive = new(new MemoryStream(SaveArchive, writable: false), ZipArchiveMode.Read);
            long total = 0;
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            if (archive.Entries.Count > MaximumEntries + 2)
            {
                throw new InvalidDataException("Too many files in the received save.");
            }

            // Validate every path and declared length before writing any entry.
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ValidateEntryName(entry.FullName);
                if (!names.Add(entry.FullName.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new InvalidDataException("Duplicate paths or links in the received save.");
                }

                total = checked(total + entry.Length);
                if (total > MaximumSaveBytes)
                {
                    throw new InvalidDataException("Received save exceeds the 256 MiB extraction limit.");
                }
            }

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destination = Path.Combine(sessionDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using Stream source = entry.Open();
                using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write);
                byte[] buffer = new byte[65536];
                long remaining = entry.Length;
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    remaining -= read;
                    if (remaining < 0)
                    {
                        throw new InvalidDataException("Received save entry exceeds its declared size.");
                    }
                    target.Write(buffer, 0, read);
                }
                if (remaining != 0)
                {
                    throw new InvalidDataException("Truncated save entry.");
                }
            }

            Directory.CreateDirectory(Path.Combine(sessionDirectory, "account"));
            Directory.CreateDirectory(Path.Combine(sessionDirectory, "device"));
        }

        private static IEnumerable<string> EnumerateTree(string root)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
            {
                RejectLink(path);
                yield return path;
                if (Directory.Exists(path))
                {
                    foreach (string child in EnumerateTree(path)) yield return child;
                }
            }
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Links are not allowed in RyuSync save snapshots.");
            }
        }

        private static void ValidateEntryName(string name)
        {
            string[] parts = name.TrimEnd('/').Split('/');
            if (name.Contains('\\') || parts.Length == 0 || parts[0] is not ("account" or "device") ||
                parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                    part.IndexOfAny([':', '\0', '<', '>', '"', '|', '?', '*']) >= 0))
            {
                throw new InvalidDataException("Unsafe path in the received save.");
            }
        }
    }
}
