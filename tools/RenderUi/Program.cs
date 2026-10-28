using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        Render("restore", 1124, 810);
        Render("restore-small", 884, 700);
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
