using System;
using System.Collections;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Reproduces the engine's phone-app layout math from game state, so
    /// a view can know where each line of an email / chat sits inside its
    /// scroller without hooking draw calls.
    ///
    /// - <see cref="MeasureHeight"/> calls the engine's own text measure
    ///   (GClass105.smethod_42 — the measure-only path, no drawing) with
    ///   the same font / scale / spacing / wrap width the app draws with.
    /// - <see cref="ImageHeight"/> reads the inline-image texture height
    ///   from GClass127.dictionary_0 (the "{key}" body tokens).
    /// - <see cref="SetScroll"/> drives a GClass45 scroller. Scroll
    ///   position is the distance scrolled down from the top of the
    ///   content, so scrolling to a line's top offset puts that line at
    ///   the top of the viewport. The setter clamps to [0, max].
    /// </summary>
    internal static class TextLayout
    {
        public enum Font { MailBody, Phone }

        // GClass7 (asset root) → smethod_1() → font set.
        private const string AssetsTypeName      = "#=q0eFdIPqnyZ$RtGuEw$WGAw==";
        private const string FontSetMethod       = "#=q8u8NUFKmJqRN8gCmiamZgg==";
        private const string MailBodyFontMethod  = "#=qms93k$OQHMZuJGu5ORo0QQ==";  // method_8
        private const string PhoneFontMethod     = "#=q8KAWdDHzWdBu8yhRLHBnCuwA7NONEfaj3xj3SPpmtvk=";  // method_13
        // GClass105 — text/sprite drawing.
        private const string DrawTypeName        = "#=qgvyw$0FrJv4yN4xJbBLpCQ==";
        // GClass127.dictionary_0 — inline email image textures by key.
        private const string ImagesTypeName      = "#=qnJmdH_EZfDuvfXnEK99owQ==";
        private const string ImagesField         = "#=qBflGY7afUFYFDbRpSvVMgg==";
        private const string TextureHeightMethod = "#=q0hg7w3619$VUeF8FnzMX0w==";  // Texture.method_2
        // GClass45 scroller — method_9 (set position, clamped).
        private const string ScrollSetMethod     = "#=qwp_MZlk8oypfEboUc0lURxGhlbj3CHrDYsyDG83oeJs=";

        private static bool        _resolved;
        private static MethodInfo? _fontSet;
        private static MethodInfo? _mailBodyFont;
        private static MethodInfo? _phoneFont;
        private static MethodInfo? _measure;
        private static Type?       _vec2Type;
        private static Type?       _alignType;
        private static PropertyInfo? _boundsHeight;
        private static FieldInfo?  _images;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            const BindingFlags stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            Type? tAssets = Nagoya.FindType(AssetsTypeName);
            _fontSet = tAssets?.GetMethod(FontSetMethod, stat, null, Type.EmptyTypes, null);
            if (_fontSet != null)
            {
                _mailBodyFont = _fontSet.ReturnType.GetMethod(MailBodyFontMethod, inst, null, Type.EmptyTypes, null);
                _phoneFont    = _fontSet.ReturnType.GetMethod(PhoneFontMethod,    inst, null, Type.EmptyTypes, null);
            }

            // smethod_42(string, Vector2, Font, Align, scale, spacing,
            // wrapWidth, truncateWidth, extraCharSpacing) → Bounds2.
            // Matched by shape: the only 9-arg string-first static on
            // the draw type with an enum 4th arg and five floats.
            Type? tDraw = Nagoya.FindType(DrawTypeName);
            if (tDraw != null)
            {
                foreach (MethodInfo m in tDraw.GetMethods(stat))
                {
                    if (m.ReturnType.Name != "Bounds2") continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length != 9 || p[0].ParameterType != typeof(string)) continue;
                    if (p[1].ParameterType.Name != "Vector2" || !p[3].ParameterType.IsEnum) continue;
                    bool floats = true;
                    for (int i = 4; i < 9; i++) floats &= p[i].ParameterType == typeof(float);
                    if (!floats) continue;
                    _measure   = m;
                    _vec2Type  = p[1].ParameterType;
                    _alignType = p[3].ParameterType;
                    _boundsHeight = m.ReturnType.GetProperty("Height", inst);
                    break;
                }
            }

            _images = Nagoya.FindType(ImagesTypeName)?.GetField(ImagesField, stat);
        }

        public static bool IsReady
        {
            get { Resolve(); return _measure != null && _boundsHeight != null && _fontSet != null; }
        }

        /// <summary>Height of <paramref name="text"/> as the engine would
        /// lay it out. Alignment 0 = left, 2 = right. Returns null if the
        /// measure path isn't available.</summary>
        public static float? MeasureHeight(string text, Font font, int align,
                                           float lineSpacing, float doubleNewlineSpacing, float wrapWidth)
        {
            Resolve();
            if (_measure == null || _boundsHeight == null || _fontSet == null
                || _vec2Type == null || _alignType == null) return null;
            try
            {
                object? fonts = _fontSet.Invoke(null, null);
                MethodInfo? fontGetter = font == Font.MailBody ? _mailBodyFont : _phoneFont;
                if (fonts == null || fontGetter == null) return null;
                object? f = fontGetter.Invoke(fonts, null);
                object bounds = _measure.Invoke(null, new object?[]
                {
                    text, Activator.CreateInstance(_vec2Type, 0f, 0f), f,
                    Enum.ToObject(_alignType, align),
                    lineSpacing, doubleNewlineSpacing, wrapWidth, float.MaxValue, 0f,
                })!;
                return (float)_boundsHeight.GetValue(bounds)!;
            }
            catch { return null; }
        }

        /// <summary>Height of the inline image the engine draws for body
        /// token <c>{key}</c>, or null if the engine has no texture for it
        /// (in which case it draws nothing and takes no space).</summary>
        public static float? ImageHeight(string key)
        {
            Resolve();
            try
            {
                if (!(_images?.GetValue(null) is IDictionary dict)) return null;
                object? tex = dict.Contains(key) ? dict[key] : null;
                if (tex == null) return null;
                MethodInfo? h = tex.GetType().GetMethod(TextureHeightMethod,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return h == null ? (float?)null : Convert.ToSingle(h.Invoke(tex, null));
            }
            catch { return null; }
        }

        /// <summary>Set a GClass45 scroller's position (distance from the
        /// top of the content). The engine clamps it to the valid range.</summary>
        public static void SetScroll(object? scroller, float position)
        {
            if (scroller == null) return;
            try
            {
                MethodInfo? set = scroller.GetType().GetMethod(ScrollSetMethod,
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
                set?.Invoke(scroller, new object[] { Math.Max(0f, position) });
            }
            catch { }
        }
    }
}
