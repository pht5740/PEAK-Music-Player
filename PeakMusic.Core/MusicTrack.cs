using System;

namespace PeakMusic.Core
{
    /// <summary>
    /// 一首歌的最小信息。字段名刻意保持和网易云响应里的 key 对齐，
    /// 反序列化时不用额外映射（阶段 2 定稿后再补完整模型）。
    /// </summary>
    public sealed class MusicTrack
    {
        public long id { get; set; }
        public string name { get; set; }
        public string ar { get; set; }          // 歌手，多个用 / 连接
        public string al { get; set; }          // 专辑名
        public string picUrl { get; set; }      // 封面
        public int dt { get; set; }             // 时长 ms
        public long fee { get; set; }           // 0 免费 / 1 会员 / 4 付费 / 8 低音质免费
        public int st { get; set; }             // privileges.st，< 0 表示无版权

        public TimeSpan Duration
        {
            get { return TimeSpan.FromMilliseconds(Math.Max(0, dt)); }
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(ar)
                ? name
                : name + " - " + ar;
        }
    }

    /// <summary>
    /// 播放地址查询结果。url 为空、freeTrialInfo 非空、code == -460 这三个分支
    /// 是阶段 2 明确要求必须分别处理、分别测过的。
    /// </summary>
    public sealed class PlayUrlResult
    {
        public string Url { get; set; }
        public int Bitrate { get; set; }
        public string Type { get; set; }
        public int Code { get; set; }

        /// <summary>非空 = 只给了试听片段，必须丢弃并提示，不能静默播放。</summary>
        public bool IsTrialOnly { get; set; }

        public bool IsPlayable
        {
            get { return Code == 200 && !IsTrialOnly && !string.IsNullOrEmpty(Url); }
        }
    }
}
