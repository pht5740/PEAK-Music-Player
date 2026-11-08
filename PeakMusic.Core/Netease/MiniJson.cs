using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PeakMusic.Core.Netease
{
    /// <summary>
    /// 极简 JSON 解析器，刻意不引 Newtonsoft。
    ///
    /// 理由：Unity 里再塞一份 Newtonsoft.dll 很容易和游戏自带的版本打架
    /// （BepInEx 5 下表现为莫名其妙的 MissingMethodException，极难查）。
    /// 我们只需要"读"网易的响应，从不生成 JSON 回传（明文 /api 接口都走表单提交），
    /// 所以一个 200 行的递归下降解析器足够了。
    ///
    /// 返回类型：Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null。
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length)
                throw new FormatException("JSON 尾部有多余内容 @" + i + ": " + Snippet(text, i));
            return v;
        }

        // ───────────────────────────── 解析 ─────────────────────────────

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true"); return true;
                case 'f':
                    Expect(s, ref i, "false"); return false;
                case 'n':
                    Expect(s, ref i, "null"); return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;                                  // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }

            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (s[i] != '"') throw new FormatException("对象 key 不是字符串 @" + i + ": " + Snippet(s, i));
                string key = ParseString(s, ref i);

                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("缺少冒号 @" + i);
                i++;

                d[key] = ParseValue(s, ref i);

                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("对象未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("对象里出现非法字符 '" + s[i] + "' @" + i);
            }
            throw new FormatException("对象未闭合");
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;                                  // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("数组未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("数组里出现非法字符 '" + s[i] + "' @" + i);
            }
            throw new FormatException("数组未闭合");
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;                                  // 起始引号
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("\\u 转义不完整");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("字符串未闭合");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9')) i++;
                else break;
            }
            if (start == i) throw new FormatException("不是数字 @" + i + ": " + Snippet(s, i));
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("期望 " + word + " @" + i);
            i += word.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        private static string Snippet(string s, int i)
        {
            int len = Math.Min(40, s.Length - i);
            return i < 0 || len <= 0 ? "" : s.Substring(i, len).Replace("\n", "\\n");
        }

        // ───────────────────────────── 取值助手 ─────────────────────────────
        // 网易的响应字段经常"有时是对象、有时是 null、有时是数组"，所以每个取值都要能容忍缺失。

        public static Dictionary<string, object> AsObject(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> AsArray(object o)
        {
            return o as List<object>;
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> List(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static string Str(Dictionary<string, object> d, string key, string def = null)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            var s = v as string;
            return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Lng(Dictionary<string, object> d, string key, long def = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (long)Math.Round((double)v);
            if (v is string)
            {
                long r;
                if (long.TryParse((string)v, out r)) return r;
            }
            return def;
        }

        public static double Dbl(Dictionary<string, object> d, string key, double def = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (double)v;
            if (v is string)
            {
                double r;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            }
            return def;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def = false)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            return def;
        }

        /// <summary>把对象渲染成一行可读文本，调试接口时省得自己拼。</summary>
        public static string Preview(object v, int max = 300)
        {
            var sb = new StringBuilder();
            Dump(v, sb, max);
            return sb.ToString();
        }

        private static void Dump(object v, StringBuilder sb, int max)
        {
            if (sb.Length >= max) return;
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { sb.Append('"').Append(s.Length > 60 ? s.Substring(0, 60) + "…" : s).Append('"'); return; }
            var d = v as Dictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                foreach (var kv in d)
                {
                    if (sb.Length >= max) break;
                    sb.Append(kv.Key).Append(':');
                    Dump(kv.Value, sb, max);
                    sb.Append(',');
                }
                if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
                sb.Append('}');
                return;
            }
            var l = v as List<object>;
            if (l != null)
            {
                sb.Append('[');
                for (int i = 0; i < l.Count && sb.Length < max; i++)
                {
                    if (i > 0) sb.Append(',');
                    Dump(l[i], sb, max);
                }
                sb.Append(']');
                return;
            }
            sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
        }
    }
}
