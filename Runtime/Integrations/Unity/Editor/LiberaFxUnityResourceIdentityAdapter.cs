#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    public enum LiberaFxUnityProjectIdentityState
    {
        Ready,
        Unregistered,
        CloneDecisionRequired,
        QueryFailure
    }

    [Serializable]
    public sealed class LiberaFxUnityResourceProbeResult
    {
        public string status;
        public string actualKind;
        public string detail;

        internal LiberaFxUnityResourceProbeResult(string resultStatus, string kind = "", string message = "")
        {
            status = resultStatus;
            actualKind = kind;
            detail = message;
        }
    }

    [Serializable]
    public sealed class LiberaFxUnityShaderParameter
    {
        // This is engine metadata only. The Libera parameter GUID is allocated separately
        // and must be explicitly bound to this property by the author.
        public string name;
        public string kind;
        public int association = 2;
        public int index = -1;
        public string defaultValueJson;
        public string minimumJson;
        public string maximumJson;
    }

    [Serializable]
    internal sealed class UnityShaderPropertyBinding
    {
        public string name;
        public int association;
        public int index;
    }

    [Serializable]
    internal sealed class UnityResourceIdentityRequestBinding
    {
        public string format;
        public string value;
    }

    [Serializable]
    internal sealed class UnityResourceIdentityRequest
    {
        public string type;
        public string requestId;
        public string operation;
        public string projectId;
        public UnityResourceIdentityRequestBinding binding;
        public UnityResourceIdentityRequestBinding resourceBinding;
        public UnityResourceIdentityRequestBinding parameterBinding;
        public UnityRuntimeBindingDeliveryResource[] resources;
    }

    [Serializable]
    internal sealed class UnityRuntimeBindingDeliveryResource
    {
        public string resourceId;
        public string kind;
        public UnityResourceIdentityRequestBinding binding;
        public UnityRuntimeBindingDeliveryParameter[] parameters;
    }

    [Serializable]
    internal sealed class UnityRuntimeBindingDeliveryParameter
    {
        public string parameterId;
        public string name;
        public string kind;
        public UnityResourceIdentityRequestBinding binding;
    }

    [Serializable]
    internal sealed class UnityResourceIdentityProject
    {
        public string engine = "Unity";
        public string projectId;
    }

    [Serializable]
    internal sealed class UnityResourceIdentityReply
    {
        public string type = "resource_identity_result";
        public string requestId;
        public string operation;
        public string status;
        public string projectState;
        public UnityResourceIdentityProject project;
        public string actualKind;
        public string detail;
        public LiberaFxUnityShaderParameter[] parameters;
        public string assetPath;
    }

    /// <summary>
    /// Unity Editor-side identity provider and exact asset/property probe. All methods
    /// require the main Editor thread. No asset is matched by display name or old path.
    /// The app transport must call these methods and report their explicit statuses.
    /// </summary>
    public static class LiberaFxUnityResourceIdentityAdapter
    {
        public const string AssetBindingFormat = "unity.asset-guid-local-id.v1";
        public const string ParameterBindingFormat = "unity.shader-property.v1";
        private const string IdentityAssetPath = "Assets/LiberaFx/Editor/LiberaFxUnityResourceProjectIdentity.asset";
        private const string MarkerRelativePath = "Library/LiberaFxProjectIdentity.marker";

        [MenuItem("Tools/LiberaFx/Resources/Initialize or Resolve Project Identity")]
        private static void InitializeOrResolveProjectIdentity()
        {
            var asset = AssetDatabase.LoadAssetAtPath<LiberaFxUnityResourceProjectIdentity>(IdentityAssetPath);
            if (!asset)
            {
                EnsureAssetFolders();
                asset = ScriptableObject.CreateInstance<LiberaFxUnityResourceProjectIdentity>();
                asset.SetProjectId(Guid.NewGuid().ToString("D"));
                AssetDatabase.CreateAsset(asset, IdentityAssetPath);
                AssetDatabase.SaveAssets();
                WriteLocalMarker(asset.ProjectId);
                return;
            }

            var state = GetProjectIdentity(out var projectId);
            if (state == LiberaFxUnityProjectIdentityState.Ready) return;
            if (state == LiberaFxUnityProjectIdentityState.Unregistered)
            {
                EditorUtility.DisplayDialog("LIBERA FX Resource Identity",
                    "The project identity asset is missing or invalid. Recreate it explicitly before registering resources.",
                    "OK");
                return;
            }

            var choice = EditorUtility.DisplayDialogComplex("LIBERA FX Project Identity",
                "This checkout has no matching local identity marker. It may be a copied project. " +
                "Choose whether it intentionally shares the existing project's bindings or is a new project.",
                "Keep shared identity", "Cancel", "Fork new identity");
            if (choice == 0) WriteLocalMarker(projectId);
            else if (choice == 2) ForkProjectIdentity(asset);
        }

        [MenuItem("Tools/LiberaFx/Resources/Copy Selected Asset Binding", false, 202)]
        private static void CopySelectedAssetBinding()
        {
            try
            {
                var binding = CreateAssetBinding(Selection.activeObject);
                EditorGUIUtility.systemCopyBuffer = binding;
                var window = EditorWindow.GetWindow<LiberaFxUnityResourceIdentityConnection>();
                window.ShowNotification(new GUIContent("Copied stable Unity asset binding."));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentNullException)
            {
                EditorUtility.DisplayDialog("LIBERA FX Resource Identity",
                    "Select a saved Mesh, Texture2D, or Material asset, then copy its stable binding.", "OK");
            }
        }

        [MenuItem("Tools/LiberaFx/Resources/Copy Selected Asset Binding", true)]
        private static bool CanCopySelectedAssetBinding() =>
            Selection.activeObject && KindOf(Selection.activeObject).Length > 0;

        public static LiberaFxUnityProjectIdentityState GetProjectIdentity(out string projectId)
        {
            projectId = "";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return LiberaFxUnityProjectIdentityState.QueryFailure;
            var asset = AssetDatabase.LoadAssetAtPath<LiberaFxUnityResourceProjectIdentity>(IdentityAssetPath);
            if (!asset || !Guid.TryParseExact(asset.ProjectId, "D", out var id) || id == Guid.Empty)
                return LiberaFxUnityProjectIdentityState.Unregistered;
            projectId = id.ToString("D");
            var marker = MarkerPath();
            if (!File.Exists(marker)) return LiberaFxUnityProjectIdentityState.CloneDecisionRequired;
            try
            {
                var localId = File.ReadAllText(marker).Trim();
                return Guid.TryParseExact(localId, "D", out var localGuid) && localGuid == id
                    ? LiberaFxUnityProjectIdentityState.Ready
                    : LiberaFxUnityProjectIdentityState.CloneDecisionRequired;
            }
            catch (IOException)
            {
                return LiberaFxUnityProjectIdentityState.QueryFailure;
            }
            catch (UnauthorizedAccessException)
            {
                return LiberaFxUnityProjectIdentityState.QueryFailure;
            }
        }

        public static LiberaFxUnityResourceProbeResult QueryResource(
            string expectedProjectId, string bindingFormat, string bindingValue)
        {
            if (!IsSelectedProject(expectedProjectId))
                return new("Offline", message: "Selected Unity project identity is not ready in this Editor.");
            if (bindingFormat != AssetBindingFormat ||
                !TryParseAssetBinding(bindingValue, out var guid, out var localId))
                return new("QueryFailure", message: "Unsupported or malformed Unity asset binding.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return new("QueryFailure", message: "Unity AssetDatabase is updating.");

            var failure = FindAsset(guid, localId, out var asset);
            if (failure != null) return failure;
            var kind = KindOf(asset);
            return kind.Length == 0
                ? new("TypeMismatch", message: "The registered Unity object type is unsupported.")
                : new("Resolved", kind);
        }

        public static LiberaFxUnityResourceProbeResult QueryParameter(
            string expectedProjectId, string materialFormat, string materialBindingValue,
            string parameterFormat, string parameterBindingValue)
        {
            if (materialFormat != AssetBindingFormat)
                return new("QueryFailure", message: "Unsupported Unity material binding format.");
            return QueryParameter(expectedProjectId, materialBindingValue, parameterFormat, parameterBindingValue);
        }

        public static LiberaFxUnityResourceProbeResult QueryParameter(
            string expectedProjectId, string materialBindingValue, string parameterFormat, string parameterBindingValue)
        {
            if (!IsSelectedProject(expectedProjectId))
                return new("Offline", message: "Selected Unity project identity is not ready in this Editor.");
            if (!TryParseAssetBinding(materialBindingValue, out var guid, out var localId))
                return new("QueryFailure", message: "Malformed Unity material binding.");
            if (parameterFormat != ParameterBindingFormat)
                return new("QueryFailure", message: "Unsupported Unity material parameter binding format.");
            UnityShaderPropertyBinding property;
            try { property = JsonUtility.FromJson<UnityShaderPropertyBinding>(parameterBindingValue); }
            catch (ArgumentException) { return new("QueryFailure", message: "Malformed Unity shader-property binding."); }
            if (property == null || string.IsNullOrWhiteSpace(property.name) ||
                property.name.Length > 256 || property.association != 2 || property.index != -1)
                return new("QueryFailure", message: "Malformed or unsupported Unity shader-property binding.");
            var assetResult = FindAsset(guid, localId, out var asset);
            if (assetResult != null) return assetResult;
            var material = asset as Material;
            if (!material || !material.shader)
                return new("TypeMismatch", message: "The registered resource is not a Unity Material.");
            var shader = material.shader;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                if (!string.Equals(shader.GetPropertyName(index), property.name, StringComparison.Ordinal)) continue;
                var kind = KindOf(shader.GetPropertyType(index));
                return kind.Length == 0
                    ? new("TypeMismatch", message: "The Unity shader property type is unsupported.")
                    : new("Resolved", kind);
            }
            return new("Missing", message: "The registered shader property no longer exists.");
        }

        public static LiberaFxUnityShaderParameter[] ReadMaterialSchema(
            string expectedProjectId, string materialBindingValue, out string error)
        {
            error = "";
            if (!IsSelectedProject(expectedProjectId))
            {
                error = "Selected Unity project identity is not ready in this Editor.";
                return Array.Empty<LiberaFxUnityShaderParameter>();
            }
            if (!TryParseAssetBinding(materialBindingValue, out var guid, out var localId))
            {
                error = "Malformed Unity material binding.";
                return Array.Empty<LiberaFxUnityShaderParameter>();
            }
            var result = FindAsset(guid, localId, out var asset);
            if (result != null)
            {
                error = result.detail;
                return Array.Empty<LiberaFxUnityShaderParameter>();
            }
            var material = asset as Material;
            if (!material || !material.shader)
            {
                error = "The registered resource is not a Unity Material.";
                return Array.Empty<LiberaFxUnityShaderParameter>();
            }
            var shader = material.shader;
            var parameters = new List<LiberaFxUnityShaderParameter>();
            for (var index = 0; index < shader.GetPropertyCount() && parameters.Count < 256; index++)
            {
                var kind = KindOf(shader.GetPropertyType(index));
                if (kind.Length == 0) continue;
                var parameter = new LiberaFxUnityShaderParameter
                {
                    name = shader.GetPropertyName(index),
                    kind = kind
                };
                if (kind == "scalar")
                {
                    parameter.defaultValueJson = shader.GetPropertyDefaultFloatValue(index)
                        .ToString("R", CultureInfo.InvariantCulture);
                    if (shader.GetPropertyType(index) == ShaderPropertyType.Range)
                    {
                        var range = shader.GetPropertyRangeLimits(index);
                        parameter.minimumJson = range.x.ToString("R", CultureInfo.InvariantCulture);
                        parameter.maximumJson = range.y.ToString("R", CultureInfo.InvariantCulture);
                    }
                }
                else if (kind == "vector")
                {
                    var value = shader.GetPropertyDefaultVectorValue(index);
                    parameter.defaultValueJson = "[" + value.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                        value.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                        value.z.ToString("R", CultureInfo.InvariantCulture) + "," +
                        value.w.ToString("R", CultureInfo.InvariantCulture) + "]";
                }
                else
                {
                    parameter.defaultValueJson = "null";
                }
                parameters.Add(parameter);
            }
            return parameters.ToArray();
        }

        // The app's preview transport must explicitly route resource_identity_* messages
        // to this Editor-side entry point. The project ID in replies is never the transport peer ID.
        public static string ProcessRequest(string requestJson)
        {
            UnityResourceIdentityRequest request;
            try { request = JsonUtility.FromJson<UnityResourceIdentityRequest>(requestJson); }
            catch (ArgumentException) { request = null; }
            if (request == null || !Guid.TryParseExact(request.requestId, "D", out var requestId) ||
                requestId == Guid.Empty)
                return JsonUtility.ToJson(new UnityResourceIdentityReply
                {
                    operation = "invalid",
                    status = "QueryFailure",
                    detail = "Invalid resource identity request."
                });

            if (LiberaFx.UnityIntegration.Editor.LiberaFxUnityAssetBrowser.CanHandle(request.type))
                return LiberaFx.UnityIntegration.Editor.LiberaFxUnityAssetBrowser.ProcessRequest(requestJson);

            if (request.type == "resource_identity_project")
            {
                var operation = string.IsNullOrEmpty(request.operation) ? "status" : request.operation;
                var detail = ApplyProjectOperation(operation, out var actionStatus);
                var projectState = GetProjectIdentity(out var projectId);
                return JsonUtility.ToJson(new UnityResourceIdentityReply
                {
                    requestId = request.requestId,
                    operation = operation,
                    status = actionStatus ?? projectState.ToString(),
                    projectState = projectState.ToString(),
                    project = new UnityResourceIdentityProject { projectId = projectId },
                    detail = detail ?? ""
                });
            }

            if (request.type == "resource_identity_schema")
            {
                var schemaResult = QueryResource(request.projectId, request.resourceBinding?.format,
                    request.resourceBinding?.value);
                var schema = Array.Empty<LiberaFxUnityShaderParameter>();
                var detail = schemaResult.detail;
                var status = schemaResult.status;
                if (status == "Resolved" && schemaResult.actualKind != "material")
                {
                    status = "TypeMismatch";
                    detail = "The registered Unity resource is not a Material.";
                }
                if (status == "Resolved")
                {
                    schema = ReadMaterialSchema(request.projectId, request.resourceBinding.value, out detail);
                    if (!string.IsNullOrEmpty(detail)) status = "QueryFailure";
                }
                GetProjectIdentity(out var schemaProjectId);
                return JsonUtility.ToJson(new UnityResourceIdentityReply
                {
                    requestId = request.requestId,
                    operation = "schema",
                    status = status,
                    projectState = GetProjectIdentity(out _).ToString(),
                    project = new UnityResourceIdentityProject { projectId = schemaProjectId },
                    actualKind = schemaResult.actualKind ?? "",
                    detail = detail ?? "",
                    parameters = schema
                });
            }

            if (request.type == "resource_identity_deliver")
                return DeliverBindings(request);

            if (request.type != "resource_identity_probe")
                return JsonUtility.ToJson(new UnityResourceIdentityReply
                {
                    requestId = request.requestId,
                    operation = "probe",
                    status = "QueryFailure",
                    detail = "Unsupported resource identity request."
                });

            // JsonUtility materializes empty objects for absent optional bindings.
            var isParameterProbe = !string.IsNullOrEmpty(request.parameterBinding?.format) ||
                !string.IsNullOrEmpty(request.parameterBinding?.value) ||
                !string.IsNullOrEmpty(request.resourceBinding?.format) ||
                !string.IsNullOrEmpty(request.resourceBinding?.value);
            var result = isParameterProbe
                ? QueryParameter(request.projectId, request.resourceBinding?.format, request.resourceBinding?.value,
                    request.parameterBinding?.format, request.parameterBinding?.value)
                : QueryResource(request.projectId, request.binding?.format, request.binding?.value);
            GetProjectIdentity(out var currentProjectId);
            return JsonUtility.ToJson(new UnityResourceIdentityReply
            {
                requestId = request.requestId,
                operation = "probe",
                status = result.status,
                projectState = GetProjectIdentity(out _).ToString(),
                project = new UnityResourceIdentityProject { projectId = currentProjectId },
                actualKind = result.actualKind ?? "",
                detail = result.detail ?? ""
            });
        }

        internal static LiberaFxUnityBindingAsset CreatePreviewBindings(string json)
        {
            var stored = AssetDatabase.LoadAssetAtPath<LiberaFxUnityBindingAsset>(
                "Assets/LiberaFx/LiberaFxBindings.asset");
            var bindings = stored ? UnityEngine.Object.Instantiate(stored) :
                ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            bindings.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                if (json != "null")
                {
                    var request = JsonUtility.FromJson<UnityResourceIdentityRequest>(json) ??
                        throw new InvalidOperationException("Missing Unity preview binding metadata.");
                    var reply = DeliverBindings(request, bindings);
                    if (reply.status != "Resolved")
                    {
                        throw new InvalidOperationException(reply.status + ": " + reply.detail);
                    }
                }
                return bindings;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(bindings);
                throw;
            }
        }

        private static string DeliverBindings(UnityResourceIdentityRequest request) =>
            JsonUtility.ToJson(DeliverBindings(request, null));

        private static UnityResourceIdentityReply DeliverBindings(UnityResourceIdentityRequest request,
            LiberaFxUnityBindingAsset transientBindings)
        {
            GetProjectIdentity(out var projectId);
            var reply = new UnityResourceIdentityReply
            {
                requestId = request.requestId,
                operation = "deliver",
                project = new UnityResourceIdentityProject { projectId = projectId },
                projectState = GetProjectIdentity(out _).ToString()
            };
            if (!IsSelectedProject(request.projectId))
            {
                reply.status = "Offline";
                reply.detail = "The selected Unity project identity is not ready in this Editor.";
                return reply;
            }
            if (request.resources == null || request.resources.Length is < 1 or > 256)
            {
                reply.status = "QueryFailure";
                reply.detail = "Unity binding delivery must include 1..256 resources.";
                return reply;
            }

            var existingBindingAsset = AssetDatabase.LoadAssetAtPath<LiberaFxUnityBindingAsset>(
                "Assets/LiberaFx/LiberaFxBindings.asset");
            var resolved = new List<(UnityRuntimeBindingDeliveryResource Entry, UnityEngine.Object Asset,
                string Identity, string TintPropertyName)>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in request.resources)
            {
                if (entry == null || !Guid.TryParseExact(entry.resourceId, "D", out var resourceId) ||
                    resourceId == Guid.Empty || entry.kind is not ("model" or "texture" or "material") ||
                    entry.binding == null || entry.binding.format != AssetBindingFormat ||
                    !TryParseAssetBinding(entry.binding.value, out var guid, out var localId))
                {
                    reply.status = "QueryFailure";
                    reply.detail = "Unity resource delivery contains an invalid stable ID, kind, or opaque binding.";
                    return reply;
                }
                var identity = guid + ":" + localId.ToString(CultureInfo.InvariantCulture);
                if (!identities.Add(identity))
                {
                    reply.status = "QueryFailure";
                    reply.detail = "Multiple LIBERA IDs resolve to the same Unity object; resolve the alias explicitly before delivery.";
                    return reply;
                }
                var probe = FindAsset(guid, localId, out var asset);
                if (probe != null || !asset || KindOf(asset) != entry.kind)
                {
                    reply.status = probe?.status ?? "TypeMismatch";
                    reply.detail = probe?.detail ?? "Unity object's actual type does not match the registered resource kind.";
                    return reply;
                }
                if (entry.parameters?.Length > 256)
                {
                    reply.status = "QueryFailure";
                    reply.detail = "Unity material parameter binding count exceeds 256.";
                    return reply;
                }
                if (entry.kind == "material" && !ValidateDeliveredParameters(entry, asset as Material, out var error))
                {
                    reply.status = "TypeMismatch";
                    reply.detail = error;
                    return reply;
                }
                var tintPropertyName = "";
                if (entry.kind == "material")
                {
                    var existingMaterial = existingBindingAsset == null
                        ? null
                        : (existingBindingAsset.Materials ?? Array.Empty<LiberaFxUnityMaterialBinding>())
                            .FirstOrDefault(item => item != null && item.assetIdentity == identity);
                    tintPropertyName = existingMaterial?.tintPropertyName;
                    if (string.IsNullOrWhiteSpace(tintPropertyName)) tintPropertyName = "_Color";
                    if (!HasTintProperty(asset as Material, tintPropertyName))
                    {
                        reply.status = "TypeMismatch";
                        reply.detail = "Unity Material has no supported tint property '" + tintPropertyName +
                            "'. Configure a Material exposing a Color/Vector tint property before delivery.";
                        return reply;
                    }
                }
                resolved.Add((entry, asset, identity, tintPropertyName));
            }

            const string bindingAssetPath = "Assets/LiberaFx/LiberaFxBindings.asset";
            var bindingAsset = transientBindings;
            var created = false;
            if (!transientBindings)
            {
                EnsureAssetFolders();
                bindingAsset = existingBindingAsset;
                if (!bindingAsset)
                {
                    bindingAsset = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                    AssetDatabase.CreateAsset(bindingAsset, bindingAssetPath);
                    created = true;
                }
                Undo.RecordObject(bindingAsset, "Deliver LIBERA FX resource bindings");
            }
            var serialized = new SerializedObject(bindingAsset);
            var renderAssets = serialized.FindProperty("renderAssets");
            var materials = serialized.FindProperty("materials");
            foreach (var item in resolved)
            {
                var entry = item.Entry;
                if (entry.kind is "model" or "texture")
                {
                    var target = FindOrAppend(renderAssets, "assetIdentity", item.Identity);
                    target.FindPropertyRelative("assetIdentity").stringValue = item.Identity;
                    target.FindPropertyRelative("mesh").objectReferenceValue =
                        entry.kind == "model" ? item.Asset : null;
                    target.FindPropertyRelative("texture").objectReferenceValue =
                        entry.kind == "texture" ? item.Asset : null;
                }
                else
                {
                    var target = FindOrAppend(materials, "assetIdentity", item.Identity, isMaterial: true);
                    target.FindPropertyRelative("assetIdentity").stringValue = item.Identity;
                    target.FindPropertyRelative("material").objectReferenceValue = item.Asset;
                    target.FindPropertyRelative("tintPropertyName").stringValue = item.TintPropertyName;
                    var parameterMappings = target.FindPropertyRelative("parameterMappings");
                    parameterMappings.arraySize = entry.parameters?.Length ?? 0;
                    for (var index = 0; index < parameterMappings.arraySize; index++)
                    {
                        var parameter = entry.parameters[index];
                        var mapping = parameterMappings.GetArrayElementAtIndex(index);
                        var locator = JsonUtility.FromJson<UnityShaderPropertyBinding>(parameter.binding.value);
                        mapping.FindPropertyRelative("authoredName").stringValue = parameter.name;
                        mapping.FindPropertyRelative("unityPropertyName").stringValue = locator.name;
                    }
                }
            }
            if (transientBindings)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
                reply.status = "Resolved";
                return reply;
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(bindingAsset);
            AssetDatabase.SaveAssets();
            reply.status = "Resolved";
            reply.assetPath = bindingAssetPath;
            reply.detail = created
                ? "Created the explicitly requested project-local binding asset; existing graph values were not changed."
                : "Updated matching entries and preserved unrelated Unity bindings.";
            return reply;
        }

        private static bool HasTintProperty(Material material, string propertyName)
        {
            if (!material || !material.shader || string.IsNullOrWhiteSpace(propertyName)) return false;
            var shader = material.shader;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                if (!string.Equals(shader.GetPropertyName(index), propertyName, StringComparison.Ordinal)) continue;
                var type = shader.GetPropertyType(index);
                return type == ShaderPropertyType.Color || type == ShaderPropertyType.Vector;
            }
            return false;
        }

        private static bool ValidateDeliveredParameters(UnityRuntimeBindingDeliveryResource entry,
            Material material, out string error)
        {
            error = "";
            if (!material || !material.shader)
            {
                error = "Unity material or shader is missing.";
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in entry.parameters ?? Array.Empty<UnityRuntimeBindingDeliveryParameter>())
            {
                if (parameter == null || !Guid.TryParseExact(parameter.parameterId, "D", out var id) ||
                    id == Guid.Empty || string.IsNullOrWhiteSpace(parameter.name) ||
                    !seen.Add(parameter.name) || parameter.kind is not ("scalar" or "vector" or "texture") ||
                    parameter.binding == null || parameter.binding.format != ParameterBindingFormat)
                {
                    error = "Unity parameter delivery contains an invalid stable ID, name, kind, or binding.";
                    return false;
                }
                UnityShaderPropertyBinding locator;
                try { locator = JsonUtility.FromJson<UnityShaderPropertyBinding>(parameter.binding.value); }
                catch (ArgumentException) { locator = null; }
                if (locator == null || string.IsNullOrWhiteSpace(locator.name) ||
                    locator.association != 2 || locator.index != -1 || !material ||
                    !material.HasProperty(locator.name))
                {
                    error = "Unity material property is missing or has an unsupported locator.";
                    return false;
                }
                var propertyKind = "";
                for (var index = 0; index < material.shader.GetPropertyCount(); index++)
                    if (material.shader.GetPropertyName(index) == locator.name)
                    {
                        propertyKind = KindOf(material.shader.GetPropertyType(index));
                        break;
                    }
                if (propertyKind != parameter.kind)
                {
                    error = "Unity shader property's actual type does not match its LIBERA parameter definition.";
                    return false;
                }
            }
            return true;
        }

        private static SerializedProperty FindOrAppend(SerializedProperty array, string identityField, string identity,
            bool isMaterial = false)
        {
            for (var index = 0; index < array.arraySize; index++)
            {
                var element = array.GetArrayElementAtIndex(index);
                if (element.FindPropertyRelative(identityField).stringValue == identity) return element;
            }
            var newIndex = array.arraySize++;
            var target = array.GetArrayElementAtIndex(newIndex);
            target.FindPropertyRelative(identityField).stringValue = "";
            if (identityField == "assetIdentity")
            {
                var mesh = target.FindPropertyRelative("mesh");
                if (mesh != null) mesh.objectReferenceValue = null;
                var texture = target.FindPropertyRelative("texture");
                if (texture != null) texture.objectReferenceValue = null;
            }
            if (isMaterial)
            {
                target.FindPropertyRelative("material").objectReferenceValue = null;
                target.FindPropertyRelative("tintPropertyName").stringValue = "";
                target.FindPropertyRelative("parameterMappings").arraySize = 0;
            }
            return target;
        }

        public static string CreateAssetBinding(UnityEngine.Object asset)
        {
            if (!asset || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long localId) ||
                !Guid.TryParse(guid, out var parsedGuid) || parsedGuid == Guid.Empty || localId < 0)
                throw new InvalidOperationException("Unity object has no stable GUID/local-file-ID identity.");
            return parsedGuid.ToString("D") + ":" + localId.ToString(CultureInfo.InvariantCulture);
        }

        private static LiberaFxUnityResourceProbeResult FindAsset(string guid, long localId, out UnityEngine.Object asset)
        {
            asset = null;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return new("QueryFailure", message: "Unity AssetDatabase is updating.");
            // The wire/runtime key uses the dashed UUID; Unity's AssetDatabase uses 32 hex digits.
            var expectedGuid = Guid.ParseExact(guid, "D");
            var path = AssetDatabase.GUIDToAssetPath(expectedGuid.ToString("N"));
            if (string.IsNullOrEmpty(path)) return new("Missing", message: "The registered asset GUID is not present.");
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets == null || assets.Length == 0)
                return new("QueryFailure", message: "Unity found the asset path but could not enumerate its objects.");
            foreach (var candidate in assets)
            {
                if (!candidate || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out var actualGuid, out long actualLocalId) ||
                    !Guid.TryParse(actualGuid, out var candidateGuid) || candidateGuid != expectedGuid ||
                    actualLocalId != localId)
                    continue;
                asset = candidate;
                return null;
            }
            return new("Missing", message: "The registered local file ID is not present in the asset.");
        }

        private static string KindOf(UnityEngine.Object asset)
        {
            if (asset is Mesh) return "model";
            if (asset is Texture2D) return "texture";
            if (asset is Material) return "material";
            return "";
        }

        private static string KindOf(ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: return "scalar";
                case ShaderPropertyType.Vector:
                case ShaderPropertyType.Color: return "vector";
                case ShaderPropertyType.Texture: return "texture";
                default: return "";
            }
        }

        private static bool IsSelectedProject(string expectedProjectId)
        {
            if (!Guid.TryParseExact(expectedProjectId, "D", out var requested) || requested == Guid.Empty)
                return false;
            return GetProjectIdentity(out var actual) == LiberaFxUnityProjectIdentityState.Ready &&
                Guid.TryParseExact(actual, "D", out var current) && current == requested;
        }

        private static bool TryParseAssetBinding(string value, out string guid, out long localId)
        {
            guid = "";
            localId = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var separator = value.IndexOf(':');
            if (separator <= 0 || separator != value.LastIndexOf(':') ||
                !Guid.TryParseExact(value.Substring(0, separator), "D", out var parsedGuid) || parsedGuid == Guid.Empty ||
                !long.TryParse(value.Substring(separator + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out localId) || localId < 0)
                return false;
            guid = parsedGuid.ToString("D");
            return true;
        }

        private static void EnsureAssetFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/LiberaFx"))
                AssetDatabase.CreateFolder("Assets", "LiberaFx");
            if (!AssetDatabase.IsValidFolder("Assets/LiberaFx/Editor"))
                AssetDatabase.CreateFolder("Assets/LiberaFx", "Editor");
        }

        private static string MarkerPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null) throw new InvalidOperationException("Cannot determine Unity project root.");
            return Path.Combine(projectRoot.FullName, MarkerRelativePath);
        }

        private static void WriteLocalMarker(string id)
        {
            var marker = MarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            File.WriteAllText(marker, id);
        }

        private static void ForkProjectIdentity(LiberaFxUnityResourceProjectIdentity asset)
        {
            if (!EditorUtility.DisplayDialog("Fork Unity Project Identity",
                    "This changes the project GUID and breaks existing engine bindings. Continue?",
                    "Fork", "Cancel")) return;
            asset.SetProjectId(Guid.NewGuid().ToString("D"));
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            WriteLocalMarker(asset.ProjectId);
        }

        private static string ApplyProjectOperation(string operation, out string status)
        {
            status = null;
            if (operation == "status") return null;
            if (operation == "initialize")
            {
                if (AssetDatabase.LoadAssetAtPath<LiberaFxUnityResourceProjectIdentity>(IdentityAssetPath))
                {
                    status = "QueryFailure";
                    return "Project identity already exists.";
                }
                EnsureAssetFolders();
                var created = ScriptableObject.CreateInstance<LiberaFxUnityResourceProjectIdentity>();
                created.SetProjectId(Guid.NewGuid().ToString("D"));
                AssetDatabase.CreateAsset(created, IdentityAssetPath);
                AssetDatabase.SaveAssets();
                WriteLocalMarker(created.ProjectId);
                return null;
            }
            if (operation == "keep")
            {
                var state = GetProjectIdentity(out var projectId);
                if (state != LiberaFxUnityProjectIdentityState.CloneDecisionRequired)
                {
                    status = "QueryFailure";
                    return "Project identity is not awaiting a clone-policy decision.";
                }
                WriteLocalMarker(projectId);
                return null;
            }
            if (operation == "fork")
            {
                var asset = AssetDatabase.LoadAssetAtPath<LiberaFxUnityResourceProjectIdentity>(IdentityAssetPath);
                if (!asset)
                {
                    status = "QueryFailure";
                    return "Project identity asset is missing.";
                }
                asset.SetProjectId(Guid.NewGuid().ToString("D"));
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                WriteLocalMarker(asset.ProjectId);
                return null;
            }
            status = "QueryFailure";
            return "Unsupported project identity operation.";
        }
    }
}
#endif
