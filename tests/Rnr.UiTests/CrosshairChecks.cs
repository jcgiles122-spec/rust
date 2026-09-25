using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rnr.Core;
using Rnr.Desktop;
using Rnr.Desktop.Services;
using Rnr.Desktop.ViewModels;

internal static class CrosshairChecks
{
    public static void Run(MainWindow main, string output, Action<bool, string> check)
    {
        var vm = (DashboardViewModel)main.DataContext;
        var tabs = (TabControl)main.FindName("Tabs");
        vm.Settings.UiScale = 1;
        typeof(MainWindow).GetMethod("ApplyWindowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
        tabs.SelectedIndex = 3; Pump();
        var overlay = Application.Current.Windows.OfType<CrosshairWindow>().Single();
        check(tabs.Items.Count == 4 && !overlay.IsVisible, "four sections; crosshair starts disabled");
        var toggle = (CheckBox)main.FindName("CrosshairToggle");
        ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle(); Pump();
        check(overlay.IsVisible && vm.Settings.Crosshair.Enabled && main.IsActive, "crosshair enables without stealing focus");
        nint handle = new WindowInteropHelper(overlay).Handle;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        check((style & 0x08080020) == 0x08080020 && !overlay.IsHitTestVisible && !overlay.ShowActivated,
            "native overlay is layered, click-through, and nonactivating");
        check(ClickThrough(overlay), "real Windows mouse click reaches a button underneath the crosshair");
        var color = (TextBox)main.FindName("CrosshairColorBox");
        color.Text = "#FF6487"; Pump();
        color.Text = "#ZZ"; Pump();
        check(vm.Settings.Crosshair.Color == "#FF6487", "invalid color editing preserves last valid dot color");
        color.Text = "#00E5FF";
        ((Slider)main.FindName("DotSizeSlider")).Value = 7.25; Pump();
        check(vm.Settings.Crosshair.DotSize == 7.25 && vm.Settings.Crosshair.Color == "#00E5FF", "fractional dot size and custom color apply live");
        var grid = (Grid)overlay.Content;
        double scale = VisualTreeHelper.GetDpi(overlay).DpiScaleX;
        check(Math.Abs(((FrameworkElement)grid.Children[0]).Width * scale - 7.25) < .1, "dot diameter remains in physical pixels at the current DPI");
        Capture(main, Path.Combine(output, "08-crosshair-dot.png"));
        main.Hide(); Pump();
        check(overlay.IsVisible, "crosshair remains visible when the main menu is hidden");
        main.Show(); main.Activate(); Pump();

        string fixture = Path.Combine(output, "transparent-crosshair.png");
        WritePng(fixture, 16, 16, true);
        check(main.ImportCrosshairFile(fixture), "transparent PNG imports successfully");
        byte[] imported = File.ReadAllBytes(CrosshairImage.SavedPath);
        File.Delete(fixture); // The user's source is not required after import.
        check(CrosshairImage.Load(out _) is not null, "import is an independent local copy");
        WritePng(fixture, 16, 16, false);
        check(!main.ImportCrosshairFile(fixture) && imported.SequenceEqual(File.ReadAllBytes(CrosshairImage.SavedPath)), "opaque PNG rejected without replacing the saved artwork");
        File.WriteAllText(fixture, "not a png");
        check(!main.ImportCrosshairFile(fixture), "malformed PNG rejected cleanly");
        WritePng(fixture, 2049, 2, true);
        check(!main.ImportCrosshairFile(fixture), "oversized PNG dimensions rejected");
        ((Slider)main.FindName("ImageSizeSlider")).Value = 43.5; Pump();
        check(vm.Settings.Crosshair.UseImage && ((Grid)overlay.Content).Children[0] is Image, "imported artwork replaces the dot");
        Capture(main, Path.Combine(output, "09-crosshair-image.png"));
        vm.Store.Flush();
        var saved = JsonFiles.LoadSettings(AppPaths.Settings).Crosshair;
        check(saved.Enabled && saved.UseImage && saved.ImageSize == 43.5 && saved.DotSize == 7.25 && saved.Color == "#00E5FF", "crosshair choices persist to the local profile");
        ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle(); Pump();
        check(!overlay.IsVisible && !vm.Settings.Crosshair.Enabled, "crosshair toggle off removes overlay immediately");
        // Leave enabled to verify restart and shutdown in the main suite.
        ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
        vm.Store.Flush();
        File.Delete(fixture);
        tabs.SelectedIndex = 0; Pump();
    }

    private static bool ClickThrough(CrosshairWindow overlay)
    {
        // This test sends one click to its own probe window only, never a game or arbitrary target.
        GetWindowRect(new WindowInteropHelper(overlay).Handle, out var rect);
        int centerX = (rect.Left + rect.Right) / 2, centerY = (rect.Top + rect.Bottom) / 2;
        bool clicked = false;
        var button = new Button { Content = "Crosshair click-through check" };
        button.Click += (_, _) => clicked = true;
        var probe = new Window { Title = "RNR UI test probe", Width = 300, Height = 180, Topmost = true,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = button };
        GetCursorPos(out var cursor);
        try
        {
            probe.Show(); Pump();
            var probeHandle = new WindowInteropHelper(probe).Handle;
            GetWindowRect(probeHandle, out var probeRect);
            SetWindowPos(probeHandle, new nint(-1), centerX - (probeRect.Right - probeRect.Left) / 2,
                centerY - (probeRect.Bottom - probeRect.Top) / 2, 0, 0, 0x0001);
            probe.Activate();
            SetWindowPos(new WindowInteropHelper(overlay).Handle, new nint(-1), 0, 0, 0, 0, 0x0013);
            Pump();
            SetCursorPos(centerX, centerY);
            MouseInput[] clicks = [new() { Flags = 2 }, new() { Flags = 4 }];
            if (SendInput(2, clicks, Marshal.SizeOf<MouseInput>()) != 2) return false;
            for (int i = 0; i < 10 && !clicked; i++)
            { Pump(); System.Threading.Thread.Sleep(15); }
            return clicked && probe.IsActive;
        }
        finally { probe.Close(); SetCursorPos(cursor.X, cursor.Y); Pump(); }
    }

    private static void WritePng(string path, int width, int height, bool transparent)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 4;
            pixels[index + 1] = 230;
            pixels[index + 3] = (byte)(!transparent || x == width / 2 || y == height / 2 ? 255 : 0);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct MouseInput { [FieldOffset(0)] public uint Type; [FieldOffset(20)] public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
}
