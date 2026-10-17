using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace OcAI
{
    /// <summary>
    /// Lets the character actually know what is going on in her own game, so that
    /// "我有多少金币" gets a real number instead of a guess.
    ///
    /// Two sources, best first:
    ///
    ///  1. The live PlayerDataManager in memory, read through reflection. Authoritative
    ///     and always current.
    ///  2. The save file (playerdata.json). Only written every so often, but it still
    ///     beats knowing nothing, and it keeps the feature working if the game renames
    ///     its manager class.
    ///
    /// The text is injected into the SYSTEM prompt of a request, never into the chat
    /// history: it must not be replayed as if the character had said it, and it must
    /// not be paid for again on every later turn.
    ///
    /// Token cost is kept down by only attaching it when the user's line actually
    /// sounds like it is about the game (see WantsGameContext).
    /// </summary>
    internal static class GameContext
    {
        // ------------------------------------------------- what SHE has been saying
        //
        // The game pushes every line she says through UI.UiPreviewDialogue.ShowText,
        // which the plugin already patches. Those lines are kept here rather than in
        // the chat history: putting them in the history made the model believe it had
        // said them itself and derailed the conversation. Injected as clearly-labelled
        // background they are useful instead of harmful.

        private const int UtteranceKeep = 12;
        private static readonly Queue<string> Utterances = new Queue<string>();
        private static readonly object UtteranceLock = new object();

        // The game's own bubbles. Our clone is one of them (it is built from the same
        // prefab), so it has to be excluded by identity or the plugin's replies would
        // be filed as things the game character said.
        private static readonly List<object> IgnoredBubbles = new List<object>();
        private static readonly Dictionary<object, string> LastSeen = new Dictionary<object, string>();

        internal static void IgnoreBubble(object bubble)
        {
            try
            {
                if (bubble == null) return;
                lock (UtteranceLock) if (!IgnoredBubbles.Contains(bubble)) IgnoredBubbles.Add(bubble);
            }
            catch { }
        }

        /// <summary>
        /// Collects the lines she is showing right now.
        ///
        /// Reading the bubbles is the reliable route: the game does NOT call
        /// UI.UiPreviewDialogue.ShowText (0 hits across a whole session), so patching
        /// that method captured nothing. What it certainly does is fill the text field
        /// of UI.DialogueBubble -- there are 21 of those in the scene -- so the text is
        /// read straight from there and a change is treated as a new line.
        /// </summary>
        internal static void PollBubbles()
        {
            try
            {
                var type = Plugin.GameType("UI.DialogueBubble");
                if (type == null) return;

                var il2 = Il2CppInterop.Runtime.Il2CppType.From(type);
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(il2);
                if (arr == null) return;

                for (int i = 0; i < arr.Length; i++)
                {
                    var mb = arr[i] as UnityEngine.MonoBehaviour;
                    if (mb == null) continue;
                    if (!mb.gameObject.activeInHierarchy) continue;

                    var typed = mb.TryCast<UI.DialogueBubble>();
                    if (typed == null) continue;

                    string text = null;
                    try
                    {
                        var t = typed._strTxt;
                        if (t != null) text = t.text;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(text)) continue;

                    text = text.Trim();
                    if (text.Length == 0 || text.Length > 200) continue;

                    string previous;
                    if (LastSeen.TryGetValue(mb, out previous) && previous == text) continue;
                    LastSeen[mb] = text;

                    bool ours;
                    lock (UtteranceLock) ours = IgnoredBubbles.Contains(mb);
                    if (ours) continue;

                    NoteUtterance(text);
                    Plugin.Dbg("game bubble line: " + text);
                }
            }
            catch (Exception e) { Plugin.Dbg("GameContext.PollBubbles: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Records a line the game made her say.</summary>
        internal static void NoteUtterance(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;
                text = text.Trim();
                if (text.Length == 0 || text.Length > 200) return;

                lock (UtteranceLock)
                {
                    // The plugin speaks through the same widget, so drop our own echoes.
                    foreach (var mine in _ourOwnLines)
                        if (string.Equals(mine, text, StringComparison.Ordinal)) return;

                    Utterances.Enqueue(text);
                    while (Utterances.Count > UtteranceKeep) Utterances.Dequeue();
                }
            }
            catch { }
        }

        private static readonly List<string> _ourOwnLines = new List<string>();

        /// <summary>
        /// Registers a line the plugin itself is about to display, so it is not filed
        /// as something the game character said.
        /// </summary>
        internal static void NoteOwnLine(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;
                _ourOwnLines.Add(text.Trim());
                if (_ourOwnLines.Count > 24) _ourOwnLines.RemoveAt(0);
            }
            catch { }
        }

        private static List<string> RecentUtterances()
        {
            lock (UtteranceLock) return new List<string>(Utterances);
        }

        // ---------------------------------------------------------------- public

        /// <summary>
        /// Whether a line looks like it is about the game, herself, or something she
        /// just said -- which is when the snapshot is worth its tokens.
        /// </summary>
        internal static bool WantsGameContext(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] keys =
            {
                // game state
                "金币", "多少钱", "余额", "经验", "等级", "分数", "点击", "按键", "玩了多久",
                "游玩", "存档", "设置", "音量", "角色大小", "窗口", "游戏", "怎么玩", "卡包",
                "成就", "签到", "陪", "累不累", "多久", "进度", "数据",
                // herself: looks, clothes, mood, pose
                "衣服", "裙子", "头发", "发型", "颜色", "样子", "长相", "好看", "可爱", "穿",
                "帽子", "发饰", "表情", "姿势", "姿态", "坐着", "站着", "爬到", "屏幕",
                // what she said
                "刚才", "刚刚", "说过", "说了什么", "那句话", "之前说", "你说了", "台词"
            };
            for (int i = 0; i < keys.Length; i++)
                if (text.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Builds the context block, or null when nothing useful is available.
        /// </summary>
        internal static string Build()
        {
            try
            {
                var live = ReadLive();
                var save = ReadSaveFile();

                // Deliberately NOT bailing out when both are empty: what she looks like
                // and what she just said are still worth sending.
                if (live.Count == 0 && save.Count == 0)
                    Plugin.Dbg("GameContext: no numeric data, sending appearance/utterances only");

                // Live wins where both have a value.
                var merged = new Dictionary<string, string>(save);
                foreach (var kv in live) merged[kv.Key] = kv.Value;

                var sb = new StringBuilder();
                sb.Append("【你所在的这款游戏里的情况】");

                // What she looks like and what she is doing. Described rather than
                // inferred: the model has no way to see the sprite, and asking it to
                // guess an appearance is how a blue-haired maid ends up described as
                // whatever the model feels like.
                sb.Append("你自己是这个桌面宠物：蓝头发、白色蕾丝发饰、女仆装，用 Spine 骨骼动画演出。");
                try
                {
                    sb.Append("你现在的姿态是").Append(SitState.IsSitting() ? "坐着" : "站着");
                    sb.Append("，就待在这个屏幕窗口里陪玩家。");
                }
                catch { }

                var said = RecentUtterances();
                if (said.Count > 0)
                {
                    sb.Append("你刚才在游戏里自己说过这些话（是游戏台词，不是你和他聊天时说的）：");
                    for (int i = 0; i < said.Count; i++)
                        sb.Append('「').Append(said[i]).Append('」');
                    sb.Append('。');
                }

                sb.Append("【游戏实时数据】玩家当前：");

                Field(sb, merged, "gold", "金币");
                Field(sb, merged, "experience", "经验");
                Field(sb, merged, "totalClickCount", "鼠标累计点击");
                Field(sb, merged, "mouseDailyClicks", "今日鼠标点击");
                Field(sb, merged, "keyboardDailyClicks", "今日键盘按键");

                if (merged.ContainsKey("totalPlaytimeSeconds"))
                {
                    long secs;
                    if (long.TryParse(merged["totalPlaytimeSeconds"], out secs) && secs > 0)
                        sb.Append("累计游玩=").Append(DescribeDuration(secs)).Append("；");
                }

                try
                {
                    sb.Append("她现在的姿态=")
                      .Append(SitState.IsSitting() ? "坐着" : "站着")
                      .Append('。');
                }
                catch { }

                sb.Append("数据来源=").Append(live.Count > 0 ? "游戏内存（实时）" : "存档文件（可能稍有延迟）").Append('。');
                sb.Append("请照着这些真实数值回答，不要编造其它数据；问的东西不在上面就直说不清楚。");

                Plugin.Dbg("game context: live=" + live.Count + " save=" + save.Count +
                           " chars=" + sb.Length);
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Dbg("GameContext.Build: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static void Field(StringBuilder sb, Dictionary<string, string> map, string key, string label)
        {
            string v;
            if (map.TryGetValue(key, out v) && !string.IsNullOrEmpty(v))
                sb.Append(label).Append('=').Append(v).Append('；');
        }

        private static string DescribeDuration(long seconds)
        {
            long h = seconds / 3600;
            long m = (seconds % 3600) / 60;
            if (h > 0) return h + " 小时 " + m + " 分钟";
            return m + " 分钟";
        }

        // ----------------------------------------------------------- live source

        private static bool _lookupDone;
        private static Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase _data;

        /// <summary>
        /// Reads the player data object held by the game's DataManager.
        ///
        /// Reflection on purpose: the member names come from the save file, but the
        /// compiled game classes are IL2CPP-generated and a hard reference to one of
        /// them would take the whole plugin down if the game renames it, instead of
        /// just this one feature.
        /// </summary>
        private static Dictionary<string, string> ReadLive()
        {
            var map = new Dictionary<string, string>();
            try
            {
                if (_data == null && !_lookupDone)
                {
                    _lookupDone = true;
                    _data = GrabLiveData();
                }
                if (_data == null) return map;

                Copy(map, _data, "gold");
                Copy(map, _data, "experience");
                Copy(map, _data, "totalPlaytimeSeconds");
                Copy(map, _data, "playerName");

                var input = Member(_data, "inputData");
                var inputObj = input as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (inputObj != null)
                {
                    Copy(map, inputObj, "totalClickCount");
                    Copy(map, inputObj, "mouseDailyClicks");
                    Copy(map, inputObj, "keyboardDailyClicks");
                }
            }
            catch (Exception e)
            {
                Plugin.Dbg("GameContext.ReadLive: " + e.GetType().Name + ": " + e.Message);
            }
            return map;
        }

        private static void Copy(Dictionary<string, string> map,
                                 Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj, string member)
        {
            var v = Member(obj, member);
            if (v != null) map[member] = v.ToString();
        }

        /// <summary>Reads one field or property by name, or null.</summary>
        private static object Member(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj, string member)
        {
            if (obj == null) return null;
            try
            {
                var t = obj.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var f = t.GetField(member, F);
                if (f != null) return f.GetValue(obj);

                var p = t.GetProperty(member, F);
                if (p != null && p.CanRead) return p.GetValue(obj);
            }
            catch (Exception e) { Plugin.Dbg("GameContext." + member + ": " + e.Message); }
            return null;
        }

        /// <summary>
        /// The live player-data singleton, or null when the game does not expose one
        /// in a shape this can call.
        /// </summary>
        private static Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase GrabLiveData()
        {
            try
            {
                Type manager = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        manager = asm.GetType("PlayerDataManager", false);
                        if (manager != null) break;
                    }
                    catch { }
                }
                if (manager == null)
                {
                    Plugin.Dbg("GameContext: PlayerDataManager not found");
                    return null;
                }

                // DataManager<T> exposes GetData(); find it, closing the generic over
                // the manager's own data type.
                var probe = manager;
                while (probe != null)
                {
                    foreach (var m in probe.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (m.Name != "GetData" || m.GetParameters().Length != 0) continue;

                        MethodInfo call = m;
                        if (m.IsGenericMethodDefinition)
                        {
                            var arg = manager.BaseType != null && manager.BaseType.IsGenericType
                                ? manager.BaseType.GetGenericArguments()[0] : null;
                            if (arg == null) continue;
                            call = m.MakeGenericMethod(arg);
                        }

                        var result = call.Invoke(null, null);
                        var obj = result as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                        if (obj != null)
                        {
                            Plugin.Dbg("GameContext: live data via " + manager.Name + "." + call.Name);
                            return obj;
                        }
                    }
                    probe = probe.BaseType;
                }
                Plugin.Dbg("GameContext: DataManager.GetData() not found");
            }
            catch (Exception e)
            {
                Plugin.Dbg("GameContext.GrabLiveData: " + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }

        // ------------------------------------------------------- save-file source

        private static string _savePath;
        private static DateTime _lastRead = DateTime.MinValue;
        private static Dictionary<string, string> _cached;

        /// <summary>
        /// Reads the scalar values this feature needs out of the save file. Cached for
        /// a few seconds so a burst of messages does not re-read the disk each time.
        /// </summary>
        private static Dictionary<string, string> ReadSaveFile()
        {
            var map = new Dictionary<string, string>();
            try
            {
                if (_cached != null && (DateTime.UtcNow - _lastRead).TotalSeconds < 10.0) return _cached;

                if (_savePath == null)
                {
                    string root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "LocalLow");
                    if (!Directory.Exists(root)) return map;

                    // Company/product folder names belong to the game, so search for
                    // the file instead of hard-coding them.
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        try
                        {
                            var hits = Directory.GetFiles(dir, "playerdata.json", SearchOption.AllDirectories);
                            if (hits.Length > 0) { _savePath = hits[0]; break; }
                        }
                        catch { }
                    }
                    if (_savePath == null)
                    {
                        Plugin.Dbg("GameContext: playerdata.json not found");
                        return map;
                    }
                    Plugin.Dbg("GameContext: save file = " + _savePath);
                }

                string json = File.ReadAllText(_savePath, Encoding.UTF8);
                foreach (var key in new[]
                {
                    "gold", "experience", "totalPlaytimeSeconds", "playerName",
                    "totalClickCount", "mouseDailyClicks", "keyboardDailyClicks"
                })
                {
                    var v = JsonScalar(json, key);
                    if (!string.IsNullOrEmpty(v)) map[key] = v;
                }

                _cached = map;
                _lastRead = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Plugin.Dbg("GameContext.ReadSaveFile: " + e.GetType().Name + ": " + e.Message);
            }
            return map;
        }

        /// <summary>Reads "key": value for a string / number / bool, or null.</summary>
        private static string JsonScalar(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i += pat.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == ':' || json[i] == '\t' ||
                                       json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length) return null;

            if (json[i] == '"')
            {
                int end = json.IndexOf('"', i + 1);
                return end > i ? json.Substring(i + 1, end - i - 1) : null;
            }
            int e = i;
            while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != '\n') e++;
            return json.Substring(i, e - i).Trim();
        }
    }
}
