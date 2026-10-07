using DetroitAudio.Desktop;
using Xunit;

namespace DetroitAudio.Desktop.Tests;

public sealed class OperationSessionTests
{
    [Fact]
    public void CancellationIsReportedUntilWorkSettlesAndLateProgressIsIgnored()
    {
        using var session = new OperationSession("Exporting", OperationKind.Export);
        session.Report("Copying", 2, 10, "Clip");
        Assert.Equal(20, session.Progress);
        session.Cancel();
        Assert.True(session.Token.IsCancellationRequested);
        Assert.Equal(OperationState.Cancelling, session.State);
        Assert.False(session.Completion.IsCompleted);
        session.Report("Complete", 10, 10);
        Assert.Equal("Cancelling…", session.Phase);
        session.Settle();
        Assert.Equal(OperationState.Cancelled, session.State);
        Assert.True(session.Completion.IsCompleted);
        Assert.False(session.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void AcceptedExecutionIsIndependentOfBrowserCancellationButStillCancelable()
    {
        using var browser = new CancellationTokenSource();
        using var session = new OperationSession("Exporting", OperationKind.Export, browser.Token);
        session.BeginExecution(); browser.Cancel();
        Assert.False(session.Token.IsCancellationRequested);
        session.Cancel(); Assert.True(session.Token.IsCancellationRequested);
        session.Settle();
    }

    [Fact]
    public void FinishedTaskCannotBeOverwrittenByQueuedProgressOrCancel()
    {
        using var session = new OperationSession("Opening", OperationKind.Opening);
        session.Finish(OperationState.Completed, "Ready"); session.Report("Opening", 0, 1); session.Cancel();
        Assert.Equal("Ready", session.Phase); Assert.Equal(100, session.Progress);
        session.Settle(); Assert.True(session.Completion.IsCompleted);
    }
}
