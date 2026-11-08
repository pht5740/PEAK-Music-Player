using System;
using System.Threading;
using System.Threading.Tasks;

namespace PeakMusic.Core
{
    /// <summary>
    /// 请求节流闸门。文档阶段 2.4 的三条铁律都在这里：
    ///   1) 串行——同一时刻最多一个请求在飞；
    ///   2) 间隔 200–500ms 随机；
    ///   3) 调用方自己保证"不预取"（不要批量取歌单里每首歌的播放地址）。
    /// 这条是防封号的核心，任何新接口都必须从这里走，不许直接 new HttpClient 就发。
    /// </summary>
    public sealed class Throttle : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Random _rng = new Random(unchecked(Environment.TickCount * 31));
        private readonly int _minDelayMs;
        private readonly int _maxDelayMs;
        private bool _disposed;

        public Throttle(int minDelayMs = 200, int maxDelayMs = 500)
        {
            if (minDelayMs < 0) minDelayMs = 0;
            if (maxDelayMs < minDelayMs) maxDelayMs = minDelayMs;
            _minDelayMs = minDelayMs;
            _maxDelayMs = maxDelayMs;
        }

        /// <summary>已经串行跑过多少次（调试用）。</summary>
        public int Completed { get; private set; }

        public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 随机抖动，避免固定节奏被识别成脚本
                int delay;
                lock (_rng) delay = _rng.Next(_minDelayMs, _maxDelayMs + 1);
                if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);

                var result = await work().ConfigureAwait(false);
                Completed++;
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task RunAsync(Func<Task> work, CancellationToken ct = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            return RunAsync(async () => { await work().ConfigureAwait(false); return true; }, ct);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
        }
    }
}
