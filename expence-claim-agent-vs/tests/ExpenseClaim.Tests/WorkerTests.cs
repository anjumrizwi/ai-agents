using ExpenseClaim.Worker;
using Microsoft.Extensions.Logging;
using WorkerService = ExpenseClaim.Worker.Worker;

namespace ExpenseClaim.Tests;

public class WorkerTests
{
    [Fact]
    public async Task ExecuteAsync_WhenInformationLoggingEnabled_WritesLog()
    {
        var logger = new TestLogger(isEnabled: true);
        var worker = new TestableWorker(logger);
        using var cts = new CancellationTokenSource();
        var executionTask = worker.RunAsync(cts.Token);

        await Task.Delay(20);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionTask);

        Assert.True(logger.LogEntries.Count > 0);
        Assert.Contains(logger.LogEntries, message => message.Contains("Worker running at:"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenInformationLoggingDisabled_DoesNotWriteLog()
    {
        var logger = new TestLogger(isEnabled: false);
        var worker = new TestableWorker(logger);
        using var cts = new CancellationTokenSource();
        var executionTask = worker.RunAsync(cts.Token);

        await Task.Delay(20);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionTask);

        Assert.Empty(logger.LogEntries);
    }

    private sealed class TestableWorker(ILogger<WorkerService> logger) : WorkerService(logger)
    {
        public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
    }

    private sealed class TestLogger(bool isEnabled) : ILogger<WorkerService>
    {
        public List<string> LogEntries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => isEnabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogEntries.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
