namespace Ninmagic.Nx.Core;

/// <summary>Estimates from reported bytes only; it never reads a disk or starts background work.</summary>
public sealed class ProgressEstimator
{
    private static readonly TimeSpan MinimumSample = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallInterval = TimeSpan.FromSeconds(15);
    private readonly Dictionary<WorkRateKind, double> rates = new();
    private readonly List<(TimeSpan Time, long Bytes)> samples = new();
    private WorkProgress? current;
    private TimeSpan clock;
    private TimeSpan lastAdvance;
    private double? currentRate;

    public void Reset()
    {
        rates.Clear();
        samples.Clear();
        current = null;
        clock = lastAdvance = TimeSpan.Zero;
        currentRate = null;
    }

    /// <param name="elapsed">Monotonic elapsed time since Reset, supplied by the caller's stopwatch.</param>
    public ProgressEstimate Update(WorkProgress progress, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (current is not null && current.Operation != progress.Operation) Reset();
        elapsed = AdvanceClock(elapsed);
        var step = GetStep(progress);
        var previousStep = current is null ? null : GetStep(current);
        bool changedStep = current is null || current.StepNumber != progress.StepNumber ||
            previousStep?.Id != step?.Id || previousStep?.RateKind != step?.RateKind;

        // An out-of-order UI callback must not move a running operation backwards.
        if (current is not null && current.Operation == progress.Operation &&
            progress.StepNumber > 0 && progress.StepNumber < current.StepNumber)
            return Estimate(elapsed);

        bool measurable = IsMeasurable(progress, step);
        long completed = measurable ? Math.Clamp(progress.Completed, 0, progress.Total) : 0;
        long previousCompleted = current is null ? 0 : Math.Clamp(current.Completed, 0, Math.Max(0, current.Total));
        if (changedStep || !measurable || current?.Total != progress.Total || completed < previousCompleted)
        {
            BeginSample(elapsed, completed);
        }
        else if (completed > previousCompleted)
        {
            // A long pause invalidates the local speed. Wait for a fresh sample after recovery.
            if (elapsed - lastAdvance >= StallInterval)
                BeginSample(elapsed, completed);
            else
            {
                samples.Add((elapsed, completed));
                while (samples.Count > 2 && samples[1].Time <= elapsed - SampleWindow)
                    samples.RemoveAt(0);
                var first = samples[0];
                var duration = elapsed - first.Time;
                if (duration >= MinimumSample)
                {
                    double speed = (completed - first.Bytes) / duration.TotalSeconds;
                    if (double.IsFinite(speed) && speed > 0)
                    {
                        currentRate = speed;
                        rates[step!.RateKind] = speed;
                    }
                }
                lastAdvance = elapsed;
            }
        }
        current = progress;
        return Estimate(elapsed);
    }

    public ProgressEstimate Tick(TimeSpan elapsed) => Estimate(AdvanceClock(elapsed));

    private TimeSpan AdvanceClock(TimeSpan elapsed)
    {
        if (elapsed > clock) clock = elapsed;
        return clock;
    }

    private void BeginSample(TimeSpan elapsed, long completed)
    {
        samples.Clear();
        samples.Add((elapsed, completed));
        currentRate = null;
        lastAdvance = elapsed;
    }

    private ProgressEstimate Estimate(TimeSpan elapsed)
    {
        if (current is null) return ProgressEstimate.Unknown;
        if (current.IsOperationComplete)
            return new(TimeSpan.Zero, TimeSpan.Zero, null, false);
        var step = GetStep(current);
        if (!IsMeasurable(current, step)) return ProgressEstimate.Unknown;

        long remainingBytes = current.Total - Math.Clamp(current.Completed, 0, current.Total);
        if (remainingBytes > 0 && elapsed - lastAdvance >= StallInterval)
            return new(null, null, null, true);
        if (currentRate is not double speed) return ProgressEstimate.Unknown;

        double secondsSinceAdvance = Math.Max(0, (elapsed - lastAdvance).TotalSeconds);
        double stepSeconds = remainingBytes > 0 ? Math.Max(1, remainingBytes / speed - secondsSinceAdvance) : 0;
        TimeSpan? stepRemaining = remainingBytes > 0 ? Duration(stepSeconds) : null;
        double operationSeconds = stepSeconds;
        bool hasPendingBytes = remainingBytes > 0;
        for (int i = current.StepNumber; i < current.Steps.Count; i++)
        {
            var pending = current.Steps[i];
            if (pending.Bytes <= 0) continue;
            hasPendingBytes = true;
            if (pending.RateKind == WorkRateKind.None || !rates.TryGetValue(pending.RateKind, out double rate))
                return new(stepRemaining, null, speed, false);
            operationSeconds += pending.Bytes / rate;
        }

        // Metadata, mounting and final flush have no byte-based duration. Do not call 100% success.
        TimeSpan? operationRemaining = hasPendingBytes ? Duration(Math.Max(1, operationSeconds)) : null;
        return new(stepRemaining, operationRemaining, speed, false);
    }

    private static WorkStep? GetStep(WorkProgress progress) =>
        progress.StepNumber > 0 && progress.StepNumber <= progress.Steps.Count
            ? progress.Steps[progress.StepNumber - 1] : null;

    private static bool IsMeasurable(WorkProgress progress, WorkStep? step) =>
        step is { Bytes: > 0, RateKind: not WorkRateKind.None } && progress.Total == step.Bytes;

    private static TimeSpan? Duration(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds - 1
            ? TimeSpan.FromSeconds(seconds) : null;
}

/// <summary>OperationRemaining covers measurable byte transfers; short finalization work can add time.</summary>
public sealed record ProgressEstimate(TimeSpan? StepRemaining, TimeSpan? OperationRemaining,
    double? BytesPerSecond, bool IsStalled)
{
    public static ProgressEstimate Unknown { get; } = new(null, null, null, false);
}
