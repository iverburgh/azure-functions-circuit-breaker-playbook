using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class StateMachineTests
{
    [Test]
    public void Every_state_change_the_engine_makes_is_in_the_transition_table()
    {
        // A seeded random walk through thousands of operations; the history must only contain listed transitions.
        var random = new Random(20260105);
        var data = Registered();
        var now = T0;
        var transitions = new List<TransitionRecord>();
        for (var step = 0; step < 5_000; step++)
        {
            now = now.AddSeconds(random.Next(0, 15));
            var effects = random.Next(9) switch
            {
                0 or 1 => CircuitEngine.ReportOutcomes(data, Failures(random.Next(1, 3)), now),
                2 => CircuitEngine.ReportOutcomes(data, Successes(random.Next(1, 5)), now),
                3 => CircuitEngine.RecoveryTimeoutElapsed(data, new RecoveryTimer(data.Generation - random.Next(0, 2)), now),
                4 => ProbeAndReport(data, now, random),
                5 => CircuitEngine.ProbeLeaseExpired(data, new ProbeLeaseTimer(data.Probes.FirstOrDefault()?.Id ?? Guid.NewGuid(), data.Generation), now),
                6 => CircuitEngine.ApplyManualAction(data, new ManualActionCommand(RandomAction(random), "test", "tester"), now).Effects,
                _ => Advance(data, now),
            };
            transitions.AddRange(effects.Transitions);
            AssertInvariants(data, now);
        }

        Assert.That(transitions, Has.Count.GreaterThan(100), "The walk should exercise many transitions.");
        Assert.That(transitions.Select(t => (t.From, t.Trigger, t.To)).Distinct(),
            Is.SubsetOf(CircuitTransitions.All.Select(t => (t.From, t.Trigger.ToString(), t.To))));
        Assert.That(data.Generation, Is.EqualTo(transitions.Count), "Every transition increments the generation exactly once.");
    }

    [Test]
    public void The_table_has_no_duplicate_entries_and_every_state_can_reach_closed()
    {
        Assert.That(CircuitTransitions.All.Select(t => (t.From, t.Trigger)), Is.Unique);
        foreach (var state in Enum.GetValues<CircuitState>())
        {
            Assert.That(CanReach(state, CircuitState.Closed), Is.True, $"{state} must be able to return to Closed.");
        }
    }

    [TestCase(CircuitState.Closed, new[] { CircuitAction.Isolate, CircuitAction.Disable, CircuitAction.Delete })]
    [TestCase(CircuitState.Open, new[] { CircuitAction.Isolate, CircuitAction.Disable, CircuitAction.Reset, CircuitAction.Delete })]
    [TestCase(CircuitState.HalfOpen, new[] { CircuitAction.Isolate, CircuitAction.Disable, CircuitAction.Reset, CircuitAction.Delete })]
    [TestCase(CircuitState.Isolated, new[] { CircuitAction.Disable, CircuitAction.Reset, CircuitAction.Delete })]
    [TestCase(CircuitState.Disabled, new[] { CircuitAction.Isolate, CircuitAction.Reset, CircuitAction.Delete })]
    public void The_allowed_actions_follow_from_the_table(CircuitState state, CircuitAction[] expected) =>
        Assert.That(CircuitTransitions.AllowedActions(state), Is.EqualTo(expected));

    [TestCase(CircuitState.Closed, SwitchPosition.Closed)]
    [TestCase(CircuitState.HalfOpen, SwitchPosition.Closed)]
    [TestCase(CircuitState.Disabled, SwitchPosition.Closed)]
    [TestCase(CircuitState.Open, SwitchPosition.Open)]
    [TestCase(CircuitState.Isolated, SwitchPosition.Open)]
    public void Only_open_and_isolated_circuits_stop_work_behind_a_switch(CircuitState state, SwitchPosition position) =>
        Assert.That(CircuitTransitions.SwitchPositionFor(state), Is.EqualTo(position));

    [Test]
    public void The_state_machine_diagram_in_the_documentation_matches_the_table()
    {
        var doc = Path.Combine(RepositoryRoot(), "docs", "state-machine.md");
        var documented = File.ReadAllLines(doc)
            .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\w+)\s*-->\s*(\w+)\s*:\s*(\w+)\s*$"))
            .Where(match => match.Success && match.Groups[1].Value != "[")
            .Select(match => (match.Groups[1].Value, match.Groups[3].Value, match.Groups[2].Value))
            .ToHashSet();

        var table = CircuitTransitions.All.Select(t => (t.From.ToString(), t.Trigger.ToString(), t.To.ToString())).ToHashSet();
        Assert.That(documented, Is.EquivalentTo(table), "Update the mermaid diagram in docs/state-machine.md together with CircuitTransitions.");
    }

    private static CircuitEffects ProbeAndReport(CircuitData data, DateTimeOffset now, Random random)
    {
        var (permit, effects) = CircuitEngine.TryAcquireProbe(data, new ProbeRequest(), Guid.NewGuid(), now);
        if (permit.ProbeId is { } probeId && random.Next(3) > 0)
        {
            var succeeded = random.Next(2) == 0;
            var report = CircuitEngine.ReportOutcomes(data, Probe(probeId, succeeded, succeeded ? null : FailureCategory.Timeout), now);
            effects.Transitions.AddRange(report.Transitions);
        }

        return effects;
    }

    private static CircuitEffects Advance(CircuitData data, DateTimeOffset now) =>
        data is { State: CircuitState.Open, RecoveryAt: { } recoveryAt } && recoveryAt <= now
            ? CircuitEngine.RecoveryTimeoutElapsed(data, new RecoveryTimer(data.Generation), now)
            : new CircuitEffects();

    private static CircuitAction RandomAction(Random random) =>
        random.Next(10) switch
        {
            0 => CircuitAction.Isolate,
            1 => CircuitAction.Disable,
            _ => CircuitAction.Reset,
        };

    private static void AssertInvariants(CircuitData data, DateTimeOffset now)
    {
        Assert.That(data.Probes, Has.Count.LessThanOrEqualTo(data.RequiredDefinition.HalfOpenMaxProbes), "Never more probes than allowed.");
        if (data.State != CircuitState.HalfOpen)
        {
            Assert.That(data.Probes, Is.Empty, "Probe leases exist only while half-open.");
        }

        Assert.That(data.RecoveryAt.HasValue, Is.EqualTo(data.State == CircuitState.Open), "A recovery time exists exactly while open.");
        Assert.That(data.History, Has.Count.LessThanOrEqualTo(CircuitData.MaxHistory));
        Assert.That(data.Window, Has.Count.LessThanOrEqualTo(FailureWindow.BucketsPerWindow + 1), "The window stays bounded.");
    }

    private static bool CanReach(CircuitState from, CircuitState to)
    {
        var seen = new HashSet<CircuitState> { from };
        var queue = new Queue<CircuitState>([from]);
        while (queue.TryDequeue(out var state))
        {
            if (state == to)
            {
                return true;
            }

            foreach (var next in CircuitTransitions.All.Where(t => t.From == state).Select(t => t.To))
            {
                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CircuitBreaker.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
