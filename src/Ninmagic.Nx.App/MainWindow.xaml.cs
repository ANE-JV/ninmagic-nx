using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Ninmagic.Nx.Core;
using Ninmagic.Nx.Windows;

namespace Ninmagic.Nx.App;

public partial class MainWindow : Window
{
    private readonly WindowsDiskAccess disks = new();
    private readonly BackupService service;
    private readonly bool preview = Environment.GetCommandLineArgs().Contains("--preview");
    private CancellationTokenSource? operation;
    private bool restoring;
    private bool refreshing;
    private string? lastBackup;
    private readonly string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NinmagicNX", "Logs");
    private string logFile = "";
    private string lastStage = "";
    private long lastProgressTick;
    private int lastStepNumber;
    private int progressGeneration;
    private readonly Stopwatch operationClock = new();
    private readonly ProgressEstimator progressEstimator = new();
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WorkProgress? latestProgress;
    private static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public MainWindow()
    {
        InitializeComponent();
        progressTimer.Tick += (_, _) => RenderEstimate(progressEstimator.Tick(operationClock.Elapsed));
        service = new BackupService(disks);
        FolderText.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        AdminBanner.Visibility = IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            if (preview)
            {
                Title += " · Vista de demostración";
                AdminBanner.Visibility = Visibility.Collapsed;
                DiskCombo.ItemsSource = new[] { new DiskInfo(2, "Lector de microSD · Ejemplo", "DEMO", "DEMO", 64_000_000_000, 512, "USB", false, false, false, "MBR", new[] { new VolumeInfo(1, 16_777_216, 32_000_000_000, "E:\\", "FAT32", "SWITCH") }) };
                DiskCombo.SelectedIndex = 0;
                ActionButton.IsEnabled = false;
                Summary.Text = "Vista de demostración: las operaciones sobre tarjetas están desactivadas.";
            }
            else await RefreshDisksAsync();
        };
        Closing += OnClosing;
    }

    private async Task RefreshDisksAsync()
    {
        if (operation != null || preview || refreshing) return;
        refreshing = true;
        Inputs.IsEnabled = VerificationOptions.IsEnabled = ModeButtons.IsEnabled = VerifyButton.IsEnabled = AdminButton.IsEnabled = ActionButton.IsEnabled = false;
        try
        {
            var selected = (DiskCombo.SelectedItem as DiskInfo)?.Identity;
            var available = await disks.ListDisksAsync(CancellationToken.None);
            DiskCombo.ItemsSource = available;
            DiskCombo.SelectedItem = available.FirstOrDefault(d => d.Identity == selected);
            if (DiskCombo.SelectedItem == null && available.Count == 1) DiskCombo.SelectedIndex = 0;
            if (available.Count == 0) DiskDetails.Text = "No hay tarjetas compatibles. Conecta un lector USB/SD y pulsa Actualizar.";
        }
        catch (Exception ex) { StatusText.Text = "No se pudieron consultar las tarjetas"; ProgressDetail.Text = ex.Message; }
        finally { refreshing = false; Inputs.IsEnabled = VerificationOptions.IsEnabled = ModeButtons.IsEnabled = VerifyButton.IsEnabled = AdminButton.IsEnabled = ActionButton.IsEnabled = true; }
    }

    private void SetMode(bool restore)
    {
        if (operation != null || refreshing) return;
        restoring = restore;
        BackupTab.Style = (Style)FindResource(restore ? typeof(Button) : "Primary");
        RestoreTab.Style = (Style)FindResource(restore ? "Primary" : typeof(Button));
        SectionTitle.Text = restore ? "Prepara tu tarjeta nueva" : "Guarda todo antes de empezar";
        SectionDescription.Text = restore ? "Revisaremos la copia y, tras confirmar el borrado, prepararemos la tarjeta nueva y restauraremos tus datos." : "La copia incluye archivos visibles, ocultos y las particiones emuMMC compatibles. La tarjeta original se abre solo para lectura.";
        DiskLabel.Text = restore ? "MICROSD NUEVA · SE BORRARÁ COMPLETAMENTE" : "MICROSD ORIGINAL";
        FolderLabel.Text = restore ? "CARPETA DE LA COPIA TERMINADA" : "CARPETA PARA LA COPIA EN EL PC";
        FolderHint.Text = restore ? "Elige la carpeta que contiene manifest.json, files y raw." : "Necesitas espacio para los archivos y la emuMMC. Se creará una carpeta nueva dentro de la elegida.";
        ActionButton.Content = restore ? "Comprobar copia y preparar traslado" : "Crear y verificar copia";
        VerifyButton.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        VerificationOptions.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        if (restore && lastBackup != null) FolderText.Text = lastBackup;
        Summary.Text = restore ? "La nueva debe tener al menos la capacidad de la original. Si está en NTFS, se preparará en FAT32. Elige si quieres verificar los archivos después de copiarlos." : "La copia se comprueba con SHA-256 antes de poder restaurarla. Puedes usar un solo lector.";
        UpdateDiskDetails();
        ActionButton.IsEnabled = !preview;
    }
    private void BackupTab_Click(object sender, RoutedEventArgs e) => SetMode(false);
    private void RestoreTab_Click(object sender, RoutedEventArgs e) => SetMode(true);
    private void VerificationOptionChanged(object sender, RoutedEventArgs e)
    {
        if (VerificationHint == null) return;
        var verify = VerifyFilesOption.IsChecked == true;
        VerificationHint.Text = verify ? "Relee los archivos guardados en la tarjeta para detectar errores. Tarda más." :
            "Más rápido: archivos sin comprobación final, incluida la emuMMC por archivos. La emuMMC en partición se verifica.";
        VerificationHint.Foreground = verify ? (Brush)FindResource("Muted") : Brushes.LightSalmon;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDisksAsync();
    private void DiskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDiskDetails();
    private void UpdateDiskDetails()
    {
        if (DiskDetails == null) return;
        DiskDetails.Text = DiskCombo.SelectedItem is DiskInfo d ? $"{d.BusType} · {d.PartitionStyle} · Serie: {d.SerialNumber.Trim()} · {string.Join(" / ", d.Volumes.Select(v => $"{v.FileSystem} {v.Label}"))}" : "Selecciona la tarjeta que quieres utilizar.";
        if (restoring && DiskCombo.SelectedItem is DiskInfo target && HasNtfs(target))
            DiskDetails.Text += "\nNTFS detectado: se creará FAT32 después de confirmar el borrado.";
    }
    private static bool HasNtfs(DiskInfo disk) => disk.Volumes.Any(v => string.Equals(v.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase));
    private void FolderText_TextChanged(object sender, TextChangedEventArgs e) { }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = restoring ? "Selecciona la carpeta de copia ninmagic-nx" : "Selecciona dónde guardar la copia" };
        if (Directory.Exists(FolderText.Text)) dialog.InitialDirectory = FolderText.Text;
        if (dialog.ShowDialog(this) == true) FolderText.Text = dialog.FolderName;
    }
    private IProgress<WorkProgress> ProgressReporter
    {
        get
        {
            var generation = progressGeneration;
            return new Progress<WorkProgress>(p =>
            {
                if (operation != null && generation == progressGeneration) DisplayProgress(p);
            });
        }
    }
    private void BeginProgress(string name)
    {
        progressGeneration++;
        latestProgress = null;
        lastStage = "";
        lastStepNumber = 0;
        lastProgressTick = 0;
        progressEstimator.Reset();
        operationClock.Restart();
        StepText.Text = name + " · Preparando pasos…";
        StatusText.Text = "Preparando operación";
        StatusText.Foreground = Foreground;
        ProgressDetail.Text = "Preparando la información del proceso…";
        StepTimeText.Text = "Este paso: calculando…";
        TotalTimeText.Text = "Total restante: calculando…";
        ElapsedText.Text = "Transcurrido: 0 s";
        TimingPanel.Visibility = EstimateNote.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        progressTimer.Start();
    }
    private void DisplayProgress(WorkProgress p)
    {
        latestProgress = p;
        var estimate = progressEstimator.Update(p, operationClock.Elapsed);
        var now = Environment.TickCount64;
        if (p.Stage == lastStage && p.StepNumber == lastStepNumber && now - lastProgressTick < 100 && p.Completed != p.Total) return;
        lastProgressTick = now;
        lastStepNumber = p.StepNumber;
        Progress.IsIndeterminate = !p.IsOperationComplete && p.Total <= 0;
        Progress.Value = p.IsOperationComplete ? 100 : p.Fraction * 100;
        var step = p.StepNumber > 0 && p.StepNumber <= p.Steps.Count ? p.Steps[p.StepNumber - 1] : null;
        StepText.Text = step != null ? $"{p.Operation} · Paso {p.StepNumber} de {p.Steps.Count}" : p.Operation;
        StatusText.Text = p.IsOperationComplete ? p.Stage : step?.Title ?? p.Stage;
        ProgressDetail.Text = p.Total > 0 && !p.IsOperationComplete ? $"{p.Fraction:P0} del paso · {p.Detail}" : p.Detail;
        ProgressDetail.ToolTip = p.Detail;
        RenderEstimate(estimate);
        if (lastStage != p.Stage) { Log(p.Stage); lastStage = p.Stage; }
    }
    private void RenderEstimate(ProgressEstimate estimate)
    {
        if (latestProgress is not { } p) return;
        ElapsedText.Text = "Transcurrido: " + FormatDuration(operationClock.Elapsed);
        if (p.IsOperationComplete)
        {
            StepTimeText.Text = "Pasos completados";
            TotalTimeText.Text = "Tiempo restante: 0 s";
            return;
        }
        StepTimeText.Text = p.Total <= 0 ? "Este paso: duración variable" :
            estimate.IsStalled ? "Este paso: recalculando al avanzar…" :
            p.Completed >= p.Total ? "Completando este paso…" :
            estimate.StepRemaining is { } remaining ? "Este paso: ≈ " + FormatDuration(remaining) : "Este paso: calculando…";
        TotalTimeText.Text = estimate.OperationRemaining is { } total ? "Total restante: ≈ " + FormatDuration(total) : "Total restante: calculando…";
    }
    private static string FormatDuration(TimeSpan duration)
    {
        var seconds = (long)Math.Max(0, Math.Ceiling(duration.TotalSeconds));
        if (seconds >= 3600) return $"{seconds / 3600} h {seconds / 60 % 60:00} min {seconds % 60:00} s";
        if (seconds >= 60) return $"{seconds / 60} min {seconds % 60:00} s";
        return $"{seconds} s";
    }
    private void StopProgress(string message, bool completed = false)
    {
        progressGeneration++;
        progressTimer.Stop();
        operationClock.Stop();
        Progress.IsIndeterminate = false;
        if (completed)
        {
            Progress.Value = 100;
            if (latestProgress is { Steps.Count: > 0 } last)
                StepText.Text = $"{last.Operation} · Paso {last.Steps.Count} de {last.Steps.Count}";
        }
        StepTimeText.Text = message;
        TotalTimeText.Text = completed ? "Tiempo restante: 0 s" : "";
        ElapsedText.Text = "Transcurrido: " + FormatDuration(operationClock.Elapsed);
    }
    private async Task RunAsync(string name, Func<CancellationToken, Task> action)
    {
        if (operation != null || preview || refreshing) return;
        operation = new CancellationTokenSource();
        Inputs.IsEnabled = VerificationOptions.IsEnabled = ModeButtons.IsEnabled = ActionButton.IsEnabled = VerifyButton.IsEnabled = AdminButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BeginProgress(name);
        try
        {
            Directory.CreateDirectory(logFolder);
            logFile = Path.Combine(logFolder, $"ninmagic-nx-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            Log("Inicio de operación ninmagic-nx 1.4.0");
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            StopProgress("Operación incompleta · cancelada");
            StatusText.Text = "Operación cancelada";
            ProgressDetail.Text = "La original y la copia terminada se conservan. Un destino interrumpido no está listo para usar.";
            Log("Cancelado; destino o copia en curso incompletos.");
        }
        catch (Exception ex)
        {
            StopProgress("Operación incompleta · revisa el error");
            StatusText.Text = "Operación detenida · No se ha completado";
            ProgressDetail.Text = ex.Message;
            Log(ex.ToString());
            MessageBox.Show(this, ex.Message + "\n\nLa operación no se ha marcado como correcta. Conserva la tarjeta original y la copia; si ya se había empezado a escribir el destino, vuelve a restaurarlo desde el principio.\n\nRegistro: " + logFile, "ninmagic-nx · Revisar operación", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressTimer.Stop();
            operationClock.Stop();
            progressGeneration++;
            Progress.IsIndeterminate = false;
            operation.Dispose(); operation = null;
            Inputs.IsEnabled = VerificationOptions.IsEnabled = ModeButtons.IsEnabled = ActionButton.IsEnabled = VerifyButton.IsEnabled = AdminButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (preview) return;
        if (!IsAdmin) { MessageBox.Show(this, "Pulsa «Abrir como administrador» y acepta la solicitud de Windows para acceder a la microSD.", "Permisos necesarios", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (DiskCombo.SelectedItem is not DiskInfo disk) { MessageBox.Show(this, "Selecciona una microSD.", "Falta la tarjeta"); return; }
        var folder = FolderText.Text.Trim();
        var restore = restoring;
        var restoreOptions = new RestoreOptions(VerifyFilesOption.IsChecked == true);
        await RunAsync(restore ? "Comprobar copia" : "Crear copia", async ct =>
        {
            if (!restore)
            {
                if (!Directory.Exists(folder)) throw new IOException("Elige una carpeta existente en el PC.");
                var target = Path.Combine(folder, $"ninmagic-nx-copia-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
                var reporter = ProgressReporter;
                var manifest = await Task.Run(() => service.CreateAsync(disk, target, reporter, ct), ct);
                StopProgress("Todos los pasos completados", true);
                lastBackup = target;
                Progress.Value = 100;
                StatusText.Text = "Copia terminada y verificada";
                ProgressDetail.Text = target;
                Summary.Text = $"Copia preparada: {manifest.Files.Count(f => !f.IsDirectory):N0} archivos y {manifest.RawPartitions.Count} partición(es) emuMMC. Ahora puedes retirar la original y conectar la nueva.";
                Log("Copia verificada: " + target);
            }
            else
            {
                var reporter = ProgressReporter;
                var plan = await Task.Run(() => service.PlanAsync(folder, disk, reporter, ct), ct);
                ct.ThrowIfCancellationRequested();
                StopProgress("En pausa · esperando tu confirmación");
                StepText.Text = "Comprobación previa terminada · Confirmar tarjeta";
                if (!ConfirmErase(disk, plan, restoreOptions)) { StopProgress("Restauración sin iniciar"); StatusText.Text = "Restauración sin iniciar"; ProgressDetail.Text = "No has confirmado el borrado de la tarjeta de destino."; return; }
                BeginProgress("Restaurar tarjeta");
                var restoreReporter = ProgressReporter;
                Log(restoreOptions.VerifyFilesAfterCopy ? "Verificación final de archivos activada." : "Verificación final de archivos omitida por elección del usuario.");
                var result = await Task.Run(() => service.RestoreAsync(folder, disk, restoreReporter, ct, restoreOptions), ct);
                StopProgress("Todos los pasos completados", true);
                Progress.Value = 100;
                StatusText.Text = result.FilesVerified ? "Traslado terminado y verificado" : "Traslado terminado · archivos sin verificar";
                StatusText.Foreground = result.FilesVerified ? Foreground : Brushes.LightSalmon;
                ProgressDetail.Text = "Expulsa la tarjeta de forma segura y pruébala en la misma consola. Conserva la original.";
                var fileSummary = result.FilesVerified ? "Archivos copiados y verificados." : "Archivos copiados sin comprobación final en la tarjeta.";
                var rawSummary = plan.RawPartitions.Count > 0 ? " Las particiones emuMMC se han copiado y verificado." : "";
                Summary.Text = $"{fileSummary}{rawSummary} Destino {plan.FileSystem}: {plan.DataPartition.Length / 1_000_000_000.0:0.0} GB brutos para archivos.";
                Log(result.FilesVerified ? "Restauración y comprobación terminadas." : "Restauración terminada; archivos sin verificación final.");
            }
        });
    }
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderText.Text.Trim();
        await RunAsync("Comprobar copia", async ct =>
        {
            var reporter = ProgressReporter;
            var manifest = await Task.Run(() => service.LoadAndVerifyAsync(folder, reporter, ct), ct);
            StopProgress("Comprobación completada", true);
            Progress.Value = 100; StatusText.Text = "Integridad de la copia comprobada";
            ProgressDetail.Text = $"{manifest.Files.Count(f => !f.IsDirectory):N0} archivos · {manifest.RawPartitions.Count} partición(es) emuMMC · SHA-256 correcto.";
            Log("Verificación correcta: " + folder);
        });
    }
    private bool ConfirmErase(DiskInfo disk, MigrationPlan plan, RestoreOptions options)
    {
        var dialog = CreateEraseDialog(disk, plan, options);
        dialog.Owner = this;
        return dialog.ShowDialog() == true;
    }
    private Window CreateEraseDialog(DiskInfo disk, MigrationPlan plan, RestoreOptions options)
    {
        var text = $"BORRAR DISCO {disk.Number}";
        var dialog = new Window { Title = "Confirmar la tarjeta de destino", Width = 620, Height = 550, MaxHeight = SystemParameters.WorkArea.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("Surface"), Foreground = Foreground, FontFamily = FontFamily, FontSize = 14 };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "Se borrará toda la tarjeta seleccionada", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.LightSalmon, Margin = new Thickness(0, 0, 0, 15) });
        var verificationNotice = options.VerifyFilesAfterCopy
            ? "Verificación de archivos: activada. Se releerán los archivos guardados en la tarjeta para comprobarlos."
            : "Verificación de archivos: desactivada. Ahorra la lectura final, pero no permite detectar errores en los archivos guardados, incluida la emuMMC por archivos.";
        panel.Children.Add(new TextBlock { Text = verificationNotice, Foreground = options.VerifyFilesAfterCopy ? Foreground : Brushes.LightSalmon, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) });
        panel.Children.Add(new TextBlock { Text = $"{disk}\nSerie: {disk.SerialNumber}\n\nFormato final: {plan.FileSystem}{(HasNtfs(disk) ? " · conversión desde NTFS" : "")}\nArchivos: {plan.DataPartition.Length / 1_000_000_000.0:0.0} GB · emuMMC: {plan.RawPartitions.Count} partición(es).\n\nSe crearán las particiones y se copiarán los archivos y la emuMMC. La emuMMC en partición (RAW) se verifica siempre. Un error o una cancelación dejará el destino incompleto.\n\nLa copia del PC ya ha sido verificada. El borrado del destino no se puede deshacer.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "Para continuar, escribe: " + text, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Margin = new Thickness(0, 0, 0, 20) }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Volver", Margin = new Thickness(0, 0, 10, 0), IsCancel = true }; cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = new Button { Content = "Borrar y restaurar", IsEnabled = false, Background = Brushes.LightSalmon, Foreground = Brushes.Black }; confirm.Click += (_, _) => dialog.DialogResult = true;
        input.TextChanged += (_, _) => confirm.IsEnabled = string.Equals(input.Text, text, StringComparison.Ordinal);
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopProgress("Cancelando · esperando que se detenga la operación");
        operation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "Cancelando de forma ordenada…";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (operation == null) return;
        e.Cancel = true;
        MessageBox.Show(this, "Hay una operación en curso. Pulsa Cancelar y espera a que termine antes de cerrar.", "Operación en curso");
    }
    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("No se encuentra el ejecutable.");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "Permisos de administrador no concedidos"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "No se pudo reiniciar"); }
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. Apaga la consola completamente y conecta la tarjeta original. No uses sus archivos durante la copia.\n2. Abre ninmagic-nx como administrador. Guarda la copia en un disco NTFS/exFAT del PC con espacio libre suficiente.\n3. Espera a «Copia terminada y verificada». Retira la original y conecta la nueva.\n4. Elige «Restaurar», la copia y la tarjeta de destino. Comprueba su modelo y tamaño antes de confirmar. Si está en NTFS, se preparará en FAT32. Se copiarán los archivos y la emuMMC. La opción para verificar los archivos está activada por defecto y puedes desmarcarla antes de empezar.\n5. Expulsa la tarjeta nueva y comprueba el arranque en la misma consola. Conserva la original y la copia.\n\nCompatibilidad: Windows 10/11 x64, discos externos USB/SD/MMC, sectores lógicos de 512 bytes, tabla MBR, una partición FAT32 o exFAT y emuMMC Hekate RAW o por archivos. Hasta el límite MBR de 2 TiB. La capacidad real del destino debe ser igual o mayor que la original. Las tarjetas originales GPT, Android/Linux y diseños desconocidos se bloquean. Un destino NTFS se convierte a FAT32; un archivo individual mayor de 4 GiB menos 1 byte bloquea la conversión antes de borrar. El tamaño total de un juego sí puede superar los 4 GB: sus archivos y partes se conservan.\n\nSe conservan las partidas dentro de emuMMC y los archivos de la SD. Las partidas de sysMMC están en la memoria de la consola. No se cambia de consola ni se descifran datos.\n\nLa copia del PC y las particiones emuMMC RAW se verifican siempre. Puedes omitir la lectura final de los archivos restaurados para ahorrar tiempo; en ese caso el resultado indica archivos sin verificar. Una emuMMC por archivos está incluida en esa opción. Los errores del contenido guardado pueden pasar inadvertidos sin la lectura final. No se realiza una prueba completa de capacidad ni de calidad de la tarjeta. Los errores al copiar o verificar detienen la operación. Un corte de corriente también puede interrumpir el traslado. Una operación incompleta nunca se presenta como correcta. La verificación del PC no sustituye la prueba de arranque en tu consola.", "ninmagic-nx · Ayuda", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(logFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", logFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { MessageBox.Show(this, ex.Message, "No se pudo abrir la carpeta de registros"); }
    }
    private void Log(string message)
    {
        try { if (logFile.Length > 0) File.AppendAllText(logFile, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
