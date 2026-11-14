using System.Text.Json;

namespace InputForge.Core;

public sealed record Preferences(string Application, MouseSettings Mouse, Dictionary<Control, int> Bindings)
{
    public static Preferences Create() => new("", new(), DefaultBindings.Create());

    public bool IsValid() =>
        Application is not null && Mouse is not null && Bindings is not null &&
        Bindings.Count == Enum.GetValues<Control>().Length &&
        Bindings.All(pair => Enum.IsDefined(pair.Key) && pair.Value is >= 0 and < 261) &&
        Mouse.IsValid();
}

public static class MouseSettingsValidation
{
    public static bool IsValid(this MouseSettings mouse) =>
        mouse.Horizontal is >= .1f and <= 4 && mouse.Vertical is >= .1f and <= 4 &&
        mouse.Deadzone is >= 0 and <= .35f && mouse.Acceleration is >= 0 and <= 3 &&
        mouse.Point1 is >= 0 and <= 1 && mouse.Point2 is >= 0 and <= 1 &&
        (!mouse.InterpolatedKnots || mouse.Point1 <= mouse.Point2) &&
        mouse.Input1 >= CurveKnots.Gap && mouse.Input2 <= 1 - CurveKnots.Gap &&
        mouse.Input2 - mouse.Input1 >= CurveKnots.Gap - .000001f &&
        mouse.Saturation is >= .5f and <= 1 && mouse.AntiDeadzone is >= 0 and <= .35f &&
        mouse.MaxOutput is >= .1f and <= 1 && mouse.NoiseCounts is >= 0 and <= 8 &&
        Enum.IsDefined(mouse.Curve) && Enum.IsDefined(mouse.Filter);
}

public sealed record NamedProfile(Guid Id, string Name, Preferences Settings, InputPreset Preset = InputPreset.Custom);

public sealed record ProfileLibrary(Guid Selected, List<NamedProfile> Profiles, int Schema = 0)
{
    public static ProfileLibrary FromLegacy(Preferences settings)
    {
        var profile = new NamedProfile(Guid.NewGuid(), "Default profile", settings);
        return new(profile.Id, [profile]);
    }

    public bool IsValid() => Schema is 0 or 1 && Profiles is { Count: > 0 and <= 512 } &&
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
        var library = JsonSerializer.Deserialize<ProfileLibrary>(ReadBounded(path));
        return library is not null && library.IsValid() ? library : throw new InvalidDataException("Invalid InputForge profile library.");
    }

    public static void WriteLibrary(string path, ProfileLibrary library)
    {
        if (!library.IsValid()) throw new InvalidDataException("Invalid InputForge profile library.");
        // Back up the old library once, before writing the versioned format.
        if (File.Exists(path) && ReadLibrary(path).Schema == 0 && !File.Exists(path + ".v0.bak"))
            File.Copy(path, path + ".v0.bak");
        WriteAtomic(path, JsonSerializer.Serialize(library with { Schema = 1 }));
    }

    public static Preferences Read(string path)
    {
        var profile = JsonSerializer.Deserialize<Preferences>(ReadBounded(path));
        return profile is not null && profile.IsValid() ? profile : throw new InvalidDataException("Invalid InputForge preferences.");
    }

    private static string ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("InputForge profile file exceeds 4 MiB.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
