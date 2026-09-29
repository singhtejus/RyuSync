using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Text;
using Path = System.IO.Path;

string root = Directory.CreateTempSubdirectory("ryusync-mount-tests-").FullName;
try
{
    string personal = Path.Combine(root, "personal");
    string session = Path.Combine(root, "session");
    Directory.CreateDirectory(personal);
    Directory.CreateDirectory(Path.Combine(session, "account"));
    Directory.CreateDirectory(Path.Combine(session, "device"));
    File.WriteAllText(Path.Combine(personal, "progress"), "personal");
    File.WriteAllText(Path.Combine(session, "account", "progress"), "hostsave");

    using LibHac.Fs.Path path = new();
    path.Initialize("/progress"u8).ThrowIfFailure();
    using (SharedRef<IFileSystem> save = RyuSyncSaveMount.Open(session, SaveDataType.Account, readOnly: false))
    {
        using (UniqueRef<IFile> file = new())
        {
            save.Get.OpenFile(ref file.Ref, in path, OpenMode.ReadWrite).ThrowIfFailure();
            byte[] data = new byte[8];
            file.Get.Read(out long count, 0, data, ReadOption.None).ThrowIfFailure();
            if (count != 8 || Encoding.UTF8.GetString(data) != "hostsave") throw new Exception("Wrong save mounted");
            file.Get.Write(0, "session!"u8, WriteOption.Flush).ThrowIfFailure();
        }
        save.Get.Commit().ThrowIfFailure();
    }

    if (File.ReadAllText(Path.Combine(personal, "progress")) != "personal" ||
        File.ReadAllText(Path.Combine(session, "account", "progress")) != "session!")
        throw new Exception("Save writes did not remain isolated");

    using (SharedRef<IFileSystem> save = RyuSyncSaveMount.Open(session, SaveDataType.Account, readOnly: true))
    {
        using UniqueRef<IFile> file = new();
        if (save.Get.OpenFile(ref file.Ref, in path, OpenMode.Write).IsSuccess())
            throw new Exception("Read-only save allowed writes");
        save.Get.OpenFile(ref file.Ref, in path, OpenMode.Read).ThrowIfFailure();
    }

    using (SharedRef<IFileSystem> save = RyuSyncSaveMount.Open(session, SaveDataType.Device, readOnly: false))
    {
        save.Get.CreateFile(in path, 0).ThrowIfFailure();
        save.Get.Commit().ThrowIfFailure();
    }
    if (!File.Exists(Path.Combine(session, "device", "progress"))) throw new Exception("Device save was not isolated");

    bool rejected = false;
    try { using var missing = RyuSyncSaveMount.Open(Path.Combine(root, "missing"), SaveDataType.Account, false); }
    catch (IOException) { rejected = true; }
    if (!rejected) throw new Exception("Missing session save was silently replaced");

    Console.WriteLine("PASS: real save filesystem reads, writes, commit, read-only mode and guest isolation.");
}
finally
{
    Directory.Delete(root, recursive: true);
}
