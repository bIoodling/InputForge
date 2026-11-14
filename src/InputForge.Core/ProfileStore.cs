using System.Text.Json;

namespace InputForge.Core;

public sealed record Preferences(string Application, MouseSettings Mouse, Dictionary<Control, int> Bindings)
{
    public static Preferences Create() => new("", new(), DefaultBindings.Create());

    public bool IsValid() =>
        Application is not null && Mouse is not null && Bindings is not null &&
        Bindings.Count == Enum.GetValues<Control>().Length &&
        Bindings.All(pair => Enum.IsDefined(pair.Key) && pair.Value is >= 0 and < 261) &&
        Mouse.Horizontal is >= .1f and <= 4 && Mouse.Vertical is >= .1f and <= 4 &&
        Mouse.Deadzone is >= 0 and <= .35f && Mouse.Acceleration is >= 0 and <= 3 &&
        Mouse.Point1 is >= 0 and <= 1 && Mouse.Point2 is >= 0 and <= 1 &&
        (!Mouse.InterpolatedKnots || Mouse.Point1 <= Mouse.Point2) &&
        Mouse.Input1 >= CurveKnots.Gap && Mouse.Input2 <= 1 - CurveKnots.Gap &&
        Mouse.Input2 - Mouse.Input1 >= CurveKnots.Gap - .000001f &&
        Mouse.Saturation is >= .5f and <= 1 && Mouse.AntiDeadzone is >= 0 and <= .35f &&
        Mouse.MaxOutput is >= .1f and <= 1 &&
        Enum.IsDefined(Mouse.Curve) && Enum.IsDefined(Mouse.Filter);
}

public sealed record NamedProfile(Guid Id, string Name, Preferences Settings, InputPreset Preset = InputPreset.Custom);

public sealed record ProfileLibrary(Guid Selected, List<NamedProfile> Profiles)
{
    public static ProfileLibrary FromLegacy(Preferences settings)
    {
        var profile = new NamedProfile(Guid.NewGuid(), "Default profile", settings);
        return new(profile.Id, [profile]);
    }

    public bool IsValid() => Profiles is { Count: > 0 } &&
        Profiles.All(p => p is not null && p.Id != Guid.Empty && !string.IsNullOrWhiteSpace(p.Name) &&
            p.Name.Length <= 64 && p.Settings is not null && p.Settings.IsValid()) &&
        Profiles.Select(p => p.Id).Distinct().Count() == Profiles.Count &&
        Profiles.Any(p => p.Id == Selected) &&
        Profiles.Where(p => p.Settings.Application.Length > 0).Select(p => p.Settings.Application)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == Profiles.Count(p => p.Settings.Application.Length > 0);

    public NamedProfile? ForApplication(string path) => path.Length == 0 ? null :
        Profiles.FirstOrDefault(p => string.Equals(p.Settings.Application, path, StringComparison.OrdinalIgnoreCase));
}

public static class ProfileStore
{
    public static ProfileLibrary ReadLibrary(string path)
    {
        var library = JsonSerializer.Deserialize<ProfileLibrary>(File.ReadAllText(path));
        return library is not null && library.IsValid() ? library : throw new InvalidDataException("Invalid InputForge profile library.");
    }

    public static void WriteLibrary(string path, ProfileLibrary library)
    {
        if (!library.IsValid()) throw new InvalidDataException("Invalid InputForge profile library.");
        WriteAtomic(path, JsonSerializer.Serialize(library));
    }

    public static Preferences Read(string path)
    {
        var profile = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
        return profile is not null && profile.IsValid() ? profile : throw new InvalidDataException("Invalid InputForge preferences.");
    }

    public static void Write(string path, Preferences profile)
    {
        if (!profile.IsValid()) throw new InvalidDataException("Invalid InputForge preferences.");
        WriteAtomic(path, JsonSerializer.Serialize(profile));
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
