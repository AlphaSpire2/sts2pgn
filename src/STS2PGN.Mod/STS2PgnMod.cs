using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using STS2PGN.Mod.Playback;
using STS2PGN.Mod.Recording;

namespace STS2PGN.Mod;

/// <summary>
/// STS2 mod entry point.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class STS2PgnMod
{
    public const string Version = "0.17.1";

    public static void Initialize()
    {
        new Harmony("com.sts2pgn.recorder").PatchAll();
        RecorderService.Initialize();
        PlaybackService.StartFromCommandLine();
        GD.Print($"[STS2PGN] v{Version} initialized");
    }
}
