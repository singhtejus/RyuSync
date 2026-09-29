using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using System.IO;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    public static class RyuSyncSaveMount
    {
        public static SharedRef<IFileSystem> Open(string root, SaveDataType type, bool readOnly)
        {
            if (type is not (SaveDataType.Account or SaveDataType.Device))
            {
                throw new InvalidDataException("Unsupported RyuSync save type.");
            }

            string path = System.IO.Path.Combine(root, type == SaveDataType.Account ? "account" : "device");
            // Never open a personal save as a fallback if the session copy fails.
            if (!Directory.Exists(path))
            {
                throw new IOException("The RyuSync session save is missing. Stop and reconnect before retrying.");
            }
            using SharedRef<IFileSystem> local = new(new LocalFileSystem(path));
            return readOnly ? new SharedRef<IFileSystem>(new ReadOnlyFileSystem(in local))
                : SharedRef<IFileSystem>.CreateCopy(in local);
        }
    }
}
