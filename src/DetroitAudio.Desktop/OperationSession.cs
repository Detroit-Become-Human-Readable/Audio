using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DetroitAudio.Desktop;

public enum OperationKind { Opening, Export }
public enum OperationState { Preparing, Running, Cancelling, Completed, Cancelled, Failed }

public sealed partial class OperationSession : ObservableObject, IDisposable
{
    private CancellationTokenSource cancellation;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public OperationKind Kind { get; }
    public CancellationToken Token { get; private set; }
    public Task Completion => completion.Task;
    [ObservableProperty] private OperationState state = OperationState.Preparing;
    [ObservableProperty] private string phase = "Preparing";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string counts = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool indeterminate = true;
    [ObservableProperty] private string outputDirectory = "";
    public bool CanCancel => !disposed && State is OperationState.Preparing or OperationState.Running;
    public bool IsFinished => State is OperationState.Completed or OperationState.Cancelled or OperationState.Failed;

    public OperationSession(string title, OperationKind kind, CancellationToken parent = default)
    {
        Title = title; Kind = kind;
        cancellation = parent.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(parent) : new();
        Token = cancellation.Token;
    }

    public void Report(string phase, int completed = 0, int total = 0, string? detail = null)
    {
        if (disposed || IsFinished || Token.IsCancellationRequested || State == OperationState.Cancelling) return;
        State = OperationState.Running; Phase = phase; Detail = detail ?? "";
        Counts = total > 0 ? $"{completed:N0} / {total:N0}" : "";
        Indeterminate = total <= 0; Progress = total > 0 ? Math.Clamp(completed * 100d / total, 0, 100) : 0;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (!CanCancel) return;
        State = OperationState.Cancelling; Phase = "Cancelling…"; Detail = "";
        Indeterminate = true; cancellation.Cancel();
    }

    public void Finish(OperationState state, string summary)
    {
        if (IsFinished) return;
        State = state; Phase = summary; Detail = ""; Counts = ""; Indeterminate = false;
        if (state == OperationState.Completed) Progress = 100;
    }
    public void BeginExecution()
    {
        Token.ThrowIfCancellationRequested();
        cancellation.Dispose(); cancellation = new(); Token = cancellation.Token;
        State = OperationState.Running;
    }

    public void Settle()
    {
        if (!IsFinished) Finish(Token.IsCancellationRequested ? OperationState.Cancelled : OperationState.Failed,
            Token.IsCancellationRequested ? "Cancelled" : "Task stopped");
        completion.TrySetResult();
        Dispose();
    }

    partial void OnStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(IsFinished)); CancelCommand.NotifyCanExecuteChanged();
    }
    public void Dispose() { if (disposed) return; disposed = true; cancellation.Dispose(); CancelCommand.NotifyCanExecuteChanged(); }
}
