using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Minimal JSON reader/writer for the mod's config file. Handles
    /// exactly the shape we use: a top-level object whose values are
    /// themselves objects whose values are scalars (string / number /
    /// bool / null). No arrays, no deeper nesting, no Unicode escape
    /// sequences past <c>\\</c>, <c>\"</c>, <c>\n</c>, <c>\r</c>, and
    /// <c>\t</c>. Bails out cleanly on anything weird — the caller
    /// falls back to defaults.
    ///
    /// Numbers parse as <see cref="double"/>; the caller coerces to
    /// int/bool/etc. on the way out. Writer emits pretty-printed
    /// output (2-space indent) so the file is hand-editable.
    /// </summary>
    internal static class JsonMini
    {
        public sealed class ParseException : Exception
        {
            public ParseException(string msg) : base(msg) { }
        }

        // ── Parser ─────────────────────────────────────────────────────

        public static Dictionary<string, Dictionary<string, object?>> Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var result = p.ReadOuterObject();
            p.SkipWs();
            if (!p.AtEnd) throw new ParseException($"trailing junk at offset {p.Pos}");
            return result;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public Parser(string s) { _s = s; _i = 0; }
            public int  Pos   => _i;
            public bool AtEnd => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            private char Peek()
            {
                if (_i >= _s.Length) throw new ParseException($"unexpected end at offset {_i}");
                return _s[_i];
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw new ParseException($"expected '{c}' at offset {_i}, got '{Peek()}'");
                _i++;
            }

            public Dictionary<string, Dictionary<string, object?>> ReadOuterObject()
            {
                var outer = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                Expect('{');
                SkipWs();
                if (Peek() == '}') { _i++; return outer; }
                while (true)
                {
                    SkipWs();
                    string key = ReadString();
                    SkipWs(); Expect(':'); SkipWs();
                    var inner = ReadInnerObject();
                    outer[key] = inner;
                    SkipWs();
                    char nc = Peek();
                    if (nc == ',') { _i++; continue; }
                    if (nc == '}') { _i++; break; }
                    throw new ParseException($"expected ',' or '}}' at offset {_i}");
                }
                return outer;
            }

            private Dictionary<string, object?> ReadInnerObject()
            {
                var inner = new Dictionary<string, object?>(StringComparer.Ordinal);
                Expect('{');
                SkipWs();
                if (Peek() == '}') { _i++; return inner; }
                while (true)
                {
                    SkipWs();
                    string key = ReadString();
                    SkipWs(); Expect(':'); SkipWs();
                    object? val = ReadScalar();
                    inner[key] = val;
                    SkipWs();
                    char nc = Peek();
                    if (nc == ',') { _i++; continue; }
                    if (nc == '}') { _i++; break; }
                    throw new ParseException($"expected ',' or '}}' at offset {_i}");
                }
                return inner;
            }

            private object? ReadScalar()
            {
                char c = Peek();
                if (c == '"') return ReadString();
                if (c == 't' || c == 'f') return ReadBool();
                if (c == 'n') { ReadLiteral("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                throw new ParseException($"unexpected '{c}' at offset {_i}");
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new ParseException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new ParseException("trailing backslash");
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"':  sb.Append('"');  break;
                            case '\\': sb.Append('\\'); break;
                            case '/':  sb.Append('/');  break;
                            case 'n':  sb.Append('\n'); break;
                            case 'r':  sb.Append('\r'); break;
                            case 't':  sb.Append('\t'); break;
                            default:   throw new ParseException($"unsupported escape \\{e} at offset {_i - 1}");
                        }
                    }
                    else sb.Append(c);
                }
            }

            private bool ReadBool()
            {
                if (_s[_i] == 't') { ReadLiteral("true");  return true;  }
                ReadLiteral("false");
                return false;
            }

            private void ReadLiteral(string lit)
            {
                if (_i + lit.Length > _s.Length || _s.Substring(_i, lit.Length) != lit)
                    throw new ParseException($"expected '{lit}' at offset {_i}");
                _i += lit.Length;
            }

            private double ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                string slice = _s.Substring(start, _i - start);
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new ParseException($"bad number '{slice}' at offset {start}");
                return v;
            }
        }

        // ── Writer ─────────────────────────────────────────────────────

        public static string Write(Dictionary<string, Dictionary<string, object?>> data)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            int sectionsRemaining = data.Count;
            foreach (var section in data)
            {
                sb.Append("  ");
                AppendString(sb, section.Key);
                sb.Append(": {\n");
                int keysRemaining = section.Value.Count;
                foreach (var kv in section.Value)
                {
                    sb.Append("    ");
                    AppendString(sb, kv.Key);
                    sb.Append(": ");
                    AppendScalar(sb, kv.Value);
                    keysRemaining--;
                    if (keysRemaining > 0) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  }");
                sectionsRemaining--;
                if (sectionsRemaining > 0) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:   sb.Append(c);      break;
                }
            }
            sb.Append('"');
        }

        private static void AppendScalar(StringBuilder sb, object? v)
        {
            if (v == null)              { sb.Append("null"); return; }
            if (v is bool b)            { sb.Append(b ? "true" : "false"); return; }
            if (v is string s)          { AppendString(sb, s); return; }
            if (v is double d)          { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is float  f)          { sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is int    i)          { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is long   l)          { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            // Anything else: fall back to invariant string with quotes.
            AppendString(sb, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
        }
    }
}
