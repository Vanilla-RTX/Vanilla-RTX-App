using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules.DLSS;

/// <summary>
/// One value a driver setting can be given, as the DLSS manager offers it. <see cref="Writes"/> is
/// every profile setting that value is made of; a null value deletes that setting from the
/// profile, which hands it back to whatever NVIDIA predefined for the game or set globally.
/// </summary>
internal sealed record DriverChoice(string Label, IReadOnlyList<(uint Id, uint? Value)> Writes);

/// <summary>One row of the driver settings card. Its first choice is the one that writes nothing of ours.</summary>
internal sealed record DriverSetting(string Title, string Description, IReadOnlyList<DriverChoice> Choices);

/// <summary>
/// What the driver profile an executable runs under says now. <see cref="ProfileName"/> is null
/// when the driver has no profile for it at all - the first write creates one.
/// <see cref="Current"/> maps each setting to the choice its values match, or to null when they
/// match none of ours: set by the NVIDIA app or Profile Inspector to something this window
/// doesn't offer.
/// </summary>
internal sealed record DriverProfileState(string? ProfileName, bool IsPredefined, IReadOnlyDictionary<DriverSetting, DriverChoice?> Current);

/// <summary>
/// The three NVIDIA driver settings the DLSS manager offers for Minecraft - the DLSS preset
/// override, Low Latency Mode and Smooth Motion - read from and written to the driver's profile
/// database, the one the NVIDIA app and NVIDIA Profile Inspector edit (NVAPI's DRS).
///
/// <para><b>Reads run in-process; writes run in a PowerShell child</b>, which first runs as the
/// user and, if the driver refuses that, once more behind a UAC prompt. Profile writes are
/// documented as needing administrator rights on some drivers and not others, so both cases are
/// real. Keeping one writer for both means the elevated path is the same code that runs on every
/// write rather than a fallback that is only ever exercised when something is already wrong.
/// Whether a write took is decided by reading the profile back, never by an exit code.</para>
///
/// <para><b>No NVAPI header or wrapper library.</b> Functions come from <c>nvapi_QueryInterface</c>
/// by their published IDs, and structs are raw buffers at the offsets of nvapi.h's
/// <c>NVDRS_SETTING_V1</c> (12,320 bytes), <c>NVDRS_APPLICATION_V4</c> (20,492) and
/// <c>NVDRS_PROFILE_V1</c> (4,116). A wrong size is refused with
/// NVAPI_INCOMPATIBLE_STRUCT_VERSION (-9): measured against driver 621.14, the 12,328 a padded
/// union suggests for the setting struct is refused and 12,320 is accepted.</para>
///
/// <para><b>Which profile.</b> The driver matches a game by executable, so an edition resolves by
/// its own <c>Minecraft.Windows.exe</c>, then by the bare file name. Release and Preview both
/// resolve to NVIDIA's predefined "Minecraft" profile (measured: 9 applications, 25 settings), so
/// a change applies to both - the window says so when it sees it. A driver with no profile for the
/// game gets <see cref="OwnProfileName"/>, created on first write with the executable added to it.
/// The writer is handed the profile by name, so it resolves exactly what the read did.</para>
///
/// <para>Nothing takes effect in a running game; the driver reads the profile at launch.</para>
/// </summary>
internal static class NvidiaDriverSettings
{
    public const string ExecutableName = "Minecraft.Windows.exe";
    private const string OwnProfileName = "Minecraft (Vanilla RTX App)";

    // Setting IDs, as NvApiDriverSettings.h and Profile Inspector name them.
    private const uint DlssSrOverride = 0x10E41E01;          // 1 = the driver overrides the game's DLSS-SR model
    private const uint DlssModelPresetProfile = 0x00634291;  // 2 = custom, which is what makes the letter below apply
    private const uint DlssSrPreset = 0x10E41DF3;            // 1-15 = preset A-O
    private const uint DlssSrPresetLatest = 0x00FFFFFF;
    private const uint ModelPresetCustom = 2;
    private const uint LowLatencyCplState = 0x0005F543;      // the control panel's own 0 Off / 1 On / 2 Ultra
    private const uint LowLatencyUltra = 0x10835000;
    private const uint PreRenderLimit = 0x007BA09E;          // 0 = the application decides
    private const uint SmoothMotionEnable = 0xB0D384C0;
    private const uint SmoothMotionApis = 0xB0CC0875;        // bitmask of the APIs it applies to
    private const uint SmoothMotionApiDx12 = 1;

    public static readonly DriverSetting DlssPreset = new(
        "DLSS preset",
        "Which DLSS model the driver has Minecraft use. Latest is the newest the driver has; J and later are transformer models, A to F the older convolutional ones.",
        PresetChoices());

    public static readonly DriverSetting LowLatency = new(
        "Low Latency Mode",
        "How many frames the CPU may queue ahead: On allows one, Ultra submits each just in time. NVIDIA documents it as doing nothing for DirectX 12 games, and ray tracing always runs on DirectX 12.",
        [
            new("Driver default", [(LowLatencyCplState, null), (LowLatencyUltra, null), (PreRenderLimit, null)]),
            new("Off", [(LowLatencyCplState, 0u), (LowLatencyUltra, 0u), (PreRenderLimit, 0u)]),
            new("On", [(LowLatencyCplState, 1u), (LowLatencyUltra, 0u), (PreRenderLimit, 1u)]),
            new("Ultra", [(LowLatencyCplState, 2u), (LowLatencyUltra, 1u), (PreRenderLimit, 1u)]),
        ]);

    public static readonly DriverSetting SmoothMotion = new(
        "Smooth Motion",
        "Frame generation by the driver: a generated frame between each two the game renders. Needs a GeForce RTX 40 or 50 series GPU.",
        [
            new("Driver default", [(SmoothMotionEnable, null), (SmoothMotionApis, null)]),
            new("Off", [(SmoothMotionEnable, 0u), (SmoothMotionApis, null)]),
            new("On", [(SmoothMotionEnable, 1u), (SmoothMotionApis, SmoothMotionApiDx12)]),
        ]);

    public static readonly IReadOnlyList<DriverSetting> All = [DlssPreset, LowLatency, SmoothMotion];

    private static IReadOnlyList<DriverChoice> PresetChoices()
    {
        var choices = new List<DriverChoice>
        {
            new("Game decides", [(DlssSrOverride, null), (DlssModelPresetProfile, null), (DlssSrPreset, null)]),
            new("Latest", Preset(DlssSrPresetLatest)),
        };

        // G, H and I are reserved and have never shipped.
        foreach (var letter in "ABCDEFJKLM")
            choices.Add(new($"Preset {letter}", Preset((uint)(letter - 'A' + 1))));
        return choices;

        static IReadOnlyList<(uint Id, uint? Value)> Preset(uint value) =>
            [(DlssSrOverride, 1u), (DlssModelPresetProfile, ModelPresetCustom), (DlssSrPreset, value)];
    }

    /// <summary>Whether this machine has an NVIDIA driver NVAPI answers on. Cheap after the first call.</summary>
    public static bool IsAvailable => Nv.Api is not null;

    /// <summary>The executable the driver matches an edition by, given its install folder (which is the <c>Content</c> folder).</summary>
    public static string ExecutableIn(string installPath) => Path.Combine(installPath, ExecutableName);

    // ======================= Reading =======================

    /// <summary>The profile <paramref name="exePath"/> runs under and what it says. Null when NVAPI is unavailable or the database can't be read.</summary>
    public static Task<DriverProfileState?> ReadAsync(string exePath) => Task.Run(() => Read(exePath));

    private static readonly object Gate = new();

    private static DriverProfileState? Read(string exePath)
    {
        lock (Gate)
        {
            if (Nv.Api is not { } api) return null;
            if (api.CreateSession(out var session) != Nv.Ok) return null;
            try
            {
                var status = api.LoadSettings(session);
                if (status != Nv.Ok)
                {
                    Trace.WriteLine($"[DLSS] [Driver] Loading the driver profiles failed ({status})");
                    return null;
                }

                var profile = FindProfile(api, session, exePath);
                if (profile == IntPtr.Zero)
                    return new DriverProfileState(null, false, All.ToDictionary(s => s, s => (DriverChoice?)s.Choices[0]));

                using var info = new NativeBuffer(Nv.ProfileSize);
                info.WriteUInt32(0, Nv.Version(Nv.ProfileSize, 1));
                var name = api.GetProfileInfo(session, profile, info.Ptr) == Nv.Ok ? info.ReadString(Nv.ProfileNameOffset) : OwnProfileName;
                var predefined = info.ReadUInt32(Nv.ProfileIsPredefinedOffset) != 0;

                using var setting = new NativeBuffer(Nv.SettingSize);
                var values = new Dictionary<uint, (bool UserSet, uint? Value)>();
                foreach (var id in All.SelectMany(s => s.Choices).SelectMany(c => c.Writes).Select(w => w.Id).Distinct())
                    values[id] = ReadSetting(api, session, profile, id, setting);

                var current = All.ToDictionary(s => s, s => s.Choices.FirstOrDefault(c => Matches(c, values)));
                return new DriverProfileState(name, predefined, current);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DLSS] [Driver] Reading the profile failed: {ex.Message}");
                return null;
            }
            finally
            {
                api.DestroySession(session);
            }
        }
    }

    /// <summary>
    /// A deleting write matches a setting nobody set on this profile - absent, inherited, or still
    /// NVIDIA's predefined value - or one set to its <see cref="Neutral"/> value. A value write
    /// matches that value, whoever set it.
    /// </summary>
    private static bool Matches(DriverChoice choice, IReadOnlyDictionary<uint, (bool UserSet, uint? Value)> values) =>
        choice.Writes.All(w => w.Value is { } wanted
            ? values[w.Id].Value == wanted
            : !values[w.Id].UserSet || (Neutral.TryGetValue(w.Id, out var neutral) && values[w.Id].Value == neutral));

    /// <summary>
    /// Values that mean the same as the setting being absent. NVIDIA's tools can leave "override
    /// off" as explicit zeroes rather than deleting them (measured on a real profile: override 0,
    /// letter 0, both user-set), which is "Game decides" by any reading. Only settings no choice
    /// here ever writes that value to belong in this table - Low Latency and Smooth Motion offer
    /// an explicit Off that writes 0, and a zero counted as neutral there would read back as
    /// "Driver default" and fail the check that the write took.
    /// </summary>
    private static readonly Dictionary<uint, uint> Neutral = new()
    {
        [DlssSrOverride] = 0,
        [DlssModelPresetProfile] = 0,
        [DlssSrPreset] = 0,
    };

    private static (bool UserSet, uint? Value) ReadSetting(Nv.Functions api, IntPtr session, IntPtr profile, uint id, NativeBuffer setting)
    {
        setting.Clear();
        setting.WriteUInt32(0, Nv.Version(Nv.SettingSize, 1));
        if (api.GetSetting(session, profile, id, setting.Ptr) != Nv.Ok) return (false, null);

        // Only DWORD settings carry their value at this offset; nothing offered here is any other type.
        if (setting.ReadUInt32(Nv.SettingTypeOffset) != 0) return (false, null);

        var onThisProfile = setting.ReadUInt32(Nv.SettingLocationOffset) == 0;
        var predefined = setting.ReadUInt32(Nv.SettingIsCurrentPredefinedOffset) != 0;
        return (onThisProfile && !predefined, setting.ReadUInt32(Nv.SettingCurrentValueOffset));
    }

    private static IntPtr FindProfile(Nv.Functions api, IntPtr session, string exePath)
    {
        using var name = new NativeBuffer(Nv.StringBytes);
        using var application = new NativeBuffer(Nv.ApplicationSize);

        foreach (var candidate in new[] { exePath, Path.GetFileName(exePath) })
        {
            name.Clear();
            application.Clear();
            name.WriteString(0, candidate);
            application.WriteUInt32(0, Nv.Version(Nv.ApplicationSize, 4));
            if (api.FindApplicationByName(session, name.Ptr, out var profile, application.Ptr) == Nv.Ok)
                return profile;
        }

        name.Clear();
        name.WriteString(0, OwnProfileName);
        return api.FindProfileByName(session, name.Ptr, out var own) == Nv.Ok ? own : IntPtr.Zero;
    }

    // ======================= Writing =======================

    // The writer's exit codes. Only a refusal by the driver is worth a UAC prompt: a script that
    // can't compile or a machine without NVAPI fails exactly the same way elevated.
    private const int ExitSaved = 0;
    private const int ExitDriverRefused = 10;

    /// <summary>
    /// Gives <paramref name="setting"/> the value <paramref name="choice"/> stands for on the
    /// profile <paramref name="exePath"/> runs under, asking for elevation only if the driver
    /// refuses the user. Returns whether reading the profile back shows the choice, and a short
    /// reason when it doesn't.
    /// </summary>
    public static async Task<(bool Saved, string? Problem)> ApplyAsync(string exePath, DriverSetting setting, DriverChoice choice)
    {
        var before = await ReadAsync(exePath);
        if (before is null) return (false, "the NVIDIA driver couldn't be read");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"vrtx_nvidia_profile_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, BuildWriterScript(before.ProfileName, exePath, choice), new UTF8Encoding(true));

            var exit = await RunUnelevatedAsync(scriptPath);
            Trace.WriteLine($"[DLSS] [Driver] Writer exited {exit} for {setting.Title} = {choice.Label}");

            if (exit == ExitDriverRefused)
            {
                Trace.WriteLine("[DLSS] [Driver] The driver refused the write as the user - asking for elevation");
                await Helpers.RunPowerShellWithElevation(scriptPath, "[DLSS]", "nvidia_profile");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] [Driver] Writing failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }

        var after = await ReadAsync(exePath);
        if (after is not null && after.Current.TryGetValue(setting, out var now) && now == choice)
            return (true, null);
        return (false, "the driver didn't keep the change - administrator permission may have been declined");
    }

    private static async Task<int> RunUnelevatedAsync(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Helpers.WindowsPowerShellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process is null) return -1;

        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();

        // Add-Type compiles the writer on every run, a few seconds on a cold machine. Far past that
        // something is wrong, and a hung child must not hold the window's busy state forever.
        using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Trace.WriteLine("[DLSS] [Driver] The writer timed out");
            return -1;
        }

        var said = ((await output) + (await errors)).Trim();
        if (said.Length > 0) Trace.WriteLine($"[DLSS] [Driver] Writer said: {said}");
        return process.ExitCode;
    }

    /// <summary>
    /// The writer: a PowerShell script compiling <see cref="WriterSource"/> and calling it with
    /// this write. Arguments are PowerShell single-quoted strings, in which a quote is the only
    /// character that needs escaping.
    /// </summary>
    private static string BuildWriterScript(string? profileName, string exePath, DriverChoice choice)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        var ops = string.Join(",", choice.Writes.Select(w => w.Value is { } v ? $"{w.Id:X8}:{v:X8}" : $"{w.Id:X8}:-"));
        var create = profileName is null;

        return string.Join("\r\n",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            "Add-Type -TypeDefinition @'",
            WriterSource,
            "'@",
            "} catch { Write-Output ('compile failed: ' + $_); exit 2 }",
            "try {",
            $"    exit ([VanillaRtxDriverProfile]::Apply({Quote(profileName ?? OwnProfileName)}, {(create ? "$true" : "$false")}, {Quote(Path.GetFileName(exePath).ToLowerInvariant())}, {Quote(ops)}))",
            "} catch { Write-Output ('write failed: ' + $_); exit 2 }");
    }

    /// <summary>
    /// The writer's C#, compiled by Windows PowerShell's <c>Add-Type</c> - which is the .NET
    /// Framework compiler, so this is C# 5: no <c>out var</c>, no interpolation, no expression
    /// bodies. Same IDs and offsets as <see cref="Nv"/>; a change to one is a change to both.
    /// Exit codes: 0 saved, 3 no NVAPI, 10 the driver refused (status printed).
    /// </summary>
    private const string WriterSource = """
        using System;
        using System.Runtime.InteropServices;

        public static class VanillaRtxDriverProfile
        {
            [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr QueryInterface(uint id);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OutHandle(out IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OneHandle(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindProfile(IntPtr session, IntPtr name, out IntPtr profile);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProfile(IntPtr session, IntPtr info, out IntPtr profile);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateApplication(IntPtr session, IntPtr profile, IntPtr application);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting(IntPtr session, IntPtr profile, IntPtr setting);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSetting(IntPtr session, IntPtr profile, uint id);

            private static T Fn<T>(uint id) where T : class
            {
                IntPtr p = QueryInterface(id);
                if (p == IntPtr.Zero) throw new EntryPointNotFoundException(id.ToString("X8"));
                return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }

            private static IntPtr Alloc(int size)
            {
                IntPtr p = Marshal.AllocHGlobal(size);
                Marshal.Copy(new byte[size], 0, p, size);
                return p;
            }

            private static void Text(IntPtr p, int offset, string s)
            {
                char[] c = s.ToCharArray();
                Marshal.Copy(c, 0, IntPtr.Add(p, offset), Math.Min(c.Length, 2047));
            }

            private static int Refused(string what, int status)
            {
                Console.WriteLine(what + " returned " + status);
                return 10;
            }

            public static int Apply(string profileName, bool create, string exeName, string ops)
            {
                try
                {
                    if (Fn<NoArgs>(0x0150E828)() != 0) return 3;
                }
                catch (DllNotFoundException) { return 3; }
                catch (EntryPointNotFoundException) { return 3; }

                IntPtr session;
                int status = Fn<OutHandle>(0x0694D52E)(out session);
                if (status != 0) return Refused("create session", status);

                IntPtr name = Alloc(4096), info = Alloc(4116), app = Alloc(20492), setting = Alloc(12320);
                try
                {
                    status = Fn<OneHandle>(0x375DBD6B)(session);
                    if (status != 0) return Refused("load", status);

                    IntPtr profile;
                    if (create)
                    {
                        Marshal.WriteInt32(info, 0, 4116 | (1 << 16));
                        Text(info, 4, profileName);
                        Marshal.WriteInt32(info, 4100, 1);
                        status = Fn<CreateProfile>(0xCC176068)(session, info, out profile);
                        if (status != 0) return Refused("create profile", status);

                        Marshal.WriteInt32(app, 0, 20492 | (4 << 16));
                        Text(app, 8, exeName);
                        status = Fn<CreateApplication>(0x4347A9DE)(session, profile, app);
                        if (status != 0) return Refused("add application", status);
                    }
                    else
                    {
                        Text(name, 0, profileName);
                        status = Fn<FindProfile>(0x7E4A9A0B)(session, name, out profile);
                        if (status != 0) return Refused("find profile", status);
                    }

                    foreach (string op in ops.Split(','))
                    {
                        string[] parts = op.Split(':');
                        uint id = Convert.ToUInt32(parts[0], 16);
                        if (parts[1] == "-")
                        {
                            status = Fn<DeleteSetting>(0xE4A26362)(session, profile, id);
                            if (status != 0 && status != -160) return Refused("delete " + parts[0], status);
                        }
                        else
                        {
                            Marshal.Copy(new byte[12320], 0, setting, 12320);
                            Marshal.WriteInt32(setting, 0, 12320 | (1 << 16));
                            Marshal.WriteInt32(setting, 4100, unchecked((int)id));
                            Marshal.WriteInt32(setting, 8220, unchecked((int)Convert.ToUInt32(parts[1], 16)));
                            status = Fn<SetSetting>(0x577DD202)(session, profile, setting);
                            if (status != 0) return Refused("set " + parts[0], status);
                        }
                    }

                    status = Fn<OneHandle>(0xFCBC7E14)(session);
                    return status == 0 ? 0 : Refused("save", status);
                }
                finally
                {
                    Marshal.FreeHGlobal(name);
                    Marshal.FreeHGlobal(info);
                    Marshal.FreeHGlobal(app);
                    Marshal.FreeHGlobal(setting);
                    Fn<OneHandle>(0xDAD9CFF8)(session);
                }
            }
        }
        """;

    // ======================= NVAPI =======================

    /// <summary>
    /// The NVAPI functions the reader uses, resolved once. <see cref="Api"/> is null when there is
    /// no NVIDIA driver, when the process isn't one NVAPI ships for (it is x64 only, so ARM64 lands
    /// here), or when <c>NvAPI_Initialize</c> refuses.
    /// </summary>
    private static class Nv
    {
        public const int Ok = 0;

        public const int StringBytes = 2048 * 2;
        public const int SettingSize = 12320, ApplicationSize = 20492, ProfileSize = 4116;

        // NVDRS_SETTING_V1: version, name[2048 wchar], id, type, location, isCurrentPredefined,
        // isPredefinedValid, then two 4,100-byte unions - predefined value, current value.
        public const int SettingTypeOffset = 4104, SettingLocationOffset = 4108, SettingIsCurrentPredefinedOffset = 4112, SettingCurrentValueOffset = 8220;

        // NVDRS_PROFILE_V1: version, name[2048 wchar], gpuSupport, isPredefined, numOfApps, numOfSettings.
        public const int ProfileNameOffset = 4, ProfileIsPredefinedOffset = 4104;

        public static uint Version(int size, int version) => (uint)size | ((uint)version << 16);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int NoArgsFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OutHandleFn(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int HandleFn(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FindApplicationFn(IntPtr session, IntPtr name, out IntPtr profile, IntPtr application);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FindProfileFn(IntPtr session, IntPtr name, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ProfileInfoFn(IntPtr session, IntPtr profile, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, IntPtr setting);

        public sealed record Functions(
            OutHandleFn CreateSession,
            HandleFn DestroySession,
            HandleFn LoadSettings,
            FindApplicationFn FindApplicationByName,
            FindProfileFn FindProfileByName,
            ProfileInfoFn GetProfileInfo,
            GetSettingFn GetSetting);

        private static readonly Lazy<Functions?> _api = new(Load);
        public static Functions? Api => _api.Value;

        private static Functions? Load()
        {
            try
            {
                // By full path first: the driver installs it into System32, and nothing else on
                // the search path should be able to answer for it.
                var name = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
                if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, name), out var library)
                    && !NativeLibrary.TryLoad(name, out library))
                    return null;
                if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var queryAddress))
                    return null;

                var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(queryAddress);
                T? Fn<T>(uint id) where T : Delegate
                {
                    var address = query(id);
                    return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
                }

                if (Fn<NoArgsFn>(0x0150E828) is not { } initialize || initialize() != Ok)
                    return null;

                var create = Fn<OutHandleFn>(0x0694D52E);
                var destroy = Fn<HandleFn>(0xDAD9CFF8);
                var load = Fn<HandleFn>(0x375DBD6B);
                var findApp = Fn<FindApplicationFn>(0xEEE566B2);
                var findProfile = Fn<FindProfileFn>(0x7E4A9A0B);
                var info = Fn<ProfileInfoFn>(0x61CD6FD6);
                var get = Fn<GetSettingFn>(0x73BF8338);
                if (create is null || destroy is null || load is null || findApp is null || findProfile is null || info is null || get is null)
                    return null;

                return new Functions(create, destroy, load, findApp, findProfile, info, get);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DLSS] [Driver] NVAPI unavailable: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>A zeroed block of unmanaged memory for one NVAPI struct.</summary>
    private sealed class NativeBuffer : IDisposable
    {
        private static readonly byte[] Zeroes = new byte[Nv.ApplicationSize];

        public IntPtr Ptr { get; }
        private readonly int _size;

        public NativeBuffer(int size)
        {
            _size = size;
            Ptr = Marshal.AllocHGlobal(size);
            Clear();
        }

        public void Clear() => Marshal.Copy(Zeroes, 0, Ptr, _size);

        public void WriteUInt32(int offset, uint value) => Marshal.WriteInt32(Ptr, offset, unchecked((int)value));
        public uint ReadUInt32(int offset) => unchecked((uint)Marshal.ReadInt32(Ptr, offset));

        /// <summary>An NvAPI_UnicodeString: up to 2,047 UTF-16 units, the buffer's zeroes terminating it.</summary>
        public void WriteString(int offset, string value)
        {
            var chars = value.ToCharArray();
            Marshal.Copy(chars, 0, Ptr + offset, Math.Min(chars.Length, 2047));
        }

        public string ReadString(int offset) => Marshal.PtrToStringUni(Ptr + offset) ?? "";

        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }
}
