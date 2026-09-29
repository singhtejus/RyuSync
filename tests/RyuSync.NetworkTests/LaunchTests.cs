using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class LaunchTests
{
    public static async Task Run()
    {
        string root = Directory.CreateTempSubdirectory("ryusync-tests-").FullName;
        try
        {
            string hostSave = Path.Combine(root, "host");
            string personalSave = Path.Combine(root, "guest-personal");
            Directory.CreateDirectory(hostSave);
            Directory.CreateDirectory(personalSave);
            File.WriteAllText(Path.Combine(hostSave, "progress.bin"), "host unlocks and settings");
            // Multiple chunks exercise transfer boundaries without relying on compressible fixtures.
            File.WriteAllBytes(Path.Combine(hostSave, "large.bin"), RandomNumberGenerator.GetBytes(150000));
            File.WriteAllText(Path.Combine(personalSave, "progress.bin"), "guest's original progress");
            RyuSyncLaunchSnapshot snapshot = RyuSyncLaunchSnapshot.Create("13.0.4", "matching-settings", 1800000000,
                new Dictionary<string, string> { ["account"] = hostSave });

            await CheckTransfer(snapshot, personalSave);
            await CheckMismatch(snapshot);
            await CheckDisconnect(snapshot);
            await CheckCorruption(snapshot);
            CheckUnsafeArchives(snapshot);
            CheckEmptySave();
            Console.WriteLine("PASS: save transfer, isolation, shared randomness, mismatch/corruption rejection and disconnect handling.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (RyuSyncLaunchTransfer Host, RyuSyncLaunchTransfer Guest) Pair(Func<string, string> alter = null)
    {
        RyuSyncLaunchTransfer guest = null;
        RyuSyncLaunchTransfer host = new(RyuSyncRole.Host,
            line => guest.HandleMessage((alter?.Invoke(line) ?? line).Split('|')), _ => { });
        guest = new(RyuSyncRole.Guest, line => host.HandleMessage(line.Split('|')), _ => { });
        return (host, guest);
    }

    private static async Task CheckTransfer(RyuSyncLaunchSnapshot snapshot, string personalSave)
    {
        RyuSyncLaunchContext hostContext = null;
        RyuSyncLaunchContext guestContext = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource hostSentSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = Pair(line =>
        {
            if (line == "SAVE_END") hostSentSave.TrySetResult();
            return line;
        });
        RyuSyncLaunchTransfer host = pair.Host;
        RyuSyncLaunchTransfer guest = pair.Guest;

        try
        {
            Task hostTask = host.PrepareAsync("13.0.4", "matching-settings", () => snapshot,
                data => hostContext = new(data, guest: false), timeout.Token);
            await hostSentSave.Task.WaitAsync(timeout.Token);
            if (hostTask.IsCompleted) throw new Exception("Host launched before the guest prepared its save");

            Task guestTask = guest.PrepareAsync("13.0.4", "matching-settings", () => throw new Exception("Guest tried to export its own save"),
                data => guestContext = new(data, guest: true), timeout.Token);
            await Task.WhenAll(hostTask, guestTask);

            string copiedSave = Path.Combine(guestContext.GuestSaveDirectory, "account", "progress.bin");
            if (File.ReadAllText(copiedSave) != "host unlocks and settings")
                throw new Exception("Guest did not receive the host's progress");

            File.WriteAllText(copiedSave, "guest session changes");
            guest.Cancel(); // Losing the connection must not change the mounted save directory.
            if (!File.Exists(copiedSave) || File.ReadAllText(Path.Combine(personalSave, "progress.bin")) != "guest's original progress")
                throw new Exception("Guest personal save was changed or session save disappeared on disconnect");

            byte[] hostRandom = new byte[100];
            byte[] guestRandom = new byte[100];
            hostContext.FillRandom(hostRandom);
            guestContext.FillRandom(guestRandom.AsSpan(0, 13));
            guestContext.FillRandom(guestRandom.AsSpan(13));
            if (!hostRandom.SequenceEqual(guestRandom) || hostContext.UnixTime != guestContext.UnixTime)
                throw new Exception("Startup time or random byte streams differ");

            string temporarySave = guestContext.GuestSaveDirectory;
            guestContext.Dispose();
            if (Directory.Exists(temporarySave)) throw new Exception("Session save was not cleaned up");
        }
        finally
        {
            hostContext?.Dispose();
            guestContext?.Dispose();
            host.Cancel();
            guest.Cancel();
        }
    }

    private static async Task CheckMismatch(RyuSyncLaunchSnapshot snapshot)
    {
        foreach (bool versionMismatch in new[] { true, false })
        {
            var (host, guest) = Pair();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            bool prepared = false;
            Task hostTask = host.PrepareAsync("13.0.4", "matching-settings", () => snapshot, _ => { }, timeout.Token);
            Task guestTask = guest.PrepareAsync(versionMismatch ? "wrong-version" : "13.0.4",
                versionMismatch ? "matching-settings" : "wrong-settings", () => snapshot, _ => prepared = true, timeout.Token);
            await MustFail(guestTask);
            await MustFail(hostTask);
            if (prepared) throw new Exception("Guest installed an incompatible snapshot");
        }
    }

    private static async Task CheckDisconnect(RyuSyncLaunchSnapshot snapshot)
    {
        var (host, guest) = Pair();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task guestTask = guest.PrepareAsync("13.0.4", "matching-settings", () => snapshot, _ => { }, timeout.Token);
        guest.Cancel();
        await MustFail(guestTask);
        host.Cancel();
    }

    private static async Task CheckCorruption(RyuSyncLaunchSnapshot snapshot)
    {
        bool changed = false;
        var (host, guest) = Pair(line =>
        {
            if (!changed && line.StartsWith("SAVE_CHUNK|"))
            {
                byte[] bytes = Convert.FromBase64String(line.Split('|')[1]);
                bytes[0] ^= 1;
                changed = true;
                return "SAVE_CHUNK|" + Convert.ToBase64String(bytes);
            }
            return line;
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        bool prepared = false;
        Task hostTask = host.PrepareAsync("13.0.4", "matching-settings", () => snapshot, _ => { }, timeout.Token);
        Task guestTask = guest.PrepareAsync("13.0.4", "matching-settings", () => snapshot, _ => prepared = true, timeout.Token);
        await MustFail(guestTask);
        await MustFail(hostTask);
        if (prepared) throw new Exception("Guest installed a corrupt snapshot");
    }

    private static void CheckUnsafeArchives(RyuSyncLaunchSnapshot valid)
    {
        foreach (string name in new[] { "account/../../escape", "/tmp/escape", "account/C:escape", "account/../escape", "account/..\\escape" })
        {
            using MemoryStream stream = new();
            using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes("must not escape"));
            }
            byte[] bytes = stream.ToArray();
            RyuSyncLaunchSnapshot bad = new(valid.Manifest with
            {
                SaveHash = Convert.ToHexString(SHA256.HashData(bytes)),
                SaveLength = bytes.Length,
            }, bytes);
            string directory = Directory.CreateTempSubdirectory("ryusync-unsafe-test-").FullName;
            try
            {
                bool rejected = false;
                try { bad.Extract(directory); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new Exception("Unsafe archive was not rejected before extraction");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void CheckEmptySave()
    {
        RyuSyncLaunchSnapshot snapshot = RyuSyncLaunchSnapshot.Create("13.0.4", "settings", 1800000000,
            new Dictionary<string, string>());
        using RyuSyncLaunchContext guest = new(snapshot, guest: true);
        if (Directory.EnumerateFileSystemEntries(Path.Combine(guest.GuestSaveDirectory, "account")).Any())
            throw new Exception("An absent host save must produce an empty guest session save");
    }

    private static async Task MustFail(Task task)
    {
        try { await task; }
        catch (Exception) { return; }
        throw new Exception("Launch unexpectedly succeeded");
    }
}
