#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using LiberaFx.Unity;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    // Synchronous real-Editor adapter/runtime bridge check. Run only against the
    // exact dedicated project named by LIBERAFX_VERIFICATION_PROJECT.
    public static class LiberaFxResourceIdentityVerification
    {
        private const string BindingAssetPath = "Assets/LiberaFx/LiberaFxBindings.asset";
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly List<string> proof = new List<string>();
        private static string projectRoot;
        private static string temporaryAssetFolder;
        private static bool bindingAssetCreated;

        [Serializable]
        public sealed class Binding
        {
            public string format;
            public string value;
        }

        [Serializable]
        public sealed class DeliveryParameter
        {
            public string parameterId;
            public string name;
            public string kind;
            public Binding binding;
        }

        [Serializable]
        public sealed class DeliveryResource
        {
            public string resourceId;
            public string kind;
            public Binding binding;
            public DeliveryParameter[] parameters;
        }

        [Serializable]
        public sealed class Request
        {
            public string type;
            public string requestId;
            public string operation;
            public string projectId;
            public Binding binding;
            public Binding resourceBinding;
            public Binding parameterBinding;
            public DeliveryResource[] resources;
        }

        [Serializable]
        public sealed class Project
        {
            public string projectId;
        }

        [Serializable]
        public sealed class SchemaParameter
        {
            public string name;
            public string kind;
            public int association;
            public int index;
            public string defaultValueJson;
            public string minimumJson;
            public string maximumJson;
        }

        [Serializable]
        public sealed class Reply
        {
            public string requestId;
            public string operation;
            public string status;
            public string projectState;
            public Project project;
            public string actualKind;
            public string detail;
            public string assetPath;
            public SchemaParameter[] parameters;
        }

        [Serializable]
        private sealed class ShaderBinding
        {
            public string name;
            public int association;
            public int index;
        }

        [Serializable]
        private sealed class RenderConfiguration
        {
            public string type = "render_config";
            public long programSequence = 1;
            public RenderNode[] nodes;
        }

        [Serializable]
        private sealed class RenderNode
        {
            public string nodeId;
            public string kind = "sprite";
            public string assetPath;
            public RenderMaterial[] materials;
            public RenderInput[] inputs;
            public RenderTexture[] textures = Array.Empty<RenderTexture>();
            public bool billboard;
            public string rotationConvention = "";
        }

        [Serializable]
        private sealed class RenderMaterial
        {
            public int slot;
            public string path;
        }

        [Serializable]
        private sealed class RenderTexture
        {
            public string name;
            public int slot;
            public string path;
            public int association;
            public int index;
        }

        [Serializable]
        private sealed class RenderInput
        {
            public string id;
            public int width;
            public string sourceNodeId = "";
            public int sourceComponent;
            public double[] defaults;
            public RenderMaterialParameter materialParameter;
        }

        [Serializable]
        private sealed class RenderMaterialParameter
        {
            public string name;
            public string kind;
            public int slot;
            public int association;
            public int index;
        }

        public static void RunBatch()
        {
            proof.Clear();
            projectRoot = null;
            temporaryAssetFolder = null;
            bindingAssetCreated = false;
            var resultPath = "";
            var mayWriteResult = false;
            try
            {
                RequireExactVerificationProject();
                resultPath = Path.Combine(projectRoot, "resource-identity-verification.txt");
                Require(!File.Exists(resultPath),
                    "Verification result already exists; refusing to overwrite it: " + resultPath);
                mayWriteResult = true;
                Require(!AssetDatabase.LoadMainAssetAtPath(BindingAssetPath),
                    "Existing LiberaFxBindings.asset found; refusing to overwrite or mutate it.");
                File.WriteAllText(resultPath, "STARTED\n");

                proof.Add("unity_version=" + Application.unityVersion);
                proof.Add("project=" + projectRoot);
                proof.Add("mode=real Unity Editor AssetDatabase and runtime binding lookup");

                var projectStatus = Send(new Request
                {
                    type = "resource_identity_project",
                    operation = "status"
                });
                if (projectStatus.projectState == "Unregistered")
                {
                    var initialized = Send(new Request
                    {
                        type = "resource_identity_project",
                        operation = "initialize"
                    });
                    Require(initialized.projectState == "Ready" && initialized.project != null,
                        "Could not initialize the explicitly selected verification project's identity: " +
                        initialized.detail);
                    projectStatus = initialized;
                }
                Require(projectStatus.projectState == "Ready" && projectStatus.project != null &&
                    Guid.TryParseExact(projectStatus.project.projectId, "D", out var projectGuid) &&
                    projectGuid != Guid.Empty,
                    "Selected Unity project identity is not Ready; clone-policy decisions are never automated.");
                var projectId = projectStatus.project.projectId;
                proof.Add("project_identity_status_initialize=" + projectStatus.projectState);

                CreateIsolatedAssets(out var texture, out var material, out var texturePath, out var materialPath);
                var textureBinding = LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(texture);
                var materialBinding = LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(material);
                var textureId = Guid.NewGuid().ToString("D");
                var materialId = Guid.NewGuid().ToString("D");
                var parameterId = Guid.NewGuid().ToString("D");

                var textureProbe = Probe(projectId, new Binding
                {
                    format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                    value = textureBinding
                });
                var materialProbe = Probe(projectId, new Binding
                {
                    format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                    value = materialBinding
                });
                Require(textureProbe.status == "Resolved" && textureProbe.actualKind == "texture" &&
                    materialProbe.status == "Resolved" && materialProbe.actualKind == "material",
                    "Real Texture2D/Material GUID and local-file-ID probes did not resolve: texture=" +
                    textureProbe.status + " (" + textureProbe.detail + "), material=" +
                    materialProbe.status + " (" + materialProbe.detail + ").");
                proof.Add("stable_asset_probes=PASS");

                var wrongProject = Probe(Guid.NewGuid().ToString("D"), new Binding
                {
                    format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                    value = textureBinding
                });
                Require(wrongProject.status == "Offline", "A different project GUID was not rejected as Offline.");
                var wrongFormat = Probe(projectId, new Binding
                {
                    format = "unity.path.v0",
                    value = textureBinding
                });
                Require(wrongFormat.status == "QueryFailure", "An unsupported Unity binding format was not rejected.");
                proof.Add("wrong_project_and_format_failures=PASS");

                var materialOpaqueBinding = new Binding
                {
                    format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                    value = materialBinding
                };
                var schema = Send(new Request
                {
                    type = "resource_identity_schema",
                    projectId = projectId,
                    resourceBinding = materialOpaqueBinding
                });
                var colorParameter = (schema.parameters ?? Array.Empty<SchemaParameter>())
                    .SingleOrDefault(item => item.name == "_Color");
                Require(schema.status == "Resolved" && colorParameter != null &&
                    colorParameter.kind == "vector" && colorParameter.association == 2 &&
                    colorParameter.index == -1,
                    "Material schema omitted the supported _Color property or Unity's global locator.");
                var parameterBindingValue = JsonUtility.ToJson(new ShaderBinding
                {
                    name = "_Color",
                    association = colorParameter.association,
                    index = colorParameter.index
                });
                var parameterProbe = Send(new Request
                {
                    type = "resource_identity_probe",
                    projectId = projectId,
                    resourceBinding = materialOpaqueBinding,
                    parameterBinding = new Binding
                    {
                        format = LiberaFxUnityResourceIdentityAdapter.ParameterBindingFormat,
                        value = parameterBindingValue
                    }
                });
                Require(parameterProbe.status == "Resolved" && parameterProbe.actualKind == "vector",
                    "Real Unity shader-property parameter probe did not resolve.");
                proof.Add("material_schema_and_parameter_probe=PASS");

                var wrongResourceType = Send(new Request
                {
                    type = "resource_identity_deliver",
                    projectId = projectId,
                    resources = new[]
                    {
                        new DeliveryResource
                        {
                            resourceId = Guid.NewGuid().ToString("D"),
                            kind = "model",
                            binding = new Binding
                            {
                                format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                                value = textureBinding
                            }
                        }
                    }
                });
                Require(wrongResourceType.status == "TypeMismatch",
                    "Delivering a Texture2D as a model was not rejected as TypeMismatch.");
                var wrongParameterType = Send(new Request
                {
                    type = "resource_identity_deliver",
                    projectId = projectId,
                    resources = new[]
                    {
                        new DeliveryResource
                        {
                            resourceId = materialId,
                            kind = "material",
                            binding = materialOpaqueBinding,
                            parameters = new[]
                            {
                                new DeliveryParameter
                                {
                                    parameterId = parameterId,
                                    name = "Authored Tint",
                                    kind = "scalar",
                                    binding = new Binding
                                    {
                                        format = LiberaFxUnityResourceIdentityAdapter.ParameterBindingFormat,
                                        value = parameterBindingValue
                                    }
                                }
                            }
                        }
                    }
                });
                Require(wrongParameterType.status == "TypeMismatch",
                    "Delivering a vector shader property as a scalar was not rejected as TypeMismatch.");
                Require(!AssetDatabase.LoadAssetAtPath<LiberaFxUnityBindingAsset>(BindingAssetPath),
                    "Rejected delivery unexpectedly created the binding asset.");
                proof.Add("wrong_resource_and_parameter_types_failures=PASS");

                var movedTexturePath = temporaryAssetFolder + "/TextureMoved.asset";
                var movedMaterialPath = temporaryAssetFolder + "/MaterialMoved.mat";
                Require(string.IsNullOrEmpty(AssetDatabase.MoveAsset(texturePath, movedTexturePath)),
                    "Unity could not move the verification Texture2D asset.");
                Require(string.IsNullOrEmpty(AssetDatabase.MoveAsset(materialPath, movedMaterialPath)),
                    "Unity could not move the verification Material asset.");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(movedTexturePath);
                material = AssetDatabase.LoadAssetAtPath<Material>(movedMaterialPath);
                Require(texture && material &&
                    LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(texture) == textureBinding &&
                    LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(material) == materialBinding &&
                    Probe(projectId, materialOpaqueBinding).status == "Resolved" &&
                    Probe(projectId, new Binding
                    {
                        format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                        value = textureBinding
                    }).status == "Resolved",
                    "Asset rename changed a GUID/local-file-ID identity or made it unresolvable.");
                proof.Add("rename_preserves_asset_identity=PASS");

                // The path was verified absent before any fixture writes. Mark ownership
                // before dispatch so failure cleanup can remove a partially created asset.
                bindingAssetCreated = true;
                var delivered = Send(new Request
                {
                    type = "resource_identity_deliver",
                    projectId = projectId,
                    resources = new[]
                    {
                        new DeliveryResource
                        {
                            resourceId = textureId,
                            kind = "texture",
                            binding = new Binding
                            {
                                format = LiberaFxUnityResourceIdentityAdapter.AssetBindingFormat,
                                value = textureBinding
                            }
                        },
                        new DeliveryResource
                        {
                            resourceId = materialId,
                            kind = "material",
                            binding = materialOpaqueBinding,
                            parameters = new[]
                            {
                                new DeliveryParameter
                                {
                                    parameterId = parameterId,
                                    name = "Authored Tint",
                                    kind = "vector",
                                    binding = new Binding
                                    {
                                        format = LiberaFxUnityResourceIdentityAdapter.ParameterBindingFormat,
                                        value = parameterBindingValue
                                    }
                                }
                            }
                        }
                    }
                });
                var createdBindingAsset =
                    AssetDatabase.LoadAssetAtPath<LiberaFxUnityBindingAsset>(BindingAssetPath);
                Require(delivered.status == "Resolved" && delivered.assetPath == BindingAssetPath &&
                    createdBindingAsset,
                    "Explicit binding delivery did not create its project-local Unity binding asset: " + delivered.detail);
                bindingAssetCreated = true;

                var textureIdentity = CanonicalBinding(textureBinding);
                var materialIdentity = CanonicalBinding(materialBinding);
                Require(createdBindingAsset.RenderAssets.Any(item => item.assetIdentity == textureIdentity &&
                        item.texture == texture) &&
                    createdBindingAsset.Materials.Any(item => item.assetIdentity == materialIdentity &&
                        item.material == material && item.tintPropertyName == "_Color" &&
                        item.parameterMappings.Any(mapping =>
                            mapping.authoredName == "Authored Tint" && mapping.unityPropertyName == "_Color")),
                    "Delivered binding asset did not preserve the canonical opaque keys and explicit parameter map.");
                VerifyRuntimeLookup(createdBindingAsset, texture, material, textureIdentity, materialIdentity);
                proof.Add("delivery_matches_runtime_asset_lookup=PASS");

                AssetDatabase.DeleteAsset(BindingAssetPath);
                bindingAssetCreated = false;
                if (!string.IsNullOrEmpty(temporaryAssetFolder))
                    AssetDatabase.DeleteAsset(temporaryAssetFolder);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Require(!AssetDatabase.LoadMainAssetAtPath(BindingAssetPath),
                    "Verification failed to remove only its newly created binding asset.");
                proof.Add("cleanup=PASS");
                proof.Add("result=PASS");
                File.WriteAllLines(resultPath, proof);
                Debug.Log("LiberaFx Unity resource identity verification PASS. Result: " + resultPath);
            }
            catch (Exception error)
            {
                proof.Add("result=FAIL");
                proof.Add(error.ToString());
                CleanupOwnedAssets();
                if (mayWriteResult && !string.IsNullOrEmpty(resultPath))
                    File.WriteAllLines(resultPath, proof);
                Debug.LogException(error);
                EditorApplication.Exit(2);
            }
        }

        private static void RequireExactVerificationProject()
        {
            var configured = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            Require(!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) &&
                Directory.Exists(configured),
                "Set LIBERAFX_VERIFICATION_PROJECT to the exact dedicated Unity project path.");
            var actual = Directory.GetParent(Application.dataPath);
            Require(actual != null, "Unity project root could not be determined.");
            projectRoot = NormalizePath(actual.FullName);
            Require(string.Equals(projectRoot, NormalizePath(configured), StringComparison.OrdinalIgnoreCase),
                "Current Unity project does not exactly match LIBERAFX_VERIFICATION_PROJECT.");
        }

        public static async void RunMappedPackageWssBatch()
        {
            var result = 2;
            var ownsBindings = false;
            LiberaFxUnityResourceIdentityConnection connection = null;
            string directory = null;
            try
            {
                RequireExactVerificationProject();
                var scratch = Environment.GetEnvironmentVariable("LIBERAFX_RESOURCE_WSS_FIXTURE");
                var uri = Environment.GetEnvironmentVariable("LIBERAFX_RESOURCE_WSS_ENDPOINT");
                var token = Environment.GetEnvironmentVariable("LIBERAFX_RESOURCE_WSS_TOKEN");
                var fingerprint = Environment.GetEnvironmentVariable("LIBERAFX_RESOURCE_WSS_FINGERPRINT");
                Environment.SetEnvironmentVariable("LIBERAFX_RESOURCE_WSS_TOKEN", null);
                Require(Path.IsPathFullyQualified(scratch ?? "") && Directory.Exists(scratch) &&
                    !File.Exists(Path.Combine(scratch, "unity-bindings.json")) &&
                    Uri.TryCreate(uri, UriKind.Absolute, out var address) && address.IsLoopback,
                    "Supply a fresh local fixture directory and isolated loopback preview endpoint.");
                Require(!AssetDatabase.LoadMainAssetAtPath(BindingAssetPath),
                    "Refusing to overwrite an existing project binding asset.");
                ownsBindings = true;
                var folderName = "LiberaFxResourceWss-" + Guid.NewGuid().ToString("N");
                Require(AssetDatabase.CreateFolder("Assets", folderName).Length != 0,
                    "Failed to create the isolated resource fixture folder.");
                directory = "Assets/" + folderName;
                var mesh = UnityEngine.Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx"));
                var replacement = UnityEngine.Object.Instantiate(mesh);
                var vertices = replacement.vertices;
                for (var index = 0; index < vertices.Length; ++index)
                {
                    vertices[index].x *= 2;
                }
                replacement.vertices = vertices;
                replacement.RecalculateBounds();
                var shader = Shader.Find("Unlit/Color");
                Require(shader && shader.isSupported, "The Unity graphics device must support Unlit/Color.");
                var material = new Material(shader);
                AssetDatabase.CreateAsset(mesh, directory + "/Marker.asset");
                AssetDatabase.CreateAsset(replacement, directory + "/Replacement.asset");
                AssetDatabase.CreateAsset(material, directory + "/Marker.mat");
                AssetDatabase.SaveAssets();
                var meshIdentity = LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(mesh);
                var materialIdentity = LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(material);

                connection = ScriptableObject.CreateInstance<LiberaFxUnityResourceIdentityConnection>();
                var connect = connection.ConnectAsync(uri, token, fingerprint, enableLivePreview: true);
                Require(await Task.WhenAny(connect, Task.Delay(15000)) == connect,
                    "Timed out waiting for the real Unity preview connection.");
                await connect;
                Require(connection.IsConnected, "Unity resource WSS connection failed.");
                var identityTemp = Path.Combine(scratch, "unity-bindings.tmp");
                File.WriteAllText(identityTemp, JsonUtility.ToJson(new MappedAssets
                {
                    mesh = meshIdentity,
                    replacement = LiberaFxUnityResourceIdentityAdapter.CreateAssetBinding(replacement),
                    material = materialIdentity
                }));
                File.Move(identityTemp, Path.Combine(scratch, "unity-bindings.json"));
                await WaitForPreview(connection, () => HasMesh(connection.PreviewObject, mesh), "initial mapped render");
                var retired = connection.PreviewObject;
                VerifyFramebuffer(retired, mesh, material, Path.Combine(scratch, "unity-live-first.png"));
                File.WriteAllText(Path.Combine(scratch, "unity-live-first.ok"), "");
                await WaitForPreview(connection, () => !connection.PreviewObject, "wrong-project map to clear the preview");
                Require(!retired && connection.Status.Contains("Unregistered"),
                    "Wrong-project mapping did not retire the previous effect with an explicit diagnostic.");
                File.WriteAllText(Path.Combine(scratch, "unity-live-rejected.ok"), "");
                await WaitForPreview(connection, () => HasMesh(connection.PreviewObject, replacement), "replacement mapping");
                Require(!AssetDatabase.LoadMainAssetAtPath(BindingAssetPath),
                    "Unity live preview wrote to the project binding asset.");
                VerifyFramebuffer(connection.PreviewObject, replacement, material, Path.Combine(scratch, "unity-live-second.png"));
                File.WriteAllText(Path.Combine(scratch, "unity-live-second.ok"), "");
                await WaitForPreview(connection, () => PreviewTime(connection, .25), "forward playback seek");
                File.WriteAllText(Path.Combine(scratch, "unity-live-seek.ok"), "");
                var beforeRewind = connection.PreviewObject;
                await WaitForPreview(connection, () => PreviewTime(connection, 0), "backward playback seek");
                Require(!beforeRewind && HasMesh(connection.PreviewObject, replacement),
                    "Rewinding did not rebuild the mapped Unity effect at time zero.");
                File.WriteAllText(Path.Combine(scratch, "unity-live-rewind.ok"), "");
                var packagePath = Path.Combine(scratch, "unity-mapped.lfxp");
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (!File.Exists(packagePath) && DateTime.UtcNow < deadline && connection.IsConnected)
                {
                    await Task.Delay(50);
                }
                Require(File.Exists(packagePath), "The desktop did not publish the WSS-resolved runtime package.");
                var bindings = AssetDatabase.LoadAssetAtPath<LiberaFxUnityBindingAsset>(BindingAssetPath);
                Require(bindings, "The actual WSS delivery did not create the runtime binding asset.");
                VerifyMappedPackage(packagePath, bindings, replacement, material);
                var latest = connection.PreviewObject;
                UnityEngine.Object.DestroyImmediate(connection);
                connection = null;
                Require(!latest, "Disconnect retained the loaded Unity preview effect.");
                File.WriteAllText(Path.Combine(scratch, "unity-result.txt"),
                    "PASS: Unity WSS live mapped render, wrong-project retirement and recovery, binding delivery and mapped package framebuffer\n");
                result = 0;
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
            finally
            {
                if (connection)
                {
                    UnityEngine.Object.DestroyImmediate(connection);
                }
                if (ownsBindings)
                {
                    AssetDatabase.DeleteAsset(BindingAssetPath);
                }
                if (directory != null)
                {
                    AssetDatabase.DeleteAsset(directory);
                }
                EditorApplication.Exit(result);
            }
        }

        [Serializable]
        private sealed class MappedAssets
        {
            public string mesh;
            public string replacement;
            public string material;
        }

        private static bool HasMesh(GameObject owner, Mesh mesh) =>
            owner && owner.GetComponentsInChildren<MeshFilter>().Any(filter => filter.sharedMesh == mesh);

        private static bool PreviewTime(LiberaFxUnityResourceIdentityConnection connection, double time)
        {
            var owner = connection.PreviewObject;
            return owner && Math.Abs(owner.GetComponent<LiberaFxSpawnableComponent>().ElapsedTime - time) < 1e-8;
        }

        private static async Task WaitForPreview(LiberaFxUnityResourceIdentityConnection connection,
            Func<bool> predicate, string operation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (!predicate() && DateTime.UtcNow < deadline && connection.IsConnected)
            {
                await Task.Delay(50);
            }
            Require(predicate(), "Timed out waiting for " + operation + ": " + connection.Status);
        }

        private static void VerifyMappedPackage(string path, LiberaFxUnityBindingAsset bindings, Mesh mesh, Material material)
        {
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var owner = new GameObject("LIBERA FX WSS mapped package");
            owner.SetActive(false);
            try
            {
                package.SetPackageBytes(File.ReadAllBytes(path));
                var effect = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(effect.ConfigureForSession(package, bindings) == NativeResult.Ok,
                    "Could not configure the actual mapped Unity package.");
                owner.SetActive(true);
                Require(effect.Play() == NativeResult.Ok, "Mapped package failed to play: " + effect.LastError);
                VerifyFramebuffer(owner, mesh, material, Path.Combine(Path.GetDirectoryName(path), "unity-mapped.png"));
                Require(effect.Stop() == NativeResult.Ok, "Could not stop the mapped Unity package.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(package);
            }
        }

        private static void VerifyFramebuffer(GameObject owner, Mesh mesh, Material material, string imagePath)
        {
            var cameraObject = new GameObject("LIBERA FX WSS framebuffer");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, owner.scene);
            var target = new UnityEngine.RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var previous = UnityEngine.RenderTexture.active;
            try
            {
                var filter = owner.GetComponentsInChildren<MeshFilter>().Single();
                var renderer = filter.GetComponent<MeshRenderer>();
                Require(filter.sharedMesh == mesh && renderer && renderer.sharedMaterial == material,
                    "Mapped package did not instantiate the exact WSS-delivered model and material.");
                var previousLayer = filter.gameObject.layer;
                filter.gameObject.layer = 31;
                var camera = cameraObject.AddComponent<Camera>();
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(owner.scene))
                {
                    camera.scene = owner.scene;
                    camera.overrideSceneCullingMask =
                        UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(owner.scene);
                }
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 2;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31;
                camera.transform.position = Vector3.back * 8;
                Require(target.Create(), "Could not create the Unity framebuffer.");
                camera.targetTexture = target;
                camera.Render();
                UnityEngine.RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                pixels.Apply();
                filter.gameObject.layer = previousLayer;
                Require(pixels.GetPixels32().Count(pixel => pixel.r > 24 || pixel.g > 24 || pixel.b > 24) > 32,
                    "The WSS-mapped Unity model did not render visible pixels.");
                File.WriteAllBytes(imagePath, pixels.EncodeToPNG());
            }
            finally
            {
                UnityEngine.RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(pixels);
            }
        }

        private static void CreateIsolatedAssets(out Texture2D texture, out Material material,
            out string texturePath, out string materialPath)
        {
            var parent = "Assets/LiberaFx";
            if (!AssetDatabase.IsValidFolder(parent))
            {
                Require(AssetDatabase.CreateFolder("Assets", "LiberaFx").Length > 0,
                    "Could not create Assets/LiberaFx.");
            }
            temporaryAssetFolder = parent + "/ResourceIdentityVerification-" + Guid.NewGuid().ToString("N");
            Require(AssetDatabase.CreateFolder(parent, Path.GetFileName(temporaryAssetFolder)).Length > 0,
                "Could not create the isolated resource identity fixture folder.");
            var shader = Shader.Find("LiberaFx/Atlas Unlit");
            Require(shader, "The dedicated Unity project must contain the LIBERA FX Atlas Unlit shader.");

            texturePath = temporaryAssetFolder + "/Texture.asset";
            materialPath = temporaryAssetFolder + "/Material.mat";
            texture = new Texture2D(8, 8, TextureFormat.RGBA32, false)
            {
                name = "LIBERA FX Resource Identity Texture",
                filterMode = FilterMode.Point
            };
            material = new Material(shader) { name = "LIBERA FX Resource Identity Material" };
            Require(material.HasProperty("_MainTex") && material.HasProperty("_AtlasRect") &&
                material.HasProperty("_Color"),
                "The LIBERA FX Atlas Unlit shader lacks required fixture properties.");
            material.SetTexture("_MainTex", texture);
            AssetDatabase.CreateAsset(texture, texturePath);
            AssetDatabase.CreateAsset(material, materialPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Require(texture && material, "Unity did not import the isolated Texture2D and Material assets.");
        }

        private static Reply Probe(string projectId, Binding binding) =>
            Send(new Request
            {
                type = "resource_identity_probe",
                projectId = projectId,
                binding = binding
            });

        private static Reply Send(Request request)
        {
            request.requestId = Guid.NewGuid().ToString("D");
            var response = LiberaFxUnityResourceIdentityAdapter.ProcessRequest(JsonUtility.ToJson(request));
            var reply = JsonUtility.FromJson<Reply>(response);
            Require(reply != null && reply.requestId == request.requestId,
                "Unity resource identity adapter returned an invalid response: " + response);
            return reply;
        }

        private static string CanonicalBinding(string value)
        {
            var separator = value.IndexOf(':');
            if (separator <= 0 || separator != value.LastIndexOf(':') ||
                !Guid.TryParseExact(value.Substring(0, separator), "D", out var guid) || guid == Guid.Empty ||
                !long.TryParse(value.Substring(separator + 1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var localId))
                throw new InvalidOperationException("Adapter returned an invalid Unity asset binding.");
            return guid.ToString("D") + ":" + localId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void VerifyRuntimeLookup(LiberaFxUnityBindingAsset bindingsAsset,
            Texture2D texture, Material material, string textureIdentity, string materialIdentity)
        {
            var lookupType = typeof(LiberaFxUnityBindingAsset).Assembly
                .GetType("LiberaFx.Unity.LiberaFxUnityBindingLookup", throwOnError: true);
            var constructor = lookupType.GetConstructor(InstanceFields, null,
                new[] { typeof(LiberaFxUnityBindingAsset) }, null);
            Require(constructor != null, "Runtime binding lookup constructor was not found.");
            var lookup = constructor.Invoke(new object[] { bindingsAsset });

            var payload = new RenderConfiguration
            {
                nodes = new[]
                {
                    new RenderNode
                    {
                        nodeId = Guid.NewGuid().ToString("D"),
                        kind = "sprite",
                        assetPath = textureIdentity,
                        materials = new[] { new RenderMaterial { slot = 0, path = materialIdentity } },
                        inputs = new[]
                        {
                            Input("position", 3, 0, 0, 0),
                            Input("rotation", 3, 0, 0, 0),
                            Input("scale", 3, 1, 1, 1),
                            Input("tint", 4, 1, 1, 1, 1),
                            Input("frame", 1, 0),
                            Input("columns", 1, 1),
                            Input("rows", 1, 1),
                            Input("fps", 1, 8),
                            Input("playing", 1, 1),
                            new RenderInput
                            {
                                id = "authoredTint",
                                width = 4,
                                defaults = new[] { 1.0, 1.0, 1.0, 1.0 },
                                materialParameter = new RenderMaterialParameter
                                {
                                    name = "Authored Tint",
                                    kind = "vector",
                                    slot = 0,
                                    association = 2,
                                    index = -1
                                }
                            }
                        }
                    }
                }
            };
            var renderBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            var prepare = lookupType.GetMethod("Prepare", InstanceFields);
            Require(prepare != null, "Runtime binding lookup Prepare method was not found.");
            var prepared = prepare.Invoke(lookup,
                new object[] { "root", renderBytes, Array.Empty<byte>(), Array.Empty<byte>() });
            var preparedNodes = (Array)lookupTypeField(prepared, "renderNodes").GetValue(prepared);
            Require(preparedNodes.Length == 1, "Runtime did not resolve exactly one render node.");
            var preparedNode = preparedNodes.GetValue(0);
            Require(ReferenceEquals(lookupTypeField(preparedNode, "texture").GetValue(preparedNode), texture),
                "Runtime lookup did not resolve the delivered texture by the render_config opaque identity.");
            var materials = (Material[])lookupTypeField(preparedNode, "materials").GetValue(preparedNode);
            Require(materials.Length == 1 && ReferenceEquals(materials[0], material),
                "Runtime lookup did not resolve the delivered material by its opaque identity.");
            var mappedProperties = (string[])lookupTypeField(preparedNode, "materialParameterPropertyNames")
                .GetValue(preparedNode);
            Require(mappedProperties.Length == 10 && mappedProperties[9] == "_Color",
                "Runtime lookup did not resolve the authored parameter name to Unity's shader property.");
        }

        private static RenderInput Input(string id, int width, params double[] defaults) =>
            new RenderInput
            {
                id = id,
                width = width,
                defaults = defaults,
                materialParameter = new RenderMaterialParameter
                {
                    name = "__LIBERAFX_NULL_MATERIAL_PARAMETER__",
                    kind = "__LIBERAFX_NULL_MATERIAL_PARAMETER__"
                }
            };

        private static FieldInfo lookupTypeField(object instance, string name)
        {
            var field = instance.GetType().GetField(name, InstanceFields);
            Require(field != null, "Runtime prepared payload field was not found: " + name);
            return field;
        }

        private static void CleanupOwnedAssets()
        {
            try
            {
                if (bindingAssetCreated && AssetDatabase.LoadAssetAtPath<LiberaFxUnityBindingAsset>(BindingAssetPath))
                    AssetDatabase.DeleteAsset(BindingAssetPath);
                bindingAssetCreated = false;
                if (!string.IsNullOrEmpty(temporaryAssetFolder) &&
                    AssetDatabase.IsValidFolder(temporaryAssetFolder))
                    AssetDatabase.DeleteAsset(temporaryAssetFolder);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (Exception cleanupError)
            {
                proof.Add("cleanup_error=" + cleanupError);
            }
        }

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
