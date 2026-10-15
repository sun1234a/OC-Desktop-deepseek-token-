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
                "DeepSeek", "Temperature", 1.1f,
                "Sampling temperature for the character's replies.");

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
                "UI", "EditKeyHotkey", "MiddleMouse",
                "How to open the API key editor. One of: MiddleMouse, ScrollLock, Insert, " +
                "Pause, F8, None. Deliberately avoids F9: on many laptops F9 is the " +
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
