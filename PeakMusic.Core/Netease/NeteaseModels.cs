using System;
using System.Collections.Generic;
using PeakMusic.Core.Sources;

namespace PeakMusic.Core.Netease
{
    /// <summary>搜索/歌单里的一首歌。字段名对齐网易的 ar / al / dt，省得每次都要查文档。</summary>
    public sealed class SongInfo
    {
        public long Id;
        public string Name = "";
        public string Artists = "";
        public string Album = "";
        public long DurationMs;
        public string AlbumPicUrl = "";

        /// <summary>这首歌来自哪个平台。默认网易云，所以老代码一行都不用改。</summary>
        public Platform Platform = Platform.Netease;

        /// <summary>
        /// 平台内部的字符串 ID。网易用 long <see cref="Id"/> 就够了，QQ 的 songmid 是字符串
        /// （如 <c>000rh0dE2TyUic</c>），只能放这里。取流/取词优先用它，空则退回 Id。
        /// </summary>
        public string SourceId = "";

        /// <summary>跨平台的唯一键，用于列表选中态与播放态比对。QQ 与网易的同名不同曲不会串。</summary>
        public string Key
        {
            get { return Platform + ":" + (string.IsNullOrEmpty(SourceId) ? Id.ToString() : SourceId); }
        }

        /// <summary>网易的 fee：0 免费 / 1 需会员 / 4 购买专辑 / 8 低音质免费高音质付费。</summary>
        public int Fee;

        /// <summary>privileges.st：&lt; 0 表示无版权，不要拿去请求播放地址。</summary>
        public int PrivilegeSt;

        public string DurationText
        {
            get
            {
                var t = TimeSpan.FromMilliseconds(DurationMs);
                return t.TotalHours >= 1
                    ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                    : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
            }
        }

        public override string ToString()
        {
            return Name + " - " + Artists + "  [" + Album + "] " + DurationText + " (id=" + Id + ", fee=" + Fee + ")";
        }
    }

    /// <summary>
    /// 播放地址结果。三种情况必须区分开，不能都当成"拿到 URL"：
    ///   1) Url 有值                → 可以播；
    ///   2) Url 为 null + FreeTrial → 只给 30 秒试听，必须丢弃并提示；
    ///   3) Url 为 null + Code     → -110 无权限（没登录/无版权/需会员），-460 风控。
    /// </summary>
    public sealed class SongUrlResult
    {
        public long Id;
        public string Url;
        public int Br;
        public long Size;
        public int Code;
        public int Fee;
        public string Level = "";
        public bool HasFreeTrial;
        public string Message;

        public bool Ok
        {
            get { return !string.IsNullOrEmpty(Url) && Code == 200; }
        }

        public string Explain()
        {
            if (Ok) return "OK " + Br / 1000 + "kbps " + (Size / 1024) + "KB";
            if (HasFreeTrial) return "只给了试听片段（freeTrialInfo 非空），必须丢弃";
            if (Code == -110) return "-110 无权限：没登录 / 无版权 / 需会员";
            if (Code == -460) return "-460 触发风控：请求过于频繁";
            return "url 为空 code=" + Code + (string.IsNullOrEmpty(Message) ? "" : " msg=" + Message);
        }
    }

    public sealed class LyricResult
    {
        public long Id;
        /// <summary>原始 LRC 文本（网易老格式），交给 LrcParser 解析。</summary>
        public string Lrc = "";
        /// <summary>翻译 LRC，可能为空。</summary>
        public string Translated = "";

        public bool HasLyric
        {
            get { return !string.IsNullOrEmpty(Lrc); }
        }
    }

    public sealed class AccountInfo
    {
        public long UserId;
        public string Nickname = "";
        public string AvatarUrl = "";

        public bool LoggedIn
        {
            get { return UserId > 0; }
        }
    }

    public enum QrState
    {
        Expired = 800,
        Waiting = 801,
        Scanned = 802,
        Success = 803,

        /// <summary>
        /// 8821：安全环境风险。扫码后服务端拒绝完成登录，跟二维码本身无关，
        /// 换码重扫也没用——要换登录方式（手机号验证码）或直接从浏览器搬 cookie。
        /// </summary>
        RiskBlocked = 8821,

        Unknown = -1
    }

    public sealed class QrLoginResult
    {
        public QrState State = QrState.Unknown;
        /// <summary>服务端原始 code（不等于 State，8821 之类会原样留着）。</summary>
        public int Code = -1;
        public string RawMessage = "";
        public string CookieText = "";

        public string Explain()
        {
            switch (State)
            {
                case QrState.Expired: return "二维码已过期，重新生成";
                case QrState.Waiting: return "等待手机扫码";
                case QrState.Scanned: return "已扫码，等手机上点确认";
                case QrState.Success: return "登录成功，cookie 已保存";
                case QrState.RiskBlocked:
                    return "8821 安全环境风险，网易拒绝这次登录（" + RawMessage + "）。换码重扫没用，得换登录方式";
                default: return "未知状态 code=" + Code + " " + RawMessage;
            }
        }
    }
}
