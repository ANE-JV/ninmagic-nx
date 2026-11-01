using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Threading;
using Ninmagic.Nx.Core;
using Ninmagic.Nx.App;
using Ninmagic.Nx.Updates;

public static class RenderUi
{
    // Renders our WPF visual tree only: never shows a window or triggers Loaded/device enumeration.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Provide output folder for disposable UI renders.");
        var app = new Ninmagic.Nx.App.App();
        app.InitializeComponent();
        var window = new Ninmagic.Nx.App.MainWindow();
        ((TextBox)window.FindName("FolderText")).Text = @"D:\Copias microSD";
        var root = (FrameworkElement)window.Content;
        Brush renderBackground = window.Background;
        Directory.CreateDirectory(args[0]);
        var checks = 0;
        var validateRestart = typeof(Ninmagic.Nx.App.App).GetMethod("ValidateRestartArguments", BindingFlags.Static | BindingFlags.NonPublic)!;
        validateRestart.Invoke(null, new object[] { new[] { "--wait-for-parent", "1", "1" } });
        Check("elevation restart arguments are accepted", true);
        validateRestart.Invoke(null, new object[] { new[] { "--wait-for-parent", "1", "1", "--updated", "1.6.0" } });
        Check("updater restart arguments are accepted", true);
        var invalidRestartRejected = false;
        try { validateRestart.Invoke(null, new object[] { new[] { "--wait-for-parent", "1", "1", "--unexpected", "1.6.0" } }); }
        catch (TargetInvocationException e) when (e.InnerException is IOException) { invalidRestartRejected = true; }
        Check("unexpected restart arguments are rejected", invalidRestartRejected);
        var windowType = typeof(Ninmagic.Nx.App.MainWindow);
        var combo = (ComboBox)window.FindName("DiskCombo");
        var action = (Button)window.FindName("ActionButton");
        var notice = (TextBlock)window.FindName("ActionNotice");
        var source = new DiskInfo(3, "Lector de microSD · Ejemplo", "DEMO", "DEMO", 128_000_000_000, 512, "USB", false, false, false, "MBR",
            new[] { new VolumeInfo(1, 16_777_216, 96_000_000_000, @"\\?\Volume{11111111-1111-1111-1111-111111111111}\", "FAT32", "SWITCH") { DriveLetter = "E" } });
        combo.ItemsSource = new[] { source }; combo.SelectedIndex = 0;
        Check("letter from GUID-backed volume appears in selector", combo.SelectedItem!.ToString()!.StartsWith("E: ·"));
        Check("letter appears in card details", ((TextBlock)window.FindName("DiskDetails")).Text.Contains("Unidad: E:"));
        Render("backup", 1124, 810);
        AssertActionHit("backup normal");
        Render("backup-small", 884, 700);
        AssertActionHit("backup minimum");
        var updateButton = (Button)window.FindName("UpdateButton");
        var updateBounds = new Rect(updateButton.TranslatePoint(new Point(), root), updateButton.RenderSize);
        Check("update button is visible at minimum size", updateButton.Visibility == Visibility.Visible && updateButton.IsEnabled && updateBounds.Top >= 0 && updateBounds.Bottom <= root.ActualHeight && updateBounds.Right <= root.ActualWidth);
        var updatePoint = updateButton.TranslatePoint(new Point(updateButton.ActualWidth / 2, updateButton.ActualHeight / 2), root);
        var updateHit = VisualTreeHelper.HitTest(root, updatePoint)?.VisualHit;
        while (updateHit != null && updateHit != updateButton) updateHit = VisualTreeHelper.GetParent(updateHit);
        Check("update button receives pointer hit", updateHit == updateButton);
        var showNotice = windowType.GetMethod("ShowStartNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        showNotice.Invoke(window, new object[] { "Faltan permisos. Pulsa «Abrir como administrador» en la parte superior y acepta la solicitud de Windows." });
        ((Border)window.FindName("AdminBanner")).Visibility = Visibility.Visible;
        Render("startup-notice-small", 884, 700);
        Check("startup notice is visible with its explanation", notice.Visibility == Visibility.Visible && notice.Text.StartsWith("Faltan permisos."));
        AssertActionHit("minimum with startup notice");
        var noticeBounds = new Rect(notice.TranslatePoint(new Point(), root), notice.RenderSize);
        Check("startup notice fits the minimum window", noticeBounds.Bottom <= root.ActualHeight && noticeBounds.Right <= root.ActualWidth);
        var canStart = windowType.GetMethod("CanStartOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var refreshing = windowType.GetField("refreshing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var canUpdate = windowType.GetMethod("CanOpenUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        refreshing.SetValue(window, true);
        try
        {
            Check("pending inventory blocks a second operation", !(bool)canStart.Invoke(window, null)!);
            Check("pending inventory explains the wait inline", notice.Visibility == Visibility.Visible && notice.Text.Contains("consultando las tarjetas"));
            Check("pending inventory blocks application update", !(bool)canUpdate.Invoke(window, null)!);
        }
        finally { refreshing.SetValue(window, false); }
        var operationField = windowType.GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var active = new CancellationTokenSource())
        {
            operationField.SetValue(window, active);
            try
            {
                Check("active operation prevents reentry", !(bool)canStart.Invoke(window, null)!);
                Check("active backup or restore blocks update", !(bool)canUpdate.Invoke(window, null)!);
            }
            finally { operationField.SetValue(window, null); }
        }
        var updateOpen = windowType.GetField("updateOpen", BindingFlags.Instance | BindingFlags.NonPublic)!;
        updateOpen.SetValue(window, true);
        try
        {
            Check("open update dialog blocks new backup or verify", !(bool)canStart.Invoke(window, null)!);
            Check("open update dialog blocks another updater", !(bool)canUpdate.Invoke(window, null)!);
            var closing = new System.ComponentModel.CancelEventArgs();
            windowType.GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, new object?[] { window, closing });
            Check("updater blocks closing the parent before handoff", closing.Cancel);
        }
        finally { updateOpen.SetValue(window, false); }
        var renderEstimate = windowType.GetMethod("RenderEstimate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var beginProgress = windowType.GetMethod("BeginProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operationClock = (Stopwatch)windowType.GetField("operationClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var progressTimer = (DispatcherTimer)windowType.GetField("progressTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            beginProgress.Invoke(window, new object[] { "Crear copia" });
            Check("start acknowledgment is immediate", ((TextBlock)window.FindName("StatusText")).Text.StartsWith("Inicio recibido"));
            Check("preflight has no data progress yet", windowType.GetField("latestProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == null);
            Thread.Sleep(25);
            operationClock.Stop();
            renderEstimate.Invoke(window, new object[] { new ProgressEstimate(null, null, 0, false) });
            Check("elapsed time advances before first progress event", ((TextBlock)window.FindName("ElapsedText")).Text != "Transcurrido: 0 s");
        }
        finally { operationClock.Stop(); progressTimer.Stop(); }
        ((ProgressBar)window.FindName("Progress")).IsIndeterminate = false;
        ((WrapPanel)window.FindName("TimingPanel")).Visibility = Visibility.Collapsed;
        ((TextBlock)window.FindName("EstimateNote")).Visibility = Visibility.Collapsed;
        ((TextBlock)window.FindName("StepText")).Text = "SIN OPERACIÓN EN CURSO";
        ((TextBlock)window.FindName("StatusText")).Text = "Listo para empezar";
        ((TextBlock)window.FindName("ProgressDetail")).Text = "Apaga completamente la consola antes de retirar su microSD.";
        ((Border)window.FindName("AdminBanner")).Visibility = Visibility.Collapsed;
        var restore = (Button)window.FindName("RestoreTab");
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var target = new DiskInfo(2, "Lector de microSD · Ejemplo", "DEMO", "DEMO", 256_000_000_000, 512, "USB", false, false, false, "GPT", new[] { new VolumeInfo(1, 1_048_576, 255_000_000_000, "E:\\", "NTFS", "NUEVA") });
        combo.ItemsSource = new[] { target }; combo.SelectedIndex = 0;
        Render("restore", 1124, 810);
        Render("restore-small", 884, 700);
        var verifyFiles = (CheckBox)window.FindName("VerifyFilesOption");
        verifyFiles.IsChecked = false;
        Render("restore-no-verification", 1124, 810);
        Render("restore-no-verification-small", 884, 700);
        verifyFiles.IsChecked = true;
        var displayProgress = typeof(Ninmagic.Nx.App.MainWindow).GetMethod("DisplayProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steps = new WorkStep[]
        {
            new("verify-backup", "Comprobando la copia del PC", 100_000_000_000, WorkRateKind.PcRead),
            new("prepare-target", "Preparando las particiones de la tarjeta", 0, WorkRateKind.None),
            new("copy-raw-2", "Copiando emuMMC · partición 2", 13_000_000_000, WorkRateKind.TargetWrite),
            new("verify-raw-2", "Verificando emuMMC · partición 2", 13_000_000_000, WorkRateKind.TargetRead),
            new("mount-target", "Abriendo la partición de archivos", 0, WorkRateKind.None),
            new("copy-files", "Copiando archivos del PC a la microSD", 80_000_000_000, WorkRateKind.TargetWrite),
            new("verify-files", "Comprobando los archivos de la tarjeta nueva", 80_000_000_000, WorkRateKind.TargetRead),
            new("finalize", "Guardando atributos y terminando las escrituras", 0, WorkRateKind.None)
        };
        ((WrapPanel)window.FindName("TimingPanel")).Visibility = Visibility.Visible;
        ((TextBlock)window.FindName("EstimateNote")).Visibility = Visibility.Visible;
        ((Button)window.FindName("CancelButton")).IsEnabled = true;
        ((Button)window.FindName("ActionButton")).IsEnabled = false;
        var example = new WorkProgress("Restaurando archivos", "Nintendo/Contents/registered/carpeta-de-ejemplo-con-nombre-largo/00", 28_000_000_000, 80_000_000_000)
            { Operation = "Restaurar tarjeta", Steps = steps, StepNumber = 6 };
        displayProgress.Invoke(window, new object[] { example });
        renderEstimate.Invoke(window, new object[] { new ProgressEstimate(TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(75), 30_000_000, false) });
        ((TextBlock)window.FindName("ElapsedText")).Text = "Transcurrido: 22 min 15 s";
        Render("progress", 1124, 810);
        Render("progress-small", 884, 700);
        displayProgress.Invoke(window, new object[] { new WorkProgress("Finalizando", "Esperando que Windows termine de guardar los datos", 0, 0)
            { Operation = "Restaurar tarjeta", Steps = steps, StepNumber = 8 } });
        Render("finalizing-small", 884, 700);
        var method = typeof(Ninmagic.Nx.App.MainWindow).GetMethod("CreateEraseDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plan = new MigrationPlan(new PartitionInfo(1, 0x0C, 32768, 440_000_000), new[] { new PartitionInfo(2, 0xE0, 440_100_000, 59_000_000) }, new Dictionary<uint, uint>(), 80_000_000_000, "FAT32", target.Size);
        var dialog = (Window)method.Invoke(window, new object[] { target, plan, new RestoreOptions() })!;
        root = (FrameworkElement)dialog.Content;
        renderBackground = dialog.Background;
        Render("confirmation", 604, 510);
        dialog.Close();
        dialog = (Window)method.Invoke(window, new object[] { target, plan, new RestoreOptions(false) })!;
        root = (FrameworkElement)dialog.Content;
        Render("confirmation-no-verification", 604, 510);
        dialog.Close();
        var fakeUpdates = new FakeUpdateService();
        var updateDialog = new UpdateDialog(fakeUpdates, autoCheck: false);
        root = (FrameworkElement)updateDialog.Content;
        renderBackground = updateDialog.Background;
        Render("updates", 634, 610);
        Check("constructing update dialog makes no network request", fakeUpdates.CheckCount == 0);
        var updateType = typeof(UpdateDialog);
        T UpdateField<T>(string name) => (T)updateType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(updateDialog)!;
        Task UpdateCall(string name) => (Task)updateType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(updateDialog, null)!;
        Check("public updater has no authentication input", !VisualChildren(root).Any(child => child is PasswordBox or TextBox));
        Check("public updater has no credential instructions", !VisualChildren(root).OfType<TextBlock>().Any(text =>
            text.Text.Contains("token", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("credencial", StringComparison.OrdinalIgnoreCase) ||
            text.Text.Contains("privado", StringComparison.OrdinalIgnoreCase)));
        UpdateCall("CheckAsync").GetAwaiter().GetResult();
        Check("up-to-date result is visible", UpdateField<TextBlock>("status").Text.Contains("más reciente"));
        Check("up-to-date result cannot install", !UpdateField<Button>("install").IsEnabled);
        fakeUpdates.Update = new UpdateInfo("9.0.0", "Mejoras de ejemplo para comprobar el diseño.\nLas copias de seguridad se conservan.", 1, 1024, new string('0', 64));
        UpdateCall("CheckAsync").GetAwaiter().GetResult();
        Check("new version offers in-app installation", UpdateField<Button>("install").IsEnabled && UpdateField<TextBlock>("status").Text.Contains("9.0.0"));
        Render("updates-available", 634, 610);
        UpdateCall("InstallAsync").GetAwaiter().GetResult();
        Check("failed download never starts replacement", fakeUpdates.InstallCount == 0 && UpdateField<TextBlock>("status").Text.Contains("No se pudo instalar"));
        fakeUpdates.CheckError = new System.Net.Http.HttpRequestException("No se pudo conectar con el servicio de actualizaciones.");
        UpdateCall("CheckAsync").GetAwaiter().GetResult();
        Check("network failure is explained without changing installed app", UpdateField<TextBlock>("status").Text.Contains("No se pudo comprobar") &&
            UpdateField<TextBlock>("notes").Text.Contains("versión instalada se conserva"));
        Check("network failure cannot install stale release", !UpdateField<Button>("install").IsEnabled && fakeUpdates.InstallCount == 0);
        Render("updates-network-error", 634, 610);
        Render("updates-network-error-small", 544, 490);
        fakeUpdates.CheckError = new IOException("Las actualizaciones no están disponibles en este momento.");
        UpdateCall("CheckAsync").GetAwaiter().GetResult();
        Check("unavailable public update cannot install", !UpdateField<Button>("install").IsEnabled &&
            UpdateField<TextBlock>("progressDetail").Text.Contains("no están disponibles"));
        fakeUpdates.CheckError = null;
        UpdateCall("CheckAsync").GetAwaiter().GetResult();
        Check("successful recheck recovers after temporary failure", UpdateField<Button>("install").IsEnabled &&
            UpdateField<TextBlock>("status").Text.Contains("9.0.0"));
        fakeUpdates.DownloadError = new OperationCanceledException();
        UpdateCall("InstallAsync").GetAwaiter().GetResult();
        Check("cancelled download preserves installed app", fakeUpdates.InstallCount == 0 && UpdateField<TextBlock>("status").Text.Contains("cancelada"));
        fakeUpdates.Update = null;
        UpdateCall("CheckAsync").GetAwaiter().GetResult();
        Check("no newer public release clears previous update choice", !UpdateField<Button>("install").IsEnabled);
        Render("updates-current", 634, 610);
        updateDialog.Close();
        window.Close();
        Console.WriteLine($"UI CHECKS: {checks} passed; no windows shown or device operations started.");
        void Check(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException("UI check failed: " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }
        void AssertActionHit(string scenario)
        {
            Check(scenario + " action is enabled", action.IsEnabled);
            var point = action.TranslatePoint(new Point(action.ActualWidth / 2, action.ActualHeight / 2), root);
            var hit = VisualTreeHelper.HitTest(root, point)?.VisualHit;
            while (hit != null && hit != action) hit = VisualTreeHelper.GetParent(hit);
            Check(scenario + " button receives pointer hit", hit == action);
        }
        void Render(string name, int width, int height)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(renderBackground, null, new Rect(0, 0, width, height));
            }
            image.Render(visual);
            image.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(args[0], name + ".png")); png.Save(output);
            Console.WriteLine(name + ": rendered");
        }
        static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in VisualChildren(child)) yield return descendant;
            }
        }
    }

    private sealed class FakeUpdateService : IUpdateUiService
    {
        public int CheckCount { get; private set; }
        public int InstallCount { get; private set; }
        public UpdateInfo? Update { get; set; }
        public Exception? CheckError { get; set; }
        public Exception DownloadError { get; set; } = new IOException("La descarga simulada no pasó la comprobación de integridad.");
        public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            return CheckError is null ? Task.FromResult(Update) : Task.FromException<UpdateInfo?>(CheckError);
        }
        public Task<StagedUpdate> DownloadAsync(UpdateInfo update, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
            => Task.FromException<StagedUpdate>(DownloadError);
        public void StartInstall(StagedUpdate update) => InstallCount++;
    }
}
