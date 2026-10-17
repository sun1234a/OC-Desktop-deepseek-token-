using System;
using System.IO;
using System.Text;
using BepInEx.Configuration;

namespace OcAI
{
    /// <summary>
    /// Plugin settings, stored in BepInEx\config\com.lenovo.ocdesktop.ocai.cfg
    /// (and editable later from the in-game settings panel).
    /// </summary>
    internal sealed class OcAIConfig
    {
        public ConfigEntry<string> ApiKey;
        public ConfigEntry<string> BaseUrl;
        public ConfigEntry<string> Model;
        public ConfigEntry<float> Temperature;
        public ConfigEntry<int> MaxTokens;
        public ConfigEntry<int> HistoryTurns;
        public ConfigEntry<bool> EnableHeadPatBalance;
        public ConfigEntry<float> HeadRegionTop;
        public ConfigEntry<string> SystemPromptExtra;
        public ConfigEntry<bool> VerboseLogging;
        public ConfigEntry<string> EditKeyHotkey;
        public ConfigEntry<bool> BubbleSelfTest;
        public ConfigEntry<bool> EnableCallSound;
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<float> BubbleOffsetX;
        public ConfigEntry<float> BubbleLift;
        public ConfigEntry<float> BubbleDropDown;
        public ConfigEntry<float> BubbleStandOffsetX;
        public ConfigEntry<float> SizeScale;

        // --- right-click AI chat ---
        public ConfigEntry<bool> EnableChat;
        public ConfigEntry<string> ChatInputTips;
        public ConfigEntry<int> ChatMaxTokens;
        public ConfigEntry<bool> ChatSplitLongReplies;
        public ConfigEntry<bool> SitFailOpen;
        public ConfigEntry<bool> RequireSitting;
        public ConfigEntry<float> InputOffsetY;
        public ConfigEntry<float> InputOffsetX;
        public ConfigEntry<bool> EnableWebSearch;
        public ConfigEntry<bool> SearchAlways;
        public ConfigEntry<bool> WebSearchSelfTest;
        public ConfigEntry<bool> DisplayGuard;

        public static OcAIConfig Bind(ConfigFile cfg)
        {
            var c = new OcAIConfig();

            c.ApiKey = cfg.Bind(
                "DeepSeek", "ApiKey", "",
                "DeepSeek API key (sk-...). Leave empty to use the offline fallback lines.");

            c.BaseUrl = cfg.Bind(
                "DeepSeek", "BaseUrl", "https://api.deepseek.com",
                "DeepSeek API base URL. Do not add /v1 here; the paths are appended.");

            c.Model = cfg.Bind(
                "DeepSeek", "Model", "deepseek-chat",
                "Model name, e.g. deepseek-chat or deepseek-reasoner.");

            c.Temperature = cfg.Bind(
                "DeepSeek", "Temperature", 0.8f,
                "Sampling temperature for the character's replies. 0.8 keeps her in " +
                "character without wandering off the question; 1.1 was tried and produced " +
                "answers that did not address what was asked.");

            c.MaxTokens = cfg.Bind(
                "DeepSeek", "MaxTokens", 160,
                "Max tokens per reply. Keep small: this is a desktop pet, short lines read better.");

            c.HistoryTurns = cfg.Bind(
                "DeepSeek", "HistoryTurns", 6,
                "How many previous exchanges to send as context.");

            c.EnableHeadPatBalance = cfg.Bind(
                "HeadPat", "EnableHeadPatBalance", true,
                "Click the character's head to pop a bubble showing the DeepSeek account balance.");

            c.HeadRegionTop = cfg.Bind(
                "HeadPat", "HeadRegionPercent", 1.0f,
                "Top fraction of the character that responds to a click. 1.0 = anywhere " +
                "on her counts as a pat (native desktop-pet behaviour, and immune to " +
                "anchor imprecision). Lower it, e.g. 0.4, to require the actual head.");

            c.SystemPromptExtra = cfg.Bind(
                "Persona", "SystemPromptExtra", "",
                "Extra instructions appended to the auto-generated persona prompt.");

            c.VerboseLogging = cfg.Bind(
                "Debug", "VerboseLogging", true,
                "Write detailed diagnostics to BepInEx\\plugins\\ocai_debug.txt.");

            c.EditKeyHotkey = cfg.Bind(
                "UI", "EditKeyHotkey", "None",
                "How to open the API key editor from the keyboard. One of: MiddleMouse, " +
                "ScrollLock, Insert, Pause, F8, None. Default None: middle mouse is now " +
                "the chat trigger, and the settings panel already carries an " +
                "'AI 接入 Key' row. Deliberately avoids F9: on many laptops F9 is the " +
                "screen-lock / airplane-mode key and never reaches the game.");
            c.BubbleSelfTest = cfg.Bind(
                "Debug", "BubbleSelfTest", true,
                "Pop a test bubble a few seconds after launch. Proves the bubble display " +
                "works without needing a head-pat or a valid API key.");

            c.EnableCallSound = cfg.Bind(
                "Sound", "EnableCallSound", true,
                "Play a short synthesised 'call' when the pet is clicked. The tone design " +
                "is ported from dsh-whale-girl-pet: a sine arpeggio (ascending C major on " +
                "success, descending A minor on failure) with a 10 ms attack, a hold to 40% " +
                "of the note and an exponential tail.");

            c.Enabled = cfg.Bind(
                "General", "Enabled", true,
                "Master switch for the whole plugin. Also toggled from the in-game settings " +
                "panel. When off, clicking the pet does nothing at all: no bubble, no " +
                "sound, no network request.");

            c.BubbleOffsetX = cfg.Bind(
                "Bubble", "OffsetX", 0f,
                "Horizontal nudge for the speech bubble, in screen pixels. Negative moves " +
                "it left. The bubble is already centred on the character, so 0 is normally " +
                "right; use this if the pet's sprite is not centred on its origin.");

            c.BubbleLift = cfg.Bind(
                "Bubble", "Lift", 55f,
                "Distance in screen pixels between her head and the middle of the " +
                "bubble. Bigger = bubble higher. Measured behaviour: at 26 the bubble " +
                "sat right on her scalp while she was standing (climbing looked fine), " +
                "55 lifts it clear of the hair. Tip: Ctrl+Up / Ctrl+Down in game changes " +
                "it live and saves it here.");

            c.BubbleDropDown = cfg.Bind(
                "Bubble", "DropDown", 0f,
                "Extra downward shift in screen pixels, applied after Lift. Normally 0. " +
                "Raise it only if something makes the bubble float too high, for example " +
                "an oversized accessory inflating her bounds. The bubble is kept inside " +
                "the window whatever these two add up to.");

            c.BubbleStandOffsetX = cfg.Bind(
                "Bubble", "StandOffsetX", 130f,
                "While she is STANDING, shift the bubble this many pixels to the right of " +
                "her head so it does not cover her face. While she lies along the screen " +
                "edge the bubble stays centred above her instead. Tip: Ctrl+Left / " +
                "Ctrl+Right in game changes it live.");

            c.SizeScale = cfg.Bind(
                "Bubble", "SizeScale", 0f,
                "How much to enlarge every bubble offset for the character's body size. " +
                "0 = auto (recommended): the plugin watches her world-space height and " +
                "scales automatically, so 1x / 1.5x / 2x all keep the same visual gap. " +
                "Set a positive number to override, e.g. 1.5 when using the 1.5x body.");

            // ------------------------------------------------------------- chat
            c.EnableChat = cfg.Bind(
                "Chat", "EnableChat", true,
                "While she is SITTING, put the cursor on her and press the F key to open " +
                "the game's input modal and talk to DeepSeek. Her answer appears in the " +
                "speech bubble. Type /clear in the same box to wipe the remembered " +
                "conversation. F6/F7 are deliberately not used: on many laptops they are " +
                "system keys and never reach the game.");

            c.ChatInputTips = cfg.Bind(
                "Chat", "InputTips", "想和我说什么？",
                "Placeholder text shown in the chat input modal.");

            c.InputOffsetY = cfg.Bind(
                "Chat", "InputOffsetY", 1080f,
                "Distance of the chat input box from the BOTTOM edge of the screen, in " +
                "pixels. Bigger = higher up. NOTE: this window presents its content with the " +
                "vertical axis inverted relative to the panel layer, so the value is mirrored " +
                "internally (measured: panel layer y=272 was drawn near the TOP while Unity " +
                "called it BOTTOM-RIGHT). 1080 lands the box in the bottom-right corner on a " +
                "1080-tall screen while still clearing the taskbar; confirmed in game.");

            c.InputOffsetX = cfg.Bind(
                "Chat", "InputOffsetX", 460f,
                "Distance of the chat input box from the RIGHT edge of the screen, in " +
                "pixels. Bigger = further left. 460 keeps the confirm button clear of the " +
                "window edge (at 300 it was clipped).");

            c.ChatMaxTokens = cfg.Bind(
                "Chat", "MaxTokens", 400,
                "Max tokens per chat answer. Bigger allows longer replies, but the bubble " +
                "is small: two or three sentences read best.");

            c.ChatSplitLongReplies = cfg.Bind(
                "Chat", "SplitLongReplies", true,
                "Split a long answer at sentence enders and show the parts one after " +
                "another, instead of shrinking one huge bubble to fit.");

            c.SitFailOpen = cfg.Bind(
                "Chat", "TreatUnknownPoseAsSitting", true,
                "If her pose / state machine cannot be read at all, still allow the " +
                "chat key. Keeps the feature usable if a game update renames " +
                "the sitting state; set false to demand positive proof of sitting.");

            c.RequireSitting = cfg.Bind(
                "Chat", "RequireSitting", true,
                "Require the desk pose before the F key opens the chat. Default ON, and it " +
                "works by hooking OcSittingState.OnEnter/OnExit (the game's own sitting " +
                "state), because neither the Spine clip name nor the state machine's " +
                "current state is usable: measured live, the clip stays 'new/standing-e' " +
                "while she sits, GetCurrentStateType() answers the literal string " +
                "'Il2CppSystem.Type', and GetCurrentState() comes back as the declared " +
                "IState wrapper. If the hooks fail to install, the gate falls back to the " +
                "clip/pose checks and TreatUnknownPoseAsSitting decides the unknown case. " +
                "Set false to let the F key work wherever she is.");

            // ------------------------------------------------------------- web search
            c.EnableWebSearch = cfg.Bind(
                "Search", "EnableWebSearch", true,
                "Let the chat look things up online. Your question is sent to a search " +
                "engine, the top hits are pasted into the prompt as context, and DeepSeek " +
                "answers from them. The DeepSeek chat API itself has no browsing, so this " +
                "is the plugin doing the search. Set false to stay fully offline.");

            c.SearchAlways = cfg.Bind(
                "Search", "SearchAlways", false,
                "false (default): only search when the question looks time-sensitive " +
                "(contains 最新/今天/现在/新闻/股价/天气/搜一下 ...). true: search before " +
                "every message -- more accurate for factual questions, but every chat then " +
                "waits for a search round trip.");

            c.WebSearchSelfTest = cfg.Bind(
                "Debug", "WebSearchSelfTest", true,
                "Once, a few seconds after launch, run a real web search from inside the " +
                "game and write the outcome (engine reached, hits found, model answer) to " +
                "ocai_debug.txt. Makes the 联网 path verifiable with no typing. Set false " +
                "to keep the plugin from touching the network on startup.");

            // ------------------------------------------------------------- display guard
            c.DisplayGuard = cfg.Bind(
                "Display", "ProtectDesktopDisplay", true,
                "Stop this game from permanently changing the desktop display mode. The game " +
                "has its own window manager (WindowDisplaySync.ApplyResolution / " +
                "DisplayManager.MoveToDisplay / SwitchWindowedAndSync) which can rewrite the " +
                "resolution and the desktop DPI value; Windows then REARRANGES THE DESKTOP " +
                "ICONS, which is the damage this prevents. With the guard on, the plugin " +
                "records the resolution that was in use when it loaded and restores it if the " +
                "game changes it. Set false if the game ever needs to switch modes on purpose " +
                "(e.g. moving to another monitor).");

            return c;
        }

        public bool HasKey
        {
            get
            {
                var k = ApiKey != null ? ApiKey.Value : null;
                return !string.IsNullOrEmpty(k) && k.Trim().Length > 8;
            }
        }
    }
}
