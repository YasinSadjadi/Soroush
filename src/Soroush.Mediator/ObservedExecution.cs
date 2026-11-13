using System.Diagnostics;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

internal static class ObservedExecution
{
    internal static string Kind(Type type)
    {
        var contracts = type.GetInterfaces();
        if (contracts.Any(t => t == typeof(ICommand) || t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ICommand<>))) return "command";
        if (contracts.Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQuery<>))) return "query";
        return "request";
    }

    internal static async Task<T> Send<T>(object request, IMediatorObserver observer,
        IMediatorOutcomeClassifier? classifier, Func<Task<T>> send, CancellationToken token)
    {
        using var operation = MediatorObservation.BeginOperation(observer, request.GetType(), Kind(request.GetType()), classifier);
        using var active = operation?.Activate();
        try
        {
            var result = await send().ConfigureAwait(false);
            operation?.RecordResult(result);
            return result;
        }
        catch (Exception ex) { operation?.RecordException(ex, token); throw; }
        finally { operation?.Complete(); }
    }

    internal static async Task Send(object request, string kind, IMediatorObserver observer, Func<Task> send, CancellationToken token)
    {
        using var operation = MediatorObservation.BeginOperation(observer, request.GetType(), kind);
        using var active = operation?.Activate();
        try { await send().ConfigureAwait(false); operation?.RecordResult(null); }
        catch (Exception ex) { operation?.RecordException(ex, token); throw; }
        finally { operation?.Complete(); }
    }

    internal static IAsyncEnumerable<T> Stream<T>(object request, IMediatorObserver observer,
        IMediatorOutcomeClassifier? classifier, Func<IAsyncEnumerable<T>> create, CancellationToken token)
    {
        try { return new ObservedEnumerable<T>(create(), request.GetType(), observer, classifier, token); }
        catch (Exception ex)
        {
            using var operation = MediatorObservation.BeginOperation(observer, request.GetType(), "stream", classifier);
            using var active = operation?.Activate();
            operation?.RecordException(ex, token, "stream_create");
            operation?.Complete();
            throw;
        }
    }

    private sealed class ObservedEnumerable<T>(IAsyncEnumerable<T> source, Type requestType,
        IMediatorObserver observer, IMediatorOutcomeClassifier? classifier, CancellationToken dispatchToken) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            try
            {
                return new ObservedEnumerator<T>(source.GetAsyncEnumerator(cancellationToken),
                    requestType, observer, classifier, dispatchToken, cancellationToken);
            }
            catch (Exception ex)
            {
                using var operation = MediatorObservation.BeginOperation(observer, requestType, "stream", classifier);
                using var active = operation?.Activate();
                operation?.RecordException(ex, dispatchToken.IsCancellationRequested ? dispatchToken : cancellationToken, "enumerator_create");
                operation?.Complete();
                throw;
            }
        }
    }

    private sealed class ObservedEnumerator<T>(IAsyncEnumerator<T> inner, Type requestType,
        IMediatorObserver observer, IMediatorOutcomeClassifier? classifier,
        CancellationToken dispatchToken, CancellationToken enumerationToken) : IAsyncEnumerator<T>
    {
        private MediatorOperation? operation;
        private bool started;
        private bool ended;
        private bool disposed;
        public T Current { get; private set; } = default!;
        private CancellationToken Cancellation => dispatchToken.IsCancellationRequested ? dispatchToken : enumerationToken;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (disposed) return false;
            if (!started)
            {
                started = true;
                operation = MediatorObservation.BeginOperation(observer, requestType, "stream", classifier);
            }
            try
            {
                bool more;
                var tick = Stopwatch.GetTimestamp();
                try
                {
                    using var active = operation?.Activate();
                    more = await inner.MoveNextAsync().ConfigureAwait(false);
                    if (more)
                    {
                        Current = inner.Current;
                        operation?.RecordItem(Current);
                    }
                }
                finally { operation?.AddProducerTime(Elapsed(tick)); }
                if (!more)
                {
                    ended = true;
                    await DisposeAsync().ConfigureAwait(false);
                }
                return more;
            }
            catch (Exception ex)
            {
                if (!disposed)
                {
                    operation?.RecordException(ex, Cancellation);
                    await DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            var tick = Stopwatch.GetTimestamp();
            using var active = operation?.Activate();
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                operation ??= MediatorObservation.BeginOperation(observer, requestType, "stream", classifier);
                operation?.RecordException(ex, Cancellation, "Dispose");
                throw;
            }
            finally
            {
                operation?.AddProducerTime(Elapsed(tick));
                operation?.Complete(abandoned: !ended);
                operation?.Dispose();
            }
        }

        private static TimeSpan Elapsed(long tick) =>
            TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - tick) / (double)Stopwatch.Frequency);
    }
}
