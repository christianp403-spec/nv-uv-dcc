using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NvUv.Dcc.Core;

namespace NvUv.Dcc.Helper;

internal sealed class NvApiFrameLimiterClient : IDisposable
{
    private const uint FrameRateLimiterSettingId = 0x10835002;
    // NvApiDriverSettings.h: VSYNCMODE_ID / VSYNCMODE_FORCEON.
    private const uint VsyncModeSettingId = 0x00A879CF;
    private const uint VsyncForceOn = 0x47814940;
    private const int NvApiOk = 0;
    private const int NvApiEndEnumeration = -7;
    private const int NvApiSettingNotFound = -160;
    private const int NvApiProfileNotFound = -163;
    private const int NvApiExecutableNotFound = -166;
    private const int NvApiExecutableAlreadyInUse = -167;
    private const int NvApiExecutablePathIsAmbiguous = -182;

    private readonly Mutex _drsMutex = new(false, "Local\\NV-UV-DCC-NvApi-DRS");
    private bool _initialized;
    private bool _disposed;

    private InitializeDelegate? _initialize;
    private UnloadDelegate? _unload;
    private DrsCreateSessionDelegate? _createSession;
    private DrsDestroySessionDelegate? _destroySession;
    private DrsLoadSettingsDelegate? _loadSettings;
    private DrsSaveSettingsDelegate? _saveSettings;
    private DrsGetBaseProfileDelegate? _getBaseProfile;
    private DrsFindApplicationDelegate? _findApplication;
    private DrsFindProfileDelegate? _findProfile;
    private DrsEnumProfilesDelegate? _enumProfiles;
    private DrsEnumApplicationsDelegate? _enumApplications;
    private DrsCreateProfileDelegate? _createProfile;
    private DrsDeleteProfileDelegate? _deleteProfile;
    private DrsCreateApplicationDelegate? _createApplication;
    private DrsGetProfileInfoDelegate? _getProfileInfo;
    private DrsGetSettingDelegate? _getSetting;
    private DrsSetSettingDelegate? _setSetting;
    private DrsDeleteProfileSettingDelegate? _deleteProfileSetting;

    public IReadOnlyList<DccDriverApplication> EnumerateApplications()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                var applications = new Dictionary<string, DccDriverApplication>(StringComparer.OrdinalIgnoreCase);
                for (uint profileIndex = 0; ; profileIndex++)
                {
                    int profileStatus = _enumProfiles!(session, profileIndex, out nint profile);
                    if (profileStatus == NvApiEndEnumeration)
                        break;
                    EnsureSuccess("NvAPI_DRS_EnumProfiles", profileStatus);

                    var profileInfo = new NvDrsProfileV1 { Version = MakeVersion<NvDrsProfileV1>(1) };
                    EnsureSuccess(
                        "NvAPI_DRS_GetProfileInfo",
                        _getProfileInfo!(session, profile, ref profileInfo));
                    string profileName = profileInfo.ProfileName.GetValue();

                    for (uint applicationIndex = 0; applicationIndex < profileInfo.NumberOfApplications;)
                    {
                        uint count = 1;
                        var application = new NvDrsApplicationV1
                        {
                            Version = MakeVersion<NvDrsApplicationV1>(1)
                        };
                        int applicationStatus = _enumApplications!(
                            session,
                            profile,
                            applicationIndex,
                            ref count,
                            ref application);
                        if (applicationStatus == NvApiEndEnumeration)
                            break;
                        EnsureSuccess("NvAPI_DRS_EnumApplications", applicationStatus);
                        if (count == 0)
                            break;
                        applicationIndex += count;

                        string storedPath = application.AppName.GetValue();
                        if (string.IsNullOrWhiteSpace(storedPath))
                            continue;
                        string executable = DccGameCatalog.NormalizeExecutable(storedPath);
                        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            DccGameCatalog.IsUnsupportedExecutable(executable))
                        {
                            continue;
                        }

                        string friendlyName = application.UserFriendlyName.GetValue();
                        applications.TryAdd(
                            executable,
                            new DccDriverApplication(
                                profileName,
                                executable,
                                string.IsNullOrWhiteSpace(friendlyName) ? profileName : friendlyName,
                                application.IsPredefined != 0 || profileInfo.IsPredefined != 0));
                    }
                }

                return applications.Values
                    .OrderBy(application => application.FriendlyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(application => application.Executable, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    public DccFrameLimiterRecoveryState Apply(
        string executablePath,
        string executableName,
        string friendlyName,
        int targetFps,
        Action<DccFrameLimiterRecoveryState> persistBeforeMutation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (targetFps is < DccFrameLimitCommand.MinimumTargetFps or > DccFrameLimitCommand.MaximumTargetFps)
            throw new ArgumentOutOfRangeException(nameof(targetFps));

        executableName = DccGameCatalog.NormalizeExecutable(executableName);
        executablePath = NormalizePath(executablePath, executableName);
        friendlyName = string.IsNullOrWhiteSpace(friendlyName)
            ? Path.GetFileNameWithoutExtension(executableName)
            : friendlyName.Trim();

        return ApplyDwordSetting(
            executablePath,
            executableName,
            friendlyName,
            FrameRateLimiterSettingId,
            (uint)targetFps,
            (path, name, profileName, profileCreated, previousOverridePresent, previousValue) =>
                new DccFrameLimiterRecoveryState(
                    DccFrameLimiterRecoveryState.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    path,
                    name,
                    profileName,
                    profileCreated,
                    previousOverridePresent,
                    previousValue,
                    targetFps),
            persistBeforeMutation);
    }

    public DccFrameLimiterRecoveryState ApplyGlobalFrameLimit(
        int targetFps,
        Action<DccFrameLimiterRecoveryState> persistBeforeMutation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (targetFps is < DccFrameLimitCommand.MinimumTargetFps or > DccFrameLimitCommand.MaximumTargetFps)
            throw new ArgumentOutOfRangeException(nameof(targetFps));

        return WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                EnsureSuccess("NvAPI_DRS_GetBaseProfile", _getBaseProfile!(session, out nint profile));
                string profileName = GetProfileName(session, profile);
                NvDrsSetting previous = CreateDwordSetting(FrameRateLimiterSettingId, 0);
                int getStatus = _getSetting!(session, profile, FrameRateLimiterSettingId, ref previous);
                bool previousOverridePresent;
                uint previousValue;
                if (getStatus == NvApiOk)
                {
                    previousOverridePresent = previous.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                              previous.IsCurrentPredefined == 0;
                    previousValue = previous.CurrentValue;
                }
                else if (getStatus == NvApiSettingNotFound)
                {
                    previousOverridePresent = false;
                    previousValue = 0;
                }
                else
                {
                    ThrowStatus("NvAPI_DRS_GetSetting", getStatus);
                    throw new UnreachableException();
                }

                var recovery = new DccFrameLimiterRecoveryState(
                    DccFrameLimiterRecoveryState.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    string.Empty,
                    profileName,
                    false,
                    previousOverridePresent,
                    previousValue,
                    targetFps);

                // Persist before SaveSettings can expose the global mutation.
                persistBeforeMutation(recovery);
                NvDrsSetting target = CreateDwordSetting(FrameRateLimiterSettingId, (uint)targetFps);
                EnsureSuccess("NvAPI_DRS_SetSetting", _setSetting!(session, profile, ref target));
                EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));

                NvDrsSetting confirmed = CreateDwordSetting(FrameRateLimiterSettingId, 0);
                EnsureSuccess(
                    "NvAPI_DRS_GetSetting(global frame-limit verification)",
                    _getSetting!(session, profile, FrameRateLimiterSettingId, ref confirmed));
                if (confirmed.SettingLocation != NvDrsSettingLocation.CurrentProfile ||
                    confirmed.IsCurrentPredefined != 0 ||
                    confirmed.CurrentValue != (uint)targetFps)
                {
                    throw new InvalidOperationException(
                        $"NVIDIA global frame-limit readback did not match the requested value " +
                        $"(requested={targetFps}, actual={confirmed.CurrentValue}, " +
                        $"location={confirmed.SettingLocation}, predefined={confirmed.IsCurrentPredefined}).");
                }
                return recovery;
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    public void Restore(DccFrameLimiterRecoveryState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsGlobal)
        {
            RestoreGlobalFrameLimit(state);
            return;
        }
        RestoreDwordSetting(
            state.ExecutablePath,
            state.ExecutableName,
            state.ProfileName,
            state.ProfileCreatedByDcc,
            state.PreviousOverridePresent,
            state.PreviousValue,
            (uint)state.TargetFps,
            FrameRateLimiterSettingId);
    }

    private void RestoreGlobalFrameLimit(DccFrameLimiterRecoveryState state)
    {
        WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                EnsureSuccess("NvAPI_DRS_GetBaseProfile", _getBaseProfile!(session, out nint profile));
                NvDrsSetting current = CreateDwordSetting(FrameRateLimiterSettingId, 0);
                int currentStatus = _getSetting!(session, profile, FrameRateLimiterSettingId, ref current);
                bool dccOverrideStillPresent = currentStatus == NvApiOk &&
                                               current.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                               current.IsCurrentPredefined == 0 &&
                                               current.CurrentValue == (uint)state.TargetFps;
                if (!dccOverrideStillPresent)
                {
                    if (currentStatus != NvApiOk && currentStatus != NvApiSettingNotFound)
                        ThrowStatus("NvAPI_DRS_GetSetting", currentStatus);
                    return;
                }

                if (state.PreviousOverridePresent)
                {
                    NvDrsSetting previous = CreateDwordSetting(
                        FrameRateLimiterSettingId,
                        state.PreviousValue);
                    EnsureSuccess("NvAPI_DRS_SetSetting", _setSetting!(session, profile, ref previous));
                }
                else
                {
                    int deleteStatus = _deleteProfileSetting!(session, profile, FrameRateLimiterSettingId);
                    if (deleteStatus != NvApiOk && deleteStatus != NvApiSettingNotFound)
                        ThrowStatus("NvAPI_DRS_DeleteProfileSetting", deleteStatus);
                }
                EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    public DccVsyncRecoveryState ApplyVsync(
        Action<DccVsyncRecoveryState> persistBeforeMutation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                EnsureSuccess("NvAPI_DRS_GetBaseProfile", _getBaseProfile!(session, out nint profile));
                RepairLegacyGlobalVsyncMiswrite(session, profile);
                string profileName = GetProfileName(session, profile);
                NvDrsSetting previous = CreateDwordSetting(VsyncModeSettingId, 0);
                int getStatus = _getSetting!(session, profile, VsyncModeSettingId, ref previous);
                bool previousOverridePresent;
                uint previousValue;
                if (getStatus == NvApiOk)
                {
                    previousOverridePresent = previous.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                              previous.IsCurrentPredefined == 0;
                    previousValue = previous.CurrentValue;
                }
                else if (getStatus == NvApiSettingNotFound)
                {
                    previousOverridePresent = false;
                    previousValue = 0;
                }
                else
                {
                    ThrowStatus("NvAPI_DRS_GetSetting", getStatus);
                    throw new UnreachableException();
                }

                var recovery = new DccVsyncRecoveryState(
                    DccVsyncRecoveryState.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    profileName,
                    previousOverridePresent,
                    previousValue,
                    VsyncForceOn);

                // Persist before SaveSettings can expose the global driver mutation.
                persistBeforeMutation(recovery);
                NvDrsSetting target = CreateDwordSetting(VsyncModeSettingId, VsyncForceOn);
                EnsureSuccess("NvAPI_DRS_SetSetting", _setSetting!(session, profile, ref target));
                EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));
                NvDrsSetting confirmed = CreateDwordSetting(VsyncModeSettingId, 0);
                EnsureSuccess(
                    "NvAPI_DRS_GetSetting(VSync verification)",
                    _getSetting!(session, profile, VsyncModeSettingId, ref confirmed));
                if (confirmed.SettingLocation != NvDrsSettingLocation.CurrentProfile ||
                    confirmed.IsCurrentPredefined != 0 ||
                    confirmed.CurrentValue != VsyncForceOn)
                {
                    throw new InvalidOperationException(
                        $"NVIDIA VSync profile readback did not match the requested value " +
                        $"(requested=0x{VsyncForceOn:X8}, actual=0x{confirmed.CurrentValue:X8}, " +
                        $"location={confirmed.SettingLocation}, predefined={confirmed.IsCurrentPredefined}).");
                }
                return recovery;
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    public void RestoreVsync(DccVsyncRecoveryState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                EnsureSuccess("NvAPI_DRS_GetBaseProfile", _getBaseProfile!(session, out nint profile));
                NvDrsSetting current = CreateDwordSetting(VsyncModeSettingId, 0);
                int currentStatus = _getSetting!(session, profile, VsyncModeSettingId, ref current);
                bool dccOverrideStillPresent = currentStatus == NvApiOk &&
                                               current.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                               current.IsCurrentPredefined == 0 &&
                                               current.CurrentValue == state.ForcedValue;
                if (!dccOverrideStillPresent)
                {
                    if (currentStatus != NvApiOk && currentStatus != NvApiSettingNotFound)
                        ThrowStatus("NvAPI_DRS_GetSetting", currentStatus);
                    return;
                }

                if (state.PreviousOverridePresent)
                {
                    NvDrsSetting previous = CreateDwordSetting(VsyncModeSettingId, state.PreviousValue);
                    EnsureSuccess("NvAPI_DRS_SetSetting", _setSetting!(session, profile, ref previous));
                }
                else
                {
                    EnsureSuccess(
                        "NvAPI_DRS_DeleteProfileSetting",
                        _deleteProfileSetting!(session, profile, VsyncModeSettingId));
                }
                EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    private TRecovery ApplyDwordSetting<TRecovery>(
        string executablePath,
        string executableName,
        string friendlyName,
        uint settingId,
        uint targetValue,
        Func<string, string, string, bool, bool, uint, TRecovery> createRecovery,
        Action<TRecovery> persistBeforeMutation)
    {
        return WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                bool profileCreated = false;
                if (!TryFindApplication(session, executablePath, executableName, out nint profile))
                {
                    string requestedProfileName = BuildProfileName(executablePath, executableName);
                    (profile, profileCreated) = CreateApplicationProfile(
                        session,
                        requestedProfileName,
                        executablePath,
                        friendlyName);
                }

                string profileName = GetProfileName(session, profile);
                NvDrsSetting previous = CreateDwordSetting(settingId, 0);
                int getStatus = _getSetting!(session, profile, settingId, ref previous);
                bool previousOverridePresent;
                uint previousValue;
                if (getStatus == NvApiOk)
                {
                    previousOverridePresent = previous.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                              previous.IsCurrentPredefined == 0;
                    previousValue = previous.CurrentValue;
                }
                else if (getStatus == NvApiSettingNotFound)
                {
                    previousOverridePresent = false;
                    previousValue = 0;
                }
                else
                {
                    ThrowStatus("NvAPI_DRS_GetSetting", getStatus);
                    throw new UnreachableException();
                }

                TRecovery recovery = createRecovery(
                    executablePath,
                    executableName,
                    profileName,
                    profileCreated,
                    previousOverridePresent,
                    previousValue);

                // Persist before SaveSettings can make the driver-profile mutation visible.
                persistBeforeMutation(recovery);
                NvDrsSetting target = CreateDwordSetting(settingId, targetValue);
                EnsureSuccess("NvAPI_DRS_SetSetting", _setSetting!(session, profile, ref target));
                EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));

                NvDrsSetting confirmed = CreateDwordSetting(settingId, 0);
                EnsureSuccess(
                    "NvAPI_DRS_GetSetting(frame-limit verification)",
                    _getSetting!(session, profile, settingId, ref confirmed));
                if (confirmed.SettingLocation != NvDrsSettingLocation.CurrentProfile ||
                    confirmed.IsCurrentPredefined != 0 ||
                    confirmed.CurrentValue != targetValue)
                {
                    throw new InvalidOperationException(
                        $"NVIDIA frame-limit profile readback did not match the requested value " +
                        $"(requested={targetValue}, actual={confirmed.CurrentValue}, " +
                        $"location={confirmed.SettingLocation}, predefined={confirmed.IsCurrentPredefined}).");
                }
                return recovery;
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    private void RestoreDwordSetting(
        string executablePath,
        string executableName,
        string profileName,
        bool profileCreatedByDcc,
        bool previousOverridePresent,
        uint previousValue,
        uint appliedValue,
        uint settingId)
    {
        WithDrsLock(() =>
        {
            EnsureInitialized();
            nint session = CreateLoadedSession();
            try
            {
                if (profileCreatedByDcc)
                {
                    if (TryFindProfile(session, profileName, out nint createdProfile))
                    {
                        NvDrsSetting createdCurrent = CreateDwordSetting(settingId, 0);
                        int createdCurrentStatus = _getSetting!(session, createdProfile, settingId, ref createdCurrent);
                        bool createdDccOverrideStillPresent = createdCurrentStatus == NvApiOk &&
                                                              createdCurrent.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                                              createdCurrent.IsCurrentPredefined == 0 &&
                                                              createdCurrent.CurrentValue == appliedValue;
                        if (!createdDccOverrideStillPresent)
                        {
                            if (createdCurrentStatus != NvApiOk && createdCurrentStatus != NvApiSettingNotFound)
                                ThrowStatus("NvAPI_DRS_GetSetting", createdCurrentStatus);
                            return;
                        }

                        var profileInfo = new NvDrsProfileV1 { Version = MakeVersion<NvDrsProfileV1>(1) };
                        EnsureSuccess(
                            "NvAPI_DRS_GetProfileInfo",
                            _getProfileInfo!(session, createdProfile, ref profileInfo));
                        if (profileInfo.NumberOfApplications == 1 && profileInfo.NumberOfSettings == 1)
                        {
                            EnsureSuccess("NvAPI_DRS_DeleteProfile", _deleteProfile!(session, createdProfile));
                        }
                        else
                        {
                            EnsureSuccess(
                                "NvAPI_DRS_DeleteProfileSetting",
                                _deleteProfileSetting!(session, createdProfile, settingId));
                        }
                        EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));
                    }
                    return;
                }

                nint profile;
                if (!TryFindApplication(session, executablePath, executableName, out profile) &&
                    !TryFindProfile(session, profileName, out profile))
                {
                    return;
                }

                NvDrsSetting current = CreateDwordSetting(settingId, 0);
                int currentStatus = _getSetting!(session, profile, settingId, ref current);
                bool dccOverrideStillPresent = currentStatus == NvApiOk &&
                                               current.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                               current.IsCurrentPredefined == 0 &&
                                               current.CurrentValue == appliedValue;
                if (!dccOverrideStillPresent)
                {
                    if (currentStatus != NvApiOk && currentStatus != NvApiSettingNotFound)
                        ThrowStatus("NvAPI_DRS_GetSetting", currentStatus);
                    // An external profile change wins; never overwrite it during cleanup.
                    return;
                }

                if (previousOverridePresent)
                {
                    NvDrsSetting previous = CreateDwordSetting(settingId, previousValue);
                    EnsureSuccess("NvAPI_DRS_SetSetting", _setSetting!(session, profile, ref previous));
                }
                else
                {
                    int deleteStatus = _deleteProfileSetting!(session, profile, settingId);
                    if (deleteStatus != NvApiOk && deleteStatus != NvApiSettingNotFound)
                        ThrowStatus("NvAPI_DRS_DeleteProfileSetting", deleteStatus);
                }

                EnsureSuccess("NvAPI_DRS_SaveSettings", _saveSettings!(session));
            }
            finally
            {
                _destroySession!(session);
            }
        });
    }

    private nint CreateLoadedSession()
    {
        EnsureSuccess("NvAPI_DRS_CreateSession", _createSession!(out nint session));
        try
        {
            EnsureSuccess("NvAPI_DRS_LoadSettings", _loadSettings!(session));
            return session;
        }
        catch
        {
            _destroySession!(session);
            throw;
        }
    }

    private bool TryFindApplication(
        nint session,
        string executablePath,
        string executableName,
        out nint profile)
    {
        foreach (string candidate in new[] { executablePath, executableName }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            NvApiUnicodeString name = NvApiUnicodeString.From(candidate);
            var application = new NvDrsApplicationV1
            {
                Version = MakeVersion<NvDrsApplicationV1>(1)
            };
            int status = _findApplication!(session, ref name, out profile, ref application);
            if (status == NvApiOk)
                return true;
            if (status is NvApiExecutableNotFound or NvApiProfileNotFound or NvApiExecutablePathIsAmbiguous)
                continue;
            ThrowStatus("NvAPI_DRS_FindApplicationByName", status);
        }

        profile = nint.Zero;
        return false;
    }

    private bool TryFindProfile(nint session, string profileName, out nint profile)
    {
        NvApiUnicodeString name = NvApiUnicodeString.From(profileName);
        int status = _findProfile!(session, ref name, out profile);
        if (status == NvApiOk)
            return true;
        if (status == NvApiProfileNotFound)
        {
            profile = nint.Zero;
            return false;
        }
        ThrowStatus("NvAPI_DRS_FindProfileByName", status);
        throw new UnreachableException();
    }

    private (nint Profile, bool CreatedByDcc) CreateApplicationProfile(
        nint session,
        string profileName,
        string executablePath,
        string friendlyName)
    {
        var profileInfo = new NvDrsProfileV1
        {
            Version = MakeVersion<NvDrsProfileV1>(1),
            ProfileName = NvApiUnicodeString.From(profileName),
            GpuSupport = uint.MaxValue
        };
        EnsureSuccess("NvAPI_DRS_CreateProfile", _createProfile!(session, ref profileInfo, out nint profile));

        var application = new NvDrsApplicationV1
        {
            Version = MakeVersion<NvDrsApplicationV1>(1),
            AppName = NvApiUnicodeString.From(executablePath),
            UserFriendlyName = NvApiUnicodeString.From(friendlyName),
            Launcher = NvApiUnicodeString.From(string.Empty)
        };
        int status = _createApplication!(session, profile, ref application);
        if (status == NvApiExecutableAlreadyInUse)
        {
            // A concurrent profile writer won the race. Discard the uncommitted
            // DCC profile and use the newly discoverable application profile.
            _deleteProfile!(session, profile);
            if (TryFindApplication(session, executablePath, Path.GetFileName(executablePath), out nint existing))
                return (existing, false);
        }
        EnsureSuccess("NvAPI_DRS_CreateApplication", status);
        return (profile, true);
    }

    private string GetProfileName(nint session, nint profile)
    {
        var info = new NvDrsProfileV1 { Version = MakeVersion<NvDrsProfileV1>(1) };
        EnsureSuccess("NvAPI_DRS_GetProfileInfo", _getProfileInfo!(session, profile, ref info));
        return info.ProfileName.GetValue();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _initialize = GetDelegate<InitializeDelegate>(0x0150e828);
        _unload = GetDelegate<UnloadDelegate>(0xd22bdd7e);
        _createSession = GetDelegate<DrsCreateSessionDelegate>(0x0694d52e);
        _destroySession = GetDelegate<DrsDestroySessionDelegate>(0xdad9cff8);
        _loadSettings = GetDelegate<DrsLoadSettingsDelegate>(0x375dbd6b);
        _saveSettings = GetDelegate<DrsSaveSettingsDelegate>(0xfcbc7e14);
        _getBaseProfile = GetDelegate<DrsGetBaseProfileDelegate>(0xda8466a0);
        _findApplication = GetDelegate<DrsFindApplicationDelegate>(0xeee566b2);
        _findProfile = GetDelegate<DrsFindProfileDelegate>(0x7e4a9a0b);
        _enumProfiles = GetDelegate<DrsEnumProfilesDelegate>(0xbc371ee0);
        _enumApplications = GetDelegate<DrsEnumApplicationsDelegate>(0x7fa2173a);
        _createProfile = GetDelegate<DrsCreateProfileDelegate>(0xcc176068);
        _deleteProfile = GetDelegate<DrsDeleteProfileDelegate>(0x17093206);
        _createApplication = GetDelegate<DrsCreateApplicationDelegate>(0x4347a9de);
        _getProfileInfo = GetDelegate<DrsGetProfileInfoDelegate>(0x61cd6fd6);
        _getSetting = GetDelegate<DrsGetSettingDelegate>(0x73bf8338);
        _setSetting = GetDelegate<DrsSetSettingDelegate>(0x577dd202);
        _deleteProfileSetting = GetDelegate<DrsDeleteProfileSettingDelegate>(0xe4a26362);

        EnsureSuccess("NvAPI_Initialize", _initialize());
        _initialized = true;
    }

    private T GetDelegate<T>(uint interfaceId) where T : Delegate
    {
        nint pointer = NvApiQueryInterface(interfaceId);
        if (pointer == nint.Zero)
            throw new InvalidOperationException($"The NVIDIA driver does not expose NVAPI interface 0x{interfaceId:X8}.");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private T WithDrsLock<T>(Func<T> action)
    {
        bool lockTaken = false;
        try
        {
            try
            {
                lockTaken = _drsMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }
            if (!lockTaken)
                throw new TimeoutException("The NVIDIA profile database is busy.");
            return action();
        }
        finally
        {
            if (lockTaken)
                _drsMutex.ReleaseMutex();
        }
    }

    private void WithDrsLock(Action action) => WithDrsLock(() =>
    {
        action();
        return true;
    });

    private void RepairLegacyGlobalVsyncMiswrite(nint session, nint baseProfile)
    {
        NvDrsSetting current = CreateDwordSetting(FrameRateLimiterSettingId, 0);
        int status = _getSetting!(session, baseProfile, FrameRateLimiterSettingId, ref current);
        if (status == NvApiSettingNotFound)
            return;
        EnsureSuccess("NvAPI_DRS_GetSetting(legacy VSync repair)", status);

        // local.14 accidentally used the frame-limiter ID while writing the
        // VSync FORCEON token. The token is not a valid FPS value, so this
        // exact global override can only be the DCC test-build miswrite.
        bool isLegacyMiswrite = current.SettingLocation == NvDrsSettingLocation.CurrentProfile &&
                                current.IsCurrentPredefined == 0 &&
                                current.CurrentValue == VsyncForceOn;
        if (!isLegacyMiswrite)
            return;

        EnsureSuccess(
            "NvAPI_DRS_DeleteProfileSetting(legacy VSync repair)",
            _deleteProfileSetting!(session, baseProfile, FrameRateLimiterSettingId));
        EnsureSuccess("NvAPI_DRS_SaveSettings(legacy VSync repair)", _saveSettings!(session));
        DccLog.Helper(
            "vsync.legacy_miswrite.repaired",
            $"setting_id=0x{FrameRateLimiterSettingId:X8}; invalid_value=0x{VsyncForceOn:X8}");
    }

    private static NvDrsSetting CreateDwordSetting(uint settingId, uint value) => new()
    {
        Version = MakeVersion<NvDrsSetting>(1),
        SettingId = settingId,
        SettingType = NvDrsSettingType.Dword,
        SettingLocation = NvDrsSettingLocation.CurrentProfile,
        CurrentValue = value,
        PredefinedValue = value
    };

    internal static (uint SettingId, uint CurrentValue) CreateDwordSettingForSelfTest(
        uint settingId,
        uint value)
    {
        NvDrsSetting setting = CreateDwordSetting(settingId, value);
        return (setting.SettingId, setting.CurrentValue);
    }

    private static string NormalizePath(string executablePath, string executableName)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                string fullPath = Path.GetFullPath(executablePath.Trim());
                if (string.Equals(Path.GetFileName(fullPath), executableName, StringComparison.OrdinalIgnoreCase))
                    return fullPath;
            }
            catch
            {
                // Fall back to the unambiguous executable name when no path is readable.
            }
        }
        return executableName;
    }

    private static string BuildProfileName(string executablePath, string executableName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(executablePath.ToUpperInvariant()));
        return $"NV-UV DCC - {Path.GetFileNameWithoutExtension(executableName)} - {Convert.ToHexString(hash)[..10]}";
    }

    private static uint MakeVersion<T>(uint version) where T : unmanaged =>
        checked((uint)Unsafe.SizeOf<T>()) | (version << 16);

    private static void EnsureSuccess(string operation, int status)
    {
        if (status != NvApiOk)
            ThrowStatus(operation, status);
    }

    private static void ThrowStatus(string operation, int status) =>
        throw new InvalidOperationException($"{operation} failed with NVAPI status {status}.");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_initialized)
        {
            try
            {
                _unload?.Invoke();
            }
            catch
            {
                // Process shutdown releases NVAPI resources as a final fallback.
            }
        }
        _drsMutex.Dispose();
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint NvApiQueryInterface(uint interfaceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnloadDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateSessionDelegate(out nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDestroySessionDelegate(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsLoadSettingsDelegate(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSaveSettingsDelegate(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsGetBaseProfileDelegate(nint session, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsFindApplicationDelegate(
        nint session,
        ref NvApiUnicodeString appName,
        out nint profile,
        ref NvDrsApplicationV1 application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsFindProfileDelegate(nint session, ref NvApiUnicodeString profileName, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsEnumProfilesDelegate(nint session, uint index, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsEnumApplicationsDelegate(
        nint session,
        nint profile,
        uint startIndex,
        ref uint applicationCount,
        ref NvDrsApplicationV1 application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateProfileDelegate(nint session, ref NvDrsProfileV1 profileInfo, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDeleteProfileDelegate(nint session, nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsCreateApplicationDelegate(nint session, nint profile, ref NvDrsApplicationV1 application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsGetProfileInfoDelegate(nint session, nint profile, ref NvDrsProfileV1 profileInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsGetSettingDelegate(nint session, nint profile, uint settingId, ref NvDrsSetting setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsSetSettingDelegate(nint session, nint profile, ref NvDrsSetting setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrsDeleteProfileSettingDelegate(nint session, nint profile, uint settingId);

    private enum NvDrsSettingType : uint
    {
        Dword = 0
    }

    private enum NvDrsSettingLocation : uint
    {
        CurrentProfile = 0,
        GlobalProfile = 1,
        BaseProfile = 2,
        DefaultProfile = 3
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct NvApiUnicodeString
    {
        private fixed ushort _data[2048];

        public static NvApiUnicodeString From(string? value)
        {
            var result = new NvApiUnicodeString();
            string text = value ?? string.Empty;
            int length = Math.Min(text.Length, 2047);
            ushort* target = result._data;
            for (int index = 0; index < length; index++)
                target[index] = text[index];
            target[length] = 0;
            return result;
        }

        public string GetValue()
        {
            fixed (ushort* source = _data)
            {
                int length = 0;
                while (length < 2048 && source[length] != 0)
                    length++;
                return new string((char*)source, 0, length);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NvDrsApplicationV1
    {
        public uint Version;
        public uint IsPredefined;
        public NvApiUnicodeString AppName;
        public NvApiUnicodeString UserFriendlyName;
        public NvApiUnicodeString Launcher;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NvDrsProfileV1
    {
        public uint Version;
        public NvApiUnicodeString ProfileName;
        public uint GpuSupport;
        public uint IsPredefined;
        public uint NumberOfApplications;
        public uint NumberOfSettings;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x3020, Pack = 4)]
    private struct NvDrsSetting
    {
        [FieldOffset(0x0000)] public uint Version;
        [FieldOffset(0x0004)] public NvApiUnicodeString SettingName;
        [FieldOffset(0x1004)] public uint SettingId;
        [FieldOffset(0x1008)] public NvDrsSettingType SettingType;
        [FieldOffset(0x100C)] public NvDrsSettingLocation SettingLocation;
        [FieldOffset(0x1010)] public uint IsCurrentPredefined;
        [FieldOffset(0x1014)] public uint IsPredefinedValid;
        [FieldOffset(0x1018)] public uint PredefinedValue;
        [FieldOffset(0x201C)] public uint CurrentValue;
    }
}
