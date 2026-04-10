using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Manages bitmap image replacements for translating text embedded in sprites.
    /// Static class following the FontManager pattern.
    /// All Unity API calls use reflection for IL2CPP compatibility.
    /// </summary>
    public static class ImageReplacer
    {
        #region Types (resolved once)

        private static Type _imageType;           // UnityEngine.UI.Image
        private static Type _rawImageType;        // UnityEngine.UI.RawImage
        private static Type _spriteRendererType;  // UnityEngine.SpriteRenderer
        private static bool _typesResolved;

        // Property accessors cached per type
        private static PropertyInfo _imageSpriteProp;      // Image.sprite
        private static PropertyInfo _rawImageTextureProp;  // RawImage.texture
        private static PropertyInfo _spriteRendSpriteProp; // SpriteRenderer.sprite

        /// <summary>Resolved Image type (public for TranslatorPatches).</summary>
        public static Type ImageType => _imageType;
        /// <summary>Resolved RawImage type (public for TranslatorPatches).</summary>
        public static Type RawImageType => _rawImageType;
        /// <summary>Resolved SpriteRenderer type (public for TranslatorPatches).</summary>
        public static Type SpriteRendererType => _spriteRendererType;

        #endregion

        #region Data

        private static Dictionary<string, ImageReplacement> _replacements = new Dictionary<string, ImageReplacement>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, Sprite> _loadedSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static List<Texture2D> _createdTextures = new List<Texture2D>();
        private static string _imagesFolder;
        private static bool _initialized;

        // Tracks components where we replaced the sprite/texture, keyed by component instance id.
        // Stored so the Restore hotkey/toggle can put back the original (same as disabling replacement).
        private static Dictionary<int, (object component, object originalValue, string propertyName)> _replacedComponents = new Dictionary<int, (object, object, string)>();

        #endregion

        #region ImageReplacement class

        public class ImageReplacement
        {
            public string SpriteName;
            public string HierarchyPath;
            public int OriginalWidth;
            public int OriginalHeight;
            public float PivotX = 0.5f;
            public float PivotY = 0.5f;
            public float BorderLeft;
            public float BorderBottom;
            public float BorderRight;
            public float BorderTop;
            public float PixelsPerUnit = 100f;
            public string File;             // relative to images/ e.g. "title_en.png" (exported, then edited in place)
        }

        #endregion

        #region Initialize

        public static void Initialize(string modFolder)
        {
            if (_initialized) return;
            _initialized = true;

            _imagesFolder = Path.Combine(modFolder, "images");
            _replacements = new Dictionary<string, ImageReplacement>(StringComparer.OrdinalIgnoreCase);
            _loadedSprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            _createdTextures = new List<Texture2D>();

            TranslatorCore.LogInfo("[ImageReplacer] Initialized");
        }

        #endregion

        #region Type Resolution

        /// <summary>
        /// Resolve Unity UI types via reflection. Called once from TranslatorPatches or on first use.
        /// </summary>
        public static void ResolveTypes()
        {
            if (_typesResolved) return;
            _typesResolved = true;

            _imageType = FindType("UnityEngine.UI.Image");
            _rawImageType = FindType("UnityEngine.UI.RawImage");
            _spriteRendererType = FindType("UnityEngine.SpriteRenderer");

            if (_imageType != null)
                _imageSpriteProp = _imageType.GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);
            if (_rawImageType != null)
                _rawImageTextureProp = _rawImageType.GetProperty("texture", BindingFlags.Public | BindingFlags.Instance);
            if (_spriteRendererType != null)
                _spriteRendSpriteProp = _spriteRendererType.GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);

            TranslatorCore.LogInfo($"[ImageReplacer] Types resolved: Image={_imageType != null}, " +
                $"RawImage={_rawImageType != null}, SpriteRenderer={_spriteRendererType != null}");
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }

            // IL2CPP: try with Il2Cpp prefix
            string il2cppName = "Il2Cpp" + fullName;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(il2cppName);
                    if (type != null) return type;
                }
                catch { }
            }

            // Last resort: name-only search
            string shortName = fullName.Substring(fullName.LastIndexOf('.') + 1);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == shortName || type.Name == "Il2Cpp" + shortName)
                            return type;
                    }
                }
                catch { }
            }

            return null;
        }

        #endregion

        #region Component Detection (IL2CPP-safe)

        // Cache of image component instanceIDs for fast lookup (rebuilt periodically)
        private static HashSet<int> _imageComponentGOIds = new HashSet<int>();
        private static float _lastImageCacheTime;
        private const float IMAGE_CACHE_DURATION = 2f;

        // Resolved GetComponent method via reflection (fallback for IL2CPP)
        private static MethodInfo _getComponentMethod;
        private static bool _getComponentMethodSearched;
        private static bool _useGetComponentDirect = true; // try direct first, fallback to reflection

        /// <summary>
        /// Check if a GameObject has an Image, RawImage, or SpriteRenderer component.
        /// IL2CPP-safe: uses a cached set of known image GO instanceIDs, rebuilt periodically
        /// via TypeHelper.FindAllObjectsOfType (same pattern as TranslatorScanner).
        /// </summary>
        public static bool HasImageComponent(GameObject go)
        {
            if (go == null) return false;
            ResolveTypes();

            // Rebuild cache if stale
            float now = Time.time;
            if (now - _lastImageCacheTime > IMAGE_CACHE_DURATION)
            {
                RebuildImageComponentCache();
                _lastImageCacheTime = now;
            }

            return _imageComponentGOIds.Contains(go.GetInstanceID());
        }

        private static void RebuildImageComponentCache()
        {
            _imageComponentGOIds.Clear();

            // Find all Image components in scene
            if (_imageType != null)
                AddGOIdsForType(_imageType);
            if (_rawImageType != null)
                AddGOIdsForType(_rawImageType);
            if (_spriteRendererType != null)
                AddGOIdsForType(_spriteRendererType);

            TranslatorCore.LogDebug($"[ImageReplacer] Cache rebuilt: {_imageComponentGOIds.Count} image GOs");
        }

        private static void AddGOIdsForType(Type componentType)
        {
            try
            {
                var found = TypeHelper.FindAllObjectsOfType(componentType);
                if (found == null) return;
                foreach (var obj in found)
                {
                    if (obj == null) continue;
                    try
                    {
                        // Cast to Component to get gameObject
                        Component comp = obj as Component;
                        if (comp == null)
                        {
                            comp = TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                        }
                        if (comp != null && comp.gameObject != null)
                        {
                            _imageComponentGOIds.Add(comp.gameObject.GetInstanceID());
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[ImageReplacer] AddGOIdsForType({componentType.Name}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get a component by type from a GameObject.
        /// IL2CPP-safe: tries direct call first, falls back to reflection.
        /// </summary>
        private static Component GetComponentSafe(GameObject go, Type type)
        {
            if (go == null || type == null) return null;

            // Try direct call first (works on Mono, may be stripped on IL2CPP)
            if (_useGetComponentDirect)
            {
                try
                {
                    return go.GetComponent(type);
                }
                catch (MissingMethodException)
                {
                    _useGetComponentDirect = false;
                    TranslatorCore.LogDebug("[ImageReplacer] GetComponent(Type) stripped, switching to reflection");
                }
                catch
                {
                    return null;
                }
            }

            // Reflection fallback: find and invoke GetComponent via MethodInfo
            if (!_getComponentMethodSearched)
            {
                _getComponentMethodSearched = true;
                try
                {
                    var goType = go.GetType();
                    foreach (var method in goType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (method.Name != "GetComponent") continue;
                        var parms = method.GetParameters();
                        if (parms.Length == 1 && parms[0].ParameterType == typeof(Type))
                        {
                            _getComponentMethod = method;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (_getComponentMethod != null)
            {
                try
                {
                    var result = _getComponentMethod.Invoke(go, new object[] { type });
                    return result as Component;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Get the sprite object from an Image, RawImage, or SpriteRenderer on a GameObject.
        /// Returns the sprite/texture as object (for IL2CPP type safety).
        /// IL2CPP-safe: iterates components by type name if GetComponent(Type) is stripped.
        /// </summary>
        public static object GetSpriteFromComponent(GameObject go)
        {
            if (go == null) return null;
            ResolveTypes();
            int goId = go.GetInstanceID();
            object originalObj;

            if (_imageType != null)
            {
                var comp = FindComponentOnGO(_imageType, goId, out originalObj);
                if (originalObj != null)
                {
                    var result = GetPropertySafe(originalObj, "sprite");
                    if (result != null) return result;
                }
            }

            if (_spriteRendererType != null)
            {
                var comp = FindComponentOnGO(_spriteRendererType, goId, out originalObj);
                if (originalObj != null)
                {
                    var result = GetPropertySafe(originalObj, "sprite");
                    if (result != null) return result;
                }
            }

            if (_rawImageType != null)
            {
                var comp = FindComponentOnGO(_rawImageType, goId, out originalObj);
                if (originalObj != null)
                {
                    var result = GetPropertySafe(originalObj, "texture");
                    if (result != null) return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Find a component of the given type on a specific GameObject by instanceID.
        /// Uses TypeHelper.FindAllObjectsOfType (IL2CPP-safe, no GetComponent).
        /// </summary>
        /// <summary>
        /// Find a component of the given type on a specific GameObject by instanceID.
        /// Uses TypeHelper.FindAllObjectsOfType (IL2CPP-safe, no GetComponent).
        /// Returns the object as its ORIGINAL type (not cast to Component) so PropertyInfo works.
        /// The out parameter provides a Component reference for gameObject access.
        /// </summary>
        private static Component FindComponentOnGO(Type componentType, int goInstanceId)
        {
            return FindComponentOnGO(componentType, goInstanceId, out _);
        }

        private static Component FindComponentOnGO(Type componentType, int goInstanceId, out object originalObj)
        {
            originalObj = null;
            try
            {
                var all = TypeHelper.FindAllObjectsOfType(componentType);
                if (all == null) return null;
                foreach (var obj in all)
                {
                    if (obj == null) continue;
                    Component comp = obj as Component;
                    if (comp == null)
                        comp = TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                    if (comp == null) continue;
                    if (comp.gameObject != null && comp.gameObject.GetInstanceID() == goInstanceId)
                    {
                        // On IL2CPP, the object's runtime type may be a base proxy (e.g. Graphic
                        // instead of Image). Cast to the target type so PropertyInfo.GetValue works.
                        originalObj = TypeHelper.Il2CppCast(obj, componentType) ?? obj;
                        return comp;
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[ImageReplacer] FindComponentOnGO({componentType.Name}) error: {ex}");
            }
            return null;
        }

        /// <summary>
        /// Get the component type string for display ("Image", "RawImage", "SpriteRenderer").
        /// IL2CPP-safe: uses instanceID cache.
        /// </summary>
        public static string GetComponentTypeName(GameObject go)
        {
            if (go == null) return "Unknown";
            ResolveTypes();
            int goId = go.GetInstanceID();

            if (_imageType != null && FindComponentOnGO(_imageType, goId) != null) return "Image";
            if (_rawImageType != null && FindComponentOnGO(_rawImageType, goId) != null) return "RawImage";
            if (_spriteRendererType != null && FindComponentOnGO(_spriteRendererType, goId) != null) return "SpriteRenderer";
            return "Unknown";
        }

        #endregion

        #region Sprite Info Helpers

        /// <summary>
        /// Get a property value from an object by resolving the PropertyInfo on the object's
        /// actual runtime type. This avoids "Object does not match target type" on IL2CPP
        /// where the cached PropertyInfo may come from a different Type instance.
        /// </summary>
        private static object GetPropertySafe(object obj, string propertyName)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(obj, null);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[ImageReplacer] GetPropertySafe({propertyName}) failed on {obj.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Get the name of a sprite object via reflection (works for Sprite or Texture2D).
        /// </summary>
        public static string GetSpriteName(object spriteObj)
        {
            if (spriteObj == null) return null;
            try
            {
                // All UnityEngine.Object have .name
                var nameProp = spriteObj.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                if (nameProp != null)
                    return nameProp.GetValue(spriteObj, null) as string;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Get the dimensions of a sprite or texture via reflection.
        /// </summary>
        public static Vector2Int GetSpriteSize(object spriteObj)
        {
            if (spriteObj == null) return Vector2Int.zero;
            try
            {
                var type = spriteObj.GetType();

                // Try Sprite.rect first
                var rectProp = type.GetProperty("rect", BindingFlags.Public | BindingFlags.Instance);
                if (rectProp != null)
                {
                    var rect = (Rect)rectProp.GetValue(spriteObj, null);
                    return new Vector2Int((int)rect.width, (int)rect.height);
                }

                // Fallback: Texture2D.width/height
                var widthProp = type.GetProperty("width", BindingFlags.Public | BindingFlags.Instance);
                var heightProp = type.GetProperty("height", BindingFlags.Public | BindingFlags.Instance);
                if (widthProp != null && heightProp != null)
                {
                    int w = (int)widthProp.GetValue(spriteObj, null);
                    int h = (int)heightProp.GetValue(spriteObj, null);
                    return new Vector2Int(w, h);
                }
            }
            catch { }
            return Vector2Int.zero;
        }

        #endregion

        #region Replacement Operations

        /// <summary>
        /// Add or update a replacement entry. Called from InspectorPanel.
        /// </summary>
        public static void AddReplacement(string spriteName, string hierarchyPath,
            int width, int height, Vector2 pivot, Vector4 border, float ppu)
        {
            if (string.IsNullOrEmpty(spriteName)) return;

            var entry = new ImageReplacement
            {
                SpriteName = spriteName,
                HierarchyPath = hierarchyPath,
                OriginalWidth = width,
                OriginalHeight = height,
                PivotX = pivot.x,
                PivotY = pivot.y,
                BorderLeft = border.x,
                BorderBottom = border.y,
                BorderRight = border.z,
                BorderTop = border.w,
                PixelsPerUnit = ppu,
                File = $"{SanitizeFilename(spriteName)}.png",
            };

            _replacements[spriteName] = entry;
            TranslatorCore.SetMetadataDirty();
            TranslatorCore.LogInfo($"[ImageReplacer] Added replacement entry: {spriteName} ({width}x{height})");
        }

        /// <summary>
        /// Remove a replacement entry.
        /// </summary>
        public static void RemoveReplacement(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return;

            if (_replacements.Remove(spriteName))
            {
                if (_loadedSprites.TryGetValue(spriteName, out var sprite))
                {
                    if (sprite != null) UnityEngine.Object.Destroy(sprite);
                    _loadedSprites.Remove(spriteName);
                }
                TranslatorCore.SetMetadataDirty();
                TranslatorCore.LogInfo($"[ImageReplacer] Removed replacement: {spriteName}");
            }
        }

        /// <summary>
        /// Export the original sprite image as a PNG file.
        /// Returns the exported file path, or null on failure.
        /// </summary>
        public static string ExportOriginal(object spriteObj, string spriteName)
        {
            if (spriteObj == null || string.IsNullOrEmpty(spriteName)) return null;

            try
            {
                EnsureDirectories();

                // Extract the sprite region as a readable Texture2D
                Texture2D extracted = null;

                // Check if it's a Sprite (has textureRect) or a raw Texture2D
                var type = spriteObj.GetType();
                var textureRectProp = type.GetProperty("textureRect", BindingFlags.Public | BindingFlags.Instance);
                if (textureRectProp != null)
                {
                    // It's a Sprite — extract the region
                    extracted = TextureUtils.ExtractSpriteRegion(spriteObj);
                }
                else if (spriteObj is Texture2D tex)
                {
                    // It's a raw Texture2D (RawImage)
                    extracted = TextureUtils.MakeReadableCopy(tex);
                }

                if (extracted == null)
                {
                    TranslatorCore.LogWarning($"[ImageReplacer] Failed to extract texture for {spriteName}");
                    return null;
                }

                // Encode to PNG
                byte[] pngData = TextureUtils.EncodeToPngSafe(extracted);
                UnityEngine.Object.Destroy(extracted);

                if (pngData == null || pngData.Length == 0)
                {
                    TranslatorCore.LogWarning($"[ImageReplacer] EncodeToPNG failed for {spriteName}");
                    return null;
                }

                // Save directly to images/ (user edits this file in place)
                string filename = SanitizeFilename(spriteName) + ".png";
                string fullPath = Path.Combine(_imagesFolder, filename);
                File.WriteAllBytes(fullPath, pngData);

                if (_replacements.TryGetValue(spriteName, out var entry))
                {
                    entry.File = filename;
                }

                TranslatorCore.LogInfo($"[ImageReplacer] Exported original: {fullPath} ({pngData.Length} bytes)");
                return fullPath;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[ImageReplacer] ExportOriginal failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Import a replacement PNG for a sprite.
        /// The pngFilename is relative to the images/ folder.
        /// </summary>
        public static bool ImportReplacement(string spriteName, string pngFilename)
        {
            if (string.IsNullOrEmpty(spriteName) || string.IsNullOrEmpty(pngFilename)) return false;

            if (!_replacements.TryGetValue(spriteName, out var entry))
            {
                TranslatorCore.LogWarning($"[ImageReplacer] No replacement entry for {spriteName}");
                return false;
            }

            string fullPath = Path.Combine(_imagesFolder, pngFilename);
            if (!File.Exists(fullPath))
            {
                TranslatorCore.LogWarning($"[ImageReplacer] File not found: {fullPath}");
                return false;
            }

            try
            {
                byte[] pngData = File.ReadAllBytes(fullPath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                _createdTextures.Add(texture);

                if (!TextureUtils.LoadImageToTexture(texture, pngData))
                {
                    TranslatorCore.LogWarning($"[ImageReplacer] LoadImage failed for {pngFilename}");
                    return false;
                }

                // Create sprite with original properties
                var pivot = new Vector2(entry.PivotX, entry.PivotY);
                var border = new Vector4(entry.BorderLeft, entry.BorderBottom, entry.BorderRight, entry.BorderTop);
                var spriteObj = TextureUtils.CreateSpriteSafe(texture, pivot, entry.PixelsPerUnit, border);

                if (spriteObj == null)
                {
                    TranslatorCore.LogWarning($"[ImageReplacer] Sprite.Create failed for {spriteName}");
                    return false;
                }

                if (spriteObj is Sprite sprite)
                {
                    // Name the replacement sprite with the original name so it can be
                    // recognized by patches and ApplyToScene on subsequent reloads
                    sprite.name = spriteName;

                    // Cleanup previous loaded sprite
                    if (_loadedSprites.TryGetValue(spriteName, out var prev) && prev != null)
                        UnityEngine.Object.Destroy(prev);

                    _loadedSprites[spriteName] = sprite;
                    entry.File = pngFilename;

                    TranslatorCore.LogInfo($"[ImageReplacer] Imported replacement: {spriteName} <- {pngFilename} ({texture.width}x{texture.height})");
                    return true;
                }

                TranslatorCore.LogWarning($"[ImageReplacer] Created object is not a Sprite: {spriteObj.GetType().Name}");
                return false;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[ImageReplacer] ImportReplacement failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get a replacement sprite for a given sprite name.
        /// Called from Harmony patches. Returns null if no replacement loaded.
        /// </summary>
        public static Sprite GetReplacement(string spriteName)
        {
            if (TranslatorCore.Config != null && !TranslatorCore.Config.enable_image_replacement) return null;
            if (string.IsNullOrEmpty(spriteName)) return null;
            if (_loadedSprites.TryGetValue(spriteName, out var sprite))
                return sprite;
            return null;
        }

        /// <summary>
        /// Get all replacement entries (for UI display).
        /// </summary>
        public static IReadOnlyDictionary<string, ImageReplacement> GetAll()
        {
            return _replacements;
        }

        /// <summary>
        /// Check if a replacement file exists on disk for an entry.
        /// </summary>
        public static bool HasReplacementFile(string spriteName)
        {
            if (!_replacements.TryGetValue(spriteName, out var entry)) return false;
            if (string.IsNullOrEmpty(entry.File)) return false;
            return System.IO.File.Exists(Path.Combine(_imagesFolder, entry.File));
        }

        /// <summary>
        /// Remember a component's original sprite/texture before replacing it, keyed by instance id.
        /// Used by RestoreAllOriginalImages to revert when the debug toggle is turned off.
        /// </summary>
        private static void TrackReplacement(object component, PropertyInfo prop, string propertyName)
        {
            try
            {
                int id = component is UnityEngine.Object uObj ? uObj.GetInstanceID() : component.GetHashCode();
                if (_replacedComponents.ContainsKey(id)) return; // keep true original
                object originalValue = prop.GetValue(component, null);
                _replacedComponents[id] = (component, originalValue, propertyName);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[ImageReplacer] TrackReplacement failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Revert all tracked replacements to their original sprite/texture.
        /// Only affects components still alive. Clears the tracking dictionary.
        /// </summary>
        public static void RestoreAllOriginalImages()
        {
            int restored = 0;
            foreach (var kvp in _replacedComponents)
            {
                try
                {
                    var (component, originalValue, propertyName) = kvp.Value;
                    if (component == null) continue;
                    // Check if the Unity object still exists
                    if (component is UnityEngine.Object uObj && uObj == null) continue;

                    var prop = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null || prop.SetMethod == null) continue;
                    prop.SetValue(component, originalValue, null);
                    restored++;
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogDebug($"[ImageReplacer] RestoreAll error: {ex.Message}");
                }
            }
            _replacedComponents.Clear();
            TranslatorCore.LogInfo($"[ImageReplacer] Restored {restored} original image(s).");
        }

        /// <summary>
        /// Check if a replacement sprite is loaded in memory.
        /// </summary>
        public static bool IsReplacementLoaded(string spriteName)
        {
            return _loadedSprites.TryGetValue(spriteName, out var s) && s != null;
        }

        /// <summary>
        /// Load all replacements that have files on disk but aren't loaded yet.
        /// Called on startup or after importing.
        /// </summary>
        /// <summary>
        /// Load all replacements from disk. If forceReload, re-imports even already loaded ones
        /// (useful for hot-reloading after editing PNGs).
        /// </summary>
        public static int LoadAllReplacements(bool forceReload = false)
        {
            int loaded = 0;
            foreach (var kvp in _replacements)
            {
                if (!forceReload && IsReplacementLoaded(kvp.Key)) continue;
                if (string.IsNullOrEmpty(kvp.Value.File)) continue;

                if (ImportReplacement(kvp.Key, kvp.Value.File))
                    loaded++;
            }

            if (loaded > 0)
            {
                TranslatorCore.LogInfo($"[ImageReplacer] Loaded {loaded} replacement sprites from disk");
                // Note: we don't call ApplyToScene() here automatically.
                // Harmony patches handle new sprite assignments as the scene loads.
                // ApplyToScene() is only called manually via "Load All" button for hot-reload.
            }

            return loaded;
        }

        /// <summary>
        /// Apply loaded replacements to all Image/RawImage/SpriteRenderer in the current scene.
        /// Scans all components and replaces sprites whose name matches a loaded replacement.
        /// </summary>
        /// <summary>
        /// Apply loaded replacements to the scene by finding target GameObjects via their
        /// stored hierarchy path. Only touches the specific components that need replacing,
        /// instead of iterating all Image/RawImage/SpriteRenderer in the scene.
        /// </summary>
        public static int ApplyToScene()
        {
            if (_loadedSprites.Count == 0) return 0;
            ResolveTypes();
            int applied = 0;

            foreach (var kvp in _replacements)
            {
                var entry = kvp.Value;
                var replacement = GetReplacement(entry.SpriteName);
                if (replacement == null) continue;
                if (string.IsNullOrEmpty(entry.HierarchyPath)) continue;

                try
                {
                    // Find the target GameObject by hierarchy path
                    var go = GameObject.Find(entry.HierarchyPath);
                    if (go == null)
                    {
                        TranslatorCore.LogDebug($"[ImageReplacer] ApplyToScene: GO not found for path '{entry.HierarchyPath}'");
                        continue;
                    }

                    // Try Image.sprite
                    if (_imageType != null)
                    {
                        var comp = FindComponentOnGO(_imageType, go.GetInstanceID(), out var typed);
                        if (typed != null)
                        {
                            var prop = typed.GetType().GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null && prop.SetMethod != null)
                            {
                                TrackReplacement(typed, prop, "sprite");
                                prop.SetValue(typed, replacement, null);
                                applied++;
                                continue;
                            }
                        }
                    }

                    // Try SpriteRenderer.sprite
                    if (_spriteRendererType != null)
                    {
                        var comp = FindComponentOnGO(_spriteRendererType, go.GetInstanceID(), out var typed);
                        if (typed != null)
                        {
                            var prop = typed.GetType().GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null && prop.SetMethod != null)
                            {
                                TrackReplacement(typed, prop, "sprite");
                                prop.SetValue(typed, replacement, null);
                                applied++;
                                continue;
                            }
                        }
                    }

                    // Try RawImage.texture
                    if (_rawImageType != null && replacement.texture != null)
                    {
                        var comp = FindComponentOnGO(_rawImageType, go.GetInstanceID(), out var typed);
                        if (typed != null)
                        {
                            var prop = typed.GetType().GetProperty("texture", BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null && prop.SetMethod != null)
                            {
                                TrackReplacement(typed, prop, "texture");
                                prop.SetValue(typed, replacement.texture, null);
                                applied++;
                                continue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogDebug($"[ImageReplacer] ApplyToScene error for '{entry.SpriteName}': {ex.Message}");
                }
            }

            if (applied > 0)
                TranslatorCore.LogInfo($"[ImageReplacer] Applied {applied} image replacements to scene");

            return applied;
        }

        #endregion

        #region Persistence (JSON)

        /// <summary>
        /// Load replacement definitions from _image_replacements in translations.json.
        /// </summary>
        public static void LoadFromJson(JToken token)
        {
            _replacements.Clear();

            if (token == null || token.Type != JTokenType.Array) return;

            foreach (var item in token)
            {
                if (item.Type != JTokenType.Object) continue;
                var obj = (JObject)item;

                var spriteName = obj.Value<string>("sprite_name");
                if (string.IsNullOrEmpty(spriteName)) continue;

                var entry = new ImageReplacement
                {
                    SpriteName = spriteName,
                    HierarchyPath = obj.Value<string>("path") ?? "",
                    OriginalWidth = obj.Value<int>("original_width"),
                    OriginalHeight = obj.Value<int>("original_height"),
                    PivotX = obj.Value<float>("pivot_x"),
                    PivotY = obj.Value<float>("pivot_y"),
                    BorderLeft = obj.Value<float>("border_left"),
                    BorderBottom = obj.Value<float>("border_bottom"),
                    BorderRight = obj.Value<float>("border_right"),
                    BorderTop = obj.Value<float>("border_top"),
                    PixelsPerUnit = obj.Value<float>("pixels_per_unit"),
                    File = obj.Value<string>("file") ?? obj.Value<string>("replacement_file") ?? obj.Value<string>("original_file")
                };

                // Default PPU if missing
                if (entry.PixelsPerUnit <= 0) entry.PixelsPerUnit = 100f;

                _replacements[spriteName] = entry;
            }

            TranslatorCore.LogInfo($"[ImageReplacer] Loaded {_replacements.Count} replacement definitions from JSON");
        }

        /// <summary>
        /// Save replacement definitions to a JArray for translations.json.
        /// </summary>
        public static JToken SaveToJson()
        {
            if (_replacements.Count == 0) return null;

            var array = new JArray();
            foreach (var kvp in _replacements)
            {
                var entry = kvp.Value;
                var obj = new JObject
                {
                    ["sprite_name"] = entry.SpriteName,
                    ["path"] = entry.HierarchyPath,
                    ["original_width"] = entry.OriginalWidth,
                    ["original_height"] = entry.OriginalHeight,
                    ["pivot_x"] = entry.PivotX,
                    ["pivot_y"] = entry.PivotY,
                    ["border_left"] = entry.BorderLeft,
                    ["border_bottom"] = entry.BorderBottom,
                    ["border_right"] = entry.BorderRight,
                    ["border_top"] = entry.BorderTop,
                    ["pixels_per_unit"] = entry.PixelsPerUnit,
                    ["file"] = entry.File,
                };
                array.Add(obj);
            }

            return array;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Called on scene change: clear cached loaded sprites (they become invalid),
        /// but keep the replacement definitions.
        /// </summary>
        public static void OnSceneChange()
        {
            // Sprites survive scene transitions (we created them, not scene assets).
            // Clear GO ID cache (instanceIDs change between scenes).
            _imageComponentGOIds.Clear();
            _lastImageCacheTime = 0;

            // Load sprites from disk if not already loaded (first scene after startup)
            LoadAllReplacements();

            // Targeted apply: just GameObject.Find per replacement path (fast, no full scan)
            ApplyToScene();
        }

        /// <summary>
        /// Full cleanup: destroy all created textures and sprites.
        /// </summary>
        public static void Cleanup()
        {
            foreach (var kvp in _loadedSprites)
            {
                if (kvp.Value != null)
                {
                    try { UnityEngine.Object.Destroy(kvp.Value); }
                    catch { }
                }
            }
            _loadedSprites.Clear();

            foreach (var tex in _createdTextures)
            {
                if (tex != null)
                {
                    try { UnityEngine.Object.Destroy(tex); }
                    catch { }
                }
            }
            _createdTextures.Clear();

            _replacements.Clear();
            _initialized = false;
            _typesResolved = false;
        }

        #endregion

        #region Helpers

        private static void EnsureDirectories()
        {
            if (!Directory.Exists(_imagesFolder))
                Directory.CreateDirectory(_imagesFolder);
        }

        private static string SanitizeFilename(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sanitized = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0)
                    sanitized.Append('_');
                else
                    sanitized.Append(c);
            }
            return sanitized.ToString();
        }

        #endregion
    }
}
