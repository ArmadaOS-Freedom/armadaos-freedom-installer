using System;
using System.Windows;
using System.Windows.Controls;

namespace FreedomInstaller.App
{
    public partial class MainWindow
    {
        private int _auxiliaryWorkflowStep;
        private bool _renderingAuxiliaryWorkflow;

        private AuxiliaryWorkflowNavigation GetAuxiliaryNavigation()
        {
            if (_showingGlobalRecovery)
            {
                bool proof = _globalEdlClient?.HasInitialSaharaProof == true;
                bool busy = _busy || _globalOperationActive;
                bool frozen = _globalRestoreUncertain || _globalResetUnknown;
                bool inputReady;
                bool confirmationReady;
                if (_globalRecoveryMode == GlobalRecoveryMode.Backup)
                {
                    inputReady = proof;
                    confirmationReady = proof && !string.IsNullOrEmpty(_globalOutputParent);
                }
                else
                {
                    inputReady = GlobalRestoreEnvironmentReady;
                    confirmationReady = proof && _globalRestorePlan != null;
                }
                return new AuxiliaryWorkflowNavigation(_auxiliaryWorkflowStep, busy, frozen, inputReady, confirmationReady);
            }

            bool offlineInputReady = _offlineSsh != null && !string.IsNullOrEmpty(_offlineConnectedModel);
            bool offlineConfirmationReady =
                OfflineUpgradePanel.ConfirmCheck.IsEnabled || OfflineUpgradePanel.RestartToVerifyButton.IsEnabled;
            bool offlineBusy = _busy || _offlineUpgradeActive;
            bool offlineFrozen = _offlineUpgradeCloseInProgress;
            return new AuxiliaryWorkflowNavigation(_auxiliaryWorkflowStep, offlineBusy, offlineFrozen, offlineInputReady, offlineConfirmationReady);
        }

        private void RefreshAuxiliaryWorkflowNavigation()
        {
            if (!_ready || AuxiliaryWorkflowStepNav.Visibility != Visibility.Visible)
                return;

            var nav = GetAuxiliaryNavigation();
            AuxiliaryWorkflowNavDevice.IsEnabled = nav.CanSelect(0);
            AuxiliaryWorkflowNavInput.IsEnabled = nav.CanSelect(1);
            AuxiliaryWorkflowNavConfirm.IsEnabled = nav.CanSelect(2);
            AuxiliaryWorkflowPreviousButton.IsEnabled = nav.CanSelect(nav.Step - 1) && nav.Step > 0;
            AuxiliaryWorkflowNextButton.IsEnabled = nav.CanSelect(nav.Step + 1) && nav.Step < 2;

            BackToFunctionsButton.IsEnabled = !nav.Busy && !nav.Frozen &&
                (!_showingGlobalRecovery || !HasGlobalRecoveryProtectedState);
        }

        private void RenderAuxiliaryWorkflow()
        {
            if (!_ready || AuxiliaryWorkflowStepNav.Visibility != Visibility.Visible)
                return;

            _renderingAuxiliaryWorkflow = true;
            try
            {
                int step = _auxiliaryWorkflowStep;
                AuxiliaryWorkflowNavDevice.IsChecked = step == 0;
                AuxiliaryWorkflowNavInput.IsChecked = step == 1;
                AuxiliaryWorkflowNavConfirm.IsChecked = step == 2;

                if (_showingGlobalRecovery)
                {
                    bool backup = _globalRecoveryMode == GlobalRecoveryMode.Backup;
                    SetText(SelectedOperationText, backup ? "Static.Global.BackupTitle" : "Static.Global.RestoreTitle");
                    SetText(WorkflowNavigationTitle, "Static.AuxiliaryWorkflow.Steps");
                    SetText(AuxiliaryWorkflowDeviceLabel, backup ? "Static.NavConnect" : "Static.Global.RestoreConnect");
                    SetText(AuxiliaryWorkflowInputLabel, backup ? "Static.Global.BackupDirectoryLabel" : "Static.AuxiliaryWorkflow.VerifyBackup");
                    SetText(AuxiliaryWorkflowConfirmLabel, backup ? "Static.AuxiliaryWorkflow.ConfirmBackup" : "Static.AuxiliaryWorkflow.ConfirmRestore");

                    GlobalConnectionStep.Visibility = ShowWhen(step == 0);
                    GlobalAndroidConnectionGuide.Visibility = ShowWhen(step == 0 && backup);
                    GlobalManualEdlGuide.Visibility = ShowWhen(step == 0 && !backup);
                    GlobalOutputSection.Visibility = ShowWhen(step == 1 && backup);
                    GlobalBackupCard.Visibility = ShowWhen(step == 2 && backup);
                    GlobalRestoreCard.Visibility = ShowWhen(step >= 1 && !backup);
                    GlobalRestoreSourceInputs.Visibility = ShowWhen(step == 1 && !backup);
                    GlobalRestoreConfirmation.Visibility = ShowWhen(step == 2 && !backup);

                    SetContent(BackToFunctionsButton, EnvironmentIsReady ? "Dynamic.BackToFunctions" : "Static.Environment.Title");
                }
                else
                {
                    SetText(SelectedOperationText, "Static.OfflineUpdate.Title");
                    SetText(WorkflowNavigationTitle, "Static.AuxiliaryWorkflow.Steps");
                    SetText(AuxiliaryWorkflowDeviceLabel, "Static.NavConnect");
                    SetText(AuxiliaryWorkflowInputLabel, "Static.AuxiliaryWorkflow.ChooseUpgradePackage");
                    SetText(AuxiliaryWorkflowConfirmLabel, "Static.AuxiliaryWorkflow.ConfirmUpgrade");

                    OfflineUpgradePanel.OfflineConnectionStep.Visibility = ShowWhen(step == 0);
                    OfflineUpgradePanel.OfflinePackageStep.Visibility = ShowWhen(step == 1);
                    OfflineUpgradePanel.OfflineConfirmationStep.Visibility = ShowWhen(step == 2);

                    SetContent(BackToFunctionsButton, "Dynamic.BackToFunctions");
                }
            }
            finally
            {
                _renderingAuxiliaryWorkflow = false;
            }

            RefreshAuxiliaryWorkflowNavigation();
        }

        private static Visibility ShowWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        private void ResetAuxiliaryWorkflowStep()
        {
            _auxiliaryWorkflowStep = 0;
            GlobalRecoveryPage.ScrollToTop();
            OfflineUpgradePanel.OfflineWorkflowScrollViewer.ScrollToTop();
        }

        private void OnAuxiliaryWorkflowStepChecked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio || radio.Tag is not string tag || !int.TryParse(tag, out int target))
                return;
            TrySelectAuxiliaryWorkflowStep(target);
        }

        private void OnAuxiliaryWorkflowShift(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag || !int.TryParse(tag, out int delta))
                return;
            TrySelectAuxiliaryWorkflowStep(_auxiliaryWorkflowStep + delta);
        }

        private void TrySelectAuxiliaryWorkflowStep(int target)
        {
            if (!_ready || _renderingAuxiliaryWorkflow || AuxiliaryWorkflowStepNav.Visibility != Visibility.Visible)
                return;
            if (target < 0 || target > 2)
                return;

            int previous = _auxiliaryWorkflowStep;
            if (!GetAuxiliaryNavigation().CanSelect(target))
            {
                RenderAuxiliaryWorkflow();
                return;
            }

            _auxiliaryWorkflowStep = target;
            RenderAuxiliaryWorkflow();
            if (previous == target)
                return;

            if (_showingGlobalRecovery)
                GlobalRecoveryPage.ScrollToTop();
            else
                OfflineUpgradePanel.OfflineWorkflowScrollViewer.ScrollToTop();
        }
    }
}
