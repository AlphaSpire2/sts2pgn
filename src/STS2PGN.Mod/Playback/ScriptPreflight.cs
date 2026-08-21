using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using STS2PGN.Mod.Recording;
using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Validation;

namespace STS2PGN.Mod.Playback;

/// <summary>Everything pre-flight established; <see cref="Failures"/> is empty when the script may start.</summary>
internal sealed class PreflightResult
{
    public DecisionScript? Script { get; init; }

    /// <summary>True when the input was an authoritative recording converted to a script at load time.</summary>
    public bool ConvertedFromRecording { get; init; }
    public CharacterModel? Character { get; init; }
    public IReadOnlyList<ActModel>? Acts { get; init; }
    public List<string> Failures { get; } = [];
    public bool Passed => Failures.Count == 0 && Script is not null && Character is not null && Acts is not null;
}

/// <summary>
/// Verifies a script against the live build and profile before anything is started. Every
/// mismatch is collected rather than stopping at the first, so one report explains the whole refusal.
/// </summary>
internal static class ScriptPreflight
{
    public static PreflightResult Run(string scriptPath)
    {
        DecisionScript script;
        bool converted = false;
        try
        {
            RunRecord record;
            using (StreamReader reader = new(scriptPath))
            {
                record = Sts2PgnParser.Parse(reader, new Sts2PgnParseOptions { RecoverTruncatedFinalLine = false });
            }

            if (!ScriptProfile.IsScript(record.Metadata))
            {
                // A recording was given: replay its decisions. The conversion is the same one
                // `sts2pgn to-script` performs, so the same recording yields the same script.
                record = ScriptConverter.FromRecording(record, $"sts2pgn-playback/{STS2PgnMod.Version}");
                converted = true;
            }

            script = DecisionScript.FromRecord(record);
        }
        catch (Exception exception) when (exception is IOException or Sts2PgnParseException or Sts2PgnValidationException or ArgumentException)
        {
            PreflightResult failed = new();
            failed.Failures.Add($"{PlaybackReasons.Validation}: {exception.Message}");
            return failed;
        }

        List<string> failures = [];
        ScriptIdentity identity = script.Identity;

        string liveVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "UNKNOWN";
        string liveCommit = ReleaseInfoManager.Instance.ReleaseInfo?.Commit ?? "UNKNOWN";
        string liveModelHash = ModelIdSerializationCache.Hash.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Compare(failures, "GameVersion", identity.GameVersion, liveVersion);
        Compare(failures, "GameCommit", identity.GameCommit, liveCommit);
        Compare(failures, "ModelIdHash", identity.ModelIdHash, liveModelHash);
        Compare(failures, "Mode", identity.Mode, ScriptProfile.RequiredMode);

        CharacterModel? character = ModelDb.AllCharacters.FirstOrDefault(candidate =>
            string.Equals(candidate.Id.Entry, identity.CharacterModel, StringComparison.Ordinal));
        if (character is null)
        {
            failures.Add($"character: '{identity.CharacterModel}' is not a known character");
        }

        if (RunManager.Instance.IsInProgress)
        {
            failures.Add("run: a run is already in progress");
        }

        if (SaveManager.Instance.HasRunSave)
        {
            failures.Add("save: a single-player run save exists; playback would overwrite it (abandon or finish it first)");
        }

        UnlockState liveUnlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        ScriptUnlocks liveUnlocks = ToScriptUnlocks(liveUnlockState);
        if (!script.Unlocks.Matches(liveUnlocks, out string unlockDifference))
        {
            failures.Add($"unlocks: {unlockDifference}");
        }

        if (character is not null)
        {
            int maxAscension = SaveManager.Instance.Progress.GetOrCreateCharacterStats(character.Id).MaxAscension;
            if (identity.Ascension > maxAscension)
            {
                failures.Add($"ascension: script asks for {identity.Ascension}, profile allows {maxAscension} for {character.Id.Entry}");
            }
        }

        IReadOnlyList<ActModel>? acts = ResolveActs(script, identity.Seed, liveUnlockState, failures);

        PreflightResult result = new() { Script = script, Character = character, Acts = acts, ConvertedFromRecording = converted };
        result.Failures.AddRange(failures);
        return result;
    }

    /// <summary>The live profile's unlock state in the script's shape (sorted sets, exact run count).</summary>
    public static ScriptUnlocks CurrentLiveUnlocks() => ToScriptUnlocks(SaveManager.Instance.GenerateUnlockStateFromProgress());

    /// <summary>
    /// The acts the run starts with. The seed generates the list (<c>StartRunLobby.BeginRunLocally</c>),
    /// and the custom-run lobby may override act 1 (<c>list[0] = GetAct(Act1) ?? list[0]</c>), so the
    /// script's first act is honored when it resolves to a known act; every later act must match the seed.
    /// </summary>
    private static IReadOnlyList<ActModel>? ResolveActs(DecisionScript script, string seed, UnlockState unlockState, List<string> failures)
    {
        List<ActModel> seeded = GenerateActs(seed, unlockState).ToList();
        string[] seededIds = seeded.Select(act => PortablePayloads.ModelId(act.Id) ?? act.Id.ToString()).ToArray();
        IReadOnlyList<string> expected = script.ExpectedActModelIds;
        string describe() => $"script expects [{string.Join(", ", expected)}], seed generates [{string.Join(", ", seededIds)}] on this profile";
        if (expected.Count != seeded.Count || !expected.Skip(1).SequenceEqual(seededIds.Skip(1), StringComparer.Ordinal))
        {
            failures.Add($"acts: {describe()}");
            return null;
        }

        if (expected.Count == 0 || string.Equals(expected[0], seededIds[0], StringComparison.Ordinal))
        {
            return seeded;
        }

        ActModel? firstAct;
        try
        {
            firstAct = ModelDb.GetByIdOrNull<ActModel>(ModelId.Deserialize(expected[0]));
        }
        catch (Exception)
        {
            firstAct = null;
        }

        if (firstAct is null)
        {
            failures.Add($"acts: first act '{expected[0]}' is not a known act; {describe()}");
            return null;
        }

        seeded[0] = firstAct;
        return seeded;
    }

    /// <summary>Same act selection as <c>StartRunLobby.BeginRunLocally</c> for a single-player custom run.</summary>
    private static IReadOnlyList<ActModel> GenerateActs(string seed, UnlockState unlockState)
    {
        string canonicalSeed = SeedHelper.CanonicalizeSeed(seed);
        Rng rng = new((uint)StringHelper.GetDeterministicHashCode(canonicalSeed), "act_selection");
        return ActModel.GetRandomList(rng, unlockState, isMultiplayer: false).ToList();
    }

    private static ScriptUnlocks ToScriptUnlocks(UnlockState unlockState)
    {
        SerializableUnlockState serializable = unlockState.ToSerializable();
        return new ScriptUnlocks(
            serializable.UnlockedEpochs.Order(StringComparer.Ordinal).ToArray(),
            serializable.EncountersSeen.Select(id => PortablePayloads.ModelId(id)!).Order(StringComparer.Ordinal).ToArray(),
            serializable.NumberOfRuns);
    }

    private static void Compare(List<string> failures, string name, string expected, string live)
    {
        if (!string.Equals(expected, live, StringComparison.Ordinal))
        {
            failures.Add($"{name}: script '{expected}', live '{live}'");
        }
    }
}
