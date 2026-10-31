using System.Diagnostics;
using System.IO;
using System.Windows;
using Ninmagic.Nx.Updates;
namespace Ninmagic.Nx.App;
public partial class App : Application
{
    private IDisposable? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ninmagic-nx · Operación detenida", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            if (e.Args.FirstOrDefault() == "--apply-update")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var result = await UpdateInstaller.RunAsync(e.Args);
                if (!result.Success) MessageBox.Show(result.Message, "ninmagic-nx · No se pudo actualizar", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(result.Success ? 0 : 1);
                return;
            }
            if (e.Args.FirstOrDefault() == "--wait-for-parent") WaitForParent(e.Args);
            var executable = Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable de la aplicación.");
            instance = UpdateInstanceGuard.TryAcquire(executable);
            if (instance == null)
            {
                MessageBox.Show("ninmagic-nx ya está abierto desde esta carpeta. Utiliza esa ventana o ciérrala antes de abrir otra.", "ninmagic-nx ya está abierto", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ninmagic-nx · No se pudo iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void WaitForParent(string[] args)
    {
        ValidateRestartArguments(args);
        if (!int.TryParse(args[1], out var id) || id <= 0 ||
            !long.TryParse(args[2], out var startTicks)) throw new IOException("La solicitud de reinicio no es válida.");
        Process parent;
        try { parent = Process.GetProcessById(id); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            try
            {
                if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != startTicks) return;
                if (!parent.WaitForExit(30_000)) throw new IOException("La ventana anterior sigue abierta. Ciérrala antes de volver a abrir como administrador.");
            }
            catch (InvalidOperationException) when (parent.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (parent.HasExited) { }
        }
    }

    private static void ValidateRestartArguments(string[] args)
    {
        if (args.Length is not (3 or 5) || args[0] != "--wait-for-parent" ||
            args.Length == 5 && (args[3] != "--updated" || !Version.TryParse(args[4], out var version) || version.Build < 0 || version.Revision >= 0))
            throw new IOException("La solicitud de reinicio no es válida.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose(); instance = null;
        base.OnExit(e);
    }
}
