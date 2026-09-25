using Rnr.Core;
using Rnr.Companion;

int passed = 0;
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

Test("catalog has unique server identifiers and current additions", () =>
{
    Check(WeaponCatalog.All.Count == 29 && WeaponCatalog.All.Select(w => w.Id).Distinct().Count() == 29);
    Check(WeaponCatalog.Find("rifle.m16")!.FireMode == FireMode.Burst);
    Check(WeaponCatalog.Find("t1_smg") is not null && WeaponCatalog.Find("rifle.sks") is not null && WeaponCatalog.Find("revolver.hc") is not null);
    Check(WeaponCatalog.All.Where(w => w.FireMode is FireMode.Automatic or FireMode.Manual or FireMode.SingleShot or FireMode.Burst).All(w => !w.RapidFireApplicable));
});
Test("preview and selected profile stay independent; confirm and wrap are exclusive", () =>
{
    var selection = new WeaponSelection(); selection.Preview(-1);
    Check(selection.Candidate == WeaponCatalog.All[^1] && selection.Selected.Id == "rifle.ak");
    selection.Confirm(); Check(selection.Selected == selection.Candidate);
    selection.Preview(1); Check(selection.Candidate.Id == "rifle.ak");
    selection.Select("smg.thompson"); Check(selection.Selected.Id == "smg.thompson" && selection.Candidate == selection.Selected);
    Throws<ArgumentException>(() => selection.Select("fake"));
});
Test("keybinding conflicts rejected without changing existing binding", () =>
{
    var settings = new UserSettings();
    Check(!BindingRules.TrySet(settings, ControlAction.ConfirmWeapon, "Insert", out _));
    Check(settings.Keybinds[ControlAction.ConfirmWeapon] == "MiddleMouse");
    Check(!BindingRules.TrySet(settings, ControlAction.MenuToggle, "LeftMouse", out _));
    Check(BindingRules.TrySet(settings, ControlAction.MenuToggle, "F8", out _));
});
Test("profile defaults are on where applicable and normalization preserves explicit off choices", () =>
{
    var settings = new UserSettings();
    Check(settings.WeaponProfiles.Count == WeaponCatalog.All.Count);
    Check(WeaponCatalog.All.All(w => settings.WeaponProfiles[w.Id].NoRecoil
        && settings.WeaponProfiles[w.Id].RapidFire == w.RapidFireApplicable));
    settings.WeaponProfiles["smg.thompson"].NoRecoil = false;
    settings.WeaponProfiles["rifle.semiauto"].RapidFire = false;
    settings.WeaponProfiles["rifle.ak"].RapidFire = true;
    settings.WeaponProfiles.Remove("rifle.sks");
    settings.Normalize();
    Check(!settings.WeaponProfiles["smg.thompson"].NoRecoil && !settings.WeaponProfiles["rifle.semiauto"].RapidFire);
    Check(!settings.WeaponProfiles["rifle.ak"].RapidFire && settings.WeaponProfiles["rifle.sks"].RapidFire);
});
Test("corrupt settings recover and valid settings roundtrip", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "rnr-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "settings.json");
    try
    {
        File.WriteAllText(path, "{broken"); Check(JsonFiles.LoadSettings(path).Sensitivity == 1);
        File.WriteAllText(path, "{\"sensitivity\":-10,\"uiScale\":40,\"keybinds\":null,\"weaponProfiles\":null}");
        var settings = JsonFiles.LoadSettings(path); Check(settings.Sensitivity == 1 && settings.UiScale == 1.5 && settings.Keybinds.Count == UserSettings.DefaultBinds().Count);
        Check(settings.WeaponProfiles["rifle.ak"].NoRecoil && settings.WeaponProfiles["rifle.semiauto"].RapidFire);
        settings.WeaponProfiles["rifle.semiauto"].RapidFire = false;
        settings.LastWeapon = "smg.thompson"; settings.HudX = -400; JsonFiles.Save(path, settings);
        Check(JsonFiles.LoadSettings(path).LastWeapon == "smg.thompson" && JsonFiles.LoadSettings(path).HudX == -400);
        Check(!JsonFiles.LoadSettings(path).WeaponProfiles["rifle.semiauto"].RapidFire);
    }
    finally { File.Delete(path); Directory.Delete(dir); }
});
const string steam = "76561198000000000";
Test("crosshair settings recover missing, invalid and nonfinite values", () =>
{
    var options = new CrosshairOptions { Color = "red", DotSize = double.NaN, ImageSize = 999 };
    options.Normalize();
    Check(options.Color == "#9AE477" && options.DotSize == 4 && options.ImageSize == 128);
    Check(!CrosshairOptions.ValidColor("#12345G") && !CrosshairOptions.ValidColor(null));
    options.Color = "#abcdef"; options.DotSize = .2; options.ImageSize = double.PositiveInfinity;
    options.Normalize();
    Check(options.Color == "#ABCDEF" && options.DotSize == 1 && options.ImageSize == 32);
    var settings = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"crosshair\":null}", JsonFiles.Options)!;
    settings.Normalize(); Check(!settings.Crosshair.Enabled && settings.Crosshair.DotSize == 4);
});
var clock = new ManualTime();
BridgeReport Report(bool nr = false, bool rf = false, ObservedProfile? observed = null) =>
    new("Test private server", "test-build", [new("rifle.ak", nr, false, "test"), new("rifle.semiauto", nr, rf, "test")], observed is null ? [] : [observed]);
Test("offline and unsupported effects fail closed", () =>
{
    var coordinator = new ProfileCoordinator(steam, clock);
    Throws<InvalidOperationException>(() => coordinator.Apply(new("rifle.ak", true, false)));
    coordinator.Synchronize(Report());
    Throws<InvalidOperationException>(() => coordinator.Apply(new("rifle.semiauto", false, true)));
    Throws<ArgumentException>(() => coordinator.Apply(new("bogus", false, false)));
    Check(!StatusRules.NoRecoilActive(coordinator.Read(), "rifle.ak", clock.GetUtcNow()));
});
Test("request is never an acknowledgement; mismatched revisions cannot turn HUD green", () =>
{
    var coordinator = new ProfileCoordinator(steam, clock); coordinator.Synchronize(Report(true));
    var state = coordinator.Apply(new("rifle.ak", true, false)); long rev = state.Desired!.Revision;
    Check(!StatusRules.NoRecoilActive(state, "rifle.ak", clock.GetUtcNow()));
    coordinator.Synchronize(Report(true, false, new(steam, "rifle.ak", true, false, rev - 1, "stale")));
    Check(!StatusRules.NoRecoilActive(coordinator.Read(), "rifle.ak", clock.GetUtcNow()));
    coordinator.Synchronize(Report(true, false, new(steam, "rifle.ak", true, false, rev, "confirmed")));
    Check(StatusRules.NoRecoilActive(coordinator.Read(), "rifle.ak", clock.GetUtcNow()));
    Check(!StatusRules.NoRecoilActive(coordinator.Read(), "smg.thompson", clock.GetUtcNow()));
    var replaced = coordinator.Apply(new("rifle.semiauto", false, false));
    Check(replaced.Desired!.WeaponId == "rifle.semiauto" && !StatusRules.NoRecoilActive(replaced, "rifle.ak", clock.GetUtcNow()));
});
Test("lease expiration clears effects and reconnection cannot resume them", () =>
{
    var coordinator = new ProfileCoordinator(steam, clock); coordinator.Synchronize(Report(true));
    coordinator.Apply(new("rifle.ak", true, false)); clock.Advance(9);
    var next = coordinator.Read(); Check(!next.ServerOnline && next.Desired!.NoRecoil == false);
    coordinator.Clear(); Check(coordinator.Synchronize(Report(true)).Profiles.Count == 0);
});
Test("bridge validates ownership and duplicate capability records", () =>
{
    var coordinator = new ProfileCoordinator(steam, clock);
    Throws<ArgumentException>(() => coordinator.Synchronize(Report(true, false, new("76561198000000001", "rifle.ak", true, false, 1, "wrong player"))));
    var report = Report(); report.Capabilities.Add(report.Capabilities[0]); Throws<ArgumentException>(() => coordinator.Synchronize(report));
});
Test("fresh server report is required after connection loss", () =>
{
    var coordinator = new ProfileCoordinator(steam, clock); coordinator.Synchronize(Report(true));
    var state = coordinator.Apply(new("rifle.ak", true, false));
    coordinator.Synchronize(Report(true, false, new(steam, "rifle.ak", true, false, state.Desired!.Revision, "active")));
    clock.Advance(5); Check(!StatusRules.NoRecoilActive(coordinator.Read(), "rifle.ak", clock.GetUtcNow()));
});
Console.WriteLine($"{passed} tests passed.");

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
