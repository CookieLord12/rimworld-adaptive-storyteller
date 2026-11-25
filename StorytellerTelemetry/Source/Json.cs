using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StorytellerTelemetry
{
    /// <summary>Minimal append-only JSON object builder. One instance = one JSON object.</summary>
    internal sealed class JsonObj
    {
        private readonly StringBuilder sb = new StringBuilder(256);
        private bool any;

        public JsonObj()
        {
            sb.Append('{');
        }

        private void Key(string k)
        {
            if (any)
            {
                sb.Append(',');
            }
            any = true;
            AppendString(sb, k);
            sb.Append(':');
        }

        public JsonObj S(string k, string v)
        {
            Key(k);
            if (v == null)
            {
                sb.Append("null");
            }
            else
            {
                AppendString(sb, v);
            }
            return this;
        }

        public JsonObj N(string k, double v)
        {
            Key(k);
            AppendNumber(sb, v);
            return this;
        }

        public JsonObj I(string k, long v)
        {
            Key(k);
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonObj B(string k, bool v)
        {
            Key(k);
            sb.Append(v ? "true" : "false");
            return this;
        }

        public JsonObj O(string k, JsonObj o)
        {
            Key(k);
            sb.Append(o == null ? "null" : o.ToString());
            return this;
        }

        public JsonObj Strs(string k, IEnumerable<string> items)
        {
            Key(k);
            sb.Append('[');
            bool first = true;
            if (items != null)
            {
                foreach (string s in items)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    if (s == null)
                    {
                        sb.Append("null");
                    }
                    else
                    {
                        AppendString(sb, s);
                    }
                }
            }
            sb.Append(']');
            return this;
        }

        public JsonObj Ints(string k, IEnumerable<int> items)
        {
            Key(k);
            sb.Append('[');
            bool first = true;
            if (items != null)
            {
                foreach (int i in items)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                }
            }
            sb.Append(']');
            return this;
        }

        public JsonObj Objs(string k, IEnumerable<JsonObj> items)
        {
            Key(k);
            sb.Append('[');
            bool first = true;
            if (items != null)
            {
                foreach (JsonObj o in items)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(o == null ? "null" : o.ToString());
                }
            }
            sb.Append(']');
            return this;
        }

        /// <summary>Writes a {"name": count, ...} object.</summary>
        public JsonObj Counts(string k, Dictionary<string, int> counts)
        {
            JsonObj o = new JsonObj();
            if (counts != null)
            {
                foreach (KeyValuePair<string, int> kv in counts)
                {
                    o.I(kv.Key, kv.Value);
                }
            }
            return O(k, o);
        }

        public override string ToString()
        {
            return sb.ToString() + "}";
        }

        private static void AppendNumber(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                sb.Append("null");
                return;
            }
            sb.Append(Math.Round(v, 3).ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        public static void Bump(Dictionary<string, int> d, string key, int by = 1)
        {
            if (key == null)
            {
                key = "none";
            }
            d.TryGetValue(key, out int cur);
            d[key] = cur + by;
        }
    }
}
