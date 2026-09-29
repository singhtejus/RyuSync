using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    // One launch per connection. Save data shares the existing ordered TCP channel.
    public sealed class RyuSyncLaunchTransfer
    {
        private const int ChunkSize = 48 * 1024;
        private readonly RyuSyncRole _role;
        private readonly Action<string> _send;
        private readonly Action<string> _status;
        private readonly CancellationTokenSource _disconnected = new();
        private readonly TaskCompletionSource<RyuSyncLaunchSnapshot> _snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RyuSyncLaunchManifest _manifest;
        private MemoryStream _received;
        private int _started;
        public bool Started => Volatile.Read(ref _started) != 0 || _manifest != null;

        public RyuSyncLaunchTransfer(RyuSyncRole role, Action<string> send, Action<string> status)
        {
            _role = role;
            _send = send;
            _status = status;
        }

        public async Task PrepareAsync(string gameVersion, string settings,
            Func<RyuSyncLaunchSnapshot> createHostSnapshot, Action<RyuSyncLaunchSnapshot> prepareLocal,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("Reconnect RyuSync before launching another game.");
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnected.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            CancellationToken token = timeout.Token;
            try
            {
                if (_role == RyuSyncRole.Host)
                {
                    _status("Preparing host save for both players...");
                    RyuSyncLaunchSnapshot snapshot = await Task.Run(createHostSnapshot, token);
                    await Task.Run(() => prepareLocal(snapshot), token);
                    _status("Sending host save; waiting for the guest to launch the same SSBU version...");
                    await Task.Run(() =>
                    {
                        string header = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(snapshot.Manifest,
                            RyuSyncLaunchJsonContext.Default.RyuSyncLaunchManifest));
                        _send("SAVE_BEGIN|" + header);
                        for (int offset = 0; offset < snapshot.SaveArchive.Length; offset += ChunkSize)
                        {
                            token.ThrowIfCancellationRequested();
                            int length = Math.Min(ChunkSize, snapshot.SaveArchive.Length - offset);
                            _send("SAVE_CHUNK|" + Convert.ToBase64String(snapshot.SaveArchive, offset, length));
                        }
                        _send("SAVE_END");
                    }, token);

                    string hash = await _prepared.Task.WaitAsync(token);
                    if (hash != snapshot.Manifest.SaveHash)
                    {
                        throw new InvalidDataException("The guest acknowledged a different save snapshot.");
                    }
                    _send("SAVE_GO|" + hash);
                }
                else
                {
                    _status("Waiting for the host's SSBU save...");
                    RyuSyncLaunchSnapshot snapshot = await _snapshot.Task.WaitAsync(token);
                    string mismatch = DescribeMismatch(snapshot.Manifest, gameVersion, settings);
                    if (mismatch != null)
                    {
                        throw new InvalidDataException(mismatch);
                    }

                    _status("Verifying host save and preparing a separate session copy...");
                    await Task.Run(() => prepareLocal(snapshot), token);
                    _send("SAVE_READY|" + snapshot.Manifest.SaveHash);
                    string hash = await _go.Task.WaitAsync(token);
                    if (hash != snapshot.Manifest.SaveHash)
                    {
                        throw new InvalidDataException("The host confirmed a different save snapshot.");
                    }
                }

                token.ThrowIfCancellationRequested();
                _status("Host save verified on both computers; launching SSBU.");
            }
            catch (Exception ex)
            {
                _send("SAVE_ABORT");
                _status("Save synchronization failed: " + ex.Message + " Reconnect before retrying.");
                throw;
            }
        }

        private static string DescribeMismatch(RyuSyncLaunchManifest host, string guestVersion, string guestSettings)
        {
            List<string> differences = [];
            void Compare(string name, string hostValue, string guestValue)
            {
                if (hostValue != guestValue)
                {
                    differences.Add($"{name}: host = {hostValue}; guest = {guestValue}");
                }
            }

            Compare("SSBU version", host.GameVersion, guestVersion);
            if (host.Settings != guestSettings)
            {
                string[] names = ["Firmware", "Language", "Region", "Time zone", "Docked mode", "Emulated CPU speed multiplier (not your computer's CPU)"];
                string[] hostValues = host.Settings.Split('|');
                string[] guestValues = guestSettings.Split('|');
                if (hostValues.Length == names.Length && guestValues.Length == names.Length)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        Compare(names[i], hostValues[i], guestValues[i]);
                    }
                }
                else
                {
                    differences.Add("Settings format differs. Both players must use the same RyuSync build.");
                }
            }

            return differences.Count == 0 ? null : "RyuSync launch settings differ:\n" + string.Join("\n", differences);
        }

        // Called only by this connection's control reader, so receive state has one writer.
        public void HandleMessage(string[] parts)
        {
            try
            {
                switch (parts[0])
                {
                    case "SAVE_BEGIN" when _role == RyuSyncRole.Guest:
                        if (_manifest != null || parts.Length != 2 || parts[1].Length > 8192)
                            throw new InvalidDataException("Unexpected save header.");
                        _manifest = JsonSerializer.Deserialize(Convert.FromBase64String(parts[1]),
                            RyuSyncLaunchJsonContext.Default.RyuSyncLaunchManifest);
                        if (_manifest == null || _manifest.TitleId != RyuSyncLaunchSnapshot.SmashTitleId ||
                            _manifest.SaveLength <= 0 || _manifest.SaveLength > RyuSyncLaunchSnapshot.MaximumSaveBytes)
                            throw new InvalidDataException("Invalid save transfer size or title.");
                        _received = new MemoryStream();
                        break;
                    case "SAVE_CHUNK" when _role == RyuSyncRole.Guest:
                        if (_received == null || parts.Length != 2 || parts[1].Length > ChunkSize * 4 / 3)
                            throw new InvalidDataException("Unexpected save data.");
                        byte[] data = Convert.FromBase64String(parts[1]);
                        if (_received.Length + data.Length > _manifest.SaveLength)
                            throw new InvalidDataException("Save transfer exceeds its declared size.");
                        _received.Write(data);
                        break;
                    case "SAVE_END" when _role == RyuSyncRole.Guest:
                        if (_received == null || parts.Length != 1)
                            throw new InvalidDataException("Unexpected end of save transfer.");
                        RyuSyncLaunchSnapshot snapshot = new(_manifest, _received.ToArray());
                        _received.Dispose();
                        _received = null;
                        _snapshot.TrySetResult(snapshot);
                        break;
                    case "SAVE_READY" when _role == RyuSyncRole.Host && parts.Length == 2:
                        _prepared.TrySetResult(parts[1]);
                        break;
                    case "SAVE_GO" when _role == RyuSyncRole.Guest && parts.Length == 2:
                        _go.TrySetResult(parts[1]);
                        break;
                    case "SAVE_ABORT":
                        Cancel();
                        break;
                    default:
                        throw new InvalidDataException("Unexpected RyuSync save message.");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException or ArgumentException)
            {
                _received?.Dispose();
                _received = null;
                _status("Host save transfer was rejected: " + ex.Message);
                _send("SAVE_ABORT");
                Cancel();
            }
        }

        public void Cancel() => _disconnected.Cancel();
    }

    [JsonSerializable(typeof(RyuSyncLaunchManifest))]
    internal partial class RyuSyncLaunchJsonContext : JsonSerializerContext;
}
