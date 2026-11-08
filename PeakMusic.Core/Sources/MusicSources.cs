using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeakMusic.Core.Netease;

namespace PeakMusic.Core.Sources
{
    /// <summary>
    /// 多音源统一入口。面板只认这一个类，不直接碰各个平台的 Client ——
    /// 加新平台（酷狗、酷我…）时只要在这里补一个分支，UI 一行都不用改。
    ///
    /// <para>所有请求共用同一个 <see cref="Throttle"/>，所以跨平台也是串行的，
    /// 不会出现"同时打两家接口"这种更容易被风控识别的行为。</para>
    /// </summary>
    public sealed class MusicSources : IDisposable
    {
        private readonly Throttle _throttle;

        public NeteaseClient Netease { get; private set; }
        public QqClient QQ { get; private set; }

        public MusicSources()
        {
            _throttle = new Throttle();
            Netease = new NeteaseClient(_throttle);
            QQ = new QqClient(_throttle);
        }

        /// <summary>当前音源平台的登录态文案，直接放到面板页脚。</summary>
        public string LoginLine(Platform p)
        {
            switch (p)
            {
                case Platform.QQ:
                    return QQ.HasLogin ? "QQ音乐已登录" : "QQ音乐未登录（付费曲会取不到）";
                default:
                    return Netease.HasLogin ? "网易云已登录" : "网易云未登录（会员曲会 -110）";
            }
        }

        public Task<List<SongInfo>> SearchAsync(Platform platform, string keyword,
                                                int limit, int offset,
                                                CancellationToken ct = default(CancellationToken))
        {
            switch (platform)
            {
                case Platform.QQ:
                    return QQ.SearchAsync(keyword, limit, offset, ct);
                default:
                    return Netease.SearchAsync(keyword, limit, offset, ct);
            }
        }

        /// <summary>
        /// 取播放地址。按 song.Platform 分发 —— 不看"当前选中的平台"，
        /// 因为列表里可能还留着切平台之前搜到的歌，点它必须回到它自己的源去取流。
        /// </summary>
        public Task<SongUrlResult> GetUrlAsync(SongInfo song,
                                               CancellationToken ct = default(CancellationToken))
        {
            if (song == null)
                return Task.FromResult(new SongUrlResult { Code = -1, Message = "song 为空" });

            switch (song.Platform)
            {
                case Platform.QQ:
                    return QQ.GetSongUrlAsync(song, ct);
                default:
                    return Netease.GetSongUrlAsync(song.Id, "exhigh", ct);
            }
        }

        public Task<LyricResult> GetLyricAsync(SongInfo song,
                                               CancellationToken ct = default(CancellationToken))
        {
            if (song == null)
                return Task.FromResult(new LyricResult());

            switch (song.Platform)
            {
                case Platform.QQ:
                    return QQ.GetLyricAsync(song, ct);
                default:
                    return Netease.GetLyricAsync(song.Id, ct);
            }
        }

        public void Dispose()
        {
            try { if (Netease != null) Netease.Dispose(); } catch { }
            try { if (QQ != null) QQ.Dispose(); } catch { }
            try { _throttle.Dispose(); } catch { }
        }
    }
}
