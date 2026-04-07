using System;
using System.Collections.Generic;
using System.Reflection;
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

        // Cached for MakeReadableCopy
        private static MethodInfo _blitMethod;
        private static bool _blitMethodSearched;
        private static MethodInfo _getTemporaryMethod;
        private static MethodInfo _releaseTemporaryMethod;
        private static PropertyInfo _rtActiveProp;

        // Cached for CreateSpriteSafe
        private static MethodInfo _spriteCreateMethod;
        private static bool _spriteCreateMethodSearched;

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
        /// Encode a Texture2D to PNG via reflection (handles IL2CPP where EncodeToPNG may differ).
        /// </summary>
        public static byte[] EncodeToPngSafe(Texture2D texture)
        {
            if (texture == null) return null;

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
                        try
                        {
                            var result = method.Invoke(null, new object[] { texture });
                            if (result is byte[] bytes) return bytes;

                            // IL2CPP: might return Il2CppStructArray<byte>
                            if (result != null)
                            {
                                byte[] extracted = ExtractByteArrayFromIl2Cpp(result);
                                if (extracted != null) return extracted;
                            }
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogWarning($"[TextureUtils] EncodeToPNG failed: {ex.Message}");
                        }
                    }
                }
            }

            // Try Texture2D.EncodeToPNG() — older Unity (instance method)
            try
            {
                var method = texture.GetType().GetMethod("EncodeToPNG", BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                {
                    var result = method.Invoke(texture, null);
                    if (result is byte[] bytes) return bytes;
                }
            }
            catch { }

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

        private static void FindLoadImageMethod()
        {
            // Try ImageConversion.LoadImage first (newer Unity)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var imageConvType = asm.GetType("UnityEngine.ImageConversion");
                if (imageConvType == null) continue;

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
                    catch { continue; }
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
            var lengthProp = resultType.GetProperty("Length") ?? resultType.GetProperty("Count");
            if (lengthProp == null) return null;

            int length = (int)lengthProp.GetValue(result, null);
            var indexer = resultType.GetProperty("Item");
            if (indexer != null && length > 0)
            {
                byte[] data = new byte[length];
                for (int i = 0; i < length; i++)
                    data[i] = (byte)indexer.GetValue(result, new object[] { i });
                return data;
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
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);

                // ReadPixels(new Rect(0, 0, width, height), 0, 0)
                if (_readPixelsMethod != null)
                {
                    _readPixelsMethod.Invoke(readable, new object[] { new Rect(0, 0, width, height), 0, 0 });
                }
                else
                {
                    // Direct call fallback (works on Mono)
                    readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
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
            var result = new Texture2D(w, h, TextureFormat.RGBA32, false);

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
        /// Create a Sprite via reflection (IL2CPP-safe).
        /// Uses Sprite.Create overload 5: (Texture2D, Rect, Vector2, float, uint, SpriteMeshType, Vector4)
        /// to support 9-slice borders.
        /// </summary>
        public static object CreateSpriteSafe(Texture2D texture, Vector2 pivot, float pixelsPerUnit, Vector4 border)
        {
            if (texture == null) return null;

            try
            {
                if (!_spriteCreateMethodSearched)
                {
                    _spriteCreateMethodSearched = true;
                    ResolveSpriteCreateMethod();
                }

                var rect = new Rect(0, 0, texture.width, texture.height);

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
                TranslatorCore.LogWarning($"[TextureUtils] CreateSpriteSafe failed: {ex.Message}");

                // Last resort direct call
                try
                {
                    return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, pixelsPerUnit);
                }
                catch (Exception ex2)
                {
                    TranslatorCore.LogWarning($"[TextureUtils] CreateSpriteSafe direct fallback also failed: {ex2.Message}");
                    return null;
                }
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
