namespace Ninmagic.Nx.Core;

/// <summary>Adds a stable operation plan to each progress update without changing transfer behavior.</summary>
internal sealed class OperationProgress
{
    private readonly IProgress<WorkProgress> progress;
    private readonly string operation;
    private readonly IReadOnlyList<WorkStep> steps;

    public OperationProgress(IProgress<WorkProgress> progress, string operation, IEnumerable<WorkStep> steps)
    {
        this.progress = progress;
        this.operation = operation;
        // Published reports retain their own plan snapshot when inventory sizes become known.
        this.steps = Array.AsReadOnly(steps.ToArray());
    }

    public void Report(int stepNumber, string stage, string detail, long completed = 0, long total = 0,
        bool isOperationComplete = false) => Report(stepNumber, new(stage, detail, completed, total)
        { IsOperationComplete = isOperationComplete });

    private void Report(int stepNumber, WorkProgress value)
    {
        if (stepNumber < 1 || stepNumber > steps.Count) throw new ArgumentOutOfRangeException(nameof(stepNumber));
        progress.Report(value with { Operation = operation, Steps = steps, StepNumber = stepNumber });
    }

    public IProgress<WorkProgress> ForStep(int stepNumber) => new StepProgress(this, stepNumber);

    private sealed class StepProgress(OperationProgress owner, int stepNumber) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value) => owner.Report(stepNumber, value);
    }
}
