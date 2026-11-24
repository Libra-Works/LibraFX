#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.UnityIntegration.Editor
{
    internal static class LiberaFxUnityAssetBrowser
    {
        [Serializable]
        private sealed class AssetRequest
        {
            public string type;
            public string requestId;
            public string query;
            public string assetClass;
            public string folder;
            public bool foldersOnly;
            public int offset;
            public int limit;
            public string path;
            public int size;
        }

        [Serializable]
        private sealed class FolderEntry
        {
            public string path;
            public string name;
        }

        [Serializable]
        private sealed class MaterialParamEntry
        {
            public string name;
            public string kind;
            public float[] vectorValue;
            public float scalarValue;
            public string textureValue;
        }

        [Serializable]
        private sealed class MaterialSlotEntry
        {
            public int slot;
            public string name;
            public string path;
            public MaterialParamEntry[] parameters;
        }

        [Serializable]
        private sealed class AssetEntry
        {
            public string name;
            public string path;
            public string assetClass;
            public string engine;
            public int width;
            public int height;
            public MaterialSlotEntry[] materials;
        }

        [Serializable]
        private sealed class AssetReply
        {
            public string type = "asset_result";
            public string requestId;
            public string operation;
            public string folder;
            public bool pending;
            public int total;
            public int offset;
            public FolderEntry[] folders;
            public AssetEntry[] assets;
            public AssetEntry asset;
            public string path;
            public string mime;
            public int width;
            public int height;
            public string imageBase64;
            public string error;
        }

        public static bool CanHandle(string type) =>
            type == "asset_catalog" || type == "asset_details" || type == "asset_thumbnail";

        public static string ProcessRequest(string requestJson)
        {
            AssetRequest request;
            try { request = JsonUtility.FromJson<AssetRequest>(requestJson); }
            catch (ArgumentException) { request = null; }
            if (request == null || !Guid.TryParseExact(request.requestId, "D", out var requestId) ||
                requestId == Guid.Empty)
            {
                return JsonUtility.ToJson(new AssetReply
                {
                    operation = "invalid",
                    error = "Invalid Unity asset request."
                });
            }

            try
            {
                return request.type switch
                {
                    "asset_catalog" when request.foldersOnly => HandleFolders(request),
                    "asset_catalog" => HandleCatalog(request),
                    "asset_details" => HandleDetails(request),
                    "asset_thumbnail" => HandleThumbnail(request),
                    _ => JsonUtility.ToJson(new AssetReply
                    {
                        requestId = request.requestId,
                        operation = "invalid",
                        error = "Unsupported Unity asset request."
                    })
                };
            }
            catch (Exception exception)
            {
                var operation = request.type switch
                {
                    "asset_catalog" => "catalog",
                    "asset_details" => "details",
                    "asset_thumbnail" => "thumbnail",
                    _ => "invalid"
                };
                return JsonUtility.ToJson(new AssetReply
                {
                    requestId = request.requestId,
                    operation = operation,
                    error = exception.Message
                });
            }
        }

        private static string HandleFolders(AssetRequest request)
        {
            var folder = (request.folder ?? "").Trim().TrimEnd('/');
            var offset = Math.Max(0, request.offset);
            var limit = Math.Clamp(request.limit <= 0 ? 64 : request.limit, 1, 64);
            var entries = new List<FolderEntry>();
            if (string.IsNullOrEmpty(folder))
            {
                if (AssetDatabase.IsValidFolder("Assets"))
                    entries.Add(new FolderEntry { path = "Assets", name = "Assets" });
                if (AssetDatabase.IsValidFolder("Packages"))
                    entries.Add(new FolderEntry { path = "Packages", name = "Packages" });
            }
            else if (AssetDatabase.IsValidFolder(folder))
            {
                foreach (var sub in AssetDatabase.GetSubFolders(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var slash = sub.LastIndexOf('/');
                    var name = slash >= 0 ? sub[(slash + 1)..] : sub;
                    entries.Add(new FolderEntry { path = sub, name = name });
                }
            }
            var page = entries.Skip(offset).Take(limit).ToArray();
            return JsonUtility.ToJson(new AssetReply
            {
                requestId = request.requestId,
                operation = "catalog",
                folder = folder,
                total = entries.Count,
                offset = offset,
                folders = page
            });
        }

        private static string HandleCatalog(AssetRequest request)
        {
            var folder = (request.folder ?? "").Trim().TrimEnd('/');
            var query = (request.query ?? "").Trim();
            var assetClass = (request.assetClass ?? "").Trim();
            var offset = Math.Max(0, request.offset);
            var limit = Math.Clamp(request.limit <= 0 ? 32 : request.limit, 1, 64);
            var filter = assetClass switch
            {
                "model" => "t:Mesh",
                "texture" => "t:Texture2D",
                "material" => "t:Material",
                _ => "t:Mesh t:Texture2D t:Material"
            };
            var searchRoots = string.IsNullOrEmpty(folder)
                ? new[] { "Assets", "Packages" }.Where(AssetDatabase.IsValidFolder).ToArray()
                : AssetDatabase.IsValidFolder(folder) ? new[] { folder } : Array.Empty<string>();
            var guids = searchRoots.Length == 0
                ? Array.Empty<string>()
                : AssetDatabase.FindAssets(string.IsNullOrEmpty(query) ? filter : filter + " " + query, searchRoots);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matched = new List<AssetEntry>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (!string.IsNullOrEmpty(folder) &&
                    !path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                var kind = ClassifyType(mainType);
                if (kind == null || (!string.IsNullOrEmpty(assetClass) && kind != assetClass)) continue;
                var slash = path.LastIndexOf('/');
                var fileName = slash >= 0 ? path[(slash + 1)..] : path;
                var dot = fileName.LastIndexOf('.');
                var displayName = dot > 0 ? fileName[..dot] : fileName;
                if (!string.IsNullOrEmpty(query) &&
                    displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                matched.Add(new AssetEntry
                {
                    name = displayName,
                    path = path,
                    assetClass = kind,
                    engine = "Unity",
                    materials = Array.Empty<MaterialSlotEntry>()
                });
            }
            matched.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));
            var page = matched.Skip(offset).Take(limit).ToArray();
            return JsonUtility.ToJson(new AssetReply
            {
                requestId = request.requestId,
                operation = "catalog",
                folder = folder,
                pending = false,
                total = matched.Count,
                offset = offset,
                assets = page
            });
        }

        private static string HandleDetails(AssetRequest request)
        {
            var path = request.path ?? "";
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (!obj) throw new InvalidOperationException("Unity asset not found: " + path);
            var kind = ClassifyType(obj.GetType()) ??
                throw new InvalidOperationException("Unsupported Unity asset type for " + path);
            var entry = new AssetEntry
            {
                name = obj.name,
                path = path,
                assetClass = kind,
                engine = "Unity",
                materials = Array.Empty<MaterialSlotEntry>()
            };
            if (obj is Texture2D tex)
            {
                entry.width = tex.width;
                entry.height = tex.height;
            }
            else if (obj is Material mat)
            {
                entry.materials = new[] { BuildMaterialSlot(0, mat) };
            }
            else if (obj is GameObject go)
            {
                var renderer = go.GetComponentInChildren<Renderer>();
                if (renderer != null)
                {
                    var mats = renderer.sharedMaterials;
                    var slots = new List<MaterialSlotEntry>(mats.Length);
                    for (var i = 0; i < mats.Length && i < 128; i++)
                        if (mats[i] != null) slots.Add(BuildMaterialSlot(i, mats[i]));
                    entry.materials = slots.ToArray();
                }
            }
            return SerializeDetailsReply(request.requestId, entry);
        }

        private static string HandleThumbnail(AssetRequest request)
        {
            var path = request.path ?? "";
            var size = Math.Clamp(request.size <= 0 ? 128 : request.size, 16, 256);
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (!obj) throw new InvalidOperationException("Unity asset not found: " + path);
            var source = obj as Texture2D ?? AssetPreview.GetAssetPreview(obj) ?? AssetPreview.GetMiniThumbnail(obj);
            if (!source) throw new InvalidOperationException("No thumbnail available for " + path);
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            var copy = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                copy.Apply();
                var png = copy.EncodeToPNG();
                return JsonUtility.ToJson(new AssetReply
                {
                    requestId = request.requestId,
                    operation = "thumbnail",
                    path = path,
                    mime = "image/png",
                    width = size,
                    height = size,
                    imageBase64 = Convert.ToBase64String(png)
                });
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static string ClassifyType(Type type)
        {
            if (type == null) return null;
            if (typeof(Mesh).IsAssignableFrom(type) || typeof(GameObject).IsAssignableFrom(type)) return "model";
            if (typeof(Texture2D).IsAssignableFrom(type)) return "texture";
            if (typeof(Material).IsAssignableFrom(type)) return "material";
            return null;
        }

        private static MaterialSlotEntry BuildMaterialSlot(int slotIndex, Material mat)
        {
            var matPath = AssetDatabase.GetAssetPath(mat);
            if (string.IsNullOrEmpty(matPath)) matPath = "Assets/__InlineMaterial";
            var parameters = new List<MaterialParamEntry>();
            var shader = mat.shader;
            if (shader != null)
            {
                var count = shader.GetPropertyCount();
                for (var i = 0; i < count && parameters.Count < 256; i++)
                {
                    var propName = shader.GetPropertyName(i);
                    var propType = shader.GetPropertyType(i);
                    if (propType == ShaderPropertyType.Float || propType == ShaderPropertyType.Range)
                    {
                        var val = mat.GetFloat(propName);
                        if (!float.IsFinite(val)) continue;
                        parameters.Add(new MaterialParamEntry { name = propName, kind = "scalar", scalarValue = val });
                    }
                    else if (propType == ShaderPropertyType.Vector || propType == ShaderPropertyType.Color)
                    {
                        var vec = propType == ShaderPropertyType.Color ? (Vector4)mat.GetColor(propName) : mat.GetVector(propName);
                        if (!float.IsFinite(vec.x) || !float.IsFinite(vec.y) || !float.IsFinite(vec.z) || !float.IsFinite(vec.w)) continue;
                        parameters.Add(new MaterialParamEntry
                        {
                            name = propName,
                            kind = "vector",
                            vectorValue = new[] { vec.x, vec.y, vec.z, vec.w }
                        });
                    }
                    else if (propType == ShaderPropertyType.Texture)
                    {
                        var tex = mat.GetTexture(propName);
                        var texPath = tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                        if (string.IsNullOrEmpty(texPath)) texPath = null;
                        parameters.Add(new MaterialParamEntry { name = propName, kind = "texture", textureValue = texPath });
                    }
                }
            }
            return new MaterialSlotEntry
            {
                slot = slotIndex,
                name = mat.name,
                path = matPath,
                parameters = parameters.ToArray()
            };
        }

        // PreviewAssetStore expects parameter.Value as scalar/4-array/string|null rather than a typed wrapper.
        private static string SerializeDetailsReply(string requestId, AssetEntry entry)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"type\":\"asset_result\",\"requestId\":\"").Append(Escape(requestId))
              .Append("\",\"operation\":\"details\",\"asset\":{")
              .Append("\"name\":\"").Append(Escape(entry.name)).Append('"')
              .Append(",\"path\":\"").Append(Escape(entry.path)).Append('"')
              .Append(",\"assetClass\":\"").Append(Escape(entry.assetClass)).Append('"')
              .Append(",\"engine\":\"Unity\"")
              .Append(",\"width\":").Append(entry.width.ToString(CultureInfo.InvariantCulture))
              .Append(",\"height\":").Append(entry.height.ToString(CultureInfo.InvariantCulture))
              .Append(",\"materials\":[");
            for (var s = 0; s < entry.materials.Length; s++)
            {
                if (s > 0) sb.Append(',');
                var slot = entry.materials[s];
                sb.Append("{\"slot\":").Append(slot.slot.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"name\":\"").Append(Escape(slot.name)).Append('"')
                  .Append(",\"path\":\"").Append(Escape(slot.path)).Append('"')
                  .Append(",\"parameters\":[");
                for (var p = 0; p < slot.parameters.Length; p++)
                {
                    if (p > 0) sb.Append(',');
                    var param = slot.parameters[p];
                    sb.Append("{\"name\":\"").Append(Escape(param.name)).Append('"')
                      .Append(",\"kind\":\"").Append(Escape(param.kind)).Append("\",\"value\":");
                    if (param.kind == "scalar")
                        sb.Append(param.scalarValue.ToString("R", CultureInfo.InvariantCulture));
                    else if (param.kind == "vector")
                        sb.Append('[').Append(param.vectorValue[0].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                          .Append(param.vectorValue[1].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                          .Append(param.vectorValue[2].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                          .Append(param.vectorValue[3].ToString("R", CultureInfo.InvariantCulture)).Append(']');
                    else if (param.textureValue == null)
                        sb.Append("null");
                    else
                        sb.Append('"').Append(Escape(param.textureValue)).Append('"');
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        private static string Escape(string text) =>
            (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
#endif
