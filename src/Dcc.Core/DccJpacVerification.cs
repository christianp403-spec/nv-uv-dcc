namespace NvUv.Dcc.Core;

public sealed record DccJpacVerification(
    bool RequestedEnabled,
    int RequestedTargetFps,
    bool DriverCommandAccepted,
    bool HelperStateMatches,
    bool HelperTargetMatches,
    bool DriverReadbackAvailable,
    bool? DriverEnabled)
{
    public static DccJpacVerification NotRequested { get; } =
        new(false, 0, false, false, false, false, null);

    public bool CommandPathConfirmed =>
        DriverCommandAccepted && HelperStateMatches && HelperTargetMatches;

    public bool Confirmed =>
        CommandPathConfirmed &&
        DriverReadbackAvailable &&
        DriverEnabled == RequestedEnabled;

    // Hotfix 7 has no authoritative JPAC getter. Its verified contract is the
    // accepted command plus the helper retaining the exact requested state and
    // target. A future contradictory driver readback must still fail closed.
    public bool Hotfix7CommandConfirmed =>
        CommandPathConfirmed &&
        (!DriverReadbackAvailable || DriverEnabled == RequestedEnabled);

    public bool Hotfix7EnableConfirmed =>
        RequestedEnabled && Hotfix7CommandConfirmed;

    public bool Hotfix7RestoreConfirmed =>
        !RequestedEnabled && Hotfix7CommandConfirmed;

    public string LogValue => Confirmed
        ? $"driver-readback-confirmed:{(RequestedEnabled ? "enabled" : "disabled")};jpac_target_fps={RequestedTargetFps}"
        : Hotfix7CommandConfirmed
            ? $"set-accepted-helper-state-matches:{(RequestedEnabled ? "enabled" : "disabled")};jpac_target_fps={RequestedTargetFps};driver-readback-unavailable"
            : DriverCommandAccepted
                ? "set-accepted;state-unconfirmed"
                : "not-requested";
}
