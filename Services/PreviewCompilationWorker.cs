using System.Collections.Concurrent;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

// Native Spawnable hosts are owned by their creating OS thread. Awaiting a
// WebSocket write must never move the next evaluation or release to another thread.
internal sealed class PreviewCompilationWorker : IAsyncDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly CompiledPreviewSender _sender = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PreviewCompilationWorker()
    {
        new Thread(Run) { IsBackground = true, Name = "LiberaFX preview compiler" }.Start();
    }
    private void Run()
    {
        try { foreach (var action in _work.GetConsumingEnumerable()) action(); }
        finally { _work.Dispose(); _stopped.TrySetResult(); }
    }
    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }
    public Task<CompiledPreviewMessage?> PrepareAsync(PreviewPublication publication, bool force,
        IRenderResourceBindingContext? resources) => InvokeAsync(() => _sender.Prepare(publication, force, resources));
    public Task SentAsync(CompiledPreviewMessage message) => InvokeAsync(() => { _sender.Sent(message); return true; });
    public async ValueTask DisposeAsync()
    {
        try { await InvokeAsync(() => { _sender.Dispose(); return true; }).ConfigureAwait(false); }
        finally
        {
            _work.CompleteAdding();
            await _stopped.Task.ConfigureAwait(false);
        }
    }
}
