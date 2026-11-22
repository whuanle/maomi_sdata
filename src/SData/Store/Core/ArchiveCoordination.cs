namespace SData.Store.Core;

/// <summary>冷热数据源交接锁：查询可并发，切换时等待已有查询完成；网络归档不持有此锁.</summary>
internal sealed class ArchiveCoordination
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _readersLock = new(1, 1);
    private readonly SemaphoreSlim _empty = new(1, 1);
    private int _readers;

    public async Task<IDisposable> ReadAsync(CancellationToken token)
    {
        await _turnstile.WaitAsync(token);
        _turnstile.Release();
        await _readersLock.WaitAsync(token);
        try
        {
            if (_readers == 0)
            {
                await _empty.WaitAsync(token);
            }

            _readers++;
        }
        finally
        {
            _readersLock.Release();
        }

        return new Lease(() =>
        {
            _readersLock.Wait();
            try
            {
                if (--_readers == 0)
                {
                    _empty.Release();
                }
            }
            finally
            {
                _readersLock.Release();
            }
        });
    }

    public async Task<IDisposable> WriteAsync(CancellationToken token)
    {
        await _turnstile.WaitAsync(token);
        try
        {
            await _empty.WaitAsync(token);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }

        return new Lease(() =>
        {
            _empty.Release();
            _turnstile.Release();
        });
    }

    internal sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
