using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ElizaAccess.Common
{
    /// <summary>
    /// Reads what a WorldScreen scene is showing, from game state:
    ///
    /// - The background: <c>WorldScreen.maybe_0</c> (the only
    ///   <c>Maybe&lt;ScriptLocation&gt;</c> field), keyed by
    ///   <c>ScriptLocation.string_0</c> ("cafe-a", "nightclub", …).
    /// - The characters: <c>WorldScreen.hashSet_1</c> (the only
    ///   <c>HashSet&lt;CharacterState&gt;</c>). Each entry is a
    ///   ScriptCharacter plus fading-in / fading-out flags; ones fading
    ///   out are treated as gone. ShowOrHideCharacter and
    ///   ChangeLocation script events edit this set — dialogue lines
    ///   never change the picture.
    ///
    /// A character's sprite is <c>dictionary_0[locationKey]</c> if it has
    /// a per-location version, else its default (WorldScreen.method_13).
    /// The figure key mirrors the F11 dump / description naming:
    /// "Nora__nightclub" for a location version, else "Nora".
    ///
    /// Horizontal placement (also WorldScreen.method_13): a sprite that
    /// positions itself uses its texture offset; otherwise centre slot
    /// (bool_4), right slot (bool_2, mirrored), or the left slot.
    /// </summary>
    internal static class SceneReader
    {
        public enum Position { Left, Center, Right }

        public sealed class Figure
        {
            public string Key = "";       // description key ("Nora__nightclub")
            public string BaseKey = "";   // character key ("Nora")
            public float  X;              // horizontal centre, screen units
            public Position Slot;
        }

        // ScriptLocation.string_0
        private const string LocationNameField  = "#=qt7Iq3iBTpDcAV6YkO1EKrw==";
        // ScriptCharacter fields
        private const string CharNameField      = "#=qxj3t6IMbrKOzd_pj75PR_g==";
        private const string CharVisualField    = "#=qUV$_qeZplB770Mv8WMzXBg==";
        private const string CharVariantsField  = "#=qF54fjgEvBqv8GyHaV__dDIYmWlR_6F6ydpP_ZQGxW$A=";
        private const string CharRightField     = "#=qJN7K0xPBkJJqBABCnMk73jRXx4xh71cTAku7HJk6t$4=";   // bool_2
        private const string CharCenterField    = "#=qMutsCu4vvHerGutuwy0X9zVRew2FGl$11eOihgaP0dc=";   // bool_4
        // CharacterState (nested in WorldScreen)
        private const string StateCharField     = "#=qpm1wqrHg_I_n5h_NOKLeGQ==";
        private const string StateFadingOut     = "#=qVXbqFHnfb_cLYofKJOIF7Q==";  // bool_1
        // Texture.vector2_2 (draw offset) and Texture.method_1 (width)
        private const string TextureOffsetField = "#=qJO4KWdPZobZnwIQAYxT$LA==";
        private const string TextureWidthMethod = "#=qJRcvbVTCItFcbbiz4XVd0Q==";
        // Maybe<T>.HasValue / GetValue
        private const string MaybeHasValue      = "#=qgvgNPArrY68Bug9C$7DmMg==";
        private const string MaybeGetValue      = "#=qE_kIMPOWNCxXDU2FQiQZEw==";

        // Slot geometry from WorldScreen.method_13 (screen is 3840 wide).
        private const float LeftSlotX       = 432f;
        private const float CenterSlotX     = 1916f;
        private const float RightSlotEdge   = 3321f;
        private const float ThirdWidth      = 1280f;

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _resolved;
        private static FieldInfo? _locationField, _charactersField;
        private static MethodInfo? _locHas, _locGet;
        private static FieldInfo? _locName;
        private static FieldInfo? _stateChar, _stateFadingOut;
        private static FieldInfo? _charName, _charVisual, _charVariants, _charRight, _charCenter;
        private static FieldInfo? _visualTextures, _visualSelfPositioned;
        private static FieldInfo? _texOffset, _vecX;
        private static MethodInfo? _texWidth;

        private static void Resolve(Type worldScreen)
        {
            if (_resolved) return;
            _resolved = true;
            foreach (FieldInfo f in worldScreen.GetFields(Inst))
            {
                Type ft = f.FieldType;
                if (!ft.IsGenericType) continue;
                Type arg = ft.GetGenericArguments()[0];
                if (_locationField == null && arg.Name == "ScriptLocation" && ft.Name.StartsWith("Maybe"))
                    _locationField = f;
                else if (_charactersField == null && arg.Name == "CharacterState"
                         && ft.GetGenericTypeDefinition() == typeof(HashSet<>))
                    _charactersField = f;
            }
            if (_locationField != null)
            {
                Type mt = _locationField.FieldType;
                _locHas = mt.GetMethod(MaybeHasValue, Inst, null, Type.EmptyTypes, null);
                _locGet = mt.GetMethod(MaybeGetValue, Inst, null, Type.EmptyTypes, null);
                _locName = mt.GetGenericArguments()[0].GetField(LocationNameField, Inst);
            }
            if (_charactersField != null)
            {
                Type st = _charactersField.FieldType.GetGenericArguments()[0];
                _stateChar      = st.GetField(StateCharField, Inst);
                _stateFadingOut = st.GetField(StateFadingOut, Inst);
            }
            Type? tChar = Nagoya.FindType("ScriptCharacter");
            if (tChar != null)
            {
                _charName     = tChar.GetField(CharNameField, Inst);
                _charVisual   = tChar.GetField(CharVisualField, Inst);
                _charVariants = tChar.GetField(CharVariantsField, Inst);
                _charRight    = tChar.GetField(CharRightField, Inst);
                _charCenter   = tChar.GetField(CharCenterField, Inst);
            }
            if (_charVisual != null)
            {
                // The sprite type has one Texture[] (layers) and one bool
                // ("positions itself").
                foreach (FieldInfo f in _charVisual.FieldType.GetFields(Inst))
                {
                    if (f.FieldType.IsArray) _visualTextures = f;
                    else if (f.FieldType == typeof(bool)) _visualSelfPositioned = f;
                }
            }
            Type? tTex = Nagoya.FindType("Texture");
            if (tTex != null)
            {
                _texOffset = tTex.GetField(TextureOffsetField, Inst);
                _texWidth  = tTex.GetMethod(TextureWidthMethod, Inst, null, Type.EmptyTypes, null);
                if (_texOffset != null)
                    foreach (FieldInfo f in _texOffset.FieldType.GetFields(Inst))
                        if (f.FieldType == typeof(float)) { _vecX = f; break; }
            }
        }

        /// <summary>The current background's key, or null if none / not
        /// readable.</summary>
        public static string? LocationKey(object worldScreen)
        {
            Resolve(worldScreen.GetType());
            if (_locationField == null || _locHas == null || _locGet == null || _locName == null) return null;
            try
            {
                object? maybe = _locationField.GetValue(worldScreen);
                if (maybe == null || !(bool)_locHas.Invoke(maybe, null)!) return null;
                object? loc = _locGet.Invoke(maybe, null);
                return loc == null ? null : _locName.GetValue(loc) as string;
            }
            catch { return null; }
        }

        /// <summary>Characters currently on screen (not fading out),
        /// ordered left to right. Null if the set couldn't be read this
        /// tick (the game thread may be mutating it) — callers should
        /// treat that as "unknown", not "empty".</summary>
        public static List<Figure>? Figures(object worldScreen, string? locationKey)
        {
            Resolve(worldScreen.GetType());
            if (_charactersField == null || _stateChar == null || _charName == null) return null;
            var states = new List<object>();
            try
            {
                if (!(_charactersField.GetValue(worldScreen) is IEnumerable set)) return null;
                foreach (object s in set) states.Add(s);
            }
            catch { return null; }

            var figures = new List<Figure>();
            foreach (object state in states)
            {
                try
                {
                    if (_stateFadingOut?.GetValue(state) is bool fadingOut && fadingOut) continue;
                    object? ch = _stateChar.GetValue(state);
                    if (ch == null) continue;
                    string name = _charName.GetValue(ch) as string ?? "";
                    if (name.Length == 0) continue;

                    object? visual = _charVisual?.GetValue(ch);
                    string key = name;
                    if (locationKey != null && _charVariants?.GetValue(ch) is IDictionary variants
                        && variants.Contains(locationKey))
                    {
                        visual = variants[locationKey];
                        key = name + "__" + locationKey;
                    }

                    float x = PlacementX(ch, visual);
                    figures.Add(new Figure
                    {
                        Key = key, BaseKey = name, X = x,
                        Slot = x < ThirdWidth ? Position.Left : x < 2 * ThirdWidth ? Position.Center : Position.Right,
                    });
                }
                catch { }
            }
            figures.Sort((a, b) => a.X.CompareTo(b.X));
            return figures;
        }

        // Horizontal centre of the sprite's first layer, per method_13.
        private static float PlacementX(object ch, object? visual)
        {
            float width = 0f, offsetX = 0f;
            bool selfPositioned = false;
            try
            {
                if (visual != null && _visualTextures?.GetValue(visual) is Array layers && layers.Length > 0)
                {
                    object? tex = layers.GetValue(0);
                    if (tex != null)
                    {
                        if (_texWidth != null) width = Convert.ToSingle(_texWidth.Invoke(tex, null));
                        object? off = _texOffset?.GetValue(tex);
                        if (off != null && _vecX != null) offsetX = (float)_vecX.GetValue(off)!;
                    }
                }
                selfPositioned = visual != null && _visualSelfPositioned?.GetValue(visual) is bool b && b;
            }
            catch { }

            if (selfPositioned)                              return offsetX + width / 2f;
            if (_charCenter?.GetValue(ch) is bool c && c)    return CenterSlotX;
            if (_charRight?.GetValue(ch) is bool r && r)     return RightSlotEdge - width / 2f;
            return LeftSlotX + width / 2f;
        }

        // ── Description lookups (Localization/*/images.json) ───────────

        public static string? LocationLabel(string locationKey)
            => Loc.TryGet($"image.location.{locationKey}.label");

        public static string? LocationDescription(string locationKey)
            => Loc.TryGet($"image.location.{locationKey}.desc");

        /// <summary>Spoken name; a location version falls back to the
        /// base character, then to the raw key.</summary>
        public static string FigureName(Figure f)
            => Loc.TryGet($"image.char.{f.Key}.name")
               ?? Loc.TryGet($"image.char.{f.BaseKey}.name")
               ?? f.BaseKey;

        public static string FigureFadeIn(Figure f)
            => Loc.TryGet($"image.char.{f.Key}.fade_in")
               ?? Loc.TryGet($"image.char.{f.BaseKey}.fade_in")
               ?? FigureName(f);

        public static string? FigureDescription(Figure f)
            => Loc.TryGet($"image.char.{f.Key}.desc") ?? Loc.TryGet($"image.char.{f.BaseKey}.desc");

        public static string PositionWord(Position p)
            => Loc.Get(p == Position.Left ? "image.pos.left"
                     : p == Position.Center ? "image.pos.center"
                     : "image.pos.right");
    }
}
