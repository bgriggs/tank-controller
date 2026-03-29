using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TankController;

/// <summary>A single relay on/off transition with the temperature at the time.</summary>
public sealed record RelayEvent(DateTime Timestamp, bool IsOn, double TemperatureF);

/// <summary>Point-in-time snapshot returned to the web dashboard.</summary>
public sealed record StatusSnapshot(
    double? CurrentTemperatureF,
    bool RelayIsOn,
    IReadOnlyList<RelayEvent> RecentEvents,
    double MonthlyOnHours,
    double MonthlyOnPercent,
    DateTime AsOf);

/// <summary>
/// Thread-safe store for current temperature, relay state and historical relay events.
/// Written by <see cref="Service"/> and read by the /api/status endpoint.
/// State is persisted to disk and restored on startup when a <paramref name="dataFilePath"/> is supplied.
/// </summary>
public sealed class StatusTracker : IDisposable
{
    private readonly object _lock = new();
    private double? _currentTemperatureF;
    private bool _relayIsOn;

    // Ordered oldest-first; capped at MaxStoredEvents to bound memory use.
    // 2 000 events covers > 30 days at one toggle per minute – well beyond real usage.
    private readonly LinkedList<RelayEvent> _events = new();
    private const int MaxStoredEvents  = 2_000;
    private const int MaxDisplayEvents = 100;
    private static readonly TimeSpan MonthlyWindow = TimeSpan.FromDays(30);

    private readonly string? _dataFilePath;
    private readonly ILogger<StatusTracker>? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="dataFilePath">
    ///   Path to the JSON persistence file. Pass <c>null</c> (default) to run without persistence,
    ///   which is the default used by unit tests.
    /// </param>
    /// <param name="logger">Optional logger for load/save diagnostics.</param>
    public StatusTracker(string? dataFilePath = null, ILogger<StatusTracker>? logger = null)
    {
        _dataFilePath = dataFilePath;
        _logger = logger;

        if (_dataFilePath is not null)
            Load();
    }

    // ── Writer API (called from the background Service thread) ───────────────

    public void UpdateTemperature(double temperatureF)
    {
        lock (_lock) _currentTemperatureF = temperatureF;
    }

    /// <summary>Records a relay-on transition only when the relay was previously off.</summary>
    public void RecordRelayOn(double temperatureF)
    {
        lock (_lock)
        {
            if (_relayIsOn) return;
            _relayIsOn = true;
            Append(new RelayEvent(DateTime.UtcNow, IsOn: true, temperatureF));
        }
        Save(); // snapshot taken inside Save(); I/O outside the lock
    }

    /// <summary>Records a relay-off transition only when the relay was previously on.</summary>
    public void RecordRelayOff(double temperatureF)
    {
        lock (_lock)
        {
            if (!_relayIsOn) return;
            _relayIsOn = false;
            Append(new RelayEvent(DateTime.UtcNow, IsOn: false, temperatureF));
        }
        Save();
    }

    // ── Reader API (called from HTTP request threads) ────────────────────────

    public StatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var (onHours, onPercent) = CalculateMonthlyOnTime(now);

            // Return events newest-first for the dashboard table
            var recent = _events.Reverse().Take(MaxDisplayEvents).ToList();

            return new StatusSnapshot(
                _currentTemperatureF,
                _relayIsOn,
                recent,
                onHours,
                onPercent,
                now);
        }
    }

    // ── IDisposable — flush latest temperature on clean shutdown ─────────────

    public void Dispose() => Save();

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_dataFilePath))
        {
            _logger?.LogInformation("No persisted state found at {Path}; starting fresh", _dataFilePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_dataFilePath!);
            var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOpts);
            if (state is null) return;

            // Load() is called from the constructor before the object is exposed
            // to other threads, so no lock is needed here.
            _relayIsOn           = state.RelayIsOn;
            _currentTemperatureF = state.LastTemperatureF;

            foreach (var ev in state.Events.OrderBy(e => e.Timestamp))
            {
                _events.AddLast(ev);
                if (_events.Count > MaxStoredEvents)
                    _events.RemoveFirst();
            }

            _logger?.LogInformation(
                "Loaded {Count} relay events from {Path} (relay was {State})",
                _events.Count, _dataFilePath, _relayIsOn ? "ON" : "OFF");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load persisted state from {Path}; starting fresh", _dataFilePath);
        }
    }

    /// <summary>
    /// Atomically persists current state.
    /// Takes a snapshot under the lock, then writes outside the lock so callers
    /// are never blocked by disk I/O.
    /// </summary>
    private void Save()
    {
        if (_dataFilePath is null) return;

        // Snapshot while holding the lock; serialize and flush outside.
        PersistedState state;
        lock (_lock)
        {
            state = new PersistedState(
                RelayIsOn:        _relayIsOn,
                LastTemperatureF: _currentTemperatureF,
                Events:           [.. _events]);
        }

        try
        {
            var dir = Path.GetDirectoryName(_dataFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json   = JsonSerializer.Serialize(state, JsonOpts);
            var tmpPath = _dataFilePath + ".tmp";

            File.WriteAllText(tmpPath, json);
            // Rename is atomic on POSIX (ext4) — protects against partial writes on power loss
            File.Move(tmpPath, _dataFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save state to {Path}", _dataFilePath);
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void Append(RelayEvent ev)
    {
        _events.AddLast(ev);
        if (_events.Count > MaxStoredEvents)
            _events.RemoveFirst();
    }

    private (double OnHours, double OnPercent) CalculateMonthlyOnTime(DateTime now)
    {
        var windowStart = now - MonthlyWindow;

        // Determine relay state at the start of the window from the most recent
        // event that occurred BEFORE the window.
        var lastBefore = _events.LastOrDefault(e => e.Timestamp < windowStart);

        bool stateAtWindowStart;
        if (lastBefore is not null)
            stateAtWindowStart = lastBefore.IsOn;
        else
        {
            // No prior event; infer from the first event inside the window (its
            // state is the result of a transition, so the prior state was opposite).
            var firstInside = _events.FirstOrDefault(e => e.Timestamp >= windowStart);
            stateAtWindowStart = firstInside is not null ? !firstInside.IsOn : _relayIsOn;
        }

        var windowEvents = _events
            .Where(e => e.Timestamp >= windowStart)
            .OrderBy(e => e.Timestamp);

        double totalSeconds = 0;
        DateTime? onSince = stateAtWindowStart ? windowStart : null;

        foreach (var ev in windowEvents)
        {
            if (ev.IsOn && onSince is null)
                onSince = ev.Timestamp;
            else if (!ev.IsOn && onSince is not null)
            {
                totalSeconds += (ev.Timestamp - onSince.Value).TotalSeconds;
                onSince = null;
            }
        }

        if (onSince is not null)
            totalSeconds += (now - onSince.Value).TotalSeconds;

        return (
            Math.Round(totalSeconds / 3_600.0, 1),
            Math.Round(totalSeconds / MonthlyWindow.TotalSeconds * 100.0, 1));
    }

    // ── Serialisation model ──────────────────────────────────────────────────

    private sealed class PersistedState
    {
        public bool RelayIsOn { get; set; }
        public double? LastTemperatureF { get; set; }
        public RelayEvent[] Events { get; set; } = [];

        // Parameterless ctor required by System.Text.Json for class-based models.
        public PersistedState() { }

        public PersistedState(bool RelayIsOn, double? LastTemperatureF, RelayEvent[] Events)
        {
            this.RelayIsOn        = RelayIsOn;
            this.LastTemperatureF = LastTemperatureF;
            this.Events           = Events;
        }
    }
}
