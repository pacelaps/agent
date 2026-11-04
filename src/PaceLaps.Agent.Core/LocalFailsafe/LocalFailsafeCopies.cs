using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.LocalFailsafe;

/// <summary>The copy the agent holds: what the server named, the document, and the procedure read from it.</summary>
public sealed record LocalFailsafeCopy(string Procedure, int Version, string Hash, bool ArmPractice, bool ArmQualify, string Document)
{
    /// <summary>The document read for the interpreter; null if it cannot run offline (then it is never kept).</summary>
    public LocalProcedure? Read() => LocalProcedure.Parse(JsonDocument.Parse(Document).RootElement).Procedure;
}

/// <summary>Where the copy is kept, so it outlives an agent restart during an outage. A file next to the settings.</summary>
public interface ILocalFailsafeStore
{
    LocalFailsafeCopy? Load();

    void Save(LocalFailsafeCopy copy);

    void Delete();
}

/// <summary>Keeps the copy in memory only (tests, and the stub server's proof of concept).</summary>
public sealed class MemoryLocalFailsafeStore : ILocalFailsafeStore
{
    public LocalFailsafeCopy? Copy { get; set; }

    public LocalFailsafeCopy? Load() => Copy;

    public void Save(LocalFailsafeCopy copy) => Copy = copy;

    public void Delete() => Copy = null;
}

/// <summary>
/// Keeps the agent's copy of the league's local failsafe in step with the server (spec: "the server copies that
/// procedure to active agents in advance together with its version number"). The hello and heartbeat answers name the
/// version to hold; when its hash differs from the copy's, the document is fetched, checked (it must be the version
/// named, and run offline) and stored. A copy that fails the check is not kept; the old one stays, and the fetch is tried
/// again after <see cref="RetryAfter"/>. When the league designates none, the copy is deleted.
/// </summary>
public sealed class LocalFailsafeCopies
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly IServerLink _server;
    private readonly ILocalFailsafeStore _store;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private string? _failedHash;
    private TimeSpan _failedAt;
    private bool _fetching;

    public LocalFailsafeCopies(IServerLink server, ILocalFailsafeStore store, IClock clock, ILogger? logger = null)
    {
        _server = server;
        _store = store;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        Held = store.Load();
    }

    /// <summary>The copy held now (loaded at start: it survives a restart).</summary>
    public LocalFailsafeCopy? Held { get; private set; }

    /// <summary>Follows an answer that names the local failsafe to hold. Answers that say nothing about it change nothing.</summary>
    public async Task FollowAsync(ServerView view, CancellationToken ct)
    {
        if (!view.LocalFailsafeKnown)
        {
            return;
        }

        if (view.LocalFailsafe is not { } target)
        {
            if (Held is not null)
            {
                _store.Delete();
                Held = null;
                _logger.LogInformation("Local failsafe: the league designates none; copy deleted");
            }

            return;
        }

        if (Held is { } held && held.Hash == target.Hash)
        {
            if (held.ArmPractice != target.ArmPractice || held.ArmQualify != target.ArmQualify)
            {
                // The league's arming changed, the procedure did not.
                Held = held with { ArmPractice = target.ArmPractice, ArmQualify = target.ArmQualify };
                _store.Save(Held);
            }

            return;
        }

        lock (_gate)
        {
            if (_fetching || (_failedHash == target.Hash && _clock.Elapsed - _failedAt < RetryAfter))
            {
                return;
            }

            _fetching = true;
        }

        try
        {
            var doc = await _server.FetchLocalFailsafeAsync(ct).ConfigureAwait(false);
            var problem = doc switch
            {
                null => "the server has no local failsafe document",
                _ when doc.Hash != target.Hash || doc.Version != target.Version => $"the server sent v{doc.Version}, not the v{target.Version} it named",
                _ => LocalProcedure.Parse(doc.Document).Error,
            };
            if (problem is not null)
            {
                Failed(target, problem);
                return;
            }

            Held = new LocalFailsafeCopy(doc!.Procedure, doc.Version, doc.Hash, target.ArmPractice, target.ArmQualify, doc.Document.GetRawText());
            _store.Save(Held);
            _logger.LogInformation("Local failsafe: holds {Procedure} v{Version}", Held.Procedure, Held.Version);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException && !ct.IsCancellationRequested)
        {
            Failed(target, e.Message);
        }
        finally
        {
            lock (_gate)
            {
                _fetching = false;
            }
        }
    }

    private void Failed(LocalFailsafeTarget target, string problem)
    {
        lock (_gate)
        {
            _failedHash = target.Hash;
            _failedAt = _clock.Elapsed;
        }

        _logger.LogWarning("Local failsafe {Procedure} v{Version} not kept: {Problem}{Old}", target.Procedure, target.Version, problem,
            Held is null ? "" : $" (still holding v{Held.Version})");
    }
}
