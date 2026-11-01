using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ninmagic.Nx.Updates;

namespace Ninmagic.Nx.App;

public interface IUpdateUiService
{
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    Task<StagedUpdate> DownloadAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken cancellationToken);
    void StartInstall(StagedUpdate update);
}

internal sealed class WindowsUpdateUiService(WindowsUpdateService service) : IUpdateUiService
{
    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken) => service.CheckAsync(cancellationToken);
    public Task<StagedUpdate> DownloadAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        => service.DownloadAsync(update, progress, cancellationToken);
    public void StartInstall(StagedUpdate update) => service.StartInstall(update);
}

public sealed class UpdateDialog : Window
{
    private readonly IUpdateUiService service;
    private readonly TextBlock heading = new() { FontSize = 23, FontWeight = FontWeights.SemiBold, Text = "Actualizaciones de ninmagic-nx" };
    private readonly TextBlock status = new() { Text = "Busca una versión nueva para Windows.", Margin = new Thickness(0, 14, 0, 8) };
    private readonly TextBlock notes = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock progressDetail = new() { FontSize = 12, Margin = new Thickness(0, 8, 0, 12) };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly Button check = new() { Content = "Buscar de nuevo", Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button install = new() { Content = "Actualizar y reiniciar", IsEnabled = false, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button close = new() { Content = "Cerrar", MinWidth = 90 };
    private CancellationTokenSource? work;
    private UpdateInfo? available;
    private bool installing;
    private bool handoffReady;
    private bool closingAfterWork;
    private bool started;

    public UpdateDialog(IUpdateUiService service, bool autoCheck = true)
    {
        this.service = service;
        Title = "ninmagic-nx · Actualizaciones";
        Width = 650; Height = 650; MinWidth = 560; MinHeight = 530;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Surface");
        Foreground = new SolidColorBrush(Color.FromRgb(240, 244, 250));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        progress.Foreground = (Brush)Application.Current.FindResource("Accent");
        progress.Background = new SolidColorBrush(Color.FromRgb(38, 55, 76));
        install.Style = (Style)Application.Current.FindResource("Primary");
        System.Windows.Automation.AutomationProperties.SetName(status, "Estado de la actualización");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        var grid = new Grid { Margin = new Thickness(26) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new StackPanel(); title.Children.Add(heading);
        title.Children.Add(new TextBlock { Text = "Versión instalada: " + MainWindow.CurrentVersion + " · Windows de 64 bits", FontSize = 12, Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 8, 0, 0) });
        grid.Children.Add(title);
        var body = new StackPanel(); body.Children.Add(status);
        body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(13, 25, 42)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 5, 0, 14), Child = notes });
        notes.Text = "La aplicación comprueba la descarga antes de instalarla. Se cerrará y abrirá de nuevo al actualizar; tus copias de seguridad se conservan.";
        body.Children.Add(progress); body.Children.Add(progressDetail);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 4, 0, 10) };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(install); buttons.Children.Add(check); buttons.Children.Add(close);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons); Content = grid;
        check.Click += async (_, _) => await CheckAsync();
        install.Click += async (_, _) => await InstallAsync();
        close.Click += (_, _) => CloseOrCancel();
        Closing += ClosingDialog;
        Loaded += async (_, _) =>
        {
            if (started) return; started = true;
            if (autoCheck) await CheckAsync();
        };
    }

    private async Task CheckAsync()
    {
        if (work != null) return;
        available = null; notes.Text = "Consultando las versiones publicadas para Windows…";
        SetBusy(true); work = new CancellationTokenSource();
        status.Text = "Buscando actualizaciones…";
        try
        {
            available = await service.CheckAsync(work.Token);
            status.Text = available is null ? "Ya tienes la versión más reciente disponible para Windows." : "Nueva versión disponible: " + available.Version;
            notes.Text = available is null ? "Puedes seguir utilizando ninmagic-nx. Vuelve a buscar cuando haya una nueva versión." :
                string.IsNullOrWhiteSpace(available.Notes) ? "Esta versión incluye mejoras publicadas por NinMagic." : available.Notes;
            progressDetail.Text = available is null ? "Comprobación terminada." : "Pulsa Actualizar y reiniciar para instalarla desde aquí.";
        }
        catch (OperationCanceledException) { status.Text = "Búsqueda cancelada."; progressDetail.Text = "La aplicación sigue como estaba."; }
        catch (Exception ex) { status.Text = "No se pudo comprobar la actualización."; notes.Text = "La versión instalada se conserva. Comprueba la conexión y vuelve a intentarlo."; progressDetail.Text = ex.Message; }
        finally { FinishWork(); }
    }

    private async Task InstallAsync()
    {
        if (work != null || available is null) return;
        var update = available;
        work = new CancellationTokenSource(); SetBusy(true); progress.Visibility = Visibility.Visible;
        status.Text = "Descargando la versión " + update.Version + "…";
        try
        {
            var reporter = new Progress<UpdateProgress>(value =>
            {
                if (work == null || installing) return;
                progress.IsIndeterminate = value.Total <= 0;
                progress.Value = value.Fraction * 100;
                progressDetail.Text = value.Total > 0 ? $"{value.Fraction:P0} · {value.Completed / 1_048_576.0:0.0} de {value.Total / 1_048_576.0:0.0} MB" : "Recibiendo la actualización…";
            });
            var staged = await service.DownloadAsync(update, reporter, work.Token);
            work.Token.ThrowIfCancellationRequested();
            status.Text = "Descarga comprobada · preparando el reinicio…";
            progressDetail.Text = "La aplicación se cerrará y volverá a abrirse con la nueva versión.";
            installing = true; close.IsEnabled = false;
            await Task.Run(() => service.StartInstall(staged));
            handoffReady = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) { status.Text = "Actualización cancelada."; progressDetail.Text = "La versión instalada se conserva."; }
        catch (UnauthorizedAccessException ex) { status.Text = "Windows no permite escribir en la carpeta de la aplicación."; progressDetail.Text = ex.Message; }
        catch (Exception ex) { status.Text = "No se pudo instalar la actualización."; progressDetail.Text = ex.Message; }
        finally { installing = false; FinishWork(); }
    }

    private void SetBusy(bool busy)
    {
        check.IsEnabled = !busy;
        install.IsEnabled = !busy && available != null;
        close.IsEnabled = !installing; close.Content = busy ? "Cancelar" : "Cerrar";
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = busy; progress.Value = 0;
    }

    private void FinishWork()
    {
        work?.Dispose(); work = null; SetBusy(false);
        if (closingAfterWork) Close();
    }

    private void CloseOrCancel()
    {
        if (installing) return;
        if (work is null) { Close(); return; }
        close.IsEnabled = false;
        progressDetail.Text = "Cancelando la descarga o consulta…";
        work.Cancel();
    }

    private void ClosingDialog(object? sender, CancelEventArgs e)
    {
        if (installing) { e.Cancel = !handoffReady; return; }
        if (work == null) return;
        e.Cancel = true; closingAfterWork = true; CloseOrCancel();
    }
}
