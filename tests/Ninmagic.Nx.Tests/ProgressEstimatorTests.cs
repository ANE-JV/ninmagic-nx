using Ninmagic.Nx.Core;

namespace Ninmagic.Nx.Tests;

public static class ProgressEstimatorTests
{
    public static void Run(Action<string, Action> test)
    {
        test("ETA waits for a timed byte sample and counts down without premature zero", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetWrite);
            Check(estimator.Tick(TimeSpan.Zero).StepRemaining is null, "Empty estimator returned an ETA");
            estimator.Update(Report(steps, 1, 0), At(0));
            Check(estimator.Update(Report(steps, 1, 300), At(3)).StepRemaining is null, "Insufficient sample accepted");
            var estimate = estimator.Update(Report(steps, 1, 400), At(4));
            Near(estimate.BytesPerSecond, 100);
            Near(estimate.StepRemaining?.TotalSeconds, 6);
            Near(estimator.Tick(At(5)).StepRemaining?.TotalSeconds, 5);
            Near(estimator.Tick(At(11)).StepRemaining?.TotalSeconds, 1);
        });
        test("ETA never extrapolates PC read speed to SD write or SD read", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.PcRead, WorkRateKind.TargetWrite, WorkRateKind.TargetRead);
            estimator.Update(Report(steps, 1, 0), At(0));
            var pc = estimator.Update(Report(steps, 1, 400), At(4));
            Near(pc.BytesPerSecond, 100);
            Check(pc.OperationRemaining is null, "PC speed was reused for an unknown SD speed");
            Check(estimator.Update(Report(steps, 2, 0), At(5)).StepRemaining is null, "New phase retained old ETA");
            var write = estimator.Update(Report(steps, 2, 40), At(9));
            Near(write.BytesPerSecond, 10);
            Check(write.OperationRemaining is null, "SD write speed was reused for unknown SD read speed");
            estimator.Update(Report(steps, 3, 0), At(10));
            var read = estimator.Update(Report(steps, 3, 200), At(14));
            Near(read.BytesPerSecond, 50);
            Near(read.OperationRemaining?.TotalSeconds, 16);
        });
        test("ETA includes future transfers only after each rate category has a sample", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetWrite, WorkRateKind.TargetRead, WorkRateKind.TargetWrite, WorkRateKind.TargetRead);
            estimator.Update(Report(steps, 1, 0), At(0));
            estimator.Update(Report(steps, 1, 1000), At(10));
            estimator.Update(Report(steps, 2, 0), At(11));
            estimator.Update(Report(steps, 2, 1000), At(31));
            // Reports are normally frequent: a single 20-second gap intentionally invalidates a rate.
            estimator.Update(Report(steps, 2, 0), At(32));
            estimator.Update(Report(steps, 2, 500), At(42));
            estimator.Update(Report(steps, 2, 1000), At(52));
            estimator.Update(Report(steps, 3, 0), At(53));
            var estimate = estimator.Update(Report(steps, 3, 400), At(57));
            Near(estimate.StepRemaining?.TotalSeconds, 6);
            Near(estimate.OperationRemaining?.TotalSeconds, 26);
        });
        test("ETA adapts when transfer speed falls", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetWrite);
            estimator.Update(Report(steps, 1, 0), At(0));
            var fast = estimator.Update(Report(steps, 1, 500), At(5));
            var slower = estimator.Update(Report(steps, 1, 550), At(10));
            Check(slower.BytesPerSecond < fast.BytesPerSecond, "Speed did not fall");
            Check(slower.StepRemaining > fast.StepRemaining, "Remaining time did not adjust upwards");
        });
        test("ETA hides stale countdown and relearns speed after stalled progress", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetWrite);
            estimator.Update(Report(steps, 1, 0), At(0));
            estimator.Update(Report(steps, 1, 400), At(4));
            var stalled = estimator.Tick(At(19));
            Check(stalled.IsStalled && stalled.StepRemaining is null && stalled.OperationRemaining is null, "Stale countdown remained visible");
            var recovered = estimator.Update(Report(steps, 1, 450), At(20));
            Check(!recovered.IsStalled && recovered.StepRemaining is null, "Recovery reused stale speed");
            Near(estimator.Update(Report(steps, 1, 650), At(24)).BytesPerSecond, 50);
        });
        test("ETA treats mounting and zero-byte work as unknown, not completed", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = new[] { new WorkStep("mount", "Montar", 0, WorkRateKind.None) };
            var progress = new WorkProgress("Montando", "", 0, 0) { Operation = "Restore", Steps = steps, StepNumber = 1 };
            var estimate = estimator.Update(progress, At(30));
            Check(estimate.StepRemaining is null && estimate.OperationRemaining is null && !estimate.IsStalled, "Indeterminate work received an ETA");
            estimate = estimator.Tick(At(100));
            Check(!estimate.IsStalled, "Indeterminate operation falsely treated as a stalled transfer");
        });
        test("ETA phase 100 percent is not operation success", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetRead);
            estimator.Update(Report(steps, 1, 0), At(0));
            var full = estimator.Update(Report(steps, 1, 1000), At(10));
            Check(full.StepRemaining is null && full.OperationRemaining is null, "Phase completion advertised zero total time");
            var done = estimator.Update(Report(steps, 1, 1000) with { IsOperationComplete = true }, At(11));
            Check(done.StepRemaining == TimeSpan.Zero && done.OperationRemaining == TimeSpan.Zero, "Explicit completion not reflected");
        });
        test("ETA reset discards prior operation speeds and confirmation delay", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.PcRead);
            estimator.Update(Report(steps, 1, 0), At(0));
            estimator.Update(Report(steps, 1, 400), At(4));
            estimator.Reset();
            Check(estimator.Update(Report(steps, 1, 0), At(0)).StepRemaining is null, "Reset retained speed");
            Near(estimator.Update(Report(steps, 1, 40), At(4)).BytesPerSecond, 10);
        });
        test("ETA automatically clears rates when operation identity changes", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.PcRead);
            estimator.Update(Report(steps, 1, 0), At(0));
            estimator.Update(Report(steps, 1, 400), At(4));
            var changed = Report(steps, 1, 0) with { Operation = "Another operation" };
            Check(estimator.Update(changed, At(0)).StepRemaining is null, "New operation retained old speed");
            Near(estimator.Update(changed with { Completed = 40 }, At(4)).BytesPerSecond, 10);
        });
        test("ETA rejects incompatible progress units and counter resets", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetWrite);
            estimator.Update(Report(steps, 1, 0), At(0));
            estimator.Update(Report(steps, 1, 400), At(4));
            var counterReset = estimator.Update(Report(steps, 1, 0), At(5));
            Check(counterReset.StepRemaining is null, "Counter reset retained old sample");
            var wrongUnits = estimator.Update(Report(steps, 1, 1) with { Total = 1 }, At(9));
            Check(wrongUnits.StepRemaining is null && wrongUnits.OperationRemaining is null, "Unit-less completion was treated as bytes");
        });
        test("ETA ignores old phase reports and tolerates backward clock samples", () =>
        {
            var estimator = new ProgressEstimator();
            var steps = Plan(WorkRateKind.TargetWrite, WorkRateKind.TargetRead);
            estimator.Update(Report(steps, 2, 0), At(10));
            estimator.Update(Report(steps, 2, 400), At(14));
            Near(estimator.Update(Report(steps, 1, 0), At(14)).StepRemaining?.TotalSeconds, 6);
            Near(estimator.Tick(At(12)).StepRemaining?.TotalSeconds, 6);
        });
    }

    private static WorkStep[] Plan(params WorkRateKind[] kinds) => kinds.Select((kind, index) =>
        new WorkStep("step-" + index, "Paso " + index, 1000, kind)).ToArray();
    private static WorkProgress Report(IReadOnlyList<WorkStep> steps, int number, long completed) =>
        new("Test", "", completed, steps[number - 1].Bytes)
        { Operation = "Operation", Steps = steps, StepNumber = number };
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double? value, double expected)
    {
        Check(value is double actual && double.IsFinite(actual) && Math.Abs(actual - expected) < 0.01,
            $"Expected approximately {expected}, got {value}");
    }
}
