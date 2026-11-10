namespace JellySend.Discord.Gateway;

/// <summary>Sliding-window limiter: at most <c>count</c> acquisitions in any <c>per</c> interval.</summary>
internal sealed class GatewayRateLimiter(int count, TimeSpan per, TimeProvider time)
{
    private readonly Queue<long> _sent = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WaitAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = time.GetTimestamp();
                while (_sent.Count > 0 && time.GetElapsedTime(_sent.Peek(), now) >= per)
                {
                    _sent.Dequeue();
                }

                if (_sent.Count < count)
                {
                    _sent.Enqueue(now);
                    return;
                }

                await Task.Delay(per - time.GetElapsedTime(_sent.Peek(), now), time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
