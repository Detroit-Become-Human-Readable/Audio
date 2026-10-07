using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DetroitAudio.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private OperationSession? currentTask;
    private TaskWindow? taskWindow;
    private OperationSession StartTask(string title, OperationKind kind, CancellationToken parent = default)
    {
        taskWindow?.CloseOwned(); taskWindow = null;
        var session = new OperationSession(title, kind, parent); CurrentTask = session;
        session.PropertyChanged += (_, _) =>
        {
            if (!disposed && ReferenceEquals(CurrentTask, session))
            {
                CancelCommand.NotifyCanExecuteChanged();
                if (session.State == OperationState.Cancelling) Status = session.Phase;
            }
        };
        ShowTask();
        return session;
    }
    [RelayCommand]
    private void ShowTask()
    {
        if (CurrentTask is null || !owner.IsVisible || disposed) return;
        if (taskWindow is null || !ReferenceEquals(taskWindow.Session, CurrentTask)) taskWindow = new TaskWindow(owner, CurrentTask);
        if (!taskWindow.IsVisible) taskWindow.Show();
        taskWindow.Activate();
    }
    private void ReportTask(OperationSession session, string phase, int completed = 0, int total = 0, string? detail = null)
    {
        if (disposed || !ReferenceEquals(CurrentTask, session)) return;
        session.Report(phase, completed, total, detail);
        if (!session.IsFinished && !session.Token.IsCancellationRequested) { Status = phase + (total > 0 ? $"  {completed:N0} / {total:N0}" : ""); Progress = session.Progress; }
    }
    private void FinishTask(OperationSession session, OperationState state, string summary)
    {
        session.Finish(state, summary);
        if (!disposed && ReferenceEquals(CurrentTask, session))
        {
            Status = summary;
            if (session.Kind == OperationKind.Opening && state == OperationState.Completed) { taskWindow?.CloseOwned(); taskWindow = null; }
        }
    }
    public async Task CancelAndWaitAsync()
    {
        var session = CurrentTask;
        Cancel();
        if (session is not null && !session.Completion.IsCompleted) await session.Completion;
        var pending = activePreviewRequests.Select(request => request.Completion).ToArray();
        await Task.WhenAll(pending);
    }
}
