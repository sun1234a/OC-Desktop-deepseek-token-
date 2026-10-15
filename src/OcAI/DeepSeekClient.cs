using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OcAI
{
    /// <summary>
    /// Minimal DeepSeek (OpenAI-compatible) client.
    /// Uses System.Net.Http from the .NET 6 runtime BepInEx hosts, so no Unity
    /// coroutine plumbing is needed for the I/O itself.
    /// </summary>
    internal sealed class DeepSeekClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private readonly Func<OcAIConfig> _cfg;

        public DeepSeekClient(Func<OcAIConfig> cfg) { _cfg = cfg; }

        public sealed class ChatResult
        {
            public bool Ok;
            public string Text;
            public string Error;
            public int PromptTokens;
            public int CompletionTokens;
            public int TotalTokens;
        }

        public sealed class BalanceResult
        {
            public bool Ok;
            public string Error;
            public bool IsAvailable;
            /// <summary>e.g. "12.34"</summary>
            public string TotalBalance;
            /// <summary>e.g. "CNY"</summary>
            public string Currency;
            public string RawJson;
        }

        private static string Norm(string baseUrl)
        {
            var b = (baseUrl ?? "").Trim();
            if (b.Length == 0) b = "https://api.deepseek.com";
            return b.TrimEnd('/');
        }

        private HttpRequestMessage Req(HttpMethod m, string url)
        {
            var r = new HttpRequestMessage(m, url);
            var key = _cfg()?.ApiKey?.Value?.Trim() ?? "";
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            r.Headers.TryAddWithoutValidation("Accept", "application/json");
            return r;
        }

        // ---------------------------------------------------------------- chat

        public Task<ChatResult> ChatAsync(List<Dictionary<string, string>> messages,
                                          float temperature, int maxTokens, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                var res = new ChatResult();
                try
                {
                    var cfg = _cfg();
                    string url = Norm(cfg.BaseUrl.Value) + "/chat/completions";

                    var sb = new StringBuilder();
                    sb.Append("{\"model\":").Append(JsonStr(cfg.Model.Value.Trim()));
                    sb.Append(",\"messages\":[");
                    for (int i = 0; i < messages.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var m = messages[i];
                        sb.Append("{\"role\":").Append(JsonStr(m["role"]))
                          .Append(",\"content\":").Append(JsonStr(m["content"])).Append('}');
                    }
                    sb.Append("],\"temperature\":")
                      .Append(temperature.ToString("0.##", CultureInfo.InvariantCulture));
                    sb.Append(",\"max_tokens\":").Append(maxTokens);
                    sb.Append(",\"stream\":false}");

                    using (var req = Req(HttpMethod.Post, url))
                    {
                        req.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                        using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                            {
                                res.Error = "HTTP " + (int)resp.StatusCode + ": " + Shorten(body);
                                return res;
                            }
                            res.Text = ExtractAssistantText(body);
                            ExtractUsage(body, res);
                            res.Ok = !string.IsNullOrEmpty(res.Text);
                            if (!res.Ok) res.Error = "empty assistant content";
                            return res;
                        }
                    }
                }
                catch (Exception e)
                {
                    res.Error = e.GetType().Name + ": " + e.Message;
                    return res;
                }
            }, ct);
        }

        // ------------------------------------------------------------- balance

        public Task<BalanceResult> BalanceAsync(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                var res = new BalanceResult();
                try
                {
                    var cfg = _cfg();
                    string url = Norm(cfg.BaseUrl.Value) + "/user/balance";
                    using (var req = Req(HttpMethod.Get, url))
                    using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        res.RawJson = body;
                        if (!resp.IsSuccessStatusCode)
                        {
                            res.Error = "HTTP " + (int)resp.StatusCode + ": " + Shorten(body);
                            return res;
                        }
                        res.IsAvailable = body.IndexOf("\"is_available\":true", StringComparison.OrdinalIgnoreCase) >= 0
                                       || body.IndexOf("\"is_available\": true", StringComparison.OrdinalIgnoreCase) >= 0;

                        // Real shape:
                        //   {"is_available":true,"balance_infos":[
                        //      {"currency":"CNY","total_balance":"110.00", ...}]}
                        // total_balance lives INSIDE the array, so prefer the first
                        // balance_infos entry and only fall back to a bare lookup.
                        int bi = body.IndexOf("\"balance_infos\"", StringComparison.Ordinal);
                        string scope = bi >= 0 ? body.Substring(bi) : body;

                        res.TotalBalance = JsonValue(scope, "total_balance");
                        res.Currency = JsonValue(scope, "currency");
                        if (res.TotalBalance == null) res.TotalBalance = JsonValue(body, "total_balance");
                        if (res.Currency == null) res.Currency = JsonValue(body, "currency");

                        res.Ok = true;
                        return res;
                    }
                }
                catch (Exception e)
                {
                    res.Error = e.GetType().Name + ": " + e.Message;
                    return res;
                }
            }, ct);
        }

        // ------------------------------------------------- tiny JSON helpers
        // Deliberately string-based: this assembly has no JSON dependency and the
        // two payload shapes we consume are tiny and well known.

        private static string JsonStr(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 300 ? s : s.Substring(0, 300) + "...";
        }

        /// <summary>Very small JSON string unescaper for values we just located.</summary>
        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Finds "key":"value" and returns the unescaped value, or null.</summary>
        private static string JsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i += pat.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == ':' || json[i] == '\t')) i++;
            if (i >= json.Length) return null;
            if (json[i] == '"')
            {
                int start = i + 1;
                var raw = new StringBuilder();
                for (int j = start; j < json.Length; j++)
                {
                    if (json[j] == '\\') { raw.Append(json[j]); if (j + 1 < json.Length) raw.Append(json[++j]); continue; }
                    if (json[j] == '"') break;
                    raw.Append(json[j]);
                }
                return Unescape(raw.ToString());
            }
            // number / bool / null
            int e = i;
            while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != ']') e++;
            return json.Substring(i, e - i).Trim();
        }

        private static void ExtractUsage(string body, ChatResult res)
        {
            var u = JsonValue(body, "prompt_tokens");
            var c = JsonValue(body, "completion_tokens");
            var t = JsonValue(body, "total_tokens");
            int v;
            if (int.TryParse(u, out v)) res.PromptTokens = v;
            if (int.TryParse(c, out v)) res.CompletionTokens = v;
            if (int.TryParse(t, out v)) res.TotalTokens = v;
        }

        /// <summary>Pulls choices[0].message.content out of a chat completion.</summary>
        private static string ExtractAssistantText(string body)
        {
            // Locate the first "content" that follows a "message" object.
            int mi = body.IndexOf("\"message\"", StringComparison.Ordinal);
            int from = mi >= 0 ? mi : 0;
            int ci = body.IndexOf("\"content\"", from, StringComparison.Ordinal);
            if (ci < 0) return null;

            int i = ci + "\"content\"".Length;
            while (i < body.Length && (body[i] == ' ' || body[i] == ':' || body[i] == '\t')) i++;
            if (i >= body.Length) return null;

            if (body[i] == '"')
            {
                i++;
                var raw = new StringBuilder();
                for (int j = i; j < body.Length; j++)
                {
                    if (body[j] == '\\') { raw.Append(body[j]); if (j + 1 < body.Length) raw.Append(body[++j]); continue; }
                    if (body[j] == '"') break;
                    raw.Append(body[j]);
                }
                return Unescape(raw.ToString());
            }

            // Reasoning models may return content: null and put text elsewhere.
            return null;
        }
    }
}
