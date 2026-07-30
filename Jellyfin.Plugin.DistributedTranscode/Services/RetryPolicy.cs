namespace Jellyfin.Plugin.DistributedTranscode.Services;

internal static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts,
        TimeSpan baseDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException("The worker request timed out.");
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("Retry policy failed without an exception.");
    }
}
