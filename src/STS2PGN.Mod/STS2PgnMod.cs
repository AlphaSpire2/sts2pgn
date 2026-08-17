using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace STS2PGN.Mod;

/// <summary>
/// STS2 mod entry point. Recording and replay services are added in later phases.
/// </summary>
public static class STS2PgnMod
{
    public const string Version = "0.1.0";

    [ModInitializer(nameof(Initialize))]
    public static void Initialize()
    {
        GD.Print($"[STS2PGN] v{Version} initialized");
    }
}
