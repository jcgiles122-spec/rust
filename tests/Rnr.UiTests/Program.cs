using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Rnr.Core;
using Rnr.Desktop;
using Rnr.Desktop.ViewModels;
using Rnr.Desktop.Services;
using Rnr.Desktop.Input;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui"); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("RNR_DATA_DIR", Path.Combine(output, "test-data"));
        JsonFiles.Save(Rnr.Desktop.Services.AppPaths.Settings, new UserSettings());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RustNoRecoil;component/Themes/Theme.xaml", UriKind.Relative) });
        var window = new MainWindow(); window.Show(); Pump();
        int passed = 0;
        void Check(bool valid, string name) { if (!valid) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        var vm = (DashboardViewModel)window.DataContext;
        var list = (ListBox)window.FindName("WeaponList");
        var tabs = (TabControl)window.FindName("Tabs");
        var search = (TextBox)window.FindName("SearchBox");
        void InvokeInput(string key) => typeof(MainWindow).GetMethod("HandleInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [key]);
        void InvokeHandler(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
        void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Toggle(string name)
        {
            var control = (CheckBox)window.FindName(name);
            var peer = new CheckBoxAutomationPeer(control);
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle(); Pump();
        }
        try
        {
            DriverChecks.Run(Check, Pump, Capture, output);
            Check(vm.Selected.Id == "rifle.ak" && list.SelectedItems.Count == 1, "menu opens with one selection; login deferred");
            Check(((CheckBox)window.FindName("NoRecoilToggle")).IsEnabled && vm.RequestedNoRecoil && !vm.NoRecoilActive,
                "No Recoil preference defaults on without claiming actual compensation");
            Check(!((CheckBox)window.FindName("RapidFireToggle")).IsVisible && !vm.RequestedRapidFire,
                "automatic weapons do not offer Rapid Fire");
            Capture(window, Path.Combine(output, "01-weapons.png"));
            list.SelectedItem = WeaponCatalog.Find("smg.thompson"); Pump();
            Check(vm.Selected.Id == "smg.thompson" && list.SelectedItems.Count == 1, "selecting Thompson replaces Assault Rifle");
            Toggle("NoRecoilToggle"); Check(!vm.RequestedNoRecoil, "No Recoil control can be toggled off");
            list.SelectedItem = WeaponCatalog.Find("rifle.ak"); Pump();
            Check(vm.RequestedNoRecoil, "changing one weapon does not change another weapon's preference");
            list.SelectedItem = WeaponCatalog.Find("smg.thompson"); Pump();
            Check(!vm.RequestedNoRecoil, "weapon selection restores its saved preference");
            search.Text = "AK-47"; Pump(); Check(list.Items.Count == 1, "AK-47 alias resolves in search");
            Check(vm.Selected.Id == "smg.thompson", "filtering does not change selection");
            search.Text = "no-such-weapon"; Pump(); Check(list.Items.Count == 0, "empty search handled");
            search.Text = ""; list.SelectedItem = WeaponCatalog.Find("rifle.semiauto"); Pump();
            Check(vm.Selected.RapidFireApplicable && vm.CanRapidFire && vm.RequestedNoRecoil && vm.RequestedRapidFire && !vm.NoRecoilActive,
                "appropriate semi-auto preferences are editable and default on while actual input remains off");
            Capture(window, Path.Combine(output, "02-semi-auto.png"));
            Toggle("RapidFireToggle"); Check(!vm.RequestedRapidFire && vm.RequestedNoRecoil, "Rapid Fire toggle is independent of No Recoil");
            Toggle("RapidFireToggle"); Check(vm.RequestedRapidFire, "Rapid Fire can be turned back on");
            Toggle("RapidFireToggle");
            Toggle("NoRecoilToggle"); Check(!vm.RequestedNoRecoil && !vm.RequestedRapidFire && vm.HudModes.Contains("PROFILE OFF"), "both preferences can be turned off");
            Toggle("NoRecoilToggle"); Check(vm.RequestedNoRecoil && vm.HudModes.Contains("PROFILE ON") && vm.HudModes.Contains("INPUT OFF"),
                "HUD distinguishes an enabled preference from unimplemented input automation");
            tabs.SelectedIndex = 1; Pump(); Capture(window, Path.Combine(output, "03-settings.png"));
            tabs.SelectedIndex = 2; Pump(); Capture(window, Path.Combine(output, "04-keybinds.png"));
            window.Activate(); Click("MenuBindButton"); InvokeInput("F8"); Pump();
            Check(vm.Settings.Keybinds[ControlAction.MenuToggle] == "F8", "keybind capture saves F8");
            Click("NextBindButton"); InvokeInput("F8"); Pump();
            Check(vm.Settings.Keybinds[ControlAction.NextWeapon] == "WheelDown", "conflicting keybind rejected");
            InvokeInput("Escape");
            tabs.SelectedIndex = 0; Pump();
            InvokeInput("F8"); Pump();
            Check(!window.IsVisible, "menu toggle opens compact HUD");
            var hud = app.Windows.OfType<HudWindow>().Single();
            Check(hud.IsVisible && hud.Width <= 300 && !hud.ShowActivated, "compact HUD is small and does not activate on show");
            string previous = vm.Selected.Id; InvokeInput("WheelDown"); Pump();
            Check(vm.Candidate.Id != previous && vm.Selected.Id == previous, "wheel previews without selecting");
            Capture(hud, Path.Combine(output, "05-hud-preview.png"));
            InvokeInput("MiddleMouse"); Pump(); Check(vm.Selected == vm.Candidate, "middle mouse confirms candidate");
            Capture(hud, Path.Combine(output, "06-hud.png"));
            InvokeInput("F8"); Pump(); Check(window.IsVisible && !hud.IsVisible, "remapped key restores expanded menu");
            vm.Settings.UiScale = 1.5;
            typeof(MainWindow).GetMethod("ApplyWindowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Pump(); Capture(window, Path.Combine(output, "07-scale-150.png"));
            Check(window.ActualWidth <= SystemParameters.WorkArea.Width, "150% scale stays within primary work area");
            vm.Store.Flush(); Check(JsonFiles.LoadSettings(Rnr.Desktop.Services.AppPaths.Settings).Keybinds[ControlAction.MenuToggle] == "F8", "keybind persisted to disk");
            tabs.SelectedIndex = 1; Pump();
            var sensitivity = (TextBox)window.FindName("SensitivityBox");
            sensitivity.Text = "NaN"; InvokeHandler("SaveSettings");
            Check(vm.Settings.Sensitivity == 1 && vm.Feedback.Contains("0.01 to 10"), "invalid sensitivity is rejected without changing the saved value");
            sensitivity.Text = "0.83";
            using (var locked = File.Open(AppPaths.Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                InvokeHandler("SaveSettings");
                Check(vm.Feedback.Contains("could not be saved"), "a real file-sharing failure cannot report settings saved");
            }
            InvokeHandler("SaveSettings");
            Check(JsonFiles.LoadSettings(AppPaths.Settings).Sensitivity == .83 && vm.Feedback == "Settings saved.", "settings save recovers after the file lock is released");
            tabs.SelectedIndex = 0; search.Text = "Thompson"; Pump();
            // Use the explicit menu command; while editing a textbox the global shortcut is intentionally ignored.
            InvokeHandler("MinimizeToHud"); Pump(); InvokeInput("WheelDown"); InvokeInput("MiddleMouse"); InvokeInput("F8"); Pump();
            Check(search.Text.Length == 0 && list.SelectedItem as WeaponDefinition == vm.Selected, "restoring after a profile change reveals selection outside the previous filter");
            vm.Settings.RememberPosition = false;
            InvokeHandler("MinimizeToHud"); Pump(); WindowPlacement.Restore(hud, 210, 180); Pump();
            var moved = WindowPlacement.Capture(hud); InvokeInput("F8"); InvokeHandler("MinimizeToHud"); Pump();
            Check(WindowPlacement.Capture(hud) == moved, "HUD keeps its position within a session when persistence is disabled");
            InvokeInput("F8"); Pump();
            WindowPlacement.Restore(window, 99999, 99999); Pump();
            var recovered = WindowPlacement.Capture(window);
            Check(recovered.X < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                && recovered.Y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight,
                "off-screen native window position recovers onto an available display");
            var fitted = WindowPlacement.Contain(new Rect(-5000, -5000, 288, 114), new Rect(-1920, 0, 1920, 1040));
            Check(fitted.Left == -1920 && fitted.Top == 0 && fitted.Width == 288, "negative-coordinate monitor geometry keeps HUD inside the work area");
            vm.Settings.HudX = 777; vm.Settings.HudY = 666; vm.Store.Flush();
            CrosshairChecks.Run(window, output, Check);
            // Close the first instance before reloading to avoid two Raw Input registrations in one process.
            window.Close(); Pump();
            Check(!app.Windows.OfType<CrosshairWindow>().Any(), "closing the app also closes the crosshair");
            var reopened = new MainWindow(); reopened.Show(); Pump();
            var reopenedVm = (DashboardViewModel)reopened.DataContext;
            Check(reopenedVm.Settings.Crosshair.UseImage && reopenedVm.Settings.Crosshair.ImageSize == 43.5
                && app.Windows.OfType<CrosshairWindow>().Single().IsVisible,
                "crosshair enabled state and imported artwork restore after restart");
            Check(!reopenedVm.Settings.RememberPosition && reopenedVm.Settings.HudX == 16 && reopenedVm.Settings.HudY == 16,
                "remember-position off ignores saved HUD coordinates on the next launch");
            Check(!reopenedVm.Settings.WeaponProfiles["smg.thompson"].NoRecoil
                && !reopenedVm.Settings.WeaponProfiles["rifle.semiauto"].RapidFire
                && reopenedVm.Settings.WeaponProfiles["rifle.semiauto"].NoRecoil,
                "per-weapon on/off choices survive application restart");
            var inputField = typeof(MainWindow).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic)!;
            (inputField.GetValue(reopened) as RawInputManager)?.Dispose(); inputField.SetValue(reopened, null);
            reopenedVm.Settings.CompactEnabled = false;
            typeof(MainWindow).GetMethod("Compact", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, null);
            Check(reopened.IsVisible && reopenedVm.Feedback.Contains("Enable the compact HUD"), "failed global input plus disabled HUD cannot strand a hidden menu");
            reopened.Close(); Pump();
            File.WriteAllText(Path.Combine(output, "results.txt"), $"{passed} WPF interaction checks passed. Screenshots rendered from the actual Windows visual tree. Input routing invoked in-process; physical hardware/game-focus behavior still requires manual testing.");
            Console.WriteLine($"{passed} WPF interaction checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); Pump(); app.Shutdown(); }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
