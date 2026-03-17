#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace TeamCreate.Editor
{
    public static class TeamCreateJson
    {
        public static string Serialize(object obj)
        {
            if (obj == null) return "null";
            var type = obj.GetType();

            if (obj is string s) return "\"" + EscapeString(s) + "\"";
            if (obj is bool bv) return bv ? "true" : "false";
            if (obj is byte[] bytes) return "\"" + Convert.ToBase64String(bytes) + "\"";
            if (obj is int iv) return iv.ToString();
            if (obj is long lv) return lv.ToString();
            if (obj is float fv) return fv.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (obj is double dv) return dv.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (type.IsEnum) return Convert.ToInt32(obj).ToString();

            if (obj is IDictionary dict)
            {
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    sb.Append('"').Append(EscapeString(entry.Key.ToString())).Append("\":");
                    sb.Append(Serialize(entry.Value));
                    first = false;
                }
                return sb.Append('}').ToString();
            }

            if (obj is IList list)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Serialize(list[i]));
                }
                return sb.Append(']').ToString();
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var osb = new StringBuilder("{");
            bool fp = true;
            foreach (var prop in props)
            {
                if (!prop.CanRead) continue;
                var val = prop.GetValue(obj);
                if (val == null) continue;
                if (!fp) osb.Append(',');
                osb.Append('"').Append(prop.Name).Append("\":");
                osb.Append(Serialize(val));
                fp = false;
            }
            return osb.Append('}').ToString();
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return default;
            int pos = 0;
            var parsed = ParseValue(json, ref pos);
            return (T)ConvertTo(parsed, typeof(T));
        }

        private static object ParseValue(string json, ref int pos)
        {
            Skip(json, ref pos);
            if (pos >= json.Length) return null;
            char c = json[pos];
            if (c == '{') return ParseObject(json, ref pos);
            if (c == '[') return ParseArray(json, ref pos);
            if (c == '"') return ParseString(json, ref pos);
            if (c == 't') { pos += 4; return true; }
            if (c == 'f') { pos += 5; return false; }
            if (c == 'n') { pos += 4; return null; }
            return ParseNumber(json, ref pos);
        }

        private static Dictionary<string, object> ParseObject(string json, ref int pos)
        {
            var d = new Dictionary<string, object>();
            pos++;
            Skip(json, ref pos);
            if (pos < json.Length && json[pos] == '}') { pos++; return d; }
            while (pos < json.Length)
            {
                Skip(json, ref pos);
                if (pos >= json.Length || json[pos] != '"') break;
                string key = ParseString(json, ref pos);
                Skip(json, ref pos);
                if (pos < json.Length && json[pos] == ':') pos++;
                d[key] = ParseValue(json, ref pos);
                Skip(json, ref pos);
                if (pos < json.Length && json[pos] == ',') pos++;
                else break;
            }
            Skip(json, ref pos);
            if (pos < json.Length && json[pos] == '}') pos++;
            return d;
        }

        private static List<object> ParseArray(string json, ref int pos)
        {
            var list = new List<object>();
            pos++;
            Skip(json, ref pos);
            if (pos < json.Length && json[pos] == ']') { pos++; return list; }
            while (pos < json.Length)
            {
                list.Add(ParseValue(json, ref pos));
                Skip(json, ref pos);
                if (pos < json.Length && json[pos] == ',') pos++;
                else break;
            }
            Skip(json, ref pos);
            if (pos < json.Length && json[pos] == ']') pos++;
            return list;
        }

        private static string ParseString(string json, ref int pos)
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '"') { pos++; break; }
                if (c == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    switch (json[pos])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 < json.Length)
                            {
                                sb.Append((char)Convert.ToInt32(json.Substring(pos + 1, 4), 16));
                                pos += 4;
                            }
                            break;
                        default: sb.Append(json[pos]); break;
                    }
                }
                else sb.Append(c);
                pos++;
            }
            return sb.ToString();
        }

        private static object ParseNumber(string json, ref int pos)
        {
            int start = pos;
            bool isFloat = false;
            if (pos < json.Length && json[pos] == '-') pos++;
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '.' || c == 'e' || c == 'E') isFloat = true;
                if (!char.IsDigit(c) && c != '.' && c != 'e' && c != 'E' && c != '+' && c != '-') break;
                pos++;
            }
            string num = json.Substring(start, pos - start);
            if (isFloat && double.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
            if (long.TryParse(num, out long l)) return l;
            return 0L;
        }

        private static void Skip(string json, ref int pos)
        {
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\t' ||
                                          json[pos] == '\n' || json[pos] == '\r')) pos++;
        }

        private static object ConvertTo(object src, Type t)
        {
            if (src == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t == typeof(string)) return src.ToString();
            if (t == typeof(bool)) return src is bool b ? b : Convert.ToBoolean(src);
            if (t == typeof(int)) return Convert.ToInt32(src);
            if (t == typeof(long)) return Convert.ToInt64(src);
            if (t == typeof(float)) return Convert.ToSingle(src);
            if (t == typeof(double)) return Convert.ToDouble(src);
            if (t == typeof(byte[])) return src is string b64 ? Convert.FromBase64String(b64) : null;
            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt32(src));

            if (t == typeof(Dictionary<string, string>))
            {
                var r = new Dictionary<string, string>();
                if (src is Dictionary<string, object> sd)
                    foreach (var kv in sd) r[kv.Key] = kv.Value?.ToString();
                return r;
            }
            if (t == typeof(Dictionary<int, string>))
            {
                var r = new Dictionary<int, string>();
                if (src is Dictionary<string, object> sd)
                    foreach (var kv in sd)
                        if (int.TryParse(kv.Key, out int k)) r[k] = kv.Value?.ToString();
                return r;
            }
            if (t == typeof(Dictionary<string, int>))
            {
                var r = new Dictionary<string, int>();
                if (src is Dictionary<string, object> sd)
                    foreach (var kv in sd) r[kv.Key] = Convert.ToInt32(kv.Value);
                return r;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elem = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(t);
                if (src is List<object> pl) foreach (var item in pl) list.Add(ConvertTo(item, elem));
                return list;
            }

            if (src is Dictionary<string, object> od)
            {
                var inst = Activator.CreateInstance(t);
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanWrite) continue;
                    if (od.TryGetValue(prop.Name, out var v))
                        try { prop.SetValue(inst, ConvertTo(v, prop.PropertyType)); } catch { }
                }
                return inst;
            }

            return src;
        }

        private static string EscapeString(string s)
        {
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
