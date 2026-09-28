using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System;

namespace Ryujinx.Ava.UI.Views.Main
{
    public partial class MainViewControls : RyujinxControl<MainWindowViewModel>
    {
        private RyuSyncWindow _ryuSyncWindow;

        public MainViewControls()
        {
            InitializeComponent();

            RyuSyncSession.Instance.InvitationReceived += RyuSyncSession_InvitationReceived;
            RyuSyncSession.Instance.StartListening();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (VisualRoot is MainWindow window)
            {
                ViewModel = window.ViewModel;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            RyuSyncSession.Instance.InvitationReceived -= RyuSyncSession_InvitationReceived;
            base.OnDetachedFromVisualTree(e);
        }

        public void Sort_Checked(object sender, RoutedEventArgs args)
        {
            if (sender is RadioButton { Tag: string sortStrategy })
                ViewModel.Sort(Enum.Parse<ApplicationSort>(sortStrategy));
        }

        public void Order_Checked(object sender, RoutedEventArgs args)
        {
            if (sender is RadioButton { Tag: string sortOrder })
                ViewModel.Sort(sortOrder is not "Descending");
        }

        private void SearchBox_OnKeyUp(object sender, KeyEventArgs e)
        {
            ViewModel.SearchText = SearchBox.Text;
        }

        private void RyuSyncButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowRyuSyncWindow();
        }

        private void RyuSyncSession_InvitationReceived()
        {
            Dispatcher.UIThread.Post(ShowRyuSyncWindow);
        }

        private void ShowRyuSyncWindow()
        {
            if (_ryuSyncWindow != null)
            {
                _ryuSyncWindow.Activate();
                return;
            }

            _ryuSyncWindow = new RyuSyncWindow();
            _ryuSyncWindow.Closed += (_, _) => _ryuSyncWindow = null;

            if (VisualRoot is MainWindow owner)
            {
                _ryuSyncWindow.Show(owner);
            }
            else
            {
                _ryuSyncWindow.Show();
            }
        }
    }
}
