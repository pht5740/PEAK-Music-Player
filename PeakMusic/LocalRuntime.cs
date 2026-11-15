using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using BepInEx;

namespace PeakMusic
{
    /// <summary>
    /// 本地模式的运行时（linlin 的 MusicListsGUI）的安装与启动。
    ///
    /// <para><b>为什么包里不再直接摊 974 个文件：</b>
    /// r2modman 安装前会清空自己的缓存目录，它的删除实现碰到
    /// <c>_internal\_tcl_data</c> 这种 800 多个时区文件的目录时会失败，
    /// 直接抛 <c>ENOTEMPTY: directory not empty</c> 中断安装（2026-11-15 实测）。</para>
    ///
    /// <para>所以改成随包放一个 <c>MusicListsGUI.runtime.zip</c>，首次启动由这里解压到
    /// <c>BepInEx\PeakMusic\Local\MusicListsGUI\</c> —— 特意放在 plugins <b>外面</b>：
    /// 否则 BepInEx 会去扫描她那几十个 Python 原生 DLL，日志里全是
    /// “Failed to load … Skipping”。解压完由这里启动她的面板，退出时由
    /// <c>Plugin.KillHelperProcesses</c> 一起收掉。</para>
    /// </summary>
    internal static class LocalRuntime
    {
        private const string Tag = "[本地]";
        private const string FolderName = "MusicListsGUI";
        private const string ExeName = "MusicListsGUI.exe";

        /// <summary>我们解压出来的位置（在 plugins 外，避免 BepInEx 扫描）。</summary>
        internal static string InstalledDir
        {
            get
            {
                try { return Path.Combine(Paths.BepInExRootPath, "PeakMusic", "Local", FolderName); }
                catch { return null; }
            }
        }

        internal static string ExePath
        {
            get { return InstalledDir == null ? null : Path.Combine(InstalledDir, ExeName); }
        }

        /// <summary>1.4.6 及以前的结构：直接摊在 plugins\MusicListsGUI（老用户还在用）。</summary>
        internal static string LegacyExe
        {
            get
            {
                try { return Path.Combine(Paths.PluginPath, FolderName, ExeName); }
                catch { return null; }
            }
        }

        /// <summary>随包附带的运行时压缩包。</summary>
        internal static string RuntimeZipPath
        {
            get
            {
                try { return Path.Combine(Paths.PluginPath, "PeakMusic", FolderName + ".runtime.zip"); }
                catch { return null; }
            }
        }

        private static bool _started;

        /// <summary>插件启动时调一次：后台解压（如果还没装）+ 拉起她的面板。不阻塞游戏启动。</summary>
        internal static void Boot()
        {
            if (_started) return;
            _started = true;

            try
            {
                if (Plugin.LocalAutoStart != null && !Plugin.LocalAutoStart.Value)
                {
                    Plugin.Log.LogInfo(Tag + " 自动启动已关闭（[本地] AutoStart=false），需要时请手动运行 MusicListsGUI.exe");
                    return;
                }

                var t = new Thread(Worker) { IsBackground = true, Name = "PeakMusic-LocalBoot" };
                t.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Tag + " 启动本地模式运行时失败：" + e.Message);
            }
        }

        private static void Worker()
        {
            try
            {
                string exe = FindExe();
                if (exe == null)
                {
                    string zip = RuntimeZipPath;
                    if (zip == null || !File.Exists(zip))
                    {
                        Plugin.Log.LogInfo(Tag + " 没找到运行时压缩包，本地模式需要手动安装 MusicListsGUI");
                        return;
                    }

                    string dir = InstalledDir;
                    if (!Extract(zip, dir)) return;
                    exe = Path.Combine(dir, ExeName);
                }
                else if (IsLegacyWithLauncher(exe))
                {
                    // 老结构（plugins\MusicListsGUI）里带的是她的启动器插件，
                    // BepInEx 会自己拉起面板；我们再开一个会抢 8765 端口。
                    Plugin.Log.LogInfo(Tag + " 检测到她的启动器插件，交给它启动，本模组不重复拉起");
                    return;
                }

                StartIfNeeded(exe);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Tag + " 准备本地运行时出错：" + e.Message);
            }
        }

        /// <summary>老的「摊开」结构且带她的启动器插件（BepInEx 会自动运行它）。</summary>
        private static bool IsLegacyWithLauncher(string exe)
        {
            try
            {
                string legacy = LegacyExe;
                if (legacy == null || exe == null) return false;
                if (!string.Equals(exe, legacy, StringComparison.OrdinalIgnoreCase)) return false;
                string dir = Path.GetDirectoryName(exe);
                return File.Exists(Path.Combine(dir, "MusicListsGUILauncher.dll"));
            }
            catch { return false; }
        }

        private static string FindExe()
        {
            try { if (File.Exists(LegacyExe)) return LegacyExe; } catch { }
            try { if (File.Exists(ExePath)) return ExePath; } catch { }
            return null;
        }

        /// <summary>解压到目标目录；用「大小 + 修改时间」做版本标记，包更新过才会重解。</summary>
        private static bool Extract(string zip, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;

            string marker = dir.TrimEnd('\\', '/') + ".installed";
            string want;
            try
            {
                var fi = new FileInfo(zip);
                want = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { want = "?"; }

            try
            {
                if (File.Exists(Path.Combine(dir, ExeName)) && File.Exists(marker)
                    && string.Equals(File.ReadAllText(marker).Trim(), want, StringComparison.Ordinal))
                {
                    Plugin.Log.LogInfo(Tag + " 运行时已就绪：" + dir);
                    return true;
                }

                var sw = Stopwatch.StartNew();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                ZipFile.ExtractToDirectory(zip, dir);
                try { File.WriteAllText(marker, want); } catch { }

                Plugin.Log.LogInfo(Tag + " 运行时解压完成（" + sw.ElapsedMilliseconds + " ms）：" + dir);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Tag + " 解压运行时失败：" + e.Message
                                      + "（本地模式会显示「未检测到」，其余功能不受影响）");
                return false;
            }
        }

        private static void StartIfNeeded(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            try
            {
                // 已经有一个在跑（比如玩家自己手动开的，或她自己的启动器开的）就不再开第二个，
                // 两个实例会抢 8765 端口，本地模式反而读到空数据。
                Process[] running = Process.GetProcessesByName("MusicListsGUI");
                if (running != null && running.Length > 0)
                {
                    Plugin.Log.LogInfo(Tag + " MusicListsGUI 已在运行（pid " + running[0].Id + "），不重复启动");
                    return;
                }
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                };
                Process p = Process.Start(psi);
                Plugin.Log.LogInfo(Tag + " 已启动 MusicListsGUI" + (p != null ? "（pid " + p.Id + "）" : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Tag + " 启动 MusicListsGUI 失败：" + e.Message);
            }
        }
    }
}
