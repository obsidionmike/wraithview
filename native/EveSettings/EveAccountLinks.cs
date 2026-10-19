namespace WraithView.EveSettings;

/// <summary>
/// Works out which account file (core_user_&lt;userID&gt;.dat) belongs to which character file
/// (core_char_&lt;characterID&gt;.dat). The files don't name their owner, and a copied file carries the source's
/// IDs, so the only signal is timing: a client that logs out writes its character file and its account file
/// within a second or two of each other.
/// </summary>
internal static class EveAccountLinks
{
    public const string Manual = "manual";
    public const string Seen = "seen";

    public static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Character/account pairs that can only mean one logout. Within <see cref="PairWindow"/> of the character
    /// file there must be exactly one account file and no other character file, and that account file must have
    /// no other character file near it either. Several clients closing together, or a copy (Windows keeps the
    /// source's timestamp), puts more than one file in the window and yields nothing.
    /// </summary>
    public static IReadOnlyList<LogoutPair> CleanLogoutPairs(IReadOnlyList<StampedFile> characters, IReadOnlyList<StampedFile> accounts)
    {
        var pairs = new List<LogoutPair>();
        foreach (var character in characters)
        {
            if (characters.Any(other => !ReferenceEquals(other, character) && Near(other, character))) continue;

            var nearby = accounts.Where(account => Near(account, character)).ToArray();
            if (nearby.Length != 1) continue;

            var account = nearby[0];
            if (characters.Any(other => !ReferenceEquals(other, character) && Near(other, account))) continue;

            var seen = character.ModifiedUtc > account.ModifiedUtc ? character.ModifiedUtc : account.ModifiedUtc;
            pairs.Add(new LogoutPair(character.Id, account.Id, seen));
        }
        return pairs;
    }

    /// <summary>
    /// Records clean pairs as "seen" links. A link the user set by hand is never changed. A seen link moves to
    /// another account only on a newer clean pair (the character was transferred). Returns true if anything changed.
    /// </summary>
    public static bool Learn(IDictionary<string, EveAccountLink> links, IEnumerable<LogoutPair> pairs)
    {
        var changed = false;
        foreach (var pair in pairs)
        {
            if (!links.TryGetValue(pair.CharacterId, out var link))
            {
                links[pair.CharacterId] = new EveAccountLink { AccountId = pair.AccountId, Source = Seen, SeenUtc = pair.SeenUtc };
                changed = true;
                continue;
            }

            if (!string.Equals(link.Source, Seen, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.SeenUtc <= link.SeenUtc) continue;

            link.AccountId = pair.AccountId;
            link.SeenUtc = pair.SeenUtc;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The account files a character copy must also overwrite: one per distinct target account, skipping the
    /// source's own account (characters on the same account already share it).
    /// </summary>
    public static AccountCopyPlan PlanAccountCopy(
        string sourceCharacterId,
        IEnumerable<string> targetCharacterIds,
        IReadOnlyDictionary<string, EveAccountLink> links,
        ISet<string> existingAccountIds)
    {
        var sourceAccount = LinkedAccount(sourceCharacterId, links, existingAccountIds);
        var targets = new List<string>();
        var unlinked = new List<string>();
        var sharing = new List<string>();

        foreach (var characterId in targetCharacterIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var account = LinkedAccount(characterId, links, existingAccountIds);
            if (account == null)
            {
                unlinked.Add(characterId);
                continue;
            }
            if (string.Equals(account, sourceAccount, StringComparison.OrdinalIgnoreCase))
            {
                sharing.Add(characterId);
                continue;
            }
            if (!targets.Contains(account, StringComparer.OrdinalIgnoreCase)) targets.Add(account);
        }

        return new AccountCopyPlan(sourceAccount, targets, unlinked, sharing);
    }

    private static string? LinkedAccount(string characterId, IReadOnlyDictionary<string, EveAccountLink> links, ISet<string> existingAccountIds)
    {
        return links.TryGetValue(characterId, out var link) && existingAccountIds.Contains(link.AccountId) ? link.AccountId : null;
    }

    private static bool Near(StampedFile left, StampedFile right)
    {
        return (left.ModifiedUtc - right.ModifiedUtc).Duration() <= PairWindow;
    }
}

internal sealed record StampedFile(string Id, DateTime ModifiedUtc);

internal sealed record LogoutPair(string CharacterId, string AccountId, DateTime SeenUtc);

/// <param name="SourceAccountId">The source character's account, or null if it isn't linked.</param>
/// <param name="TargetAccountIds">Account files to overwrite.</param>
/// <param name="UnlinkedCharacterIds">Targets with no known account; the copy can't cover their account settings.</param>
/// <param name="SharedCharacterIds">Targets on the source's own account, which need no account copy.</param>
internal sealed record AccountCopyPlan(
    string? SourceAccountId,
    IReadOnlyList<string> TargetAccountIds,
    IReadOnlyList<string> UnlinkedCharacterIds,
    IReadOnlyList<string> SharedCharacterIds);

internal sealed class EveAccountLink
{
    public string AccountId { get; set; } = "";
    /// <summary><see cref="EveAccountLinks.Manual"/> (picked by the user) or <see cref="EveAccountLinks.Seen"/> (from a logout).</summary>
    public string Source { get; set; } = "";
    public DateTime SeenUtc { get; set; }
}
