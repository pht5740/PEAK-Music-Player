using System;

namespace PeakMusic.Core
{
    /// <summary>
    /// 内核（PeakMusic.dll）往接口层挂日志/状态回调的地方。
    ///
    /// 为什么要有这么个东西：PeakMusic.Core 是**被引用的下层**，它不能反过来引用插件本体，
    /// 但又需要把「这一档拿到了什么规格」「无损被降级了」这类信息写进 BepInEx 日志。
    /// 用一组可空静态委托做单向门，Core 只管调用，谁挂谁负责实现。
    /// </summary>
    public static class PluginHooks
    {
        /// <summary>普通信息（取流规格、档位降级原因）。</summary>
        public static Action<string> LogInfo;

        /// <summary>警告（该有的能力没生效，但功能仍可用）。</summary>
        public static Action<string> LogWarn;
    }
}
