using Microsoft.Data.Sqlite;

namespace Scope.Core.Utilities
{
    /// <summary>
    /// Tuning for <see cref="SqliteRetry"/>. Backoff is exponential with FULL jitter:
    /// the delay before retry <c>attempt</c> (0-based) is a uniform random value in
    /// <c>[0, min(CapMs, BaseMs * 2^attempt)]</c>. Full jitter de-synchronizes competing
    /// writers so they do not retry in lockstep and re-collide.
    /// </summary>
    /// <param name="MaxAttempts">
    /// Total number of times the operation is invoked (the initial try plus retries). Must be at
    /// least 1. After the last attempt fails on a transient lock, the last exception is rethrown.
    /// </param>
    /// <param name="BaseMs">Base backoff in milliseconds; the ceiling doubles each attempt from here.</param>
    /// <param name="CapMs">Maximum backoff ceiling in milliseconds (the jitter window is clamped to this).</param>
    public sealed record SqliteRetryOptions(
        int MaxAttempts = SqliteRetry.DefaultMaxAttempts,
        int BaseMs = SqliteRetry.DefaultBaseMs,
        int CapMs = SqliteRetry.DefaultCapMs)
    {
        /// <summary>The default options: 6 attempts, 25ms base, 500ms cap, full jitter.</summary>
        public static readonly SqliteRetryOptions Default = new();
    }

    /// <summary>
    /// Application-level retry primitive for SQLite operations that fail on a TRANSIENT lock.
    /// Sits on top of SQLite's passive <c>busy_timeout</c>: when the busy wait is exhausted and the
    /// driver surfaces <c>SQLITE_BUSY</c> (5) or <c>SQLITE_LOCKED</c> (6), this re-invokes the
    /// operation with jittered exponential backoff. Any other exception, including any other
    /// <see cref="SqliteException"/> error code, is propagated IMMEDIATELY (those are not transient
    /// and a retry would only mask a real fault). When the attempt budget is exhausted the LAST
    /// busy/locked exception is rethrown so the caller decides what to do; this primitive never
    /// swallows and never logs (callers own logging).
    /// </summary>
    public static class SqliteRetry
    {
        /// <summary>Default total attempts (initial try plus retries).</summary>
        public const int DefaultMaxAttempts = 6;

        /// <summary>Default base backoff in milliseconds (the ceiling doubles from here each attempt).</summary>
        public const int DefaultBaseMs = 25;

        /// <summary>Default backoff ceiling in milliseconds (the jitter window is clamped to this).</summary>
        public const int DefaultCapMs = 500;

        /// <summary>SQLite primary result code for a database-level lock contention (SQLITE_BUSY).</summary>
        public const int SqliteBusy = 5;

        /// <summary>SQLite primary result code for a table-level lock contention (SQLITE_LOCKED).</summary>
        public const int SqliteLocked = 6;

        private static bool IsTransientLock(SqliteException ex)
            => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;

        /// <summary>
        /// Computes the un-jittered backoff CEILING in milliseconds for a 0-based attempt index:
        /// <c>min(capMs, baseMs * 2^attempt)</c>, saturating instead of overflowing for large attempts.
        /// The actual delay is a uniform random value in <c>[0, ceiling]</c> (full jitter).
        /// </summary>
        public static int BackoffCeilingMs(int attempt, int baseMs, int capMs)
        {
            if (attempt < 0)
            {
                attempt = 0;
            }

            // Saturate: once the shift would exceed the cap (or overflow), clamp to the cap.
            // 2^attempt overflows int well before attempt reaches 31; guard against that.
            if (attempt >= 31)
            {
                return capMs;
            }

            long scaled = (long)baseMs << attempt;
            return scaled >= capMs ? capMs : (int)scaled;
        }

        private static TimeSpan DefaultDelay(int attempt, SqliteRetryOptions options)
        {
            int ceiling = BackoffCeilingMs(attempt, options.BaseMs, options.CapMs);
            // Full jitter: uniform in [0, ceiling]. Upper bound is exclusive in Random.Next, so use
            // ceiling + 1 to make the ceiling itself reachable.
            int ms = ceiling <= 0 ? 0 : Random.Shared.Next(0, ceiling + 1);
            return TimeSpan.FromMilliseconds(ms);
        }

        // ----- Synchronous overloads --------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="operation"/>, retrying on a transient SQLite lock with jittered
        /// exponential backoff. Non-transient exceptions propagate immediately; after the attempt
        /// budget is exhausted the last busy/locked exception is rethrown.
        /// </summary>
        /// <param name="operation">The operation to execute (and retry).</param>
        /// <param name="options">Retry tuning; defaults to <see cref="SqliteRetryOptions.Default"/>.</param>
        /// <param name="delay">
        /// Optional backoff provider mapping a 0-based attempt index to a delay. Injectable so unit
        /// tests can run with zero real sleeping; defaults to the real jittered backoff.
        /// </param>
        public static T Execute<T>(
            Func<T> operation,
            SqliteRetryOptions? options = null,
            Func<int, TimeSpan>? delay = null)
        {
            ArgumentNullException.ThrowIfNull(operation);
            options ??= SqliteRetryOptions.Default;
            int maxAttempts = options.MaxAttempts < 1 ? 1 : options.MaxAttempts;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (SqliteException ex) when (IsTransientLock(ex) && attempt < maxAttempts - 1)
                {
                    TimeSpan wait = (delay ?? (a => DefaultDelay(a, options)))(attempt);
                    if (wait > TimeSpan.Zero)
                    {
                        Thread.Sleep(wait);
                    }
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="operation"/> (no return value), retrying on a transient SQLite lock.
        /// See <see cref="Execute{T}(Func{T}, SqliteRetryOptions?, Func{int, TimeSpan}?)"/>.
        /// </summary>
        public static void Execute(
            Action operation,
            SqliteRetryOptions? options = null,
            Func<int, TimeSpan>? delay = null)
        {
            ArgumentNullException.ThrowIfNull(operation);
            Execute<bool>(
                () =>
                {
                    operation();
                    return true;
                },
                options,
                delay);
        }

        // ----- Asynchronous overloads -------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="operation"/> asynchronously, retrying on a transient SQLite lock with
        /// jittered exponential backoff (awaited via <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).
        /// Non-transient exceptions propagate immediately; after the attempt budget is exhausted the
        /// last busy/locked exception is rethrown. The token is honored both around the operation and
        /// during the backoff wait.
        /// </summary>
        /// <param name="operation">The async operation to execute (and retry); receives the token.</param>
        /// <param name="options">Retry tuning; defaults to <see cref="SqliteRetryOptions.Default"/>.</param>
        /// <param name="delay">
        /// Optional backoff provider mapping a 0-based attempt index to a delay. Injectable so unit
        /// tests can run with zero real sleeping; defaults to the real jittered backoff.
        /// </param>
        /// <param name="ct">Cancellation token observed around each attempt and each backoff wait.</param>
        public static async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            SqliteRetryOptions? options = null,
            Func<int, TimeSpan>? delay = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            options ??= SqliteRetryOptions.Default;
            int maxAttempts = options.MaxAttempts < 1 ? 1 : options.MaxAttempts;

            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await operation(ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsTransientLock(ex) && attempt < maxAttempts - 1)
                {
                    TimeSpan wait = (delay ?? (a => DefaultDelay(a, options)))(attempt);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="operation"/> asynchronously (no return value), retrying on a transient
        /// SQLite lock. See
        /// <see cref="ExecuteAsync{T}(Func{CancellationToken, Task{T}}, SqliteRetryOptions?, Func{int, TimeSpan}?, CancellationToken)"/>.
        /// </summary>
        public static Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            SqliteRetryOptions? options = null,
            Func<int, TimeSpan>? delay = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            return ExecuteAsync<bool>(
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return true;
                },
                options,
                delay,
                ct);
        }
    }
}
