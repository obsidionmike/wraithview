using System.IO;

namespace WraithView;

/// <summary>
/// One-time import of an existing TriffView setup (WraithView is a fork of TriffView). Runs before anything
/// loads its settings and only copies files into a WraithView folder that doesn't have them yet, so it never
/// overwrites WraithView's own data and leaves TriffView's files untouched.
/// </summary>
internal static class LegacyImport
{
    private const string MarkerName = "imported-from-triffview.txt";

    public static void Run()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) return;
            Run(appData);
        }
        catch
        {
            // Importing is a convenience; never block startup on it.
        }
    }

    internal static void Run(string appData)
    {
        var target = Path.Combine(appData, "WraithView");
        var marker = Path.Combine(target, MarkerName);
        if (File.Exists(marker)) return;

        var triffHud = Path.Combine(appData, "TriffHud");
        var triffView = Path.Combine(appData, "TriffView");
        if (!Directory.Exists(triffHud) && !Directory.Exists(triffView)) return;

        Directory.CreateDirectory(target);
        var copied = new List<string>();
        CopyFile(Path.Combine(triffHud, "triffview-settings.json"), Path.Combine(target, "wraithview-settings.json"), copied);
        CopyFile(Path.Combine(triffHud, "eve-settings.json"), Path.Combine(target, "eve-settings.json"), copied);
        CopyTree(Path.Combine(triffHud, "EveSettings", "backups"), Path.Combine(target, "EveSettings", "backups"), copied);
        // Fleet layouts carry over; sign-ins don't (WraithView uses its own EVE SSO application).
        CopyFile(Path.Combine(triffView, "trifffleets-settings.json"), Path.Combine(target, "wraithfleets-settings.json"), copied);
        CopyTree(Path.Combine(triffView, "TriffSkills", "plans"), Path.Combine(target, "WraithSkills", "plans"), copied);

        File.WriteAllText(marker, $"Imported from TriffView on {DateTime.Now:yyyy-MM-dd HH:mm}:{Environment.NewLine}{string.Join(Environment.NewLine, copied)}{Environment.NewLine}");
    }

    private static void CopyFile(string source, string destination, List<string> copied)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        copied.Add(destination);
    }

    private static void CopyTree(string source, string destination, List<string> copied)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)), copied);
        }
    }
}
