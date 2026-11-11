using System.Collections;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace AlienTextFlow;

public static class Runtime
{
    public const string Version = "0.2.0";
    private static Settings _settings = new Settings();
    private sealed class FontPair
    {
        public TMP_FontAsset Latin = null!;
        public TMP_FontAsset Chinese = null!;
    }
    private sealed class LabelState
    {
        public string Text = "";
        public float Size, Width, Height;
        public int AxisWidth;
        public Selection Selected;
        public VerticalAlignmentOptions OriginalVertical;
    }
    private readonly struct SelectionKey : IEquatable<SelectionKey>
    {
        private readonly string _text;
        private readonly int _width, _height, _style, _weight;
        private readonly bool _extraPadding, _richText;
        private readonly float _characterSpacing, _wordSpacing;
        public SelectionKey(TMP_Text text, float available, float height, float maximum)
        {
            _text = text.text;
            _width = (int)Math.Round(available / maximum * 1000);
            _height = (int)Math.Round(height / maximum * 1000);
            _style = (int)text.fontStyle;
            _weight = (int)text.fontWeight;
            _extraPadding = text.extraPadding;
            _richText = text.richText;
            _characterSpacing = text.characterSpacing;
            _wordSpacing = text.wordSpacing;
        }
        public bool Equals(SelectionKey other) => _text == other._text && _width == other._width && _height == other._height && _style == other._style && _weight == other._weight && _extraPadding == other._extraPadding && _richText == other._richText && _characterSpacing == other._characterSpacing && _wordSpacing == other._wordSpacing;
        public override bool Equals(object? obj) => obj is SelectionKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { return ((((_text.GetHashCode() * 397 ^ _width) * 397 ^ _height) * 397 ^ _style) * 397) ^ _weight; }
        }
    }
    private readonly struct Selection
    {
        public readonly int Width;
        public readonly bool Fits;
        public readonly bool TwoLines;
        public readonly float SizeRatio, Spacing;
        public Selection(int width, bool fits, bool twoLines = false, float sizeRatio = 1, float spacing = 0)
        { Width = width; Fits = fits; TwoLines = twoLines; SizeRatio = sizeRatio; Spacing = spacing; }
    }
    private static readonly Dictionary<int, FontPair> Fonts = new Dictionary<int, FontPair>();
    private static readonly Dictionary<TMP_Text, LabelState> Labels = new Dictionary<TMP_Text, LabelState>();
    private static readonly Dictionary<SelectionKey, Selection> Selections = new Dictionary<SelectionKey, Selection>();
    private static AdvanceMetrics? _advances;
    private static TMP_FontAsset? _originalFallback;
    private static bool _attempted;
    private static bool _errorLogged;
    private static int _measurements;
    private static readonly string Root = Path.Combine(Path.GetDirectoryName(typeof(Runtime).Assembly.Location)!, "AlienTextFlow");
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "AlienTextFlow", "mod.log");

    // Called before Draw.Awake. Only the explicitly requested test mode bypasses
    // game initialization, so a test never opens or writes the player's saves.
    public static bool Bootstrap(Draw draw)
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--alien-text-flow-self-test") < 0)
            return false;
        _originalFallback = draw.defaultfont;
        draw.enabled = false;
        draw.gameObject.AddComponent<SelfTestRunner>();
        return true;
    }

    public static void Ready(Draw draw)
    {
        try
        {
            _originalFallback = draw.defaultfont;
            if (!EnsureFonts()) return;
            var normal = GetPair(100);
            draw.defaultfont = normal.Latin;
            Labels.Clear(); // Do not retain label objects across scene reloads.
            if (draw.txt_slots != null)
                foreach (var field in draw.txt_slots)
                    if (field != null) field.fontAsset = normal.Latin;
            Log($"READY {Version}; real wdth axis 100..{_settings.MinWidthPercent}; Mona Sans + WenYuan Sans SC Flex; fixed grid");
        }
        catch (Exception ex) { LogOnce(ex); }
    }

    // A postfix is necessary: the original method restores width, fontSizeMax
    // and word wrapping every frame. We keep its rectangle and projected size.
    public static void Apply(AlienCharacter character)
    {
        try
        {
            if (character == null || character.sp == null || character.txt == null || !character.txt.gameObject.activeInHierarchy || !EnsureFonts()) return;
            var field = character.txt;
            Fit(field.textComponent, field.textComponent.fontSizeMax);
            ConfigureInput(field);
        }
        catch (Exception ex) { LogOnce(ex); }
    }

    public static void SetWrapping(TMP_Text text, TextWrappingModes original)
    {
        text.textWrappingMode = EnsureFonts()
            ? (Labels.TryGetValue(text, out var state) && state.Text == text.text && state.Selected.TwoLines ? TextWrappingModes.Normal : TextWrappingModes.NoWrap)
            : original;
    }

    public static void SetMaximum(TMP_Text text, float original)
    {
        text.fontSizeMax = EnsureFonts() ? original * _settings.FontSizeMultiplier : original;
    }

    internal static int Fit(TMP_Text text, float maximum)
    {
        var rect = text.rectTransform.rect;
        var margin = text.margin;
        float available = Mathf.Max(1, rect.width - margin.x - margin.z);
        float height = Mathf.Max(1, rect.height - margin.y - margin.w);
        if (Labels.TryGetValue(text, out var previous) && previous.Text == text.text &&
            Mathf.Abs(previous.Width - available) < 0.01f && Mathf.Abs(previous.Height - height) < 0.01f && Mathf.Abs(previous.Size - maximum) < 0.01f)
            return previous.AxisWidth;
        var originalVertical = previous != null ? previous.OriginalVertical : text.verticalAlignment;
        if (previous == null)
            text.OnPreRenderText += info =>
            {
                if (Labels.TryGetValue(text, out var state) && state.Selected.TwoLines && state.Text == text.text)
                    TwoLineLayout.CenterAndFixInputMetrics(text, info);
            };
        var key = new SelectionKey(text, available, height, maximum);
        if (Selections.TryGetValue(key, out var cached))
        {
            ApplySelection(text, maximum, cached, originalVertical);
            Labels[text] = new LabelState { Text = text.text, Size = maximum, Width = available, Height = height, AxisWidth = cached.Width, Selected = cached, OriginalVertical = originalVertical };
            return cached.Width;
        }
        text.characterWidthAdjustment = 0; // Never geometrically squash glyphs.
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.verticalAlignment = originalVertical;
        text.lineSpacing = 0;
        text.fontSizeMax = maximum;
        text.fontSizeMin = Mathf.Min(_settings.MinFontSizePixels, maximum);
        text.enableAutoSizing = false;
        text.fontSize = maximum;
        int chosen = _settings.MinWidthPercent;
        bool fits = false;
        var measured = Vector2.zero;
        int start = 100;
        if (text.fontStyle == FontStyles.Normal && text.characterSpacing == 0 && text.wordSpacing == 0 && !text.richText)
            _advances?.TryPredict(text.text, available / maximum, _settings.MinWidthPercent, out start);
        for (int width = start; width >= _settings.MinWidthPercent; width -= 5)
        {
            var pair = GetPair(width);
            PopulateGlyphs(pair, text.text);
            text.font = pair.Latin;
            _measurements++;
            measured = text.GetPreferredValues(text.text, float.PositiveInfinity, float.PositiveInfinity);
            chosen = width;
            if (measured.x <= available + 0.01f && measured.y <= height + 0.01f)
            {
                fits = true;
                break;
            }
        }
        // At the narrowest real design instance, TMP may finally reduce size.
        var selection = new Selection(chosen, fits);
        if (!fits && _settings.AllowTwoLines)
        {
            float singleSize = Mathf.Clamp(maximum * Mathf.Min(available / Mathf.Max(0.01f, measured.x), height / Mathf.Max(0.01f, measured.y)),
                Mathf.Min(_settings.MinFontSizePixels, maximum), maximum);
            if (TwoLineLayout.TryChoose(text, _advances, chosen, maximum, available, height, singleSize,
                    _settings.TwoLineMinimumGain, out var two))
                selection = new Selection(chosen, false, true, two.SizeRatio, two.Spacing);
        }
        ApplySelection(text, maximum, selection, originalVertical);
        if (Selections.Count >= 4096) Selections.Clear();
        Selections[key] = selection;
        Labels[text] = new LabelState { Text = text.text, Size = maximum, Width = available, Height = height, AxisWidth = chosen, Selected = selection, OriginalVertical = originalVertical };
        // Keep the input viewport and saved annotation unchanged.
        return chosen;
    }

    private static void PopulateGlyphs(FontPair pair, string text)
    {
        if (_advances == null) return;
        List<uint>? latin = null, chinese = null;
        for (int i = 0; i < text.Length; i++)
        {
            uint unicode = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                unicode = (uint)char.ConvertToUtf32(text[i], text[++i]);
            switch (_advances.FaceFor(unicode))
            {
                case 0 when !pair.Latin.characterLookupTable.ContainsKey(unicode):
                    (latin ??= new List<uint>()).Add(unicode);
                    break;
                case 1 when !pair.Chinese.characterLookupTable.ContainsKey(unicode):
                    (chinese ??= new List<uint>()).Add(unicode);
                    break;
            }
        }
        // One native face/atlas operation for a string, rather than one per
        // character. Keep Latin kerning; SC annotations do not need CJK GSUB.
        if (latin != null) pair.Latin.TryAddCharacters(latin.ToArray(), true);
        if (chinese != null) pair.Chinese.TryAddCharacters(chinese.ToArray(), false);
    }

    private static void ApplySelection(TMP_Text text, float maximum, Selection selection, VerticalAlignmentOptions originalVertical)
    {
        text.font = GetPair(selection.Width).Latin;
        text.characterWidthAdjustment = 0;
        text.textWrappingMode = selection.TwoLines ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        text.verticalAlignment = selection.TwoLines ? VerticalAlignmentOptions.Middle : originalVertical;
        text.lineSpacing = selection.Spacing;
        text.fontSizeMax = maximum;
        text.fontSizeMin = Mathf.Min(_settings.MinFontSizePixels, maximum);
        text.enableAutoSizing = !selection.Fits && !selection.TwoLines;
        text.fontSize = maximum * selection.SizeRatio;
        text.havePropertiesChanged = true;
    }

    private static void ConfigureInput(TMP_InputField field)
    {
        if (!Labels.TryGetValue(field.textComponent, out var state)) return;
        var desired = state.Selected.TwoLines ? TMP_InputField.LineType.MultiLineSubmit : TMP_InputField.LineType.SingleLine;
        if (field.lineType != desired) field.lineType = desired;
        // A TMP lineLimit of 2 truncates the stored string during UpdateLabel.
        // Limit visible lines by layout, never by deleting input characters.
        if (state.Selected.TwoLines && field.lineLimit != 0) field.lineLimit = 0;
    }

    internal static void RenderForLayout(TMP_Text text)
    {
        _measurements++;
        text.ForceMeshUpdate(true, false);
        TwoLineLayout.CenterAndFixInputMetrics(text, text.textInfo);
    }

    internal static bool EnsureFonts()
    {
        if (_attempted) return _settings.Enabled && Fonts.ContainsKey(100);
        _attempted = true;
        try
        {
            var config = Path.Combine(Root, "config.json");
            if (File.Exists(config)) _settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(config)) ?? new Settings();
            _settings.Validate();
            if (_settings.AllowTwoLines && !TwoLineLayout.SupportsInputMetrics)
            {
                _settings.AllowTwoLines = false;
                Log("Two-line input disabled: this TMP version uses different preferred-height fields.");
            }
            if (!_settings.Enabled) { Log("Disabled by config."); return false; }
            var metricsPath = Path.Combine(Root, "Fonts", "advances.bin");
            if (File.Exists(metricsPath)) _advances = new AdvanceMetrics(metricsPath);
            var normal = GetPair(100);
            Log($"FONTS {normal.Latin.faceInfo.familyName}; {normal.Chinese.faceInfo.familyName}");
            return true;
        }
        catch (Exception ex) { LogOnce(ex); return false; }
    }

    private static FontPair GetPair(int width)
    {
        if (Fonts.TryGetValue(width, out var cached)) return cached;
        var pair = new FontPair
        {
            Latin = LoadFont($"MonaSans-w{width}.ttf", $"AlienTextFlow Mona Sans W{width}"),
            Chinese = LoadFont($"WenYuanSansSC-w{width}.ttf", $"AlienTextFlow WenYuan Sans SC W{width}")
        };
        pair.Latin.fallbackFontAssetTable = new List<TMP_FontAsset> { pair.Chinese };
        if (_originalFallback != null) pair.Latin.fallbackFontAssetTable.Add(_originalFallback);
        Fonts.Add(width, pair);
        return pair;
    }

    private static TMP_FontAsset LoadFont(string file, string name)
    {
        var path = Path.Combine(Root, "Fonts", file);
        if (!File.Exists(path)) throw new FileNotFoundException("Packaged font missing", path);
        var font = TMP_FontAsset.CreateFontAsset(path, 0, 64, 8, GlyphRenderMode.SDFAA, 2048, 2048);
        if (font == null) throw new InvalidOperationException($"FontEngine could not open {path}");
        // The player can strip TMP's mobile shader. Reuse its existing SDF
        // shader/preset while assigning this font's own dynamic atlas.
        if (_originalFallback != null && _originalFallback.material != null)
        {
            var material = new Material(_originalFallback.material);
            material.mainTexture = font.atlasTextures[0];
            material.SetFloat("_TextureWidth", 2048);
            material.SetFloat("_TextureHeight", 2048);
            material.SetFloat("_GradientScale", 9);
            var generatedMaterial = font.material;
            font.material = material;
            if (generatedMaterial != null) Object.Destroy(generatedMaterial);
        }
        font.name = name;
        if (file.StartsWith("WenYuan", StringComparison.Ordinal)) font.getFontFeatures = false;
        font.hideFlags = HideFlags.DontUnloadUnusedAsset;
        font.isMultiAtlasTexturesEnabled = true;
        return font;
    }

    private static void LogOnce(Exception ex)
    {
        if (_errorLogged) return;
        _errorLogged = true;
        Log("ERROR " + ex);
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { /* Rendering must continue even if the log directory is unwritable. */ }
    }

    public sealed class SelfTestRunner : MonoBehaviour
    {
        private IEnumerator Start()
        {
            // Other components are already awake, but Draw never loaded Global
            // or initialized Steam / game progress in this process.
            yield return null;
            var reportPath = GetArgument("--alien-text-flow-report");
            var report = new List<object>();
            try
            {
                if (!EnsureFonts()) throw new InvalidOperationException("Fonts did not load.");
                var canvas = new GameObject("Font test canvas", typeof(Canvas));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var go = new GameObject("Font test label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.transform.SetParent(canvas.transform, false);
                var text = go.GetComponent<TextMeshProUGUI>();
                text.font = GetPair(100).Latin;
                text.rectTransform.sizeDelta = new Vector2(100f, 36f);
                text.margin = Vector4.zero;
                text.richText = false;
                text.alignment = TextAlignmentOptions.Center;
                text.overflowMode = TextOverflowModes.Overflow;
                const float size = 24f;
                foreach (var sample in new[] { "中文", "英文测试", "中文译注字", "WenYuan文渊字体", "MMMM", "MMMMMM", "MMMMMMMMMMMMMMMMMMMM", "这是一个非常长的中文译注用于确认最终能够缩小字号", new string('中', 49), "hello world", "混合 Latin 123" })
                {
                    text.text = sample;
                    var axisWidth = Fit(text, size);
                    text.ForceMeshUpdate(true, true);
                    var chosen = text.fontSize;
                    var ink = TwoLineLayout.GetInk(text);
                    var rendered = ink.Width;
                    var chineseCount = 0;
                    var missing = 0;
                    for (int i = 0; i < text.textInfo.characterCount; i++)
                    {
                        var info = text.textInfo.characterInfo[i];
                        if (info.textElement == null || info.textElement.unicode == 0x25A1) missing++;
                        if (info.fontAsset == GetPair(axisWidth).Chinese) chineseCount++;
                    }
                    var item = new { sample, axisWidth, chosen, rendered, height = ink.Height, gap = ink.Lines == 2 ? ink.Gap : (float?)null, chineseCount, missing, lineCount = text.textInfo.lineCount };
                    report.Add(item);
                    Log("TEST " + JsonConvert.SerializeObject(item));
                    if (missing != 0) throw new Exception("Missing glyph: " + sample);
                    if (chosen > size + 0.1f || rendered > 101f || text.textInfo.lineCount > 2 || ink.Height > 36.1f)
                        throw new Exception("Text did not fit: " + sample);
                    if (text.text != sample) throw new Exception("Layout must not insert line breaks into the saved annotation.");
                    if (ink.Lines == 2 && ink.Gap < 0.2f) throw new Exception("Wrapped lines overlap.");
                    if (sample == "MMMM" && chosen < size - 0.1f) throw new Exception("Short text shrank unnecessarily.");
                    if (sample == "MMMMMM" && (chosen < size - 0.1f || axisWidth == 100)) throw new Exception("Moderate text did not use a real narrow instance first.");
                    if (sample == "中文译注字" && (chosen < size - 0.1f || axisWidth == 100)) throw new Exception("Chinese text did not use a real narrow instance first.");
                    if (sample == "MMMMMMMMMMMMMMMMMMMM" && chosen >= size - 0.1f) throw new Exception("Extreme text did not shrink.");
                    if (sample == "中文" && chineseCount != 2) throw new Exception("Chinese fallback not used.");
                }
                // Same label, new rectangle: cached choices must be invalidated.
                text.text = "hello world";
                text.rectTransform.sizeDelta = new Vector2(80f, 36f);
                int narrow = Fit(text, size);
                text.ForceMeshUpdate(true, true);
                text.rectTransform.sizeDelta = new Vector2(300f, 36f);
                int wide = Fit(text, size);
                text.ForceMeshUpdate(true, true);
                if (narrow == 100 || wide != 100 || text.fontSize < size - 0.1f)
                    throw new Exception("Resize did not restore normal width / font size.");
                text.text = "a";
                if (Fit(text, size) != 100) throw new Exception("Text edit did not invalidate width cache.");
                var wrapping = TestTwoLines(text, canvas.transform, GetComponent<Draw>());
                var benchmark = Benchmark(canvas.transform);
                if (reportPath != null) File.WriteAllText(reportPath, JsonConvert.SerializeObject(new { status = "passed", version = Version, samples = report, wrapping, benchmark }, Formatting.Indented));
                Log("SELF_TEST_PASSED");
                Application.Quit(0);
            }
            catch (Exception ex)
            {
                if (reportPath != null) File.WriteAllText(reportPath, JsonConvert.SerializeObject(new { status = "failed", error = ex.ToString(), samples = report }, Formatting.Indented));
                Log("SELF_TEST_FAILED " + ex);
                Application.Quit(1);
            }
        }

        private static string? GetArgument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static object Benchmark(Transform parent)
        {
            var labels = new List<TextMeshProUGUI>();
            for (int i = 0; i < 216; i++)
            {
                var go = new GameObject("Performance label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.transform.SetParent(parent, false);
                var label = go.GetComponent<TextMeshProUGUI>();
                label.font = GetPair(100).Latin;
                label.rectTransform.sizeDelta = new Vector2(60, 34);
                label.margin = Vector4.zero;
                label.richText = false;
                label.overflowMode = TextOverflowModes.Overflow;
                labels.Add(label);
            }
            var before = _measurements;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < labels.Count; i++)
            {
                labels[i].text = (i % 3 == 0 ? "译注" : i % 3 == 1 ? "混合字体" : "translation") + (i % 36);
                Fit(labels[i], 20);
                labels[i].ForceMeshUpdate(true, true);
            }
            var pageMilliseconds = watch.Elapsed.TotalMilliseconds;
            var pageMeasurements = _measurements - before;
            var typingLabel = labels[0];
            var typing = new List<double>();
            foreach (var sentence in new[] { "new translation input performance", "请输入一个较长的中文翻译测试文本" })
                for (int length = 1; length <= sentence.Length; length++)
                {
                    typingLabel.text = sentence.Substring(0, length);
                    watch.Restart();
                    Fit(typingLabel, 20);
                    typingLabel.ForceMeshUpdate(true, true);
                    typing.Add(watch.Elapsed.TotalMilliseconds);
                }
            var typingMean = typing.Average();
            var typingMax = typing.Max();
            before = _measurements;
            watch.Restart();
            for (int frame = 0; frame < 120; frame++)
                foreach (var label in labels) Fit(label, 20);
            var steadyMilliseconds = watch.Elapsed.TotalMilliseconds / 120;
            var steadyMeasurements = _measurements - before;
            var result = new { labelCount = labels.Count, uniqueTexts = 36, pageMilliseconds, pageMeasurements, typingKeystrokes = typing.Count, typingMean, typingMax, steadyMilliseconds, steadyMeasurements };
            Log("BENCHMARK " + JsonConvert.SerializeObject(result));
            return result;
        }

        private static object TestTwoLines(TextMeshProUGUI text, Transform canvas, Draw draw)
        {
            var items = new List<object>();
            text.rectTransform.sizeDelta = new Vector2(60, 33.333f);
            foreach (var sample in new[] { "中文译注字", "中文译注字体", "中文译注字体测试", "hello world", "hello worlds", "electromagnetism", "这是（一个）测试。", "Latin 混合译注 123", new string('中', 49) })
            {
                text.text = sample;
                var before = text.rectTransform.sizeDelta;
                Fit(text, 20);
                text.ForceMeshUpdate(true, true);
                var ink = TwoLineLayout.GetInk(text);
                Log("WRAPTEST " + JsonConvert.SerializeObject(new { sample, size = text.fontSize, lines = ink.Lines, width = ink.Width, height = ink.Height, gap = ink.Gap }));
                if (before != text.rectTransform.sizeDelta || text.text != sample) throw new Exception("Layout changed rectangle or model text.");
                if (ink.Lines > 2 || ink.Width > 60.1f || ink.Height > 33.433f || (ink.Lines == 2 && ink.Gap < 0.2f))
                    throw new Exception("Unsafe two-line layout: " + sample);
                if (ink.Lines == 2) AssertInside(text);
                if (sample == "中文译注字" && ink.Lines != 1) throw new Exception("Five Chinese glyphs should retain the better single-line size.");
                if (sample == "中文译注字体" && (ink.Lines != 2 || text.fontSize < 15)) throw new Exception("Six Chinese glyphs should benefit from two lines.");
                if (sample == "hello world" && ink.Lines != 1) throw new Exception("A tiny size gain should not force an extra line.");
                if (sample == "hello worlds" && (ink.Lines != 2 || text.fontSize < 19)) throw new Exception("Longer English words should wrap at the space without shrinking.");
                items.Add(new { sample, size = text.fontSize, spacing = text.lineSpacing, lines = ink.Lines, width = ink.Width, height = ink.Height, gap = ink.Lines == 2 ? ink.Gap : (float?)null });
            }
            // Use a real input field, including TMP's appended zero-width
            // caret sentinel. Layout must never trim or add stored characters.
            var parent = Object.Instantiate(draw.text_prefab);
            parent.name = "Original game input preservation test";
            parent.transform.SetParent(canvas, false);
            var frame = parent.GetComponent<RectTransform>();
            frame.anchorMin = frame.anchorMax = new Vector2(0.5f, 0.5f);
            frame.sizeDelta = new Vector2(60, 33.333f);
            var input = parent.GetComponent<TMP_InputField>();
            input.onValueChanged.RemoveAllListeners();
            input.onEndEdit.RemoveAllListeners();
            input.onSubmit.RemoveAllListeners();
            input.onSelect.RemoveAllListeners();
            var childText = input.textComponent;
            childText.font = GetPair(100).Latin;
            childText.richText = false;
            input.richText = false;
            input.characterLimit = 49;
            Canvas.ForceUpdateCanvases();
            Log($"NATIVE_INPUT_RECT frame={frame.rect.width}x{frame.rect.height} text={childText.rectTransform.rect.width}x{childText.rectTransform.rect.height} margins={childText.margin}");
            bool caretRowsPassed = false;
            foreach (var sample in new[] { "中文译注字体测试", new string('中', 49), "a" })
            {
                input.text = sample;
                input.ForceLabelUpdate();
                Fit(childText, 20);
                ConfigureInput(input);
                input.ForceLabelUpdate();
                Fit(childText, 20);
                childText.ForceMeshUpdate(true, true);
                Log("NATIVE_FIT " + JsonConvert.SerializeObject(new { sample, renderedText = childText.text, size = childText.fontSize, lines = childText.textInfo.lineCount, two = Labels[childText].Selected.TwoLines }));
                if (input.text != sample) throw new Exception("Input text was truncated by a line limit.");
                if (childText.textInfo.lineCount > 2) throw new Exception("Input has more than two lines.");
                if (Labels[childText].Selected.TwoLines && (input.lineLimit != 0 || input.lineType != TMP_InputField.LineType.MultiLineSubmit))
                    throw new Exception("Input mode does not preserve Enter-to-submit / complete text.");
                if (Labels[childText].Selected.TwoLines) AssertInside(childText);
                if (sample == "中文译注字体测试")
                {
                    if (!Labels[childText].Selected.TwoLines) throw new Exception("Original input prefab did not exercise the two-line caret test.");
                    TestCaretRows(input, childText);
                    caretRowsPassed = true;
                }
            }
            if (!caretRowsPassed) throw new Exception("Two-line caret coverage is missing.");
            return new { samples = items, inputModelPreserved = true, caretRowsPassed };
        }

        private static void TestCaretRows(TMP_InputField input, TMP_Text text)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var generate = typeof(TMP_InputField).GetMethod("GenerateCaret", flags)!;
            var adjust = typeof(TMP_InputField).GetField("m_forceRectTransformAdjustment", flags)!;
            typeof(TMP_InputField).GetField("m_CaretVisible", flags)!.SetValue(input, true);
            var positions = new List<float>();
            var originalPosition = text.rectTransform.anchoredPosition;
            for (int line = 0; line < 2; line++)
            {
                int index = text.textInfo.lineInfo[line].firstCharacterIndex;
                input.caretPosition = index;
                adjust.SetValue(input, true);
                using var vertices = new UnityEngine.UI.VertexHelper();
                generate.Invoke(input, new object[] { vertices, Vector2.zero });
                if (vertices.currentVertCount != 4) throw new Exception("Native caret geometry is missing.");
                var a = new UnityEngine.UIVertex(); var b = new UnityEngine.UIVertex();
                vertices.PopulateUIVertex(ref a, 0); vertices.PopulateUIVertex(ref b, 1);
                float center = (a.position.y + b.position.y) / 2;
                float expected = (text.textInfo.characterInfo[index].ascender + text.textInfo.characterInfo[index].descender) / 2;
                if (Mathf.Abs(center - expected) > 0.1f) throw new Exception("Caret is on the wrong wrapped line.");
                if (Mathf.Abs(text.rectTransform.anchoredPosition.y - originalPosition.y) > 0.1f)
                    throw new Exception("Caret scrolled fitted text outside its input rectangle.");
                positions.Add(center);
            }
            if (positions[0] <= positions[1] + 1) throw new Exception("Both carets appeared on the same line.");
            Log("CARET_ROWS_PASSED " + JsonConvert.SerializeObject(positions));
        }

        private static void AssertInside(TMP_Text text)
        {
            var rect = text.rectTransform.rect;
            var margin = text.margin;
            for (int i = 0; i < text.textInfo.characterCount; i++)
            {
                var c = text.textInfo.characterInfo[i];
                if (!c.isVisible) continue;
                if (c.bottomLeft.x < rect.xMin + margin.x - 0.2f || c.topRight.x > rect.xMax - margin.z + 0.2f ||
                    c.bottomLeft.y < rect.yMin + margin.w - 0.2f || c.topLeft.y > rect.yMax - margin.y + 0.2f)
                    throw new Exception($"Glyph {i} quad {c.bottomLeft} .. {c.topRight} extends outside {rect} margin={margin} alignment={text.alignment}.");
            }
        }
    }
}
