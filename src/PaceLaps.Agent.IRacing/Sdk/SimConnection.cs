using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using IRSDKSharper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.IRacing.Sdk;

/// <summary>The parts of the session info YAML the agent uses. No names: user names stay out of logs.</summary>
public sealed record SessionInfoView
{
    public required int UpdateCount { get; init; }
    public required string? BuildVersion { get; init; }
    public required string? SimMode { get; init; }
    public required int LeagueId { get; init; }
    public required int SeasonId { get; init; }
    public required int SessionId { get; init; }
    public required int SubSessionId { get; init; }
    public required int CurrentSessionNum { get; init; }
    public required IReadOnlyList<(int Num, string? Type)> Sessions { get; init; }
    public required int DriverCarIdx { get; init; }
    public required bool DriverIsAdmin { get; init; }
    public required int PaceCarIdx { get; init; }
    public required IReadOnlyList<CarEntry> Cars { get; init; }

    /// <summary>WeekendInfo: the layout being raced (TrackID, TrackDisplayName, TrackConfigName, TrackLength).</summary>
    public int TrackId { get; init; }

    public string? TrackDisplayName { get; init; }

    public string? TrackConfigName { get; init; }

    public string? TrackLength { get; init; }

    /// <summary>The current session's lap and time limits as the session info writes them ("unlimited", "30", "1800.0000 sec").</summary>
    public string? CurrentSessionLaps { get; init; }

    public string? CurrentSessionTime { get; init; }

    /// <summary>SplitTimeInfo: where the layout's sectors start, as lap distance (0 to 1), in sector order.</summary>
    public IReadOnlyList<double> SectorStarts { get; init; } = [];

    /// <summary>Length of the operator's user name, so a log can show the name is readable without containing it.</summary>
    public int OperatorNameLength { get; init; }

    /// <summary>The operator's iRacing name, for the hello message (stewards see it). Never write it to a log.</summary>
    public string? OperatorName { get; init; }

    public int OperatorCustId { get; init; }

    /// <summary>The operator's own incident count from DriverInfo.</summary>
    public int DriverIncidentCount { get; init; }

    /// <summary>Incidents per car index from the current session's results positions (what other drivers see).</summary>
    public IReadOnlyDictionary<int, int> ResultIncidents { get; init; } = new Dictionary<int, int>();

    /// <summary>Each session's results (ResultsPositions and the results fields), in iRacing's meanings.</summary>
    public IReadOnlyList<SimSessionResults> Results { get; init; } = [];

    /// <summary>Monotonic time (<see cref="IClock.Elapsed"/>) when this session info was read.</summary>
    public TimeSpan ReceivedElapsed { get; init; }

    public string? CurrentSessionType =>
        Sessions.FirstOrDefault(s => s.Num == CurrentSessionNum).Type;

    public CarEntry? CarByIdx(int idx) => Cars.FirstOrDefault(c => c.CarIdx == idx);

    public CarEntry? CarByNumber(string number) => Cars.FirstOrDefault(c => c.CarNumber == number && !c.IsSpectator);
}

public sealed record CarEntry(int CarIdx, string CarNumber, int ClassId, bool IsPaceCar, bool IsSpectator, int UserId,
    int CurDriverIncidentCount = 0, int TeamIncidentCount = 0, string? ClassShortName = null, string? ClassColor = null,
    string? CarScreenName = null);

/// <summary>
/// Telemetry values captured on the SDK thread; arrays are copies, so readers on other threads are safe.
/// <paramref name="Captured"/> is the monotonic capture time (<see cref="IClock.Elapsed"/>).
/// </summary>
public sealed record TelemetryFrame(int TickCount, double SessionTime, IReadOnlyDictionary<string, object[]> Values,
    TimeSpan Captured = default)
{
    public object? Get(string name, int index = 0) =>
        Values.TryGetValue(name, out var v) && index < v.Length ? v[index] : null;
}

/// <summary>
/// The only class that touches IRSDKSharper. Captures session info and a configured set of telemetry
/// variables, and sends the SDK's chat broadcast messages (begin chat, cancel, macro).
/// </summary>
public sealed class SimConnection : ISimChat, IDisposable
{
    private readonly IRacingSdk _sdk;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private string[] _telemetryVars = [];
    private volatile SessionInfoView? _session;
    private volatile TelemetryFrame? _latest;
    private volatile Exception? _lastError;

    public SimConnection(ILogger<SimConnection>? logger = null, IClock? clock = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _clock = clock ?? new SystemClock();
        _sdk = new IRacingSdk(throwYamlExceptions: false);
        _sdk.OnConnected += () => _logger.LogInformation("iRacing connected");
        _sdk.OnDisconnected += () => _logger.LogInformation("iRacing disconnected");
        _sdk.OnException += ex =>
        {
            _lastError = ex;
            _logger.LogError(ex, "IRSDKSharper background task failed");
        };
        _sdk.OnSessionInfo += CaptureSessionInfo;
        _sdk.OnTelemetryData += CaptureTelemetry;
    }

    public bool IsConnected => _sdk.IsConnected;

    public SessionInfoView? Session => _session;

    public TelemetryFrame? Latest => _latest;

    public Exception? LastError => _lastError;

    /// <summary>Raised on the SDK thread for every captured frame. Handlers must be quick, or frames are dropped.</summary>
    public event Action<TelemetryFrame>? FrameCaptured;

    /// <summary>Raised on the SDK thread after each session info update, with iRacing's whole session info (recorders).</summary>
    public event Action<IRacingSdkSessionInfo>? SessionInfoCaptured;

    /// <summary>All telemetry variable names the sim broadcasts, available once connected.</summary>
    public IReadOnlyCollection<string> AvailableVars =>
        _sdk.Data.TelemetryDataPropertiesReady ? _sdk.Data.TelemetryDataProperties.Keys.ToArray() : [];

    /// <param name="telemetryVars">Variables to copy into <see cref="Latest"/>. Unknown names are skipped.</param>
    /// <param name="updateInterval">Data frames per capture; the sim runs at 60 Hz, so 6 means 10 Hz.</param>
    public void Start(IEnumerable<string> telemetryVars, int updateInterval = 6)
    {
        _telemetryVars = telemetryVars.Distinct().ToArray();
        _sdk.UpdateInterval = updateInterval;
        _sdk.Start();
    }

    public void Stop() => _sdk.Stop();

    public async Task<bool> WaitForSessionAsync(IClock clock, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = clock.Elapsed + timeout;
        while (clock.Elapsed < until)
        {
            if (IsConnected && _session is not null && _latest is not null)
            {
                return true;
            }

            await clock.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        return false;
    }

    public void BeginChat() => _sdk.ChatComand(IRacingSdkEnum.ChatCommandMode.BeginChat, 0);

    public void CancelChat() => _sdk.ChatComand(IRacingSdkEnum.ChatCommandMode.Cancel, 0);

    /// <summary>Runs chat macro <paramref name="slot"/> (1 to 15, as numbered in the sim's options).</summary>
    public void TriggerMacro(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 15);
        _sdk.ChatComand(IRacingSdkEnum.ChatCommandMode.Macro, slot - 1);
    }

    /// <summary>Moves the local replay to a time in a session (SDK message; no focus needed).</summary>
    public void ReplayJump(int sessionNum, double sessionTimeSec) =>
        _sdk.ReplaySearchSessionTime(sessionNum, (int)Math.Round(sessionTimeSec * 1000));

    /// <summary>
    /// Points the local camera at a car (SDK message), keeping the camera group: <paramref name="group"/> is the
    /// current CamGroupNumber. Works live and in a replay.
    /// </summary>
    public void CameraOnCar(string carNumber, int group) =>
        _sdk.CamSwitchNum(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, CarNumberRaw.From(carNumber), group, 0);

    /// <summary>Returns the local view to the live session: jump to the end of the tape and play at normal speed.</summary>
    public void ReplayReturnToLive()
    {
        _sdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToEnd);
        _sdk.ReplaySetPlaySpeed(1, false);
    }

    public void Dispose() => _sdk.Stop();

    private void CaptureSessionInfo()
    {
        try
        {
            var info = _sdk.Data.SessionInfo;
            if (info is null)
            {
                return;
            }

            var weekend = info.WeekendInfo;
            var driver = info.DriverInfo;
            var currentNum = info.SessionInfo?.CurrentSessionNum ?? -1;
            var current = info.SessionInfo?.Sessions?.FirstOrDefault(s => s.SessionNum == currentNum);
            var results = new Dictionary<int, int>();
            foreach (var r in current?.ResultsPositions ?? [])
            {
                results[r.CarIdx] = r.Incidents;
            }

            var me = driver?.Drivers?.FirstOrDefault(d => d.CarIdx == driver.DriverCarIdx);
            _session = new SessionInfoView
            {
                UpdateCount = _sdk.Data.SessionInfoUpdate,
                BuildVersion = weekend?.BuildVersion,
                SimMode = weekend?.SimMode,
                LeagueId = weekend?.LeagueID ?? 0,
                SeasonId = weekend?.SeasonID ?? 0,
                SessionId = weekend?.SessionID ?? 0,
                SubSessionId = weekend?.SubSessionID ?? 0,
                CurrentSessionNum = info.SessionInfo?.CurrentSessionNum ?? -1,
                CurrentSessionLaps = current is null ? null : $"{current.SessionLaps}",
                CurrentSessionTime = current is null ? null : $"{current.SessionTime}",
                Sessions = info.SessionInfo?.Sessions?.Select(s => (s.SessionNum, (string?)s.SessionType)).ToArray() ?? [],
                DriverCarIdx = driver?.DriverCarIdx ?? -1,
                DriverIsAdmin = driver?.DriverIsAdmin == 1,
                PaceCarIdx = driver?.PaceCarIdx ?? -1,
                Cars = driver?.Drivers?.Select(d => new CarEntry(d.CarIdx, d.CarNumber ?? "", d.CarClassID,
                    d.CarIsPaceCar == 1, d.IsSpectator == 1, d.UserID, d.CurDriverIncidentCount, d.TeamIncidentCount,
                    d.CarClassShortName, d.CarClassColor, d.CarScreenName)).ToArray() ?? [],
                OperatorNameLength = me?.UserName?.Length ?? 0,
                OperatorName = me?.UserName,
                OperatorCustId = driver?.DriverUserID ?? 0,
                TrackId = weekend?.TrackID ?? 0,
                TrackDisplayName = weekend?.TrackDisplayName,
                TrackConfigName = weekend?.TrackConfigName,
                TrackLength = weekend?.TrackLength,
                SectorStarts = info.SplitTimeInfo?.Sectors?.OrderBy(x => x.SectorNum).Select(x => (double)x.SectorStartPct).ToArray() ?? [],
                DriverIncidentCount = driver?.DriverIncidentCount ?? 0,
                ResultIncidents = results,
                Results = info.SessionInfo?.Sessions?.Select(s => new SimSessionResults(
                    s.SessionNum,
                    s.ResultsOfficial == 1,
                    s.ResultsLapsComplete,
                    s.ResultsFastestLap?.FirstOrDefault() is { } f ? new SimFastestLap(f.CarIdx, f.FastestLap, f.FastestTime) : null,
                    s.ResultsPositions?.Select(r => new SimResultRow(r.CarIdx, r.Position, r.ClassPosition, r.LapsComplete, r.Lap, r.Time,
                        r.LastTime, r.FastestTime, r.FastestLap, r.LapsLed, r.Incidents, r.ReasonOutId, r.ReasonOutStr)).ToArray() ?? [])).ToArray() ?? [],
                ReceivedElapsed = _clock.Elapsed,
            };
            SessionInfoCaptured?.Invoke(info);
        }
        catch (Exception ex)
        {
            _lastError = ex;
            _logger.LogError(ex, "Reading session info failed");
        }
    }

    private void CaptureTelemetry()
    {
        try
        {
            var data = _sdk.Data;
            var values = new Dictionary<string, object[]>(_telemetryVars.Length);
            foreach (var name in _telemetryVars)
            {
                if (!data.TelemetryDataProperties.TryGetValue(name, out var datum))
                {
                    continue;
                }

                var array = new object[datum.Count];
                for (var i = 0; i < datum.Count; i++)
                {
                    array[i] = data.GetValue(datum, i);
                }

                values[name] = array;
            }

            var sessionTime = data.TelemetryDataProperties.ContainsKey("SessionTime") ? data.GetDouble("SessionTime") : 0;
            var frame = new TelemetryFrame(data.TickCount, sessionTime, values, _clock.Elapsed);
            _latest = frame;
            FrameCaptured?.Invoke(frame);
        }
        catch (Exception ex)
        {
            _lastError = ex;
            _logger.LogError(ex, "Reading telemetry failed");
        }
    }
}
