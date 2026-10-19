using System.IO.Compression;
using WraithView.EveSettings;

namespace WraithView.Tests;

public class EveAccountLinksTests : IDisposable
{
    private static readonly DateTime T = new(2026, 10, 18, 22, 37, 17, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wraithview-eve-settings-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static StampedFile At(string id, double seconds) => new(id, T.AddSeconds(seconds));

    [Fact]
    public void SingleLogoutPairsCharacterWithAccountWrittenAlongsideIt()
    {
        // One client closed: its character and account file are written a second apart.
        var pairs = EveAccountLinks.CleanLogoutPairs(
            [At("2124740629", 0), At("2124712233", 2053)],
            [At("33871828", 1), At("33824378", 2053)]);

        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, pair => pair is { CharacterId: "2124740629", AccountId: "33871828" });
        Assert.Contains(pairs, pair => pair is { CharacterId: "2124712233", AccountId: "33824378" });
    }

    [Fact]
    public void ClientsClosedTogetherAreAmbiguous()
    {
        // Two clients closed in the same second; the nearest account file would be the wrong one for one of them.
        var pairs = EveAccountLinks.CleanLogoutPairs(
            [At("2124661971", 0), At("2124782284", 0)],
            [At("17869643", 0), At("33938206", 2)]);

        Assert.Empty(pairs);
    }

    [Fact]
    public void CopiedFilesKeepTheSourceTimestampAndAreIgnored()
    {
        // Windows keeps the source's last-write time on a copy, so copies sit on top of the real logout.
        var pairs = EveAccountLinks.CleanLogoutPairs(
            [At("2124661971", 0), At("2122885899", 0), At("2124776173", 0)],
            [At("17869643", 0)]);

        Assert.Empty(pairs);
    }

    [Fact]
    public void AccountFileNearAnotherCharacterIsNotPaired()
    {
        // The account file is within the window of a second character that is itself outside the first one's.
        var pairs = EveAccountLinks.CleanLogoutPairs(
            [At("1", 0), At("2", 5)],
            [At("100", 2.5)]);

        Assert.Empty(pairs);
    }

    [Fact]
    public void FilesFurtherApartThanTheWindowAreNotPaired()
    {
        Assert.Empty(EveAccountLinks.CleanLogoutPairs([At("1", 0)], [At("100", 59)]));
    }

    [Fact]
    public void LearnKeepsManualLinksAndMovesSeenLinksOnlyForNewerLogouts()
    {
        var links = new Dictionary<string, EveAccountLink>
        {
            ["1"] = new() { AccountId = "100", Source = EveAccountLinks.Manual, SeenUtc = T },
            ["2"] = new() { AccountId = "200", Source = EveAccountLinks.Seen, SeenUtc = T },
            ["3"] = new() { AccountId = "300", Source = EveAccountLinks.Seen, SeenUtc = T },
        };

        var changed = EveAccountLinks.Learn(links,
        [
            new LogoutPair("1", "999", T.AddDays(1)),
            new LogoutPair("2", "201", T.AddDays(1)),
            new LogoutPair("3", "301", T.AddDays(-1)),
            new LogoutPair("4", "400", T),
        ]);

        Assert.True(changed);
        Assert.Equal("100", links["1"].AccountId);
        Assert.Equal("201", links["2"].AccountId);
        Assert.Equal("300", links["3"].AccountId);
        Assert.Equal(EveAccountLinks.Seen, links["4"].Source);
        Assert.False(EveAccountLinks.Learn(links, [new LogoutPair("4", "400", T)]));
    }

    [Fact]
    public void AnEmptyManualLinkIsNotOverwrittenByALogout()
    {
        var links = new Dictionary<string, EveAccountLink> { ["1"] = new() { AccountId = "", Source = EveAccountLinks.Manual } };
        Assert.False(EveAccountLinks.Learn(links, [new LogoutPair("1", "100", T)]));
        Assert.Equal("", links["1"].AccountId);
    }

    [Fact]
    public void AccountPlanSkipsTheSourceAccountAndDeduplicatesSharedAccounts()
    {
        var links = new Dictionary<string, EveAccountLink>
        {
            ["src"] = new() { AccountId = "100" },
            ["sameAccount"] = new() { AccountId = "100" },
            ["a1"] = new() { AccountId = "200" },
            ["a2"] = new() { AccountId = "200" },
            ["b"] = new() { AccountId = "300" },
            ["gone"] = new() { AccountId = "999" },
        };
        var existing = new HashSet<string> { "100", "200", "300" };

        var plan = EveAccountLinks.PlanAccountCopy("src", ["sameAccount", "a1", "a2", "b", "gone", "none"], links, existing);

        Assert.Equal("100", plan.SourceAccountId);
        Assert.Equal(new[] { "200", "300" }, plan.TargetAccountIds);
        Assert.Equal(new[] { "gone", "none" }, plan.UnlinkedCharacterIds);
        Assert.Equal(new[] { "sameAccount" }, plan.SharedCharacterIds);
    }

    [Fact]
    public void AccountPlanWithoutASourceLinkHasNoSourceAccount()
    {
        var plan = EveAccountLinks.PlanAccountCopy("src", ["t"], new Dictionary<string, EveAccountLink>(), new HashSet<string>());
        Assert.Null(plan.SourceAccountId);
        Assert.Equal(new[] { "t" }, plan.UnlinkedCharacterIds);
    }

    [Fact]
    public void CopyWithBackupsBacksUpEveryTargetThenCopies()
    {
        var profile = Path.Combine(_dir, "settings_Default");
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(profile);
        var sourceChar = Write(profile, "core_char_1.dat", "char one");
        var targetChar = Write(profile, "core_char_2.dat", "char two");
        var sourceUser = Write(profile, "core_user_100.dat", "user hundred");
        var targetUser = Write(profile, "core_user_200.dat", "user two hundred");

        EveSettingsController.CopyWithBackups([(sourceChar, targetChar), (sourceUser, targetUser)], backups);

        Assert.Equal("char one", File.ReadAllText(targetChar));
        Assert.Equal("user hundred", File.ReadAllText(targetUser));
        Assert.Equal("char one", File.ReadAllText(sourceChar));
        var restored = Directory.GetFiles(backups, "*.zip").Select(ReadBackedUpFile).OrderBy(text => text).ToArray();
        Assert.Equal(new[] { "char two", "user two hundred" }, restored);
    }

    [Fact]
    public void CopyWithBackupsOverwritesNothingWhenABackupFails()
    {
        var profile = Path.Combine(_dir, "settings_Default");
        Directory.CreateDirectory(profile);
        var source = Write(profile, "core_char_1.dat", "char one");
        var first = Write(profile, "core_char_2.dat", "char two");
        var second = Write(profile, "core_char_3.dat", "char three");
        // A backup root that is a file can't hold backups, so the first backup throws.
        var badRoot = Write(_dir, "not-a-folder", "");

        Assert.ThrowsAny<IOException>(() => EveSettingsController.CopyWithBackups([(source, first), (source, second)], badRoot));

        Assert.Equal("char two", File.ReadAllText(first));
        Assert.Equal("char three", File.ReadAllText(second));
    }

    [Fact]
    public void BackupsOfOneFileInTheSameSecondDoNotCollide()
    {
        var file = Write(_dir, "core_user_100.dat", "user");
        var backups = Path.Combine(_dir, "backups");
        var first = EveSettingsController.BackupFileTo(file, backups);
        var second = EveSettingsController.BackupFileTo(file, backups);
        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(backups, "*.zip").Length);
    }

    private static string Write(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string ReadBackedUpFile(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.Single(entry => entry.FullName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
