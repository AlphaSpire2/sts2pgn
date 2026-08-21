using STS2PGN.Model;

namespace STS2PGN.Scripting;

/// <summary>
/// Constants for the decision-script profile of Format version 1.
/// A script is a proposal produced by a simulator; it carries a <c>Producer</c> tag and never a
/// <c>RecorderVersion</c> tag. It uses the same grammar and record kinds as a recording but omits
/// observations, digests, resolutions, and the final <c>run.finish</c>.
/// </summary>
public static class ScriptProfile
{
    public const string ProducerTag = "Producer";
    public const string RecorderVersionTag = "RecorderVersion";
    public const string RequiredMode = "CUSTOM";

    /// <summary>Tags every script must carry, in canonical order.</summary>
    public static readonly IReadOnlyList<string> RequiredTags =
    [
        "Format",
        "FormatVersion",
        ProducerTag,
        "Seed",
        "Mode",
        "Ascension",
        "Players",
        "GameVersion",
        "GameCommit",
        "ModelIdHash",
    ];

    /// <summary>Initiating actions that may suspend for nested <c>choice.*</c> records.</summary>
    public static readonly IReadOnlySet<string> SuspendableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "combat.play_card",
        "combat.use_potion",
        "event.choose",
        "reward.claim",
        "rest.choose",
        "shop.purchase",
        "shop.remove_card",
        "treasure.choose_relic",
    };

    /// <summary>Record kinds a playback driver executes, in script order.</summary>
    public static readonly IReadOnlySet<string> DriveableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "act.next",
        "choice.cards",
        "choice.index",
        "combat.discard_potion",
        "combat.end_turn",
        "combat.play_card",
        "combat.use_potion",
        "event.advance",
        "event.choose",
        "map.choose",
        "rest.choose",
        "reward.claim",
        "reward.skip",
        "room.proceed",
        "shop.purchase",
        "shop.remove_card",
        "treasure.choose_relic",
        "treasure.skip_relic",
    };

    /// <summary>Kinds a script may carry but a driver ignores (they describe outcomes, not decisions).</summary>
    public static readonly IReadOnlySet<string> IgnoredKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "act.enter",
        "rest.resolve",
        "reward.resolve",
        "room.enter",
        "room.exit",
        "shop.purchase.resolve",
        "shop.remove_card.resolve",
        "state.observe",
    };

    /// <summary>Kinds that carry no drivable semantic identity and are rejected in scripts.</summary>
    public static readonly IReadOnlySet<string> ForbiddenKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "choice.player",
        "choice.unknown",
        "combat.undo_end_turn",
        "event.crystal_sphere.click",
        "event.crystal_sphere.proceed",
        "event.crystal_sphere.set_tool",
        "event.crystal_sphere.start",
        "game.unknown_action",
        "run.finish",
        "run.resume",
    };

    public static bool IsScript(RunMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.GetValue(ProducerTag) is not null;
    }

    /// <summary>
    /// Parses a producer tag of the form <c>name/version</c>. The name is lowercase
    /// <c>[a-z0-9_-]</c>; the version is any non-empty text without whitespace.
    /// </summary>
    public static bool TryParseProducer(string? value, out string name, out string version)
    {
        name = string.Empty;
        version = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> candidateName = value.AsSpan(0, slash);
        ReadOnlySpan<char> candidateVersion = value.AsSpan(slash + 1);
        if (candidateName.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_-") >= 0
            || candidateVersion.IndexOfAny(" \t/") >= 0)
        {
            return false;
        }

        name = candidateName.ToString();
        version = candidateVersion.ToString();
        return true;
    }
}
