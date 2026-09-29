using Ryujinx.Ava.Common;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.ViewModels
{
    public partial class MainWindowViewModel
    {
        private bool _ryuSyncLaunchPending;

        private async Task<RyuSyncLaunchContext> PrepareRyuSyncLaunchAsync(ApplicationData application)
        {
            RyuSyncSession session = RyuSyncSession.Instance;
            if (!session.IsSessionEstablished)
            {
                return null;
            }
            if (application.Id != RyuSyncLaunchSnapshot.SmashTitleId)
            {
                throw new InvalidOperationException("RyuSync currently supports only Super Smash Bros. Ultimate. Disconnect to launch another title.");
            }
            if (!session.LocalReady || !session.RemoteReady)
            {
                throw new InvalidOperationException("Both players must click Ready before launching SSBU.");
            }

            ConfigurationState config = ConfigurationState.Instance;
            string settings = string.Join("|", ContentManager.GetCurrentFirmwareVersion()?.VersionString,
                config.System.Language.Value, config.System.Region.Value,
                config.System.TimeZone.Value, config.System.EnableDockedMode.Value, config.System.TickScalar.Value);
            long offset = config.System.MatchSystemTime.Value ? 0 : config.System.SystemTimeOffset.Value;
            bool guest = session.Role == RyuSyncRole.Guest;
            RyuSyncLaunchContext launch = null;

            try
            {
                await session.LaunchTransfer.PrepareAsync(application.Version, settings,
                    () => ApplicationHelper.CreateRyuSyncSnapshot(application.Version, settings,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offset),
                    snapshot => launch = new RyuSyncLaunchContext(snapshot, guest), CancellationToken.None);
                if (!session.IsSessionEstablished)
                {
                    throw new InvalidOperationException("The peer disconnected before SSBU could start.");
                }
                return launch;
            }
            catch
            {
                launch?.Dispose();
                throw;
            }
        }
    }
}
