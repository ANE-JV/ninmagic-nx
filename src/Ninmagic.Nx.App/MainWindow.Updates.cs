using System.Windows;
using Ninmagic.Nx.Updates;

namespace Ninmagic.Nx.App;

public partial class MainWindow
{
    private bool updateOpen;
    private bool updateRestart;
    internal const string CurrentVersion = "1.6.0";

    private bool CanOpenUpdates()
    {
        if (preview) { ShowStartNotice("Las actualizaciones están desactivadas en la vista de demostración."); return false; }
        if (operation != null || refreshing || updateOpen)
        {
            ShowStartNotice("Espera a que termine la operación o la búsqueda de tarjetas antes de actualizar la aplicación.");
            return false;
        }
        return true;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenUpdates()) return;
        updateOpen = true;
        Inputs.IsEnabled = VerificationOptions.IsEnabled = ModeButtons.IsEnabled = ActionButton.IsEnabled =
            VerifyButton.IsEnabled = AdminButton.IsEnabled = UpdateButton.IsEnabled = false;
        try
        {
            using var updateService = new WindowsUpdateService(CurrentVersion);
            var dialog = new UpdateDialog(new WindowsUpdateUiService(updateService)) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                updateRestart = true;
                Application.Current.Shutdown();
            }
        }
        finally
        {
            updateOpen = false;
            if (!updateRestart)
                Inputs.IsEnabled = VerificationOptions.IsEnabled = ModeButtons.IsEnabled = ActionButton.IsEnabled =
                    VerifyButton.IsEnabled = AdminButton.IsEnabled = UpdateButton.IsEnabled = true;
        }
    }
}
