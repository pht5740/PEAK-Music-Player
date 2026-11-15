using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PeakMusic
{
    /// <summary>
    /// 系统字体库：枚举「这台机器上装了哪些字体」，并把字体名映射到字体文件。
    ///
    /// <para>为什么需要这一层：TMP 的 SDF 图集只能从<b>字体文件路径</b>创建，
    /// 而玩家（或配置文件）能给的只有<b>字体名</b>。所以这里读注册表
    /// <c>HKLM/HKCU\...\CurrentVersion\Fonts</c> 把「名字 → 文件」建出来，
    /// 顺带得到一个可以循环切换的候选表。</para>
    ///
    /// <para>参考的是 linlin 那套悬浮歌词的做法（枚举系统字体、让用户自己选），
    /// 但没走她的 <c>System.Drawing.Text.InstalledFontCollection</c>：
    /// 那个在 Unity 里要多拖一个程序集，而注册表给出来的正好是 TMP 需要的文件名。</para>
    /// </summary>
    internal static class FontLibrary
    {
        /// <summary>一个可选的字体：族名（写进 cfg）+ 文件 + 按钮上显示的名字。</summary>
        internal sealed class Option
        {
            internal readonly string Family;
            internal readonly string File;
            internal readonly string Label;

            internal Option(string family, string file, string label)
            {
                Family = family ?? "";
                File = file ?? "";
                Label = label ?? "";
            }
        }

        private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        /// <summary>首选字体（中 / 繁 / 日 / 韩），顺序就是「自动」时的优先顺序。</summary>
        private static readonly string[,] Preferred =
        {
            { "Microsoft YaHei",    "msyh.ttc",    "雅黑"  },
            { "Microsoft JhengHei", "msjh.ttc",    "正黑"  },
            { "SimHei",             "simhei.ttf",  "黑体"  },
            { "SimSun",             "simsun.ttc",  "宋体"  },
            { "DengXian",           "Deng.ttf",    "等线"  },
            { "KaiTi",              "simkai.ttf",  "楷体"  },
            { "FangSong",           "simfang.ttf", "仿宋"  },
            { "Yu Gothic",          "YuGothM.ttc", "游黑体" },
            { "Meiryo",             "meiryo.ttc",  "Meiryo" },
            { "Malgun Gothic",      "malgun.ttf",  "Malgun" },
        };

        /// <summary>回退字体：主字体缺字形时 TMP 会顺着这张表往下找。</summary>
        private static readonly string[] FallbackFiles =
        {
            "malgun.ttf",     // 韩文
            "malgunsl.ttf",   // 韩文细体
            "tahoma.ttf",     // 泰文 / 越南文 / 阿拉伯 / 拉丁
            "YuGothM.ttc",    // 日文汉字补充
            "meiryo.ttc",     // 日文
            "msjh.ttc",       // 繁中
        };

        private static readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static List<Option> _cycle;
        private static bool _scanned;

        /// <summary>系统里认到的字体数量（日志用）。</summary>
        internal static int Count
        {
            get { Scan(); return _map.Count; }
        }

        private static string FontsDir
        {
            get
            {
                try { return Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
                catch { return @"C:\Windows\Fonts"; }
            }
        }

        /// <summary>Win10 1809 起支持「仅为当前用户安装字体」，那些字体在 LocalAppData 下。</summary>
        private static string UserFontsDir
        {
            get
            {
                try
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        @"Microsoft\Windows\Fonts");
                }
                catch { return ""; }
            }
        }

        // ───────────────────────────── 扫描 ─────────────────────────────

        private static void Scan()
        {
            if (_scanned) return;
            _scanned = true;

            try
            {
                // 首选字体：文件在就直接登记（有些精简系统注册表缺项，但文件在）
                for (int i = 0; i < Preferred.GetLength(0); i++)
                {
                    string fam = Preferred[i, 0];
                    string file = ResolvePath(Preferred[i, 1]);
                    if (file != null && !_map.ContainsKey(fam)) _map[fam] = file;
                }

                // 注册表：机器级（64/32 两个视图都要看）+ 用户级
                ScanKey(RegistryHive.LocalMachine, RegistryView.Registry64, FontsKey);
                ScanKey(RegistryHive.LocalMachine, RegistryView.Registry32, FontsKey);
                ScanKey(RegistryHive.CurrentUser,  RegistryView.Default,    FontsKey);

                BuildCycle();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[字体] 扫描系统字体失败：" + e.Message);
                _cycle = new List<Option> { new Option("", "", "自动") };
            }
        }

        private static void ScanKey(RegistryHive hive, RegistryView view, string sub)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                {
                    if (baseKey == null) return;
                    using (RegistryKey key = baseKey.OpenSubKey(sub))
                    {
                        if (key == null) return;
                        string[] names = key.GetValueNames();
                        for (int i = 0; i < names.Length; i++)
                        {
                            string file = ResolvePath(key.GetValue(names[i]) as string);
                            if (file != null) Register(names[i], file);
                        }
                    }
                }
            }
            catch { /* 某些系统不让读注册表，忽略 */ }
        }

        /// <summary>注册表项名可能是 "Microsoft YaHei &amp; Microsoft YaHei UI (TrueType)"，要拆开。</summary>
        private static void Register(string entryName, string file)
        {
            if (string.IsNullOrEmpty(entryName)) return;

            string clean = entryName.Trim();
            int p = clean.LastIndexOf(" (", StringComparison.Ordinal);
            if (p > 0 && clean.EndsWith(")", StringComparison.Ordinal)) clean = clean.Substring(0, p);

            string[] parts = clean.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                string fam = parts[i].Trim();
                if (fam.Length == 0) continue;
                if (!_map.ContainsKey(fam)) _map[fam] = file;
            }
        }

        private static string ResolvePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string v = value.Trim().Trim('"');
            if (v.Length == 0) return null;

            try
            {
                if (Path.IsPathRooted(v)) return File.Exists(v) ? v : null;

                string a = Path.Combine(FontsDir, v);
                if (File.Exists(a)) return a;

                string ud = UserFontsDir;
                if (ud.Length > 0)
                {
                    string b = Path.Combine(ud, v);
                    if (File.Exists(b)) return b;
                }

                // 个别注册表项没写扩展名
                if (Path.GetExtension(v).Length == 0)
                {
                    string[] exts = { ".ttf", ".ttc", ".otf" };
                    for (int i = 0; i < exts.Length; i++)
                    {
                        string c = Path.Combine(FontsDir, v + exts[i]);
                        if (File.Exists(c)) return c;
                    }
                }
            }
            catch { }

            return null;
        }

        // ───────────────────────────── 对外 ─────────────────────────────

        /// <summary>字体名 → 字体文件绝对路径；找不到返回 null。</summary>
        internal static string ResolveFile(string family)
        {
            Scan();
            if (string.IsNullOrEmpty(family)) return null;

            string f;
            if (_map.TryGetValue(family.Trim(), out f)) return f;
            return null;
        }

        /// <summary>
        /// 「自动」模式要按顺序试的字体文件。
        /// 已经排好优先级：中文 → 繁体 → 日文 → 韩文，只返回真的存在的。
        /// </summary>
        internal static string[] AutoFiles()
        {
            Scan();

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Preferred.GetLength(0); i++)
            {
                string path = ResolvePath(Preferred[i, 1]);
                if (path != null && seen.Add(path)) list.Add(path);
            }
            return list.ToArray();
        }

        /// <summary>回退字体文件（只返回存在的）。</summary>
        internal static string[] Fallbacks()
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < FallbackFiles.Length; i++)
            {
                string path = ResolvePath(FallbackFiles[i]);
                if (path != null && seen.Add(path)) list.Add(path);
            }
            return list.ToArray();
        }

        /// <summary>可循环切换的字体列表：第 0 项永远是「自动」。</summary>
        internal static List<Option> Cycle
        {
            get
            {
                Scan();
                return _cycle ?? (_cycle = new List<Option> { new Option("", "", "自动") });
            }
        }

        private static void BuildCycle()
        {
            _cycle = new List<Option> { new Option("", "", "自动") };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Preferred.GetLength(0); i++)
            {
                string fam = Preferred[i, 0];
                string label = Preferred[i, 2];
                string file = ResolveFileNoScan(fam) ?? ResolvePath(Preferred[i, 1]);
                if (file == null) continue;
                if (!_map.ContainsKey(fam)) _map[fam] = file;
                if (!used.Add(fam)) continue;
                _cycle.Add(new Option(fam, file, label));
            }

            // 用户自己装的字体：只要名字看着像中日韩字体，也放进列表（最多再加 18 个）
            var keys = new List<string>(_map.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count && _cycle.Count < 22; i++)
            {
                string fam = keys[i];
                if (!LooksCjk(fam)) continue;
                // 同一款字体的 Bold / Light / Italic 变体指向同一个文件，
                // 全塞进来会让「字体」按钮点十几下都在同一款字上打转。
                if (IsStyleVariant(fam)) continue;
                if (!used.Add(fam)) continue;
                _cycle.Add(new Option(fam, _map[fam], Short(fam)));
            }
        }

        private static string ResolveFileNoScan(string family)
        {
            if (string.IsNullOrEmpty(family)) return null;
            string f;
            return _map.TryGetValue(family, out f) ? f : null;
        }

        private static bool IsStyleVariant(string family)
        {
            if (string.IsNullOrEmpty(family)) return false;
            string low = " " + family.ToLowerInvariant();
            string[] tails =
            {
                " bold", " light", " semilight", " semibold", " demilight", " extralight",
                " italic", " oblique", " medium", " regular", " heavy", " black", " thin",
            };
            for (int i = 0; i < tails.Length; i++)
                if (low.EndsWith(tails[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool LooksCjk(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string low = name.ToLowerInvariant();
            string[] keys =
            {
                "yahei", "jhenghei", "simhei", "simsun", "simkai", "simfang", "dengxian",
                "gothic", "meiryo", "noto", "han sans", "cjk", "hei", "song", "kai", "fang",
                "ming", "yuanti", "雅黑", "黑", "宋", "楷", "仿宋", "等线", "圆", "隶",
            };
            for (int i = 0; i < keys.Length; i++)
                if (low.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Short(string family)
        {
            if (string.IsNullOrEmpty(family)) return "自动";
            // 侧栏按钮只有 84px 宽，名字太长会被截断，先在这里压短
            if (family.Length > 6) return family.Substring(0, 6);
            return family;
        }
    }
}
