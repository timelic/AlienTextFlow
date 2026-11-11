namespace AlienTextFlow;

/// <summary>Read-only design-space advances; no Unity materials or rasterization.</summary>
internal sealed class AdvanceMetrics
{
    private readonly struct Advances
    {
        private readonly ushort _a, _b, _c, _d, _e, _f;
        public Advances(BinaryReader reader)
        {
            _a = reader.ReadUInt16(); _b = reader.ReadUInt16();
            _c = reader.ReadUInt16(); _d = reader.ReadUInt16();
            _e = reader.ReadUInt16(); _f = reader.ReadUInt16();
        }
        public ushort At(int index) => index switch { 0 => _a, 1 => _b, 2 => _c, 3 => _d, 4 => _e, _ => _f };
    }

    private sealed class Face
    {
        public readonly float Units;
        public readonly Dictionary<uint, Advances> Glyphs;
        public Face(BinaryReader reader)
        {
            Units = reader.ReadUInt16();
            var count = reader.ReadUInt32();
            if (Units <= 0 || count > 100000) throw new InvalidDataException("Invalid metrics table.");
            Glyphs = new Dictionary<uint, Advances>((int)count);
            for (uint i = 0; i < count; i++) Glyphs.Add(reader.ReadUInt32(), new Advances(reader));
        }
    }
    private readonly Face _latin, _chinese;

    public AdvanceMetrics(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "ATFADV1\0")
            throw new InvalidDataException("Unknown metrics table format.");
        _latin = new Face(reader);
        _chinese = new Face(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing metrics data.");
    }

    public bool TryPredict(string text, float availableEm, int minimumWidth, out int predicted)
    {
        var sums = new float[6];
        for (int i = 0; i < text.Length; i++)
        {
            uint codepoint = text[i];
            if (IsZeroWidth(codepoint)) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                codepoint = (uint)char.ConvertToUtf32(text[i], text[++i]);
            var face = _latin;
            if (!face.Glyphs.TryGetValue(codepoint, out var advance))
            {
                face = _chinese;
                if (!face.Glyphs.TryGetValue(codepoint, out advance))
                {
                    predicted = 100;
                    return false; // Unsupported shaping or fallback: use TMP.
                }
            }
            for (int index = 0; index < sums.Length; index++) sums[index] += advance.At(index) / face.Units;
        }
        predicted = minimumWidth;
        for (int index = 0; 100 - index * 5 >= minimumWidth; index++)
            if (sums[index] <= availableEm)
            {
                predicted = 100 - index * 5;
                break;
            }
        return true;
    }

    public int FaceFor(uint codepoint) => _latin.Glyphs.ContainsKey(codepoint) ? 0 : _chinese.Glyphs.ContainsKey(codepoint) ? 1 : -1;

    public static bool IsZeroWidth(uint codepoint) => codepoint == 0x200b || codepoint == 0x200c || codepoint == 0x200d || codepoint == 0x2060 || codepoint == 0xfeff;

    public bool TryBalancedWidth(string text, int width, out float lineWidthEm)
    {
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var prefix = new float[starts.Length + 1];
        for (int element = 0; element < starts.Length; element++)
        {
            float advanceEm = 0;
            int end = element + 1 < starts.Length ? starts[element + 1] : text.Length;
            for (int i = starts[element]; i < end; i++)
            {
                uint codepoint = text[i];
                if (IsZeroWidth(codepoint)) continue;
                if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
                    codepoint = (uint)char.ConvertToUtf32(text[i], text[++i]);
                var face = _latin;
                if (!face.Glyphs.TryGetValue(codepoint, out var advance))
                {
                    face = _chinese;
                    if (!face.Glyphs.TryGetValue(codepoint, out advance)) { lineWidthEm = 0; return false; }
                }
                advanceEm += advance.At((100 - width) / 5) / face.Units;
            }
            prefix[element + 1] = prefix[element] + advanceEm;
        }
        float wordBreak = float.PositiveInfinity, forcedBreak = float.PositiveInfinity;
        for (int i = 1; i < starts.Length; i++)
        {
            float extent = Math.Max(prefix[i], prefix[starts.Length] - prefix[i]);
            forcedBreak = Math.Min(forcedBreak, extent);
            char before = text[starts[i] - 1], after = text[starts[i]];
            bool cjkBoundary = (before >= 0x2e80 || after >= 0x2e80) &&
                "（【《「『([{".IndexOf(before) < 0 && "，。！？；：、）】》」』)]}".IndexOf(after) < 0;
            if (char.IsWhiteSpace(before) || before == '-' || cjkBoundary) wordBreak = Math.Min(wordBreak, extent);
        }
        lineWidthEm = float.IsPositiveInfinity(wordBreak) ? forcedBreak : wordBreak;
        return !float.IsInfinity(lineWidthEm) && lineWidthEm > 0;
    }
}
