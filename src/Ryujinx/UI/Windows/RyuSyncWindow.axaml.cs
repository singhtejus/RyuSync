using Avalonia.Threading;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System;

namespace Ryujinx.Ava.UI.Windows
{
    public partial class RyuSyncWindow : StyleableWindow
    {
        private readonly RyuSyncSession _session = RyuSyncSession.Instance;

        public RyuSyncWindow()
        {
            InitializeComponent();

            _session.StateChanged += Session_StateChanged;
            Closed += (_, _) => _session.StateChanged -= Session_StateChanged;
            _session.StartListening();
            UpdateUi();
        }

        private async void InviteButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                InviteButton.IsEnabled = false;
                await _session.SendInvitationAsync(PeerAddressBox.Text);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Invitation failed: {ex.Message}";
            }
            finally
            {
                UpdateUi();
            }
        }

        private void AcceptButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _session.AcceptInvitation();
        }

        private void DeclineButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _session.DeclineInvitation();
        }

        private void ReadyButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _session.SetReady(!_session.LocalReady);
        }

        private void DisconnectButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _session.Disconnect();
        }

        private void Session_StateChanged()
        {
            Dispatcher.UIThread.Post(UpdateUi);
        }

        private void UpdateUi()
        {
            bool connected = _session.IsSessionEstablished;

            StatusText.Text = _session.Status;
            RoleText.Text = _session.Role switch
            {
                RyuSyncRole.Host => "Role: Player 1 (host)",
                RyuSyncRole.Guest => "Role: Player 2 (guest)",
                _ => "Role: not connected",
            };

            InvitationPanel.IsVisible = _session.InvitationPending;
            InvitationText.Text = _session.InvitationPending
                ? $"Incoming invitation from {_session.RemoteDisplayName}. Accept to join as Player 2."
                : string.Empty;

            InviteButton.IsEnabled = !connected && !_session.InvitationPending;
            PeerAddressBox.IsEnabled = InviteButton.IsEnabled;
            ReadyButton.IsEnabled = connected;
            DisconnectButton.IsEnabled = connected;
            ReadyButton.Content = _session.LocalReady ? "Not Ready" : "Ready";

            if (connected && _session.RemoteReady && _session.LocalReady)
            {
                StatusText.Text = _session.BothGamesStarted
                    ? _session.Status
                    : "Both players ready. Launch SSBU on both computers.";
            }
        }
    }
}
