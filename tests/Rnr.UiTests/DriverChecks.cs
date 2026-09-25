using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rnr.Desktop;
using Rnr.Desktop.Input;
using Rnr.Desktop.Services;

internal static class DriverChecks
{
    public static void Run(Action<bool, string> check, Action pump, Action<Window, string> capture, string output)
    {
        uint Code(uint function, uint access) => (0x22u << 16) | (access << 14) | (function << 2);
        check(SyntheticMouseOutput.GetStatus == Code(0x800, 1) &&
            SyntheticMouseOutput.SetEnabled == Code(0x801, 2) && SyntheticMouseOutput.MoveRelative == Code(0x802, 2) &&
            SyntheticMouseOutput.SetButtons == Code(0x803, 2) && SyntheticMouseOutput.Reset == Code(0x806, 2), "driver IOCTLs match shared header");
        var move = SyntheticMouseOutput.EncodeMovement(-200, 300);
        check(move.Length == 8 && BitConverter.ToInt32(move, 0) == -200 && BitConverter.ToInt32(move, 4) == 300,
            "driver movement preserves signed 32-bit coordinates");
        byte[] status = new byte[28]; BitConverter.TryWriteBytes(status.AsSpan(), 28u);
        BitConverter.TryWriteBytes(status.AsSpan(4), 0x10000u);
        SyntheticMouseOutput.ValidateStatus(status, 28);
        void Reject(byte[] bytes, int returned, string name)
        {
            bool rejected = false;
            try { SyntheticMouseOutput.ValidateStatus(bytes, returned); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, name);
        }
        Reject(status, 19, "truncated driver status is rejected");
        Reject(status, 24, "inconsistent driver status size is rejected");
        status[4] = 1; Reject(status, 28, "incompatible driver protocol is rejected");
        check(RawInputManager.IsSyntheticDeviceName(@"\\?\HID#VID_F055&PID_0001#test") &&
            !RawInputManager.IsSyntheticDeviceName(@"\\?\HID#VID_046D&PID_C539#test"),
            "synthetic mouse feedback is excluded while physical mouse stays eligible");
        string directory = Path.Combine(output, "empty-driver-package"); Directory.CreateDirectory(directory);
        bool missing = false;
        try { DriverSetup.ValidatePackage(directory); } catch (FileNotFoundException ex) { missing = ex.Message.Contains(".sys") && ex.Message.Contains(".cat"); }
        check(missing, "source-only package cannot be reported as installed");
        var unsignedCatalog = Path.Combine(directory, "unsigned.cat"); File.WriteAllText(unsignedCatalog, "unsigned test fixture");
        bool unsignedRejected = false;
        try { DriverSignature.VerifyCatalog(unsignedCatalog); }
        catch (InvalidOperationException ex) { unsignedRejected = ex.Message.Contains("trusted digital signature"); }
        check(unsignedRejected, "Windows signature verification rejects an unsigned package before elevation");
        int attempts = 0; bool ready = false;
        var loading = new DriverLoadingWindow(progress =>
        {
            attempts++; progress("Checking driver…");
            return Task.FromException<SyntheticMouseOutput>(new InvalidOperationException("A compiled, signed driver package is required.\n\nInstallation cannot continue until the package is available."));
        });
        loading.Ready += _ => ready = true;
        loading.Show(); pump();
        var panel = (StackPanel)loading.Content;
        check(!ready && panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("cannot continue")),
            "failed driver startup shows the error without opening the menu");
        var retry = ((StackPanel)panel.Children[^1]).Children.OfType<Button>().First();
        check(retry.IsVisible && !panel.Children.OfType<ProgressBar>().Single().IsVisible,
            "failure stops the loading bar and offers Retry");
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump();
        check(attempts == 2 && !ready, "Retry repeats driver checks without bypassing readiness");
        capture(loading, Path.Combine(output, "00-driver-startup.png")); loading.Close(); pump();
    }
}
