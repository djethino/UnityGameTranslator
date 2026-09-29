using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// IL2CPP-safe texture utilities extracted from CustomFontLoader.
    /// All Unity API calls use reflection to avoid JIT crashes on IL2CPP.
    /// </summary>
    public static class TextureUtils
    {
        #region Cached fields

        private static MethodInfo _loadImageMethod;
        private static bool _loadImageMethodSearched;

        // Unity 2023.1+ on IL2CPP — see LoadImageThroughSpanWrapper
        private static MethodInfo _loadImageInjected;
        private static MethodInfo _marshalTexture;
        private static Type _spanWrapperType;
        private static FieldInfo _spanWrapperBegin;
        private static FieldInfo _spanWrapperLength;

        // Cached for MakeReadableCopy
        private static MethodInfo _blitMethod;
        private static bool _blitMethodSearched;
        private static MethodInfo _getTemporaryMethod;
        private static MethodInfo _releaseTemporaryMethod;
        private static PropertyInfo _rtActiveProp;

        // Cached for CreateSpriteSafe
        private static MethodInfo _spriteCreateMethod;
        private static bool _spriteCreateMethodSearched;

        // Unity 2023.1+ IL2CPP game that stripped Sprite.Create — see CreateSpriteThroughNative
        private static CreateSpriteInjected _createSpriteNative;
        private static MethodInfo _unmarshalSprite;
        private static bool _createSpriteNativeSearched;

        // Before 2023.1, the same game shape: the interop's own public icall wrapper
        private static MethodInfo _createSpriteInjectedManaged;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateSpriteInjected(IntPtr texture, IntPtr rect, IntPtr pivot, float pixelsPerUnit,
            uint extrude, int meshType, IntPtr border, byte generateFallbackPhysicsShape, IntPtr secondaryTextures);

        // Cached for ReadPixels
        private static MethodInfo _readPixelsMethod;
        private static bool _readPixelsMethodSearched;

        #endregion

        #region SetPixels32

        /// <summary>
        /// SetPixels32 via reflection for IL2CPP compatibility.
        /// On IL2CPP, Color32[] may need conversion to Il2CppStructArray.
        /// </summary>
        public static bool SetPixels32Safe(Texture2D texture, Color32[] colors)
        {
            if (texture == null || colors == null) return false;

            var texType = texture.GetType();
            foreach (var method in texType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "SetPixels32") continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                var paramType = parameters[0].ParameterType;
                try
                {
                    if (paramType == typeof(Color32[]))
                    {
                        method.Invoke(texture, new object[] { colors });
                        return true;
                    }

                    // IL2CPP: construct the expected array type from Color32[]
                    var ctor = paramType.GetConstructor(new Type[] { typeof(int) });
                    if (ctor != null)
                    {
                        var il2cppArray = ctor.Invoke(new object[] { colors.Length });
                        var indexer = paramType.GetProperty("Item");
                        if (indexer != null)
                        {
                            for (int i = 0; i < colors.Length; i++)
                                indexer.SetValue(il2cppArray, colors[i], new object[] { i });
                            method.Invoke(texture, new object[] { il2cppArray });
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] SetPixels32 reflection failed: {ex.Message}");
                }
            }

            // Last resort: set pixels one by one via SetPixel
            try
            {
                int w = texture.width;
                int h = texture.height;
                for (int i = 0; i < colors.Length && i < w * h; i++)
                {
                    int x = i % w;
                    int y = i / w;
                    texture.SetPixel(x, y, colors[i]);
                }
                return true;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] SetPixel fallback failed: {ex.Message}");
            }

            return false;
        }

        #endregion

        #region EncodeToPNG

        /// <summary>
        /// The texture's state for the export's log line.
        ///
        /// 🔴 **Alone in its own method, and read inside a try BY ITS CALLER.** `Texture.mipmapCount`
        /// and `Texture.isReadable` do not exist before Unity 2018.3/2019 — and the runtime resolves a
        /// missing member when it compiles the method that NAMES it, so named in EncodeToPngSafe they
        /// made the whole export unloadable on such a game, the try around them included (found by
        /// the Unity API floor check, 2026-09-22; the trap: analyse/pieges-projet.md §9).
        /// </summary>
        private static string DescribeForLog(Texture2D texture)
            => $"format={texture.format} size={texture.width}x{texture.height} mipmaps={texture.mipmapCount} readable={texture.isReadable}";

        /// <summary>
        /// Encode a Texture2D to PNG via reflection (handles IL2CPP where EncodeToPNG may differ).
        /// </summary>
        public static byte[] EncodeToPngSafe(Texture2D texture)
        {
            if (texture == null)
            {
                TranslatorCore.LogWarning("[TextureUtils] EncodeToPngSafe called with null texture");
                return null;
            }

            // Log the texture state up-front so failure diagnostics on user setups don't
            // need a second log round-trip to know what was attempted. Wrap individual
            // property reads in try/catch because a destroyed-but-not-yet-null texture
            // can throw on .format / .width access on some IL2CPP runtimes.
            string textureDiag = "?";
            try
            {
                textureDiag = DescribeForLog(texture);
            }
            catch (Exception ex)
            {
                textureDiag = $"(diag failed: {ex.GetType().Name}: {ex.Message})";
            }
            TranslatorCore.LogInfo($"[TextureUtils] EncodeToPNG attempt: {textureDiag}");

            int staticAttempts = 0;
            // Try ImageConversion.EncodeToPNG(texture) — newer Unity
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var imageConvType = asm.GetType("UnityEngine.ImageConversion");
                if (imageConvType == null) continue;

                foreach (var method in imageConvType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "EncodeToPNG") continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1)
                    {
                        staticAttempts++;
                        try
                        {
                            var result = method.Invoke(null, new object[] { texture });
                            if (result is byte[] bytes)
                            {
                                TranslatorCore.LogInfo($"[TextureUtils] EncodeToPNG returned byte[] ({bytes.Length} bytes) from ImageConversion in {asm.GetName().Name}");
                                return bytes;
                            }

                            // IL2CPP: might return Il2CppStructArray<byte>
                            if (result != null)
                            {
                                string resultType = result.GetType().FullName ?? result.GetType().Name;
                                byte[] extracted = ExtractByteArrayFromIl2Cpp(result);
                                if (extracted != null)
                                {
                                    TranslatorCore.LogInfo($"[TextureUtils] EncodeToPNG returned IL2CPP array ({resultType}, {extracted.Length} bytes), extracted to byte[]");
                                    return extracted;
                                }
                                TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG returned non-null but not extractable: type={resultType}");
                            }
                            else
                            {
                                TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG returned null from ImageConversion in {asm.GetName().Name} (no exception thrown — Unity likely refused to encode this texture/format combination)");
                            }
                        }
                        catch (Exception ex)
                        {
                            // TargetInvocationException wraps the real culprit (IL2CPP runtime
                            // failures, OOM on huge atlases, unsupported format on this build).
                            // Surface the inner so the user log shows the actual cause.
                            var inner = ex.InnerException ?? ex;
                            TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG threw via reflection: outer={ex.GetType().Name}: {ex.Message} | inner={inner.GetType().FullName}: {inner.Message}");
                            if (inner.StackTrace != null)
                                TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG inner stack: {inner.StackTrace}");
                        }
                    }
                }
            }

            // Try Texture2D.EncodeToPNG() — older Unity (instance method)
            int instanceAttempts = 0;
            try
            {
                var method = texture.GetType().GetMethod("EncodeToPNG", BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                {
                    instanceAttempts = 1;
                    var result = method.Invoke(texture, null);
                    if (result is byte[] bytes)
                    {
                        TranslatorCore.LogInfo($"[TextureUtils] EncodeToPNG returned byte[] ({bytes.Length} bytes) from instance method");
                        return bytes;
                    }
                    if (result != null)
                    {
                        string resultType = result.GetType().FullName ?? result.GetType().Name;
                        byte[] extracted = ExtractByteArrayFromIl2Cpp(result);
                        if (extracted != null)
                        {
                            TranslatorCore.LogInfo($"[TextureUtils] EncodeToPNG (instance) returned IL2CPP array ({resultType}, {extracted.Length} bytes)");
                            return extracted;
                        }
                        TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG (instance) returned non-null but not extractable: type={resultType}");
                    }
                    else
                    {
                        TranslatorCore.LogWarning("[TextureUtils] EncodeToPNG (instance) returned null");
                    }
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG (instance) threw: outer={ex.GetType().Name}: {ex.Message} | inner={inner.GetType().FullName}: {inner.Message}");
            }

            TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG exhausted all paths (static attempts: {staticAttempts}, instance attempts: {instanceAttempts}) — returning null");
            return null;
        }

        #endregion

        #region LoadImage

        /// <summary>
        /// Loads image data (PNG/JPG) into a Texture2D using reflection.
        /// Handles both ImageConversion.LoadImage (newer) and Texture2D.LoadImage (older).
        /// </summary>
        public static bool LoadImageToTexture(Texture2D texture, byte[] data)
        {
            if (texture == null || data == null || data.Length == 0)
                return false;

            if (!_loadImageMethodSearched)
            {
                _loadImageMethodSearched = true;
                FindLoadImageMethod();
            }

            if (_loadImageInjected != null)
                return LoadImageThroughSpanWrapper(texture, data);

            if (_loadImageMethod == null)
                return false;

            try
            {
                object dataArg = ConvertByteArrayForMethod(_loadImageMethod, data);

                object result;
                if (_loadImageMethod.IsStatic)
                {
                    result = _loadImageMethod.Invoke(null, new object[] { texture, dataArg });
                }
                else
                {
                    result = _loadImageMethod.Invoke(texture, new object[] { dataArg });
                }

                return result is bool b && b;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] LoadImage failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// LoadImage on Unity 2023.1+ under IL2CPP: the native entry point, handed the pinned bytes.
        ///
        /// 🔴 **The public LoadImage must never be called there.** From 2023.1 Unity passes arrays to
        /// native code as a <c>ManagedSpanWrapper</c> (pointer + length), so LoadImage has a managed
        /// body. The interop rebuilds that body and wraps the array in an Il2CppSystem.Span (2023) or
        /// ReadOnlySpan (6000) — a value type it treats as an object with no handle — and reading
        /// it is an AccessViolationException: the process dies, no catch runs, the log stops. Every
        /// such game died at startup once the About tab decoded a PNG while building its panels.
        ///
        /// What the rebuilt body meant to do is three lines: take the texture's native pointer,
        /// point a wrapper at the bytes, call <c>LoadImage_Injected</c>. Done here the same way,
        /// with the managed array pinned instead of copied into IL2CPP memory — native code reads
        /// it once, synchronously, and keeps nothing.
        /// </summary>
        private static bool LoadImageThroughSpanWrapper(Texture2D texture, byte[] data)
        {
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var nativeTexture = (IntPtr)_marshalTexture.Invoke(null, new object[] { texture });
                if (nativeTexture == IntPtr.Zero)
                {
                    TranslatorCore.LogWarning("[TextureUtils] LoadImage: the texture has no native object");
                    return false;
                }

                object wrapper = Activator.CreateInstance(_spanWrapperType);
                _spanWrapperBegin.SetValue(wrapper, pinned.AddrOfPinnedObject());
                _spanWrapperLength.SetValue(wrapper, data.Length);

                var result = _loadImageInjected.Invoke(null, new object[] { nativeTexture, wrapper, false });
                return result is bool b && b;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                TranslatorCore.LogWarning($"[TextureUtils] LoadImage (native entry) failed: {inner.GetType().Name}: {inner.Message}");
                return false;
            }
            finally
            {
                pinned.Free();
            }
        }

        /// <summary>
        /// The 2023.1+ IL2CPP shape: <c>ImageConversion.LoadImage_Injected(IntPtr, ref
        /// ManagedSpanWrapper, bool)</c> made public by the interop, plus what it needs — the
        /// texture's native pointer (<c>Object.MarshalledUnityObject.MarshalNotNull</c>, the call
        /// the rebuilt body itself starts with) and the wrapper's two fields. All or nothing: a
        /// shape found by halves falls back to nothing rather than to the body that kills the game.
        /// On Mono the method is private and never matches; below 2023.1 it does not exist.
        /// </summary>
        private static bool FindLoadImageInjected(Type imageConvType)
        {
            MethodInfo injected = null;
            foreach (var method in imageConvType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "LoadImage_Injected") continue;
                var p = method.GetParameters();
                if (p.Length == 3 && p[0].ParameterType == typeof(IntPtr) && p[1].ParameterType.IsByRef
                    && p[1].ParameterType.GetElementType()?.Name == "ManagedSpanWrapper"
                    && p[2].ParameterType == typeof(bool) && method.ReturnType == typeof(bool))
                {
                    injected = method;
                    break;
                }
            }
            if (injected == null) return false;

            var wrapperType = injected.GetParameters()[1].ParameterType.GetElementType();
            var begin = wrapperType.GetField("begin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var length = wrapperType.GetField("length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            MethodInfo marshal = null;
            var marshaller = typeof(UnityEngine.Object).GetNestedType("MarshalledUnityObject", BindingFlags.Public | BindingFlags.NonPublic);
            if (marshaller != null)
            {
                foreach (var method in marshaller.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "MarshalNotNull" && method.IsGenericMethodDefinition
                        && method.GetParameters().Length == 1 && method.ReturnType == typeof(IntPtr))
                    {
                        marshal = method.MakeGenericMethod(typeof(Texture2D));
                        break;
                    }
                }
            }

            if (begin == null || begin.FieldType != typeof(IntPtr) || length == null || length.FieldType != typeof(int) || marshal == null)
            {
                TranslatorCore.LogWarning($"[TextureUtils] LoadImage_Injected found without its parts (begin={begin != null}, length={length != null}, marshal={marshal != null}) — images will not load in this game");
                return true;
            }

            _loadImageInjected = injected;
            _marshalTexture = marshal;
            _spanWrapperType = wrapperType;
            _spanWrapperBegin = begin;
            _spanWrapperLength = length;
            TranslatorCore.LogInfo("[TextureUtils] Found ImageConversion.LoadImage_Injected (Unity 2023.1+ IL2CPP) — the public LoadImage is not used");
            return true;
        }

        private static void FindLoadImageMethod()
        {
            // Try ImageConversion.LoadImage first (newer Unity)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var imageConvType = asm.GetType("UnityEngine.ImageConversion");
                if (imageConvType == null) continue;

                // Unity 2023.1+ IL2CPP: the native entry, or nothing — never the public LoadImage.
                if (FindLoadImageInjected(imageConvType)) return;

                foreach (var method in imageConvType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "LoadImage") continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 2 && IsTextureType(parameters[0].ParameterType) && IsByteArrayType(parameters[1].ParameterType))
                    {
                        _loadImageMethod = method;
                        TranslatorCore.LogInfo($"[TextureUtils] Found ImageConversion.LoadImage({parameters[0].ParameterType.Name}, {parameters[1].ParameterType.Name})");
                        return;
                    }
                }
            }

            // Try Texture2D.LoadImage(byte[]) (older Unity)
            var texType = typeof(Texture2D);
            foreach (var method in texType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "LoadImage") continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && IsByteArrayType(parameters[0].ParameterType))
                {
                    _loadImageMethod = method;
                    TranslatorCore.LogInfo($"[TextureUtils] Found Texture2D.LoadImage({parameters[0].ParameterType.Name})");
                    return;
                }
            }

            TranslatorCore.LogWarning("[TextureUtils] No LoadImage method found");
        }

        #endregion

        #region RawTextureData

        /// <summary>
        /// Get raw texture data via reflection.
        /// On IL2CPP, GetRawTextureData() returns Il2CppStructArray&lt;byte&gt; instead of byte[].
        /// </summary>
        public static byte[] GetRawTextureDataSafe(Texture2D texture)
        {
            if (texture == null) return null;

            var type = texture.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "GetRawTextureData") continue;
                if (method.GetParameters().Length != 0) continue;
                if (method.IsGenericMethod) continue;

                try
                {
                    var result = method.Invoke(texture, null);
                    if (result == null) continue;

                    if (result is byte[] bytes)
                        return bytes;

                    // IL2CPP: extract from Il2CppStructArray<byte>
                    byte[] extracted = ExtractByteArrayFromIl2Cpp(result);
                    if (extracted != null) return extracted;

                    // Try as IEnumerable
                    if (result is System.Collections.IEnumerable enumerable)
                    {
                        var list = new List<byte>();
                        foreach (var item in enumerable)
                        {
                            if (item is byte b)
                                list.Add(b);
                        }
                        if (list.Count > 0)
                            return list.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] GetRawTextureData reflection failed: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Load raw texture data via reflection.
        /// On IL2CPP, LoadRawTextureData may expect Il2CppStructArray&lt;byte&gt; instead of byte[].
        /// </summary>
        public static bool LoadRawTextureDataSafe(Texture2D texture, byte[] data)
        {
            if (texture == null || data == null) return false;

            var type = texture.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "LoadRawTextureData") continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                var paramType = parameters[0].ParameterType;

                if (paramType == typeof(byte[]))
                {
                    try
                    {
                        method.Invoke(texture, new object[] { data });
                        return true;
                    }
                    // The engine refusing the plain overload: said, and the next one is tried.
                    catch (Exception ex) { Faults.Say("TextureUtils.LoadRawTextureDataSafe byte[]", ex); continue; }
                }

                // IL2CPP array conversion
                try
                {
                    var ctor = paramType.GetConstructor(new Type[] { typeof(byte[]) });
                    if (ctor != null)
                    {
                        var il2cppArray = ctor.Invoke(new object[] { data });
                        method.Invoke(texture, new object[] { il2cppArray });
                        return true;
                    }

                    ctor = paramType.GetConstructor(new Type[] { typeof(int) });
                    if (ctor != null)
                    {
                        var il2cppArray = ctor.Invoke(new object[] { data.Length });
                        var indexer = paramType.GetProperty("Item");
                        if (indexer != null)
                        {
                            for (int i = 0; i < data.Length; i++)
                                indexer.SetValue(il2cppArray, data[i], new object[] { i });
                            method.Invoke(texture, new object[] { il2cppArray });
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] LoadRawTextureData IL2CPP conversion failed: {ex.Message}");
                }
            }

            return false;
        }

        #endregion

        #region Type Checks

        /// <summary>
        /// Check if a type is a Texture2D or IL2CPP equivalent.
        /// </summary>
        public static bool IsTextureType(Type type)
        {
            return typeof(Texture2D).IsAssignableFrom(type) || type.Name.Contains("Texture2D");
        }

        /// <summary>
        /// Check if a type is byte[] or an IL2CPP byte array equivalent.
        /// </summary>
        public static bool IsByteArrayType(Type type)
        {
            if (type == typeof(byte[])) return true;
            if (type.Name.Contains("Array") && type.FullName != null && type.FullName.Contains("Byte")) return true;
            return false;
        }

        /// <summary>
        /// Get bytes per pixel for a texture format.
        /// </summary>
        public static int GetBytesPerPixel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.BGRA32:
                case TextureFormat.ARGB32:
                    return 4;
                case TextureFormat.RGB24:
                    return 3;
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return 1;
                case TextureFormat.RG16:
                case TextureFormat.R16:
                    return 2;
                case TextureFormat.RGBAFloat:
                    return 16;
                case TextureFormat.RGBAHalf:
                    return 8;
                default:
                    return 4;
            }
        }

        #endregion

        #region IL2CPP Array Helpers

        /// <summary>
        /// Convert a byte[] to the type expected by the method parameter (handles IL2CPP array types).
        /// </summary>
        public static object ConvertByteArrayForMethod(MethodInfo method, byte[] data)
        {
            var parameters = method.GetParameters();
            Type expectedType = null;
            foreach (var param in parameters)
            {
                if (IsByteArrayType(param.ParameterType))
                {
                    expectedType = param.ParameterType;
                    break;
                }
            }

            if (expectedType == null || expectedType == typeof(byte[]))
                return data;

            try
            {
                var ctor = expectedType.GetConstructor(new Type[] { typeof(byte[]) });
                if (ctor != null)
                    return ctor.Invoke(new object[] { data });

                ctor = expectedType.GetConstructor(new Type[] { typeof(int) });
                if (ctor != null)
                {
                    var il2cppArray = ctor.Invoke(new object[] { data.Length });
                    var indexer = expectedType.GetProperty("Item");
                    if (indexer != null)
                    {
                        for (int i = 0; i < data.Length; i++)
                            indexer.SetValue(il2cppArray, data[i], new object[] { i });
                        return il2cppArray;
                    }
                }

                TranslatorCore.LogWarning($"[TextureUtils] Cannot convert byte[] to {expectedType.Name}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] byte[] conversion failed: {ex.Message}");
            }

            return data;
        }

        /// <summary>
        /// Extract byte[] from an IL2CPP array-like object (Il2CppStructArray&lt;byte&gt;).
        /// </summary>
        private static byte[] ExtractByteArrayFromIl2Cpp(object result)
        {
            if (result == null) return null;

            var resultType = result.GetType();
            try
            {
                var lengthProp = resultType.GetProperty("Length") ?? resultType.GetProperty("Count");
                if (lengthProp == null)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] ExtractByteArrayFromIl2Cpp: {resultType.FullName} has neither Length nor Count property");
                    return null;
                }

                int length = (int)lengthProp.GetValue(result, null);
                var indexer = resultType.GetProperty("Item");
                if (indexer != null && length > 0)
                {
                    byte[] data = new byte[length];
                    for (int i = 0; i < length; i++)
                        data[i] = (byte)indexer.GetValue(result, new object[] { i });
                    return data;
                }
                TranslatorCore.LogWarning($"[TextureUtils] ExtractByteArrayFromIl2Cpp: {resultType.FullName} has Length={length} but indexer={(indexer != null ? "found" : "missing")}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] ExtractByteArrayFromIl2Cpp threw on {resultType.FullName}: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region MakeReadableCopy (NEW)

        /// <summary>
        /// Create a readable copy of a texture by blitting through a RenderTexture.
        /// Works even if the source texture is non-readable (GPU-only).
        /// All calls via reflection for IL2CPP compatibility.
        /// </summary>
        public static Texture2D MakeReadableCopy(Texture2D source)
        {
            if (source == null) return null;

            try
            {
                // Resolve methods once
                if (!_blitMethodSearched)
                {
                    _blitMethodSearched = true;
                    ResolveBlitMethods();
                }

                if (!_readPixelsMethodSearched)
                {
                    _readPixelsMethodSearched = true;
                    ResolveReadPixelsMethod();
                }

                if (_blitMethod == null || _getTemporaryMethod == null || _releaseTemporaryMethod == null || _rtActiveProp == null)
                {
                    TranslatorCore.LogWarning("[TextureUtils] Cannot MakeReadableCopy: missing Graphics/RenderTexture methods");
                    return null;
                }

                int width = source.width;
                int height = source.height;

                // RenderTexture.GetTemporary(width, height, 0)
                var rt = _getTemporaryMethod.Invoke(null, new object[] { width, height, 0 });
                if (rt == null)
                {
                    TranslatorCore.LogWarning("[TextureUtils] RenderTexture.GetTemporary returned null");
                    return null;
                }

                // Graphics.Blit(source, rt)
                _blitMethod.Invoke(null, new object[] { source, rt });

                // Save and set RenderTexture.active
                var previous = _rtActiveProp.GetValue(null, null);
                _rtActiveProp.SetValue(null, rt, null);

                // Create readable texture
                var readable = Compat.MakeTexture2D(width, height, TextureFormat.RGBA32, false);

                // ReadPixels(Compat.MakeRect(0, 0, width, height), 0, 0)
                if (_readPixelsMethod != null)
                {
                    _readPixelsMethod.Invoke(readable, new object[] { Compat.MakeRect(0, 0, width, height), 0, 0 });
                }
                else
                {
                    // Direct call fallback (works on Mono)
                    readable.ReadPixels(Compat.MakeRect(0, 0, width, height), 0, 0);
                }

                readable.Apply();

                // Restore RenderTexture.active
                _rtActiveProp.SetValue(null, previous, null);

                // RenderTexture.ReleaseTemporary(rt)
                _releaseTemporaryMethod.Invoke(null, new object[] { rt });

                return readable;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] MakeReadableCopy failed: {ex.Message}");
                return null;
            }
        }

        private static void ResolveBlitMethods()
        {
            try
            {
                // Graphics.Blit(Texture, RenderTexture)
                var graphicsType = typeof(Graphics);
                foreach (var method in graphicsType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "Blit") continue;
                    var parms = method.GetParameters();
                    if (parms.Length == 2
                        && typeof(Texture).IsAssignableFrom(parms[0].ParameterType)
                        && parms[1].ParameterType.Name.Contains("RenderTexture"))
                    {
                        _blitMethod = method;
                        break;
                    }
                }

                // RenderTexture.GetTemporary(int, int, int)
                var rtType = typeof(RenderTexture);
                foreach (var method in rtType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "GetTemporary") continue;
                    var parms = method.GetParameters();
                    if (parms.Length == 3
                        && parms[0].ParameterType == typeof(int)
                        && parms[1].ParameterType == typeof(int)
                        && parms[2].ParameterType == typeof(int))
                    {
                        _getTemporaryMethod = method;
                        break;
                    }
                }

                // RenderTexture.ReleaseTemporary(RenderTexture)
                foreach (var method in rtType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "ReleaseTemporary") continue;
                    var parms = method.GetParameters();
                    if (parms.Length == 1)
                    {
                        _releaseTemporaryMethod = method;
                        break;
                    }
                }

                // RenderTexture.active (static property)
                _rtActiveProp = rtType.GetProperty("active", BindingFlags.Public | BindingFlags.Static);

                TranslatorCore.LogDebug($"[TextureUtils] Blit={_blitMethod != null}, GetTemp={_getTemporaryMethod != null}, " +
                    $"ReleaseTemp={_releaseTemporaryMethod != null}, Active={_rtActiveProp != null}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] ResolveBlit failed: {ex.Message}");
            }
        }

        private static void ResolveReadPixelsMethod()
        {
            try
            {
                // Texture2D.ReadPixels(Rect, int, int)
                var texType = typeof(Texture2D);
                foreach (var method in texType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != "ReadPixels") continue;
                    var parms = method.GetParameters();
                    if (parms.Length == 3
                        && parms[0].ParameterType == typeof(Rect)
                        && parms[1].ParameterType == typeof(int)
                        && parms[2].ParameterType == typeof(int))
                    {
                        _readPixelsMethod = method;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] ResolveReadPixels failed: {ex.Message}");
            }
        }

        #endregion

        #region ExtractSpriteRegion (NEW)

        /// <summary>
        /// Extract the pixel region of a Sprite from its atlas texture.
        /// Returns a new readable Texture2D containing only the sprite's pixels.
        /// Uses reflection for all Unity API calls (IL2CPP-safe).
        /// </summary>
        public static Texture2D ExtractSpriteRegion(object spriteObj)
        {
            if (spriteObj == null) return null;

            try
            {
                var spriteType = spriteObj.GetType();

                // Get sprite.texture
                var textureProp = spriteType.GetProperty("texture", BindingFlags.Public | BindingFlags.Instance);
                if (textureProp == null) return null;
                var textureObj = textureProp.GetValue(spriteObj, null);
                if (!(textureObj is Texture2D sourceTexture)) return null;

                // Get sprite.textureRect (the region within the atlas)
                var textureRectProp = spriteType.GetProperty("textureRect", BindingFlags.Public | BindingFlags.Instance);
                if (textureRectProp == null) return null;
                var rect = (Rect)textureRectProp.GetValue(spriteObj, null);

                int x = (int)rect.x;
                int y = (int)rect.y;
                int w = (int)rect.width;
                int h = (int)rect.height;

                if (w <= 0 || h <= 0) return null;

                // Make a readable copy of the entire atlas
                var readable = MakeReadableCopy(sourceTexture);
                if (readable == null) return null;

                // If sprite covers the full texture, just return the readable copy
                if (x == 0 && y == 0 && w == readable.width && h == readable.height)
                    return readable;

                // Extract the sprite region via reflection (GetPixels may be stripped on IL2CPP)
                TranslatorCore.LogDebug($"[TextureUtils] Sprite region: ({x},{y}) {w}x{h} in texture {readable.width}x{readable.height}");
                var result = ExtractRegionFromReadable(readable, x, y, w, h);

                if (result != null)
                {
                    UnityEngine.Object.Destroy(readable);
                    return result;
                }

                // Fallback: return the full texture if region extraction failed (IL2CPP stripped methods)
                TranslatorCore.LogWarning($"[TextureUtils] Region extraction failed, returning full texture ({readable.width}x{readable.height})");
                return readable;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] ExtractSpriteRegion failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extract a rectangular region from a readable Texture2D.
        /// Uses reflection for GetPixels/SetPixels (may be stripped on IL2CPP).
        /// Falls back to pixel-by-pixel copy via GetPixel/SetPixel.
        /// </summary>
        private static Texture2D ExtractRegionFromReadable(Texture2D source, int x, int y, int w, int h)
        {
            var result = Compat.MakeTexture2D(w, h, TextureFormat.RGBA32, false);

            // Try GetPixels(x, y, w, h) via reflection
            try
            {
                var texType = source.GetType();
                foreach (var method in texType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != "GetPixels") continue;
                    var parms = method.GetParameters();
                    if (parms.Length == 4
                        && parms[0].ParameterType == typeof(int)
                        && parms[1].ParameterType == typeof(int)
                        && parms[2].ParameterType == typeof(int)
                        && parms[3].ParameterType == typeof(int))
                    {
                        var pixels = method.Invoke(source, new object[] { x, y, w, h });
                        if (pixels is Color[] colorArray)
                        {
                            // SetPixels may also be stripped — use reflection
                            var setMethod = result.GetType().GetMethod("SetPixels",
                                new Type[] { typeof(Color[]) });
                            if (setMethod != null)
                            {
                                setMethod.Invoke(result, new object[] { colorArray });
                                result.Apply();
                                return result;
                            }
                        }
                        // IL2CPP: GetPixels may return non-Color[] or SetPixels stripped — fall through to pixel-by-pixel
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[TextureUtils] GetPixels reflection failed: {ex.Message}");
            }

            // Fallback: pixel-by-pixel copy via GetPixel/SetPixel (always available)
            try
            {
                for (int py = 0; py < h; py++)
                {
                    for (int px = 0; px < w; px++)
                    {
                        result.SetPixel(px, py, source.GetPixel(x + px, y + py));
                    }
                }
                result.Apply();
                return result;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] Pixel-by-pixel copy failed: {ex.Message}");
                UnityEngine.Object.Destroy(result);
                return null;
            }
        }

        #endregion

        #region CreateSpriteSafe (NEW)

        /// <summary>
        /// Create a Sprite from the whole texture, via reflection (IL2CPP-safe).
        /// Uses Sprite.Create overload 5: (Texture2D, Rect, Vector2, float, uint, SpriteMeshType, Vector4)
        /// to support 9-slice borders.
        /// </summary>
        public static object CreateSpriteSafe(Texture2D texture, Vector2 pivot, float pixelsPerUnit, Vector4 border)
        {
            if (texture == null) return null;
            return CreateSpriteSafe(texture, Compat.MakeRect(0, 0, texture.width, texture.height),
                pivot, pixelsPerUnit, border);
        }

        /// <summary>
        /// Same, for one region of a larger texture.
        ///
        /// A shape atlas needs this: several 9-slice shapes share a single texture so that drawing
        /// them all costs one texture swap instead of one per shape, and each sprite is then a
        /// <paramref name="rect"/> inside it.
        /// </summary>
        public static object CreateSpriteSafe(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, Vector4 border)
        {
            if (texture == null) return null;

            if (_createSpriteNative != null || _createSpriteInjectedManaged != null)
                return CreateSpriteThroughNative(texture, rect, pivot, pixelsPerUnit, border);

            try
            {
                if (!_spriteCreateMethodSearched)
                {
                    _spriteCreateMethodSearched = true;
                    ResolveSpriteCreateMethod();
                }

                if (_spriteCreateMethod != null)
                {
                    var parms = _spriteCreateMethod.GetParameters();

                    if (parms.Length == 7)
                    {
                        // Overload 5: (Texture2D, Rect, Vector2, float, uint, SpriteMeshType, Vector4)
                        return _spriteCreateMethod.Invoke(null, new object[] {
                            texture, rect, pivot, pixelsPerUnit, (uint)0,
                            SpriteMeshType.FullRect, border
                        });
                    }
                    else if (parms.Length == 4)
                    {
                        // Overload 2: (Texture2D, Rect, Vector2, float)
                        return _spriteCreateMethod.Invoke(null, new object[] {
                            texture, rect, pivot, pixelsPerUnit
                        });
                    }
                    else if (parms.Length == 3)
                    {
                        // Overload 1: (Texture2D, Rect, Vector2)
                        return _spriteCreateMethod.Invoke(null, new object[] {
                            texture, rect, pivot
                        });
                    }
                }

                // Absolute fallback: direct call (works on Mono)
                return Sprite.Create(texture, rect, pivot, pixelsPerUnit);
            }
            catch (Exception ex)
            {
                // The game stripped Sprite.Create: the engine still has it, reached natively.
                if ((ex.InnerException ?? ex) is NotSupportedException && FindCreateSpriteNative())
                    return CreateSpriteThroughNative(texture, rect, pivot, pixelsPerUnit, border);

                TranslatorCore.LogWarning($"[TextureUtils] CreateSpriteSafe failed: {ex.Message}");

                // Last resort direct call. Same rect as asked for — falling back to the whole
                // texture would hand an atlas user every shape at once instead of the one wanted.
                try
                {
                    return Sprite.Create(texture, rect, pivot, pixelsPerUnit);
                }
                catch (Exception ex2)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] CreateSpriteSafe direct fallback also failed: {ex2.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Sprite.Create on a Unity 2023.1+ IL2CPP game whose build stripped it.
        ///
        /// ⚠ A game that never makes a sprite out of a texture loses the whole managed chain —
        /// every overload ends in « Method unstripping failed », and with it every picture this mod
        /// draws (rounded shapes, icons, flags, logos). The engine keeps the native function
        /// registered all the same: <c>UnityEngine.Sprite::CreateSprite_Injected</c>, whose shape is
        /// the one the interop shows on games that kept it — native texture pointer, Rect, Vector2
        /// and Vector4 by pointer, the secondary textures as an array (none here), and a GC handle
        /// back, turned into a Sprite by <c>Unmarshal.UnmarshalUnityObject</c>.
        ///
        /// 🔴 The raw icall only on the 2023.1+ shape (<c>Unmarshal.UnmarshalUnityObject</c> and the
        /// texture marshaller present): before it the same icall takes managed objects, and calling
        /// it with this signature would kill the process. Before 2023.1 the interop's own public
        /// <c>CreateSprite_Injected</c> wrapper is called instead — tried first, see below.
        /// </summary>
        private static bool FindCreateSpriteNative()
        {
            if (_createSpriteNativeSearched) return _createSpriteNative != null || _createSpriteInjectedManaged != null;
            _createSpriteNativeSearched = true;

            try
            {
                // Before 2023.1 (2026-09-30, a 2020.3 game that stripped Sprite.Create): the interop
                // keeps CreateSprite_Injected as a public wrapper that resolves the icall itself and
                // takes the managed objects — (Texture2D, ref Rect, ref Vector2, float, uint,
                // SpriteMeshType, ref Vector4, bool) → Sprite. Only Create's REBUILT body is broken
                // there, so calling the wrapper is all it takes, with nothing resolved here.
                foreach (var method in typeof(Sprite).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "CreateSprite_Injected" || !typeof(Sprite).IsAssignableFrom(method.ReturnType)) continue;
                    var p = method.GetParameters();
                    if (p.Length == 8 && IsTextureType(p[0].ParameterType) && p[1].ParameterType.IsByRef
                        && p[2].ParameterType.IsByRef && p[3].ParameterType == typeof(float)
                        && p[4].ParameterType == typeof(uint) && p[6].ParameterType.IsByRef
                        && p[7].ParameterType == typeof(bool))
                    {
                        _createSpriteInjectedManaged = method;
                        TranslatorCore.LogInfo("[TextureUtils] Sprite.Create is stripped in this game — using Sprite.CreateSprite_Injected");
                        return true;
                    }
                }

                if (!_loadImageMethodSearched)
                {
                    _loadImageMethodSearched = true;
                    FindLoadImageMethod();
                }
                if (_marshalTexture == null)
                {
                    TranslatorCore.LogWarning("[TextureUtils] Sprite.Create is stripped and this runtime is not the 2023.1+ shape — pictures stay blank");
                    return false;
                }

                var unmarshal = typeof(Sprite).Assembly.GetType("UnityEngine.Bindings.Unmarshal");
                MethodInfo unmarshalSprite = null;
                if (unmarshal != null)
                {
                    foreach (var method in unmarshal.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name == "UnmarshalUnityObject" && method.IsGenericMethodDefinition
                            && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(IntPtr))
                        {
                            unmarshalSprite = method.MakeGenericMethod(typeof(Sprite));
                            break;
                        }
                    }
                }

                IntPtr icall = IntPtr.Zero;
                Type il2cpp = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    il2cpp = asm.GetType("Il2CppInterop.Runtime.IL2CPP");
                    if (il2cpp != null) break;
                }
                var resolve = il2cpp?.GetMethod("il2cpp_resolve_icall", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (resolve != null)
                    icall = (IntPtr)resolve.Invoke(null, new object[] { "UnityEngine.Sprite::CreateSprite_Injected" });

                if (unmarshalSprite == null || icall == IntPtr.Zero)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] Sprite.Create is stripped and cannot be reached natively (unmarshal={unmarshalSprite != null}, icall={icall != IntPtr.Zero}) — pictures stay blank");
                    return false;
                }

                _unmarshalSprite = unmarshalSprite;
                _createSpriteNative = (CreateSpriteInjected)Marshal.GetDelegateForFunctionPointer(icall, typeof(CreateSpriteInjected));
                TranslatorCore.LogInfo("[TextureUtils] Sprite.Create is stripped in this game — using Sprite::CreateSprite_Injected");
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                TranslatorCore.LogWarning($"[TextureUtils] Native Sprite.Create lookup failed: {inner.GetType().Name}: {inner.Message}");
                return false;
            }
        }

        private static object CreateSpriteThroughNative(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, Vector4 border)
        {
            if (_createSpriteInjectedManaged != null)
            {
                try
                {
                    return _createSpriteInjectedManaged.Invoke(null, new object[] {
                        texture, rect, pivot, pixelsPerUnit, 0u, SpriteMeshType.FullRect, border, false });
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    TranslatorCore.LogWarning($"[TextureUtils] Sprite.CreateSprite_Injected failed: {inner.GetType().Name}: {inner.Message}");
                    return null;
                }
            }

            // Rect, Vector2, Vector4 as the engine lays them out: consecutive floats.
            var values = new float[] { rect.x, rect.y, rect.width, rect.height, pivot.x, pivot.y, border.x, border.y, border.z, border.w };
            var pinned = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                var nativeTexture = (IntPtr)_marshalTexture.Invoke(null, new object[] { texture });
                if (nativeTexture == IntPtr.Zero) return null;

                IntPtr start = pinned.AddrOfPinnedObject();
                IntPtr handle = _createSpriteNative(nativeTexture, start, start + 4 * sizeof(float), pixelsPerUnit,
                    0u, (int)SpriteMeshType.FullRect, start + 6 * sizeof(float), 0, IntPtr.Zero);
                if (handle == IntPtr.Zero)
                {
                    TranslatorCore.LogWarning("[TextureUtils] CreateSprite_Injected returned nothing");
                    return null;
                }
                return _unmarshalSprite.Invoke(null, new object[] { handle });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                TranslatorCore.LogWarning($"[TextureUtils] CreateSprite_Injected failed: {inner.GetType().Name}: {inner.Message}");
                return null;
            }
            finally
            {
                pinned.Free();
            }
        }

        private static void ResolveSpriteCreateMethod()
        {
            try
            {
                var spriteType = typeof(Sprite);
                MethodInfo best = null;

                foreach (var method in spriteType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "Create") continue;
                    var parms = method.GetParameters();

                    // Prefer overload 5 (7 params) for 9-slice support
                    if (parms.Length == 7)
                    {
                        _spriteCreateMethod = method;
                        TranslatorCore.LogDebug("[TextureUtils] Found Sprite.Create with 7 params (border support)");
                        return;
                    }

                    // Keep track of 4-param overload as fallback
                    if (parms.Length == 4 && best == null)
                        best = method;
                    // And 3-param as last resort
                    if (parms.Length == 3 && best == null)
                        best = method;
                }

                if (best != null)
                {
                    _spriteCreateMethod = best;
                    TranslatorCore.LogDebug($"[TextureUtils] Found Sprite.Create with {best.GetParameters().Length} params (fallback)");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] ResolveSpriteCreate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The thing the game draws, as a sprite something else can draw too — for SHOWING it, never
        /// for reading its pixels.
        ///
        /// 🔴 **No readable copy, and that is the whole point.** Readability is a CPU matter: only
        /// reading pixels back (<see cref="EncodeToPngSafe"/>, and so exporting) needs it, and paying
        /// for it costs a blit through a render target on a 2048² texture. Drawing does not: the
        /// picture is already on the card. So a preview of what is on screen costs nothing when the
        /// game hands a Sprite — the very same object is shown — and one Sprite.Create otherwise.
        ///
        /// ⚠ Two shapes arrive here, told apart exactly as <c>ImageReplacer.ExportOriginal</c> tells
        /// them apart: a Sprite (it has a <c>textureRect</c>, so it may be one region of an atlas and
        /// is shown as the game shows it), or a raw texture — a RawImage's, or the one painted on a
        /// 3D material. On IL2CPP neither arrives as its own type, hence Il2CppCast both times.
        ///
        /// ⚠ **Whoever asks owns what comes back** when <paramref name="mine"/> says so: a sprite
        /// made here is destroyed by the caller when it shows something else. A sprite that came from
        /// the game is the GAME's, and destroying it would take the picture off the object.
        /// </summary>
        /// <param name="why">Why nothing can be shown, in the interface's words; null when it can.</param>
        /// <param name="mine">True when what comes back was made here and is the caller's to destroy.</param>
        public static object SpriteForDisplay(object spriteObj, out string why, out bool mine)
        {
            why = null;
            mine = false;
            if (spriteObj == null) { why = "Nothing to show."; return null; }

            try
            {
                // A Sprite says so by having a region: shown as it is, atlas region included.
                var type = spriteObj.GetType();
                if (type.GetProperty("textureRect", BindingFlags.Public | BindingFlags.Instance) != null)
                {
                    var sprite = spriteObj as Sprite ?? TypeHelper.Il2CppCast(spriteObj, typeof(Sprite)) as Sprite;
                    if (sprite != null) return sprite;
                    why = "This image cannot be shown here (check the log).";
                    TranslatorCore.LogWarning($"[TextureUtils] SpriteForDisplay: {type.Name} has a textureRect and is not a Sprite");
                    return null;
                }

                var texture = spriteObj as Texture2D ?? TypeHelper.Il2CppCast(spriteObj, typeof(Texture2D)) as Texture2D;
                if (texture == null)
                {
                    why = $"This is a {type.Name}, which cannot be shown as a picture.";
                    return null;
                }

                var made = CreateSpriteSafe(texture, Compat.MakeRect(0, 0, texture.width, texture.height),
                                            new Vector2(0.5f, 0.5f), 100f, Vector4.zero);
                if (made == null)
                {
                    why = "This image cannot be shown here (check the log).";
                    return null;
                }
                mine = true;
                return made;
            }
            catch (Exception ex)
            {
                why = "This image cannot be shown here (check the log).";
                TranslatorCore.LogWarning($"[TextureUtils] SpriteForDisplay failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Sprite Property Helpers (NEW)

        /// <summary>
        /// Get sprite properties via reflection (IL2CPP-safe).
        /// Returns pivot, pixelsPerUnit, and border from a sprite object.
        /// </summary>
        public static bool GetSpriteProperties(object spriteObj, out Vector2 pivot, out float pixelsPerUnit, out Vector4 border)
        {
            pivot = new Vector2(0.5f, 0.5f);
            pixelsPerUnit = 100f;
            border = Vector4.zero;

            if (spriteObj == null) return false;

            try
            {
                var type = spriteObj.GetType();

                var pivotProp = type.GetProperty("pivot", BindingFlags.Public | BindingFlags.Instance);
                if (pivotProp != null)
                {
                    var pivotVal = pivotProp.GetValue(spriteObj, null);
                    if (pivotVal is Vector2 p)
                    {
                        // pivot is in pixel coords, we need normalized (0-1)
                        var rectProp = type.GetProperty("rect", BindingFlags.Public | BindingFlags.Instance);
                        if (rectProp != null)
                        {
                            var rect = (Rect)rectProp.GetValue(spriteObj, null);
                            if (rect.width > 0 && rect.height > 0)
                                pivot = new Vector2(p.x / rect.width, p.y / rect.height);
                        }
                    }
                }

                var ppuProp = type.GetProperty("pixelsPerUnit", BindingFlags.Public | BindingFlags.Instance);
                if (ppuProp != null)
                {
                    var ppuVal = ppuProp.GetValue(spriteObj, null);
                    if (ppuVal is float f) pixelsPerUnit = f;
                }

                var borderProp = type.GetProperty("border", BindingFlags.Public | BindingFlags.Instance);
                if (borderProp != null)
                {
                    var borderVal = borderProp.GetValue(spriteObj, null);
                    if (borderVal is Vector4 b) border = b;
                }

                return true;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TextureUtils] GetSpriteProperties failed: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
