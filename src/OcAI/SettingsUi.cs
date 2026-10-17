using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OcAI
{
    /// <summary>
    /// API configuration UI, built by REUSING the game's own widgets instead of
    /// creating new ones. Two pieces:
    ///
    ///  1. UI.TopicImaginationInputWidget -- the game's existing text-input modal
    ///     (it already owns an InputTxtWidget, confirm/cancel buttons, pointer
    ///     trail and CJK IME support). We show it seeded with our own title and
    ///     current value, and intercept its confirm click.
    ///
    ///  2. A settings row injected into SettingPanel by cloning an existing
    ///     OcSettingWidget row, so the entry looks native.
    ///
    /// Everything is reflection-based: the interop type names are known, but
    /// binding at compile time to private game members is brittle across builds.
    /// </summary>
    internal static class SettingsUi
    {
        internal static Type TypeInputWidget;      // UI.TopicImaginationInputWidget
        internal static Type TypeSelectTopic;      // UI.SelectTopicWidget
        internal static Type TypeBaseUiWidget;     // UI.BaseUiWidget
        internal static Type TypeSettingPanel;     // SettingPanel

        private static object _liveInputWidget;

        /// <summary>Which setting the modal is currently editing.</summary>
        internal enum Target { None, ApiKey, BaseUrl, Model, Persona }
        private static Target _target = Target.None;

        // ------------------------------------------------------------------ init

        internal static void Locate(Assembly game)
        {
            TypeInputWidget = game.GetType("UI.TopicImaginationInputWidget");
            TypeSelectTopic = game.GetType("UI.SelectTopicWidget");
            TypeBaseUiWidget = game.GetType("UI.BaseUiWidget");
            TypeSettingPanel = game.GetType("SettingPanel");
            Plugin.Dbg("SettingsUi.Locate: input=" + (TypeInputWidget != null) +
                       " settingPanel=" + (TypeSettingPanel != null));
        }

        // -------------------------------------------------- modal reuse plumbing

        /// <summary>Harmony postfix target: remembers the modal instance.</summary>
        internal static void NoteInputWidget(object __instance)
        {
            try
            {
                if (__instance != null && TypeInputWidget != null &&
                    TypeInputWidget.IsInstanceOfType(__instance))
                {
                    _liveInputWidget = __instance;
                    Plugin.Dbg("input modal captured");
                }
            }
            catch { }
        }

        /// <summary>Finds the modal in the scene if we have not seen it yet.</summary>
        private static object FindInputWidget()
        {
            if (_liveInputWidget != null) return _liveInputWidget;
            try
            {
                if (TypeInputWidget == null) return null;
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(ToIl2Cpp(TypeInputWidget));
                if (arr != null && arr.Length > 0)
                {
                    _liveInputWidget = arr[0];
                    Plugin.Dbg("input modal found by scene scan");
                }
            }
            catch (Exception e) { Plugin.Dbg("FindInputWidget failed: " + e.Message); }
            return _liveInputWidget;
        }

        /// <summary>
        /// Logs the confirm / close button screen rectangles, via the modal's own
        /// public accessors. Needed because pressing Enter does NOT confirm this
        /// modal -- only the buttons do -- so a caller (or a test harness) has to
        /// know where they actually are.
        /// </summary>
        private static void LogButtonPositions(object w)
        {
            try
            {
                var topic = ToTopicWidget(w);
                if (topic == null) return;

                foreach (var name in new[] { "GetConfirmBtn", "GetCloseBtn" })
                {
                    var m = topic.GetType().GetMethod(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m == null) { Plugin.Dbg(name + ": method missing"); continue; }

                    var btn = m.Invoke(topic, null);
                    if (btn == null) { Plugin.Dbg(name + ": null"); continue; }

                    var b = btn as UnityEngine.UI.Button;
                    if (b == null)
                    {
                        var ob = btn as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                        if (ob != null) b = ob.TryCast<UnityEngine.UI.Button>();
                    }
                    if (b == null) { Plugin.Dbg(name + ": not a Button"); continue; }

                    // Getting the rect: `transform as RectTransform` returns null on
                    // these interop wrappers, so use the typed component getter and
                    // fall back to positional maths.
                    UnityEngine.RectTransform rt = null;
                    try { rt = b.GetComponent<UnityEngine.RectTransform>(); }
                    catch (Exception e) { Plugin.Dbg(name + " GetComponent: " + e.Message); }

                    if (rt != null)
                    {
                        var corners = new UnityEngine.Vector3[4];
                        rt.GetWorldCorners(corners);
                        var cam2 = UnityEngine.Camera.main;
                        if (cam2 != null)
                        {
                            float sx = 0f, sy = 0f;
                            for (int i = 0; i < 4; i++)
                            {
                                var sp = cam2.WorldToScreenPoint(corners[i]);
                                sx += sp.x; sy += UnityEngine.Screen.height - sp.y;
                            }
                            Plugin.Dbg(string.Format("{0} at ({1:F0},{2:F0})", name, sx / 4f, sy / 4f));
                            continue;
                        }
                        Plugin.Dbg(name + ": no camera");
                        continue;
                    }

                    // Fallback: world position of the transform.
                    try
                    {
                        var cam3 = UnityEngine.Camera.main;
                        var wp = b.transform.position;
                        var sp3 = cam3.WorldToScreenPoint(wp);
                        Plugin.Dbg(string.Format("{0} pos ({1:F0},{2:F0}) [transform fallback]",
                            name, sp3.x, UnityEngine.Screen.height - sp3.y));
                    }
                    catch (Exception e) { Plugin.Dbg(name + " fallback: " + e.Message); }
                }
            }
            catch (Exception e) { Plugin.Dbg("LogButtonPositions: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>
        /// Fires a balance request right after a key is saved and reports the real
        /// outcome, so a bad key is diagnosed immediately rather than on the next
        /// head-pat.
        /// </summary>
        private static void VerifyKeyAsync()
        {
            var token = Plugin.ResetToken();
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var r = await Plugin.Api.BalanceAsync(token).ConfigureAwait(false);
                    if (r.Ok)
                    {
                        string cur = string.IsNullOrEmpty(r.Currency) ? "" : " " + r.Currency;
                        Plugin.EnqueueSpeak("Key 可用，还剩 " + (r.TotalBalance ?? "?") + cur);
                        Plugin.Info("[OcAI] key verified, balance = " + r.TotalBalance + cur);
                        if (Plugin.Cfg == null || Plugin.Cfg.EnableCallSound.Value) CallSound.PlayOk();
                    }
                    else
                    {
                        Plugin.EnqueueSpeak(Plugin.ShortErrorPublic(r.Error));
                        Plugin.Info("[OcAI] key verification failed: " + r.Error);
                        if (Plugin.Cfg == null || Plugin.Cfg.EnableCallSound.Value) CallSound.PlayFail();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Dbg("VerifyKeyAsync: " + e.Message);
                    Plugin.EnqueueSpeak("验证失败，稍后再试");
                }
            }, token);
        }

        /// <summary>
        /// Reads the text currently inside the modal's input widget.
        ///
        /// Strongly typed on purpose. The previous reflection version always
        /// returned null ("modal confirm for ApiKey -> '&lt;null&gt;'") because
        /// PropertyInfo.GetValue does not see members on Il2CppInterop wrappers.
        /// UI.SelectTopicWidget._topicInputTxt is a UI.InputTxtWidget, which exposes
        /// GetTxt()/SetTxt(), so interop can call it directly.
        /// </summary>
        private static string ReadInputText(object widget)
        {
            try
            {
                var topic = ToTopicWidget(widget);
                if (topic == null) { Plugin.Dbg("ReadInputText: not a SelectTopicWidget"); return null; }

                var input = topic._topicInputTxt;
                if (input == null) { Plugin.Dbg("ReadInputText: _topicInputTxt is null"); return null; }

                var txt = input.GetTxt();
                Plugin.Dbg("ReadInputText -> '" + (txt ?? "<null>") + "'");
                return txt;
            }
            catch (Exception e)
            {
                Plugin.Dbg("ReadInputText error: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Writes text into the modal's input field, best effort.</summary>
        private static void SeedInputText(object widget, string value)
        {
            try
            {
                var topic = ToTopicWidget(widget);
                if (topic == null) { Plugin.Dbg("SeedInputText: not a SelectTopicWidget"); return; }
                var input = topic._topicInputTxt;
                if (input == null) { Plugin.Dbg("SeedInputText: _topicInputTxt is null"); return; }

                value = value ?? "";

                // Clear through the underlying TMP field as well. The game's own
                // SetTxt alone left stale text behind, so a recycled modal handed
                // back a previous (wrong) value on confirm.
                //
                // ALSO raise the character limit: UI.InputTxtWidget enforces
                // _maxWeightedLength (weighted, CJK counts double) and mirrors it into
                // the TMP_InputField's characterLimit. The default cut the API key in
                // half ("key 就读取了一半").
                try
                {
                    var tmp = input._nameInputTxt;

                    try { input._maxWeightedLength = 256; }
                    catch (Exception e) { Plugin.Dbg("SeedInputText set maxWeightedLength: " + e.Message); }

                    if (tmp != null)
                    {
                        tmp.characterLimit = 0;   // 0 = unlimited in TMP
                        tmp.text = value;
                        Plugin.Dbg("SeedInputText: characterLimit=" + tmp.characterLimit +
                                   " maxWeightedLength=" + input._maxWeightedLength);
                    }
                    else Plugin.Dbg("SeedInputText: _nameInputTxt is null");
                }
                catch (Exception e) { Plugin.Dbg("SeedInputText tmp: " + e.Message); }

                input.SetTxt(value);

                // read back so the log proves what the field really holds
                string check = null;
                try { check = input.GetTxt(); } catch { }
                Plugin.Dbg("SeedInputText set '" + value + "' -> reads back '" + (check ?? "<null>") + "'");
            }
            catch (Exception e) { Plugin.Dbg("SeedInputText error: " + e.Message); }
        }

        /// <summary>Casts the interop wrapper to the game's SelectTopicWidget base.</summary>
        private static UI.SelectTopicWidget ToTopicWidget(object o)
        {
            if (o == null) return null;
            var direct = o as UI.SelectTopicWidget;
            if (direct != null) return direct;
            var objBase = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            if (objBase == null) return null;
            try { return objBase.TryCast<UI.SelectTopicWidget>(); }
            catch (Exception e) { Plugin.Dbg("ToTopicWidget: " + e.Message); return null; }
        }

        /// <summary>
        /// Unity's interop overloads (FindObjectsOfTypeAll, GetComponentsInChildren)
        /// take Il2CppSystem.Type, not System.Type. This bridges the two.
        /// </summary>
        private static Il2CppSystem.Type ToIl2Cpp(Type t)
        {
            if (t == null) return null;
            try { return Il2CppInterop.Runtime.Il2CppType.From(t); }
            catch (Exception e)
            {
                Plugin.Dbg("ToIl2Cpp(" + t.FullName + ") failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        // -------------------------------------------------------------- opening

        /// <summary>
        /// Opens the game's input modal to edit one of our settings.
        /// Uses ShowText-style defaults so nothing depends on game data.
        /// </summary>
        internal static bool OpenEditor(Target target, string title, string current)
        {
            try
            {
                var w = FindInputWidget();
                if (w == null) { Plugin.Dbg("OpenEditor: input modal not found"); return false; }

                _target = target;

                // SelectTopicWidget.SetTopic(string) drives the modal's own field.
                var setTopic = w.GetType().GetMethod("SetTopic",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (setTopic != null) setTopic.Invoke(w, new object[] { "OCAI:" + title });

                // Make the widget visible.
                UnityEngine.MonoBehaviour mb = SettingsUi.ToMonoBehaviour(w);
                if (mb != null)
                {
                    mb.gameObject.SetActive(true);
                    var so = w.GetType().GetMethod("OnEnable",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Plugin.Dbg("OpenEditor: modal activated for " + target);
                }

                SeedInputText(w, current);

                // Enter does not confirm this modal (the game wires only the
                // buttons), so report where the confirm / close buttons are, both
                // for the log and so a test harness can click them exactly.
                LogButtonPositions(w);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Dbg("OpenEditor failed: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Harmony postfix on the modal's confirm button.</summary>
        internal static void OnConfirm(object __instance)
        {
            try
            {
                if (_target == Target.None) return;

                string text = ReadInputText(__instance);
                Plugin.Dbg("modal confirm for " + _target + " -> '" + (text ?? "<null>") + "'");
                if (text == null) { _target = Target.None; return; }
                text = text.Trim();
                if (text.Length == 0) { _target = Target.None; return; }

                // Guards: never let the modal's own title / a masked placeholder be
                // stored as a real value.
                if (text.StartsWith("OCAI:") || text.EndsWith("...") || text.Contains("（已设置"))
                {
                    Plugin.Dbg("ignoring non-value input");
                    _target = Target.None;
                    return;
                }

                switch (_target)
                {
                    case Target.ApiKey:
                        // Only a shape check here. The first version demanded
                        // "sk-" + 32 chars and rejected real Dashboard keys
                        // ("还是显示要输入32位"), so length is no longer asserted --
                        // DeepSeek itself decides validity, verified right below.
                        if (!text.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                        {
                            Plugin.Dbg("refusing API key candidate (no sk- prefix): len=" + text.Length);
                            Plugin.EnqueueSpeak("Key 要以 sk- 开头哦");
                            _target = Target.None;
                            return;
                        }

                        Plugin.Cfg.ApiKey.Value = text.Trim();
                        try { Plugin.Cfg.ApiKey.ConfigFile.Save(); } catch { }
                        Plugin.Info("[OcAI] API key saved (" + text.Length + " chars)");
                        Plugin.EnqueueSpeak("收到，正在验证…");

                        // Prove it immediately instead of failing silently later.
                        VerifyKeyAsync();
                        break;
                    case Target.BaseUrl:
                        Plugin.Cfg.BaseUrl.Value = text;
                        Plugin.EnqueueSpeak("接口地址记住了");
                        break;
                    case Target.Model:
                        Plugin.Cfg.Model.Value = text;
                        Plugin.EnqueueSpeak("换模型啦");
                        break;
                    case Target.Persona:
                        Plugin.Cfg.SystemPromptExtra.Value = text;
                        Plugin.EnqueueSpeak("我的性格设定更新了");
                        break;
                }
                try { Plugin.Cfg.ApiKey.ConfigFile.Save(); } catch { }
                _target = Target.None;
            }
            catch (Exception e)
            {
                Plugin.Dbg("OnConfirm error: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------- quick entry

        /// <summary>
        /// Opens the modal to enter a new API key.
        /// The field is seeded EMPTY on purpose: never put the real key (or a mask
        /// of it) into an editable field, or a stray confirm click would overwrite
        /// the stored key with the mask.
        /// </summary>
        internal static void OpenApiKeyEditor()
        {
            bool has = Plugin.Cfg.HasKey;
            string title = has ? "DeepSeek Key (已设置，输入即覆盖)" : "DeepSeek Key (未设置)";
            if (!OpenEditor(Target.ApiKey, title, ""))
                Plugin.Warn("[OcAI] could not open the input modal; edit BepInEx\\config\\" +
                            MyPluginInfo.PLUGIN_GUID + ".cfg instead");
        }

        // ------------------------------------------------- injected settings rows

        /// <summary>Our cloned rows, so we can recognise clicks on them.</summary>
        internal static UnityEngine.MonoBehaviour KeyRow;
        internal static UnityEngine.MonoBehaviour ToggleRow;
        internal static UnityEngine.MonoBehaviour ChatRow;
        internal static UnityEngine.MonoBehaviour ClearRow;
        internal static UnityEngine.MonoBehaviour SitRow;
        internal static UnityEngine.MonoBehaviour WebRow;
        internal static UnityEngine.MonoBehaviour GuardRow;
        private static bool _rowsInjected;

        /// <summary>Label shown on the toggle row, reflecting the master switch.</summary>
        private static string ToggleLabel()
        {
            bool on = Plugin.Cfg == null || Plugin.Cfg.Enabled.Value;
            return on ? "AI 接入：开（点此关闭）" : "AI 接入：已关闭（点此开启）";
        }

        /// <summary>Label for the chat row.</summary>
        private static string ChatLabel()
        {
            bool on = Plugin.Cfg == null || Plugin.Cfg.EnableChat.Value;
            return on ? "坐下按 F 聊天：开（点此关闭）" : "坐下按 F 聊天：已关闭（点此开启）";
        }

        /// <summary>Label for the memory row, including how much is remembered.</summary>
        private static string ClearLabel()
        {
            int n = 0;
            try { n = ChatFlow.HistoryCount / 2; } catch { }
            return "清空对话记忆（已记 " + n + " 轮）";
        }

        /// <summary>Label for the sitting-requirement row.</summary>
        private static string SitLabel()
        {
            bool on = Plugin.Cfg != null && Plugin.Cfg.RequireSitting.Value;
            return on ? "聊天要求先坐下：是（点此关闭）" : "聊天要求先坐下：否（点此开启）";
        }

        /// <summary>Label for the web-search row.</summary>
        private static string WebLabel()
        {
            bool on = Plugin.Cfg == null || Plugin.Cfg.EnableWebSearch.Value;
            return on ? "联网搜索：开（点此关闭）" : "联网搜索：已关闭（点此开启）";
        }

        /// <summary>Label for the desktop display protection row.</summary>
        private static string GuardLabel()
        {
            bool on = Plugin.Cfg == null || Plugin.Cfg.DisplayGuard.Value;
            return on ? "保护桌面显示：开（点此关闭）" : "保护桌面显示：已关闭（点此开启）";
        }

        /// <summary>
        /// Adds rows to the game's settings panel by cloning its own SizeSetting row, so
        /// the entries inherit the panel's look, layout and animation instead of being a
        /// bolted-on canvas:
        ///
        ///   AI 接入：开 / 已关闭              -> master on/off switch
        ///   DeepSeek Key                      -> opens the key input modal
        ///   坐下右键聊天：开 / 已关闭          -> right-click chat switch
        ///   清空对话记忆（已记 N 轮）          -> wipes the remembered conversation
        ///
        /// Called from a postfix on SettingPanel.OnShow().
        /// </summary>
        internal static void InjectSettingRow(object panel)
        {
            if (_rowsInjected || panel == null) return;
            try
            {
                var template = FindWidget("SizeSetting");
                if (template == null) { Plugin.Dbg("InjectSettingRow: no SizeSetting template"); return; }

                UnityEngine.MonoBehaviour src = ToMonoBehaviour(template);
                if (src == null) { Plugin.Dbg("InjectSettingRow: template not a MonoBehaviour"); return; }

                var parent = src.transform.parent;
                if (parent == null) { Plugin.Dbg("InjectSettingRow: template has no parent"); return; }

                KeyRow = CloneRow(src, parent, "AI 接入 Key");
                ToggleRow = CloneRow(src, parent, ToggleLabel());
                ChatRow = CloneRow(src, parent, ChatLabel());
                ClearRow = CloneRow(src, parent, ClearLabel());
                SitRow = CloneRow(src, parent, SitLabel());
                WebRow = CloneRow(src, parent, WebLabel());
                GuardRow = CloneRow(src, parent, GuardLabel());

                if (KeyRow == null && ToggleRow == null && ChatRow == null &&
                    ClearRow == null && SitRow == null && WebRow == null && GuardRow == null)
                {
                    Plugin.Dbg("InjectSettingRow: all clones failed");
                    return;
                }

                _rowsInjected = true;
                Plugin.Info("[OcAI] injected settings rows (key=" + (KeyRow != null) +
                            ", toggle=" + (ToggleRow != null) +
                            ", chat=" + (ChatRow != null) +
                            ", clear=" + (ClearRow != null) +
                            ", sit=" + (SitRow != null) +
                            ", web=" + (WebRow != null) +
                            ", guard=" + (GuardRow != null) + ")");
            }
            catch (Exception e)
            {
                Plugin.Dbg("InjectSettingRow failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Clones the template row once and relabels it.</summary>
        private static UnityEngine.MonoBehaviour CloneRow(
            UnityEngine.MonoBehaviour src, Transform parent, string label)
        {
            try
            {
                // Clone via the Transform, then fetch the component by name.
                //
                // Object.Instantiate(GameObject) returns an UNTYPED interop wrapper
                // here, and every TryCast on it yields null -- that is what produced
                // "InjectSettingRow: both clones failed". Transform.Instantiate keeps a
                // usable reference, and GetComponent(string) hands back the component.
                var cloneTf = UnityEngine.Object.Instantiate(src.transform, parent, false);
                if (cloneTf == null) { Plugin.Dbg("CloneRow: Instantiate null"); return null; }

                var go = cloneTf.gameObject;
                go.name = "OcAIRow_" + Math.Abs(label.GetHashCode() % 100000);
                go.SetActive(true);

                var comp = go.GetComponent(src.GetType().Name);
                if (comp == null) { Plugin.Dbg("CloneRow: GetComponent('" + src.GetType().Name + "') null"); return null; }

                var row = comp.TryCast<UnityEngine.MonoBehaviour>();
                if (row == null) { Plugin.Dbg("CloneRow: component not a MonoBehaviour"); return null; }

                SetRowLabel(row, label);
                try { cloneTf.SetAsLastSibling(); } catch { }

                // Report the geometry so the layout can be judged from the log: the
                // first version produced rows that were too cramped to click.
                try
                {
                    var rt = cloneTf.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        Plugin.Dbg(string.Format(
                            "CloneRow OK '{0}' rect={1:F0}x{2:F0} anchored={3} parent={4}",
                            label, rt.sizeDelta.x, rt.sizeDelta.y, rt.anchoredPosition,
                            parent != null ? parent.name : "?"));
                    }
                    else Plugin.Dbg("CloneRow OK '" + label + "' (no RectTransform)");
                }
                catch { }

                return row;
            }
            catch (Exception e) { Plugin.Dbg("CloneRow: " + e.GetType().Name + ": " + e.Message); return null; }
        }

        /// <summary>
        /// Makes a row easier to hit: taller rect and a transparent raycast target
        /// covering it, so a click anywhere on the row counts rather than only on the
        /// small +/- buttons the template carries.
        /// </summary>
        private static void MakeRowClickable(UnityEngine.MonoBehaviour row, float height)
        {
            try
            {
                var go = row.gameObject;
                var rt = go.GetComponent<RectTransform>();
                if (rt != null)
                {
                    var sd = rt.sizeDelta;
                    rt.sizeDelta = new Vector2(sd.x, height);
                }

                // An Image with no sprite still receives raycasts when it has a colour
                // with alpha > 0, which is the cheapest way to widen the hit area.
                var img = go.GetComponent<UnityEngine.UI.Image>();
                if (img == null) img = go.AddComponent<UnityEngine.UI.Image>();
                if (img != null)
                {
                    img.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                    img.raycastTarget = true;
                }

                var btn = go.GetComponent<UnityEngine.UI.Button>();
                if (btn == null) btn = go.AddComponent<UnityEngine.UI.Button>();
                if (btn != null)
                {
                    btn.transition = UnityEngine.UI.Selectable.Transition.None;
                    btn.targetGraphic = img;
                }

                Plugin.Dbg("MakeRowClickable: height=" + height + " img=" + (img != null) + " btn=" + (btn != null));
            }
            catch (Exception e) { Plugin.Dbg("MakeRowClickable: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>
        /// Writes the label into the row.
        ///
        /// SizeSetting.settingName is a plain public field, but it may only be read
        /// when the row builds its text, so the visible TMP components are updated too.
        /// </summary>
        private static void SetRowLabel(UnityEngine.MonoBehaviour row, string label)
        {
            try
            {
                var f = row.GetType().GetField("settingName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) f.SetValue(row, label);

                int n = 0;
                try
                {
                    var texts = row.GetComponentsInChildren<TMPro.TMP_Text>(true);
                    if (texts != null)
                    {
                        for (int i = 0; i < texts.Length; i++)
                        {
                            if (texts[i] == null) continue;
                            texts[i].text = label;
                            n++;
                        }
                    }
                }
                catch (Exception e) { Plugin.Dbg("SetRowLabel texts: " + e.Message); }

                Plugin.Dbg("SetRowLabel '" + label + "' -> field=" + (f != null) + " tmpTexts=" + n);
            }
            catch (Exception e) { Plugin.Dbg("SetRowLabel: " + e.Message); }
        }

        /// <summary>Refreshes the toggle row's label after the switch changes.</summary>
        private static void RefreshToggleLabel()
        {
            try { if (ToggleRow != null) SetRowLabel(ToggleRow, ToggleLabel()); }
            catch { }
        }

        private static void RefreshChatLabel()
        {
            try { if (ChatRow != null) SetRowLabel(ChatRow, ChatLabel()); }
            catch { }
        }

        private static void RefreshClearLabel()
        {
            try { if (ClearRow != null) SetRowLabel(ClearRow, ClearLabel()); }
            catch { }
        }

        private static void RefreshSitLabel()
        {
            try { if (SitRow != null) SetRowLabel(SitRow, SitLabel()); }
            catch { }
        }

        private static void RefreshWebLabel()
        {
            try { if (WebRow != null) SetRowLabel(WebRow, WebLabel()); }
            catch { }
        }

        private static void RefreshGuardLabel()
        {
            try { if (GuardRow != null) SetRowLabel(GuardRow, GuardLabel()); }
            catch { }
        }

        /// <summary>
        /// Harmony prefix on SizeSetting.OnAddButtonClick / OnSubButtonClick.
        /// Returning false suppresses the original method, so clicking our rows does
        /// NOT change the character size.
        /// NOTE: the target methods return void, so this prefix must NOT declare
        /// `out bool __result`; doing so makes HarmonyX fail with
        /// "IL Compile Error (unknown location)".
        /// </summary>
        internal static bool SizeSettingClickPrefix(object __instance)
        {
            try
            {
                if (KeyRow == null && ToggleRow == null && ChatRow == null &&
                    ClearRow == null && SitRow == null && WebRow == null && GuardRow == null) return true;

                var mb = ToMonoBehaviour(__instance);
                if (mb == null) return true;

                if (ToggleRow != null && mb == ToggleRow)
                {
                    bool nowOn = !(Plugin.Cfg != null && Plugin.Cfg.Enabled.Value);
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.Enabled.Value = nowOn;
                        try { Plugin.Cfg.Enabled.ConfigFile.Save(); } catch { }
                    }
                    Plugin.Dbg("settings toggle -> enabled=" + nowOn);
                    Plugin.Info("[OcAI] plugin " + (nowOn ? "enabled" : "disabled") + " from settings");
                    RefreshToggleLabel();

                    if (nowOn) Plugin.EnqueueSpeak("我回来啦");
                    else Plugin.HideBubbleNow();
                    return false;
                }

                if (KeyRow != null && mb == KeyRow)
                {
                    Plugin.Dbg("settings key row clicked -> open editor");
                    OpenApiKeyEditor();
                    Plugin.EnqueueSpeak("在这里输入 DeepSeek 的 API Key，然后点确定");
                    return false;
                }

                // Right-click chat on/off.
                if (ChatRow != null && mb == ChatRow)
                {
                    bool nowOn = !(Plugin.Cfg != null && Plugin.Cfg.EnableChat.Value);
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.EnableChat.Value = nowOn;
                        try { Plugin.Cfg.EnableChat.ConfigFile.Save(); } catch { }
                    }
                    Plugin.Dbg("settings chat row -> enableChat=" + nowOn);
                    Plugin.Info("[OcAI] right-click chat " + (nowOn ? "enabled" : "disabled"));
                    RefreshChatLabel();

                    Plugin.EnqueueSpeak(nowOn
                        ? "坐下之后右键点我就行"
                        : "好，那我不主动搭话了");
                    return false;
                }

                // Wipe the remembered conversation. This is the "clear" the user asked
                // for, available without typing a command.
                if (ClearRow != null && mb == ClearRow)
                {
                    bool had = ChatFlow.ClearHistory();
                    Plugin.Dbg("settings clear row -> cleared, had=" + had);
                    RefreshClearLabel();
                    Plugin.EnqueueSpeak(had ? "之前的都忘掉啦" : "我们还没聊过呢");
                    return false;
                }

                // Strict "must be sitting" gate. Off by default: see RequireSitting in
                // the config for why (the sit pose is not observable from the plugin).
                if (SitRow != null && mb == SitRow)
                {
                    bool nowOn = !(Plugin.Cfg != null && Plugin.Cfg.RequireSitting.Value);
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.RequireSitting.Value = nowOn;
                        try { Plugin.Cfg.RequireSitting.ConfigFile.Save(); } catch { }
                    }
                    Plugin.Dbg("settings sit row -> requireSitting=" + nowOn);
                    Plugin.Info("[OcAI] chat requires sitting = " + nowOn);
                    RefreshSitLabel();

                    Plugin.EnqueueSpeak(nowOn
                        ? "好，我坐下你才能找我聊"
                        : "好，不坐下也能找我聊");
                    return false;
                }

                // Web search on/off.
                if (WebRow != null && mb == WebRow)
                {
                    bool nowOn = !(Plugin.Cfg != null && Plugin.Cfg.EnableWebSearch.Value);
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.EnableWebSearch.Value = nowOn;
                        try { Plugin.Cfg.EnableWebSearch.ConfigFile.Save(); } catch { }
                    }
                    Plugin.Dbg("settings web row -> enableWebSearch=" + nowOn);
                    Plugin.Info("[OcAI] web search " + (nowOn ? "enabled" : "disabled"));
                    RefreshWebLabel();

                    Plugin.EnqueueSpeak(nowOn
                        ? "好，需要查资料的时候我自己去搜"
                        : "好，我就用自己知道的回答");
                    return false;
                }

                // Desktop display protection (prevents the icon rearrangement).
                if (GuardRow != null && mb == GuardRow)
                {
                    bool nowOn = !(Plugin.Cfg != null && Plugin.Cfg.DisplayGuard.Value);
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.DisplayGuard.Value = nowOn;
                        try { Plugin.Cfg.DisplayGuard.ConfigFile.Save(); } catch { }
                    }
                    Plugin.Dbg("settings guard row -> displayGuard=" + nowOn);
                    Plugin.Info("[OcAI] desktop display protection = " + nowOn);
                    RefreshGuardLabel();

                    Plugin.EnqueueSpeak(nowOn
                        ? "好，我会守住你桌面的分辨率"
                        : "好，那游戏可以自己改显示设置了");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Plugin.Dbg("SizeSettingClickPrefix: " + e.Message);
                return true;
            }
        }
        /// <summary>
        /// IL2CPP interop objects need TryCast, not a plain `as`: the wrapper is an
        /// Il2CppSystem.Object and `as` yields null even for a real MonoBehaviour
        /// (observed: "ShowViaTips: not a MonoBehaviour").
        /// </summary>
        internal static UnityEngine.MonoBehaviour ToMonoBehaviour(object o)
        {
            if (o == null) return null;
            var direct = o as UnityEngine.MonoBehaviour;
            if (direct != null) return direct;
            try
            {
                // Interop wrappers derive from Il2CppObjectBase; TryCast is an
                // instance method there, so get to the base type first.
                var baseObj = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (baseObj == null) return null;
                return baseObj.TryCast<UnityEngine.MonoBehaviour>();
            }
            catch (Exception e) { Plugin.Dbg("ToMonoBehaviour: " + e.GetType().Name + ": " + e.Message); }
            return null;
        }

        /// <summary>Finds a live widget of the given interop type name.</summary>
        private static object FindWidget(string typeFullName)
        {
            try
            {
                var t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType(typeFullName); } catch { return null; } })
                    .FirstOrDefault(x => x != null);
                if (t == null) return null;
                var il2 = Il2CppInterop.Runtime.Il2CppType.From(t);
                var arr = UnityEngine.Resources.FindObjectsOfTypeAll(il2);
                return (arr != null && arr.Length > 0) ? arr[0] : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Harmony entry points for the settings-row injection.</summary>
    internal static class SettingsRowPatch
    {
        /// <summary>Postfix on SettingPanel.OnShow / UI.SettingPanelWidget.OnEnable.</summary>
        internal static void PanelShown(object __instance)
        {
            try { SettingsUi.InjectSettingRow(__instance); }
            catch (Exception e) { Plugin.Dbg("PanelShown: " + e.Message); }
        }

        /// <summary>Prefix on SizeSetting.OnAddButtonClick / OnSubButtonClick.</summary>
        internal static bool SizeClick(object __instance)
        {
            return SettingsUi.SizeSettingClickPrefix(__instance);
        }
    }
}
