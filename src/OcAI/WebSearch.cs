using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OcAI
{
    /// <summary>
    /// Optional web search, so the chat can answer questions about current facts.
    ///
    /// The DeepSeek chat API has no browse tool, so "联网" has to be done here: the
    /// question is turned into a search query, the top results are fetched and pasted
    /// into the system prompt as context, and the model answers from that.
    ///
    /// Implementation notes:
    ///   * Plain HttpClient + HTML scraping (no API key, no extra dependency). The
    ///     plugin already ships an HttpClient for DeepSeek and BepInEx hosts .NET 6.
    ///   * Endpoints are tried in order and the first one that returns usable results
    ///     wins, because which search engine is reachable differs per network (in the
    ///     dev environment, for example, only some hosts answer).
    ///   * Everything is best-effort: a failed search must never break the chat, it just
    ///     means the answer comes back without fresh context.
    /// </summary>
    internal static class WebSearch
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        /// <summary>One search hit.</summary>
        internal sealed class Hit
        {
            public string Title;
            public string Url;
            public string Snippet;
        }

        /// <summary>Result of one attempted search.</summary>
        internal sealed class Result
        {
            public bool Ok;
            public string Provider;
            public string Error;
            /// <summary>Which endpoints were probed, for the log.</summary>
            public string Trail;
            public readonly List<Hit> Hits = new List<Hit>();
        }

        // ------------------------------------------------------------- query building

        /// <summary>
        /// Words that mean "I want fresh information". If none is present the chat stays
        /// offline, so a normal question does not pay for a search round trip.
        /// </summary>
        private static readonly string[] SearchTriggers =
        {
            "搜", "查一", "查下", "查查", "百度", "google", "搜索", "联网", "上网",
            "最新", "今天", "今日", "现在", "目前", "当前", "实时", "新闻", "股价",
            "天气", "汇率", "比分", "赛程", "发布会", "什么时候发布", "多少钱",
            "latest", "today", "now", "news", "price", "weather", "current"
        };

        internal static bool LooksLikeNeedsWeb(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string lower = text.ToLowerInvariant();
            for (int i = 0; i < SearchTriggers.Length; i++)
            {
                if (lower.IndexOf(SearchTriggers[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>Strips the "搜一下" style prefix so the query is closer to keywords.</summary>
        private static string CleanQuery(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string q = text.Trim();
            string[] prefixes = { "帮我搜一下", "帮我搜搜", "帮我查一下", "帮我查查", "搜一下", "搜搜", "搜索一下", "查一下", "查查", "联网搜一下", "上网查一下", "百度一下", "google一下" };
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (q.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    q = q.Substring(prefixes[i].Length).Trim();
                    break;
                }
            }
            return q.Length == 0 ? text.Trim() : q;
        }

        // ------------------------------------------------------------------- searching

        internal static Task<Result> SearchAsync(string question, CancellationToken ct)
        {
            return Task.Run(() => Search(question, ct), ct);
        }

        private static Result Search(string question, CancellationToken ct)
        {
            var result = new Result();
            string query = CleanQuery(question);
            var trail = new StringBuilder();

            try
            {
                string enc = Uri.EscapeDataString(query);

                var endpoints = new List<KeyValuePair<string, string>>
                {
                    // cn.bing.com first: it returns a full server-rendered result page with
                    // <li class="b_algo"> blocks that parse cleanly (verified against the
                    // live site: 9 blocks, title/url/snippet all extracted).
                    new KeyValuePair<string, string>("bing-cn", "https://cn.bing.com/search?q=" + enc + "&setlang=zh-CN"),
                    new KeyValuePair<string, string>("bing", "https://www.bing.com/search?q=" + enc + "&setlang=zh-CN"),
                    // so.com works but its links are redirect wrappers and snippets are
                    // harder to isolate, so it is only a fallback.
                    new KeyValuePair<string, string>("so360", "https://www.so.com/s?q=" + enc),
                    // Baidu serves a ~1.5 KB anti-bot stub to this kind of client and
                    // DuckDuckGo is unreachable from some networks; kept last on purpose.
                    new KeyValuePair<string, string>("baidu", "https://www.baidu.com/s?wd=" + enc),
                    new KeyValuePair<string, string>("duckduckgo", "https://html.duckduckgo.com/html/?q=" + enc)
                };

                foreach (var ep in endpoints)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        string html = Fetch(ep.Value, ct);
                        if (string.IsNullOrEmpty(html))
                        {
                            trail.Append(ep.Key).Append(":empty; ");
                            continue;
                        }

                        var hits = Parse(html, ep.Key);
                        trail.Append(ep.Key).Append(':').Append(hits.Count).Append("; ");

                        if (hits.Count > 0)
                        {
                            result.Ok = true;
                            result.Provider = ep.Key;
                            result.Hits.AddRange(hits);
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        trail.Append(ep.Key).Append(':').Append(e.GetType().Name).Append("; ");
                    }
                }

                if (!result.Ok && result.Error == null) result.Error = "no results from any engine";
            }
            catch (Exception e)
            {
                result.Error = e.GetType().Name + ": " + e.Message;
            }

            result.Trail = trail.ToString();
            return result;
        }

        private static string Fetch(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                // A normal browser UA: several engines serve a bare page (or a block page)
                // to unknown clients.
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

                var resp = Http.SendAsync(req, ct).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>Very small HTML scraper: enough for the three engines' result blocks.</summary>
        private static List<Hit> Parse(string html, string provider)
        {
            var hits = new List<Hit>();
            try
            {
                if (provider.StartsWith("bing", StringComparison.Ordinal))
                    ParseBing(html, hits);
                else switch (provider)
                {
                    case "baidu":
                        ParseBaidu(html, hits);
                        break;
                    case "so360":
                        ParseSo360(html, hits);
                        break;
                    case "duckduckgo":
                        ParseDdg(html, hits);
                        break;
                    case "sogou":
                        ParseSogou(html, hits);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("WebSearch.Parse(" + provider + "): " + e.Message);
            }
            return hits;
        }

        private static void ParseBing(string html, List<Hit> hits)
        {
            // <li class="b_algo"> ... <h2><a href="URL">TITLE</a></h2> ... <p>SNIPPET</p>
            var blocks = Regex.Matches(html, @"<li class=""b_algo""[\s\S]*?</li>", RegexOptions.IgnoreCase);
            foreach (Match b in blocks)
            {
                if (hits.Count >= 5) break;
                var m = Regex.Match(b.Value, @"<h2[^>]*>\s*<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var hit = new Hit
                {
                    Url = m.Groups["u"].Value,
                    Title = StripTags(m.Groups["t"].Value)
                };
                var p = Regex.Match(b.Value, @"<p[^>]*>(?<s>[\s\S]*?)</p>", RegexOptions.IgnoreCase);
                if (p.Success) hit.Snippet = StripTags(p.Groups["s"].Value);
                if (hit.Title.Length > 0) hits.Add(hit);
            }
        }

        private static void ParseBaidu(string html, List<Hit> hits)
        {
            // <h3 class="t"><a href="...">TITLE</a></h3> ... <span class="content-right_...">
            var blocks = Regex.Matches(html, @"<div[^>]*class=""result[^""]*""[\s\S]*?(?=<div[^>]*class=""result|$)",
                RegexOptions.IgnoreCase);
            foreach (Match b in blocks)
            {
                if (hits.Count >= 5) break;
                var m = Regex.Match(b.Value, @"<h3[^>]*>\s*<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>",
                    RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var hit = new Hit { Url = m.Groups["u"].Value, Title = StripTags(m.Groups["t"].Value) };
                var s = Regex.Match(b.Value, @"<span[^>]*class=""[^""]*content-right[^""]*""[^>]*>(?<s>[\s\S]*?)</span>",
                    RegexOptions.IgnoreCase);
                if (!s.Success) s = Regex.Match(b.Value, @"<div[^>]*class=""[^""]*c-abstract[^""]*""[^>]*>(?<s>[\s\S]*?)</div>",
                    RegexOptions.IgnoreCase);
                if (s.Success) hit.Snippet = StripTags(s.Groups["s"].Value);
                if (hit.Title.Length > 0) hits.Add(hit);
            }
        }

        private static void ParseSo360(string html, List<Hit> hits)
        {
            var blocks = Regex.Matches(html, @"<li[^>]*class=""res-list[^""]*""[\s\S]*?</li>", RegexOptions.IgnoreCase);
            foreach (Match b in blocks)
            {
                if (hits.Count >= 5) break;
                var m = Regex.Match(b.Value, @"<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var hit = new Hit { Url = m.Groups["u"].Value, Title = StripTags(m.Groups["t"].Value) };
                var p = Regex.Match(b.Value, @"<p[^>]*>(?<s>[\s\S]*?)</p>", RegexOptions.IgnoreCase);
                if (p.Success) hit.Snippet = StripTags(p.Groups["s"].Value);
                if (hit.Title.Length > 0) hits.Add(hit);
            }
        }

        private static void ParseDdg(string html, List<Hit> hits)
        {
            var blocks = Regex.Matches(html, @"<div[^>]*class=""result[^""]*""[\s\S]*?(?=<div[^>]*class=""result|$)",
                RegexOptions.IgnoreCase);
            foreach (Match b in blocks)
            {
                if (hits.Count >= 5) break;
                var m = Regex.Match(b.Value, @"<a[^>]*class=""result__a""[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>",
                    RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var hit = new Hit { Url = m.Groups["u"].Value, Title = StripTags(m.Groups["t"].Value) };
                var s = Regex.Match(b.Value, @"<a[^>]*class=""result__snippet""[^>]*>(?<s>[\s\S]*?)</a>",
                    RegexOptions.IgnoreCase);
                if (s.Success) hit.Snippet = StripTags(s.Groups["s"].Value);
                if (hit.Title.Length > 0) hits.Add(hit);
            }
        }

        private static void ParseSogou(string html, List<Hit> hits)
        {
            var blocks = Regex.Matches(html, @"<div[^>]*class=""vrwrap[^""]*""[\s\S]*?</div>\s*</div>", RegexOptions.IgnoreCase);
            foreach (Match b in blocks)
            {
                if (hits.Count >= 5) break;
                var m = Regex.Match(b.Value, @"<a[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var hit = new Hit { Url = m.Groups["u"].Value, Title = StripTags(m.Groups["t"].Value) };
                if (hit.Title.Length > 0) hits.Add(hit);
            }
        }

        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = System.Net.WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }

        // ------------------------------------------------------------------- formatting

        /// <summary>
        /// Builds the context block handed to the model. Kept short: the model only needs
        /// the gist plus the source names, and every extra character costs tokens.
        /// </summary>
        internal static string FormatForPrompt(string question, Result r)
        {
            if (r == null || !r.Ok || r.Hits.Count == 0) return null;

            var sb = new StringBuilder();
            sb.Append("以下是刚刚联网搜到的资料（来自 ").Append(r.Provider).Append("）");
            sb.Append("，请用它回答用户的问题，并在句末用「（来源：站点名）」简单标注；");
            sb.Append("如果资料和问题无关，就直接说没搜到有用的，不要硬编。\n");
            sb.Append("用户问题：").Append(question).Append('\n');

            for (int i = 0; i < r.Hits.Count; i++)
            {
                var h = r.Hits[i];
                sb.Append(i + 1).Append(". ").Append(h.Title);
                if (!string.IsNullOrEmpty(h.Snippet)) sb.Append(" —— ").Append(Trim(h.Snippet, 220));
                if (!string.IsNullOrEmpty(h.Url)) sb.Append(" (").Append(Trim(h.Url, 120)).Append(')');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>One-line summary for the debug log.</summary>
        internal static string Describe(Result r)
        {
            if (r == null) return "search=<null>";
            return "search ok=" + r.Ok + " provider=" + (r.Provider ?? "-") +
                   " hits=" + r.Hits.Count + " trail=[" + (r.Trail ?? "") + "] err=" + (r.Error ?? "-");
        }
    }
}
