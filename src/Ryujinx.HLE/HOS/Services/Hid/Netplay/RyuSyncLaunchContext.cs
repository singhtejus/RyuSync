using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    // Lives with the emulated device, not the TCP connection. Disconnecting must
    // never switch a running guest back to its personal save.
    public sealed class RyuSyncLaunchContext : IDisposable
    {
        private readonly object _randomLock = new();
        private readonly byte[] _seed;
        private byte[] _randomBlock = [];
        private int _randomOffset;
        private ulong _randomCounter;

        public long UnixTime { get; }
        public string GuestSaveDirectory { get; }

        public RyuSyncLaunchContext(RyuSyncLaunchSnapshot snapshot, bool guest)
        {
            _seed = Convert.FromHexString(snapshot.Manifest.Seed);
            UnixTime = snapshot.Manifest.UnixTime;
            if (guest)
            {
                GuestSaveDirectory = Directory.CreateTempSubdirectory("ryusync-save-").FullName;
                try
                {
                    snapshot.Extract(GuestSaveDirectory);
                }
                catch
                {
                    Directory.Delete(GuestSaveDirectory, recursive: true);
                    throw;
                }
            }
        }

        // Share the game's csrng byte stream without changing host/network cryptography.
        // Equal seeds do not make thread scheduling or game simulation deterministic.
        public void FillRandom(Span<byte> destination)
        {
            lock (_randomLock)
            {
                Span<byte> counter = stackalloc byte[8];
                while (!destination.IsEmpty)
                {
                    if (_randomOffset == _randomBlock.Length)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(counter, _randomCounter++);
                        _randomBlock = HMACSHA256.HashData(_seed, counter);
                        _randomOffset = 0;
                    }
                    int length = Math.Min(destination.Length, _randomBlock.Length - _randomOffset);
                    _randomBlock.AsSpan(_randomOffset, length).CopyTo(destination);
                    _randomOffset += length;
                    destination = destination[length..];
                }
            }
        }

        public void Dispose()
        {
            if (GuestSaveDirectory != null && Directory.Exists(GuestSaveDirectory))
            {
                Directory.Delete(GuestSaveDirectory, recursive: true);
            }
        }
    }
}
