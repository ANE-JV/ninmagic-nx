using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
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
        Render("backup", 1124, 810);
        var restore = (Button)window.FindName("RestoreTab");
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var target = new DiskInfo(2, "Lector de microSD · Ejemplo", "DEMO", "DEMO", 256_000_000_000, 512, "USB", false, false, false, "GPT", new[] { new VolumeInfo(1, 1_048_576, 255_000_000_000, "E:\\", "NTFS", "NUEVA") });
        var combo = (ComboBox)window.FindName("DiskCombo");
        combo.ItemsSource = new[] { target }; combo.SelectedIndex = 0;
        Render("restore", 1124, 810);
        Render("restore-small", 884, 700);
        var method = typeof(Ninmagic.Nx.App.MainWindow).GetMethod("CreateEraseDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plan = new MigrationPlan(new PartitionInfo(1, 0x0C, 32768, 440_000_000), new[] { new PartitionInfo(2, 0xE0, 440_100_000, 59_000_000) }, new Dictionary<uint, uint>(), 80_000_000_000, "FAT32", target.Size);
        var dialog = (Window)method.Invoke(window, new object[] { target, plan })!;
        root = (FrameworkElement)dialog.Content;
        Render("confirmation", 604, 580);
        dialog.Close();
        window.Close();
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
