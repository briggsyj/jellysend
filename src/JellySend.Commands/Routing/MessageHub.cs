namespace JellySend.Commands.Routing;

/// <summary>
/// Fan-out of incoming messages to one-shot waiters (<c>bot.wait_for("message", check=...)</c>).
/// Waiters observe a message before the router runs it, but never swallow it.
/// </summary>
public sealed class MessageHub(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<(Func<ChatMessage, bool> Check, TaskCompletionSource<ChatMessage> Tcs)> _waiters = [];

    /// <summary>Number of pending waiters (tests use it to answer a prompt only once it listens).</summary>
    internal int WaiterCount
    {
        get
        {
            lock (_waiters)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>Deliver a message to every matching waiter; each waiter completes at most once.</summary>
    public void Publish(ChatMessage message)
    {
        List<TaskCompletionSource<ChatMessage>> matched = [];
        lock (_waiters)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Check(message))
                {
                    matched.Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var tcs in matched)
        {
            tcs.TrySetResult(message);
        }
    }

    /// <summary>Wait for the next message passing <paramref name="check"/>; null on timeout.</summary>
    public async Task<ChatMessage?> WaitForAsync(Func<ChatMessage, bool> check, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = (check, tcs);
        lock (_waiters)
        {
            _waiters.Add(entry);
        }

        try
        {
            return await tcs.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_waiters)
            {
                _waiters.Remove(entry);
            }
        }
    }
}
