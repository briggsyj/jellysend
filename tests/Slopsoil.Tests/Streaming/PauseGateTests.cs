using Slopsoil.Streaming.Contracts;

namespace Slopsoil.Tests.Streaming;

public sealed class PauseGateTests
{
    [Fact]
    public void Starts_open_closes_and_reopens()
    {
        var gate = new PauseGate();
        Assert.True(gate.IsOpen);
        Assert.True(gate.WaitOpen(TimeSpan.Zero, TestContext.Current.CancellationToken));
        gate.Close();
        gate.Close();
        Assert.False(gate.IsOpen);
        Assert.False(gate.WaitOpen(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken));
        gate.Open();
        gate.Open();
        Assert.True(gate.WaitOpen(TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Wait_is_released_by_open_and_by_cancellation()
    {
        var gate = new PauseGate();
        gate.Close();
        var waiter = Task.Run(() => gate.WaitOpen(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        gate.Open();
        Assert.True(await waiter);

        gate.Close();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.False(gate.WaitOpen(TimeSpan.FromSeconds(10), cts.Token));
    }
}
