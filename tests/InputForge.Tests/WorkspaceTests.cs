using System.Numerics;
using System.IO;
using InputForge.Core;
using Xunit;

namespace InputForge.Tests;

public class WorkspaceTests
{
    [Fact]
    public void ProfileLibraryRetainsLegacySettingsAndMatchesOnlyAssociatedApplication()
    {
        var legacy = Preferences.Create() with { Application = @"C:\Games\Sample.exe" };
        var library = ProfileLibrary.FromLegacy(legacy);
        var other = new NamedProfile(Guid.NewGuid(), "Another profile", Preferences.Create());
        var combined = new ProfileLibrary(other.Id, [..library.Profiles, other]);
        Assert.True(combined.IsValid());
        Assert.Equal(library.Selected, combined.ForApplication(@"c:\games\sample.exe")!.Id);
        Assert.Null(combined.ForApplication(@"C:\Games\Other.exe"));
        Assert.Null(combined.ForApplication(""));
        Assert.False((combined with { Profiles = [..combined.Profiles, new NamedProfile(Guid.NewGuid(), "Duplicate", legacy)] }).IsValid());

        string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ProfileStore.WriteLibrary(path, combined);
            var read = ProfileStore.ReadLibrary(path);
            Assert.Equal(other.Id, read.Selected);
            Assert.Equal(legacy.Mouse, read.ForApplication(legacy.Application)!.Settings.Mouse);
            Assert.Equal(legacy.Bindings[Control.B], read.ForApplication(legacy.Application)!.Settings.Bindings[Control.B]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void BindingListAndModelShareOneSelectionWithoutRepeatNotifications()
    {
        var selection = new BindingSelection();
        Control list = Control.A, model = Control.A;
        int count = 0;
        selection.Changed += control => { list = control; selection.Select(control); count++; };
        selection.Changed += control => model = control;

        selection.Select(Control.RightTrigger);
        Assert.Equal(Control.RightTrigger, model);
        selection.Select(Control.LeftBumper);
        Assert.Equal(Control.LeftBumper, list);
        Assert.Equal(2, count);
    }

    [Fact]
    public void TestInputUsesDefaultsAndRespectsIndependentReleaseAndFocusLoss()
    {
        var input = new InputSession();
        var config = new object();
        var profile = Preferences.Create();
        input.SetScope(1, config);
        input.SetButton(DefaultBindings.MouseLeft, true);
        input.SetButton(DefaultBindings.MouseRight, true);
        input.SetButton(DefaultBindings.LeftShift, true);
        input.SetButton(0x20, true);
        input.SetButton('W', true);
        input.Move(5, -3);

        ControllerState before = input.Update(profile.Bindings, profile.Mouse, .008f);
        Assert.True(before.IsPressed(Control.A));
        Assert.True(before.IsPressed(Control.B));
        Assert.Equal(Vector2.UnitY, before.Left);
        Assert.True(before.Right.Length() > 0);
        Assert.Equal(1f, before.LT);
        Assert.Equal(1f, before.RT);

        input.SetButton(DefaultBindings.MouseLeft, false);
        ControllerState after = input.Update(profile.Bindings, profile.Mouse, .008f);
        Assert.Equal(1f, after.LT);
        Assert.Equal(0f, after.RT);
        Assert.True(after.IsPressed(Control.B));

        Assert.True(input.SetScope(0, config));
        Assert.Equal(default, input.Update(profile.Bindings, profile.Mouse, .008f));
        input.SetButton(DefaultBindings.MouseLeft, true);
        Assert.True(input.SetScope(1, config));
        Assert.Equal(default, input.Update(profile.Bindings, profile.Mouse, .008f));
    }

    [Fact]
    public void ProfileRoundTripKeepsClearedControlsAndCustomValues()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "ProfileStoreTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "inputforge.json");
            var profile = Preferences.Create() with
            {
                Application = @"C:\Games\Sample.exe",
                Mouse = new MouseSettings(Horizontal: 1.73f, Curve: ResponseCurve.Custom)
            };
            ProfileStore.Write(path, profile);
            profile.Bindings[Control.A] = 0;
            profile.Bindings[Control.RightTrigger] = 'T';
            ProfileStore.Write(path, profile);

            var read = ProfileStore.Read(path);
            Assert.Equal(profile.Application, read.Application);
            Assert.Equal(profile.Mouse, read.Mouse);
            Assert.Equal(0, read.Bindings[Control.A]);
            Assert.Equal((int)'T', read.Bindings[Control.RightTrigger]);
            Assert.Single(Directory.GetFiles(directory));

            profile.Bindings[Control.B] = 1000;
            Assert.Throws<InvalidDataException>(() => ProfileStore.Write(path, profile));
            Assert.Equal(DefaultBindings.LeftShift, ProfileStore.Read(path).Bindings[Control.B]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }
}
