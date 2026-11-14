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
        Enum.IsDefined(Mouse.Curve) && Enum.IsDefined(Mouse.Filter);
}

public static class ProfileStore
{
    public static Preferences Read(string path)
    {
        var profile = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
        return profile is not null && profile.IsValid() ? profile : throw new InvalidDataException("Invalid InputForge preferences.");
    }

    public static void Write(string path, Preferences profile)
    {
        if (!profile.IsValid()) throw new InvalidDataException("Invalid InputForge preferences.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(profile));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
