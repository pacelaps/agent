using System.Text.Json;

namespace PaceLaps.Agent.Core.LocalFailsafe;

/// <summary>A condition the agent can evaluate offline (spec: Procedure validation, offline predicates).</summary>
public abstract record LocalCondition
{
    public abstract bool Holds(LocalFacts facts);

    public sealed record FlagIs(string Flag) : LocalCondition
    {
        public override bool Holds(LocalFacts facts) => facts.Flags.Contains(Flag);
    }

    public sealed record PaceCarOnTrack(bool Expected) : LocalCondition
    {
        public override bool Holds(LocalFacts facts) => facts.PaceCarOnTrack == Expected;
    }

    public sealed record All(IReadOnlyList<LocalCondition> Items) : LocalCondition
    {
        public override bool Holds(LocalFacts facts) => Items.All(i => i.Holds(facts));
    }

    public sealed record Any(IReadOnlyList<LocalCondition> Items) : LocalCondition
    {
        public override bool Holds(LocalFacts facts) => Items.Any(i => i.Holds(facts));
    }

    public sealed record Not(LocalCondition Item) : LocalCondition
    {
        public override bool Holds(LocalFacts facts) => !Item.Holds(facts);
    }
}

/// <summary>What the agent sees in its own telemetry: the session flags (contract names) and the pace car.</summary>
public sealed record LocalFacts(IReadOnlySet<string> Flags, bool PaceCarOnTrack)
{
    public static LocalFacts Of(IEnumerable<string> flags, bool paceCarOnTrack) => new(flags.ToHashSet(StringComparer.Ordinal), paceCarOnTrack);
}

/// <summary>A block of a local failsafe procedure: only the blocks that work offline.</summary>
public abstract record LocalBlock
{
    /// <summary>An iracing_command, announce or pits block: one command (command.schema.json type and params).</summary>
    public sealed record Command(string Type, JsonElement Params) : LocalBlock;

    public sealed record Wait(TimeSpan Duration) : LocalBlock;

    public sealed record WaitUntil(LocalCondition Condition) : LocalBlock;

    public sealed record Branch(LocalCondition Condition, string Then, string Else) : LocalBlock;

    public sealed record Goto(string Phase) : LocalBlock;

    public sealed record End : LocalBlock;
}

/// <summary>
/// A local failsafe procedure as the agent runs it (spec: Local failsafe; procedure.schema.json, offline subset): the
/// server checked it when the league designated it, and the agent checks again before keeping a copy: only offline
/// blocks (announce, iracing_command, pits, wait, wait_until, branch, goto, end) and conditions (flag_is,
/// pace_car_on_track with all, any, not), and every phase a branch or goto names must exist. The first phase starts it.
/// </summary>
public sealed class LocalProcedure
{
    private LocalProcedure(string id, IReadOnlyList<string> order, IReadOnlyDictionary<string, IReadOnlyList<LocalBlock>> phases)
    {
        Id = id;
        PhaseOrder = order;
        Phases = phases;
    }

    public string Id { get; }

    public IReadOnlyList<string> PhaseOrder { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<LocalBlock>> Phases { get; }

    public string FirstPhase => PhaseOrder[0];

    /// <summary>Reads a procedure document; null with the reason when it cannot run offline.</summary>
    public static (LocalProcedure? Procedure, string? Error) Parse(JsonElement doc)
    {
        try
        {
            if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("phases", out var phasesJson) || phasesJson.ValueKind != JsonValueKind.Array
                || phasesJson.GetArrayLength() == 0)
            {
                return (null, "not a procedure with phases");
            }

            var id = doc.TryGetProperty("id", out var idJson) && idJson.ValueKind == JsonValueKind.String ? idJson.GetString()! : "unnamed";
            var order = new List<string>();
            var phases = new Dictionary<string, IReadOnlyList<LocalBlock>>(StringComparer.Ordinal);
            var targets = new List<string>();
            foreach (var phase in phasesJson.EnumerateArray())
            {
                var phaseId = phase.GetProperty("id").GetString()!;
                var blocks = phase.TryGetProperty("blocks", out var b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray().Select(block => ReadBlock(block, targets)).ToList()
                    : [];
                if (!phases.TryAdd(phaseId, blocks))
                {
                    return (null, $"phase {phaseId} appears twice");
                }

                order.Add(phaseId);
            }

            var missing = targets.FirstOrDefault(t => !phases.ContainsKey(t));
            return missing is null ? (new LocalProcedure(id, order, phases), null) : (null, $"phase {missing} does not exist");
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, e.Message);
        }
    }

    private static LocalBlock ReadBlock(JsonElement block, List<string> targets)
    {
        var type = block.GetProperty("type").GetString();
        var p = block.TryGetProperty("params", out var ps) ? ps : default;
        switch (type)
        {
            case "iracing_command":
                return new LocalBlock.Command(p.GetProperty("command").GetString()!,
                    p.TryGetProperty("args", out var args) ? args.Clone() : JsonDocument.Parse("{}").RootElement.Clone());
            case "announce":
                return new LocalBlock.Command("announce", JsonSerializer.SerializeToElement(new { text = p.GetProperty("text").GetString() }));
            case "pits":
                return new LocalBlock.Command(p.GetProperty("state").GetString() == "open" ? "pit_open" : "pit_close", JsonDocument.Parse("{}").RootElement.Clone());
            case "wait":
                return new LocalBlock.Wait(TimeSpan.FromSeconds(p.GetProperty("seconds").GetDouble()));
            case "wait_until":
                return new LocalBlock.WaitUntil(ReadCondition(p.GetProperty("condition")));
            case "branch":
                var then = p.GetProperty("then").GetString()!;
                var otherwise = p.GetProperty("else").GetString()!;
                targets.Add(then);
                targets.Add(otherwise);
                return new LocalBlock.Branch(ReadCondition(p.GetProperty("condition")), then, otherwise);
            case "goto":
                var phase = p.GetProperty("phase").GetString()!;
                targets.Add(phase);
                return new LocalBlock.Goto(phase);
            case "end":
                return new LocalBlock.End();
            default:
                throw new InvalidDataException($"block {type} cannot run offline");
        }
    }

    private static LocalCondition ReadCondition(JsonElement c)
    {
        var p = c.EnumerateObject().Single();
        return p.Name switch
        {
            "flag_is" => new LocalCondition.FlagIs(p.Value.GetString()!),
            "pace_car_on_track" => new LocalCondition.PaceCarOnTrack(p.Value.GetBoolean()),
            "all" => new LocalCondition.All(p.Value.EnumerateArray().Select(ReadCondition).ToList()),
            "any" => new LocalCondition.Any(p.Value.EnumerateArray().Select(ReadCondition).ToList()),
            "not" => new LocalCondition.Not(ReadCondition(p.Value)),
            _ => throw new InvalidDataException($"condition {p.Name} cannot be evaluated offline"),
        };
    }
}
