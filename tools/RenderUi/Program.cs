using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Threading;
using Ninmagic.Nx.Core;

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
        Directory.CreateDirectory(args[0]);
        var checks = 0;
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
        refreshing.SetValue(window, true);
        try
        {
            Check("pending inventory blocks a second operation", !(bool)canStart.Invoke(window, null)!);
            Check("pending inventory explains the wait inline", notice.Visibility == Visibility.Visible && notice.Text.Contains("consultando las tarjetas"));
        }
        finally { refreshing.SetValue(window, false); }
        var operationField = windowType.GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var active = new CancellationTokenSource())
        {
            operationField.SetValue(window, active);
            try { Check("active operation prevents reentry", !(bool)canStart.Invoke(window, null)!); }
            finally { operationField.SetValue(window, null); }
        }
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
        Render("confirmation", 604, 510);
        dialog.Close();
        dialog = (Window)method.Invoke(window, new object[] { target, plan, new RestoreOptions(false) })!;
        root = (FrameworkElement)dialog.Content;
        Render("confirmation-no-verification", 604, 510);
        dialog.Close();
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
                context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            }
            image.Render(visual);
            image.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(args[0], name + ".png")); png.Save(output);
            Console.WriteLine(name + ": rendered");
        }
    }
}
