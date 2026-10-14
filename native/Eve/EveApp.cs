namespace WraithView.Eve;

/// <summary>
/// WraithView's EVE SSO application (developers.eveonline.com). Fleet Manager and Skill Planner share it.
/// Register it with callback http://127.0.0.1:51777/wraithfleets/callback/ and the scopes
/// esi-fleets.read_fleet.v1, esi-fleets.write_fleet.v1, esi-skills.read_skills.v1, esi-skills.read_skillqueue.v1.
/// While ClientId is empty, both tools show sign-in as not configured. WRAITHVIEW_EVE_CLIENT_ID overrides it.
/// </summary>
internal static class EveApp
{
    private const string BuiltInClientId = "";

    public static readonly string ClientId = Environment.GetEnvironmentVariable("WRAITHVIEW_EVE_CLIENT_ID")?.Trim() is { Length: > 0 } value
        ? value
        : BuiltInClientId;

    public const string RedirectUri = "http://127.0.0.1:51777/wraithfleets/callback/";

    public static string UserAgent { get; } =
        $"WraithView/{typeof(EveApp).Assembly.GetName().Version?.ToString(3) ?? "0"} (+https://github.com/obsidionmike/wraithview)";
}
