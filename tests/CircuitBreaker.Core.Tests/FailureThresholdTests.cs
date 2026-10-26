using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class FailureThresholdTests
{
    [Test]
    public void Failures_below_the_threshold_keep_the_circuit_closed()
    {
        var data = Registered();

        CircuitEngine.ReportOutcomes(data, Failures(2), T0);

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(FailureWindow.Totals(data, T0).CountedFailures, Is.EqualTo(2));
    }

    [Test]
    public void Reaching_the_threshold_within_the_window_opens_the_circuit()
    {
        var data = Registered();

        CircuitEngine.ReportOutcomes(data, Failures(2), T0);
        var effects = CircuitEngine.ReportOutcomes(data, Failures(1, FailureCategory.Timeout), T0.AddSeconds(50));

        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
        Assert.That(effects.Transitions.Single().Trigger, Is.EqualTo(nameof(CircuitTrigger.FailureThresholdReached)));
        Assert.That(effects.Transitions.Single().Reason, Does.Contain("3 counted failures"));
    }

    [Test]
    public void Failures_that_slide_out_of_the_window_no_longer_count()
    {
        var data = Registered();

        CircuitEngine.ReportOutcomes(data, Failures(2), T0);
        CircuitEngine.ReportOutcomes(data, Failures(1), T0.AddSeconds(75));

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(FailureWindow.Totals(data, T0.AddSeconds(75)).CountedFailures, Is.EqualTo(1));
    }

    [Test]
    public void Successes_do_not_erase_recent_failures()
    {
        // Unlike a consecutive-failure counter, a window keeps flapping dependencies from never tripping.
        var data = Registered();

        CircuitEngine.ReportOutcomes(data, Failures(2), T0);
        CircuitEngine.ReportOutcomes(data, Successes(50), T0.AddSeconds(1));
        CircuitEngine.ReportOutcomes(data, Failures(1), T0.AddSeconds(2));

        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
    }

    [Test]
    public void Functional_errors_are_recorded_but_do_not_count()
    {
        var data = Registered();

        CircuitEngine.ReportOutcomes(data, Failures(10, FailureCategory.Functional), T0);

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
        var totals = FailureWindow.Totals(data, T0);
        Assert.That(totals.CountedFailures, Is.Zero);
        Assert.That(totals.IgnoredFailures, Is.EqualTo(10));
    }

    [Test]
    public void Each_circuit_chooses_which_categories_count()
    {
        var data = Registered(Definition(d => d with { CountedCategories = [FailureCategory.Timeout] }));

        CircuitEngine.ReportOutcomes(data, Failures(5, FailureCategory.ServerError), T0);
        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));

        CircuitEngine.ReportOutcomes(data, Failures(3, FailureCategory.Timeout), T0);
        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
    }

    [Test]
    public void An_aggregated_report_counts_the_same_as_individual_reports()
    {
        var batched = Registered();
        var individual = Registered();
        var report = new OutcomeReport
        {
            Successes = 4,
            Failures = [new FailureCount(FailureCategory.Timeout, 1), new FailureCount(FailureCategory.ServerError, 1)],
        };

        CircuitEngine.ReportOutcomes(batched, report, T0);
        CircuitEngine.ReportOutcomes(individual, Failures(1, FailureCategory.ServerError), T0);
        CircuitEngine.ReportOutcomes(individual, Successes(4), T0);
        CircuitEngine.ReportOutcomes(individual, Failures(1, FailureCategory.Timeout), T0);

        Assert.That(FailureWindow.Totals(batched, T0), Is.EqualTo(FailureWindow.Totals(individual, T0)));
    }

    [Test]
    public void The_last_failure_is_kept_for_operators_and_truncated()
    {
        var data = Registered();

        CircuitEngine.ReportOutcomes(data, Failures(1) with { LastFailure = new FailureSample(FailureCategory.ServerError, new string('x', 2000), T0) }, T0);

        Assert.That(data.LastFailure!.Category, Is.EqualTo(FailureCategory.ServerError));
        Assert.That(data.LastFailure.Message, Has.Length.EqualTo(500));
    }

    [Test]
    public void Changing_the_window_size_starts_counting_afresh()
    {
        var data = Registered();
        CircuitEngine.ReportOutcomes(data, Failures(2), T0);

        CircuitEngine.Register(data, Definition(d => d with { FailureWindow = TimeSpan.FromSeconds(30) }), T0);

        Assert.That(FailureWindow.Totals(data, T0).CountedFailures, Is.Zero);
    }

    [Test]
    public void The_window_keeps_constant_memory_however_many_calls_are_made()
    {
        var data = Registered();
        for (var second = 0; second < 3_600; second++)
        {
            CircuitEngine.ReportOutcomes(data, Successes(1_000), T0.AddSeconds(second));
        }

        Assert.That(data.Window, Has.Count.LessThanOrEqualTo(FailureWindow.BucketsPerWindow + 1));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Invalid_definitions_are_rejected(int threshold) =>
        Assert.That(Definition(d => d with { FailureThreshold = threshold }).Validate(), Is.Not.Empty);

    [TestCase("Payments")]
    [TestCase("payments_api")]
    [TestCase("-payments")]
    public void Keys_are_lower_case_with_hyphens(string key) =>
        Assert.That(Definition(d => d with { Key = key }).Validate(), Has.Some.Contains("Key"));
}
