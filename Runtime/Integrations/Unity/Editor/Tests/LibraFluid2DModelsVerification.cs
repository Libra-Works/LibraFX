using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    public static class LibraFluid2DModelsVerification
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static Vector4[] Read(LibraFluid2DInstance fluid, int field, int count)
        {
            var values = new Vector4[count];
            fluid.FieldForDiagnostics(field).GetData(values);
            foreach (var value in values)
                for (var lane = 0; lane < 4; lane++)
                    Require(!float.IsNaN(value[lane]) && !float.IsInfinity(value[lane]), "Nonfinite GPU field.");
            return values;
        }
        private static double Sum(Vector4[] values, int lane)
        {
            double sum = 0;
            foreach (var value in values) sum += value[lane];
            return sum;
        }
        public static void Run() => EditorApplication.Exit(Verify());
        internal static int Verify()
        {
            var fixtures = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_FIXTURES");
            var output = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            GameObject owner = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D capture = null;
            var exit = 0;
            try
            {
                Require(Path.IsPathRooted(fixtures) && Path.IsPathRooted(output), "Absolute verification paths required.");
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "Real D3D11 device required.");
                Directory.CreateDirectory(output);
                owner = new GameObject("Fluid model verification") { layer = 8 };
                cameraObject = new GameObject("Fluid model camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.orthographic = true;
                camera.cullingMask = 1 << 8;
                target = new RenderTexture(320, 240, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                capture = new Texture2D(320, 240, TextureFormat.RGBA32, false);
                var report = new StringBuilder();
                foreach (var mode in new[] { "free-surface", "free-surface-source", "shallow-water", "shallow-water-source",
                    "combustion", "cold-fuel", "force-acceleration", "force-noise", "force-curl", "force-masked", "force-masked-empty", "combustion-oracle" })
                {
                    var json = File.ReadAllText(Path.Combine(fixtures, mode + ".json"));
                    var manifest = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(json);
                    var id = manifest.fluidPrograms[0].nodeId;
                    var prepared = new LiberaFxUnityBindingLookup(null).Prepare("root", Array.Empty<byte>(),
                        Encoding.UTF8.GetBytes("[{\"id\":\"" + id + "\"}]"), Encoding.UTF8.GetBytes(json), true).fluidPrograms[0];
                    prepared.sizeScale = 1;
                    var settings = prepared.settings;
                    var count = settings.width * settings.height;
                    using (var fluid = new LibraFluid2DInstance(owner.transform, prepared, null))
                    {
                        var initial = Read(fluid, 2, count);
                        var lane = settings.ModelId == 2 ? 0 : 3;
                        var initialMass = Sum(initial, lane);
                        for (var frame = 0; frame < 120; frame++) fluid.Step(1d / 60);
                        for (var field = 0; field < 7; field++) Read(fluid, field, count);
                        var dye = Read(fluid, 2, count);
                        var velocity = Read(fluid, 0, count);
                        var mass = Sum(dye, lane);
                        if (settings.ModelId == 1 || settings.ModelId == 2)
                        {
                            if (settings.sourceEnabled) Require(mass > initialMass + .01, mode + " must add water.");
                            else Require(Math.Abs(mass - initialMass) <= Math.Max(.0001, initialMass * .0001),
                                mode + " must conserve water: " + initialMass + " -> " + mass);
                            if (settings.ModelId == 2)
                            {
                                Require(Sum(dye, 3) == 0, "Numerical depth clamp activated.");
                                if (!settings.sourceEnabled)
                                    foreach (var value in velocity) Require(value.sqrMagnitude < 1e-12f, "Lake at rest moved.");
                            }
                            else if (!settings.sourceEnabled)
                                foreach (var value in velocity) Require(Math.Abs(value.x) < .15 && Math.Abs(value.y) < .15, "Pool did not remain at hydrostatic rest.");
                        }
                        if (mode == "force-masked-empty")
                            foreach (var value in velocity) Require(value.sqrMagnitude == 0, "Density mask failed in an empty field.");
                        if (mode == "force-masked")
                            Require(Array.Exists(velocity, value => value.sqrMagnitude > 1e-12f), "Masked cell-force graph did not execute.");
                        if (mode == "cold-fuel") Require(Sum(dye, 0) > 0 && Sum(dye, 2) == 0 && Sum(dye, 3) == 0, "Cold fuel ignited.");
                        if (mode == "combustion") Require(Sum(dye, 1) > 0 && Sum(dye, 2) > 0 && Sum(dye, 3) > 0, "Hot fuel did not react.");
                        if (mode == "combustion-oracle")
                        {
                            settings.sourceEnabled = false;
                            fluid.FieldForDiagnostics(0).SetData(new Vector4[count]);
                            var before = Read(fluid, 2, count);
                            fluid.Step(1d / 60);
                            var after = Read(fluid, 2, count);
                            var reacted = 0;
                            for (var cell = 0; cell < count; cell++)
                            {
                                var burnt = before[cell].x * (1 - Math.Exp(-4 * Math.Clamp((before[cell].y - .5) / .5, 0, 1) / 60));
                                Require(Math.Abs(after[cell].x - before[cell].x + burnt) < .00002 &&
                                    Math.Abs(after[cell].y - before[cell].y - burnt * 2) < .00002 &&
                                    Math.Abs(after[cell].z - before[cell].z - burnt) < .00002, "Fuel/heat/smoke disagree with reaction oracle.");
                                if (burnt > .000001) reacted++;
                            }
                            Require(reacted > 0, "Reaction oracle must exercise burning cells.");
                        }
                        if (settings.ModelId == 2)
                        {
                            camera.transform.position = new Vector3(0, 10, 0);
                            camera.transform.rotation = Quaternion.Euler(90, 0, 0);
                            camera.orthographicSize = 6;
                        }
                        else
                        {
                            camera.transform.position = new Vector3(0, 0, -2);
                            camera.transform.rotation = Quaternion.identity;
                            camera.orthographicSize = .6f;
                        }
                        fluid.UpdateTransform(Matrix4x4.identity);
                        camera.Render();
                        RenderTexture.active = target;
                        capture.ReadPixels(new Rect(0, 0, 320, 240), 0, 0); capture.Apply();
                        RenderTexture.active = null;
                        var colored = 0;
                        foreach (var color in capture.GetPixels()) if (Math.Max(color.r, Math.Max(color.g, color.b)) > .005) colored++;
                        if (mode != "cold-fuel" && mode != "force-masked-empty") Require(colored > 10, mode + " dedicated renderer produced no visible pixels.");
                        File.WriteAllBytes(Path.Combine(output, "fluid-model-" + mode + ".png"), capture.EncodeToPNG());
                        report.AppendLine(mode + ": initialMass=" + initialMass + ", mass=" + mass + ", colored=" + colored);
                        fluid.Reset();
                        Require(Math.Abs(Sum(Read(fluid, 2, count), lane) - initialMass) < .0001, "Model reset did not restore initial state.");
                    }
                }
                File.WriteAllText(Path.Combine(output, "models-proof.txt"), report.ToString());
                Debug.Log("PASS Fluid2D models, reaction oracle, finite fields, mass, hydrostatic rest and dedicated renderers\n" + report);
            }
            catch (Exception error) { Debug.LogException(error); exit = 1; }
            finally
            {
                RenderTexture.active = null;
                if (capture) UnityEngine.Object.DestroyImmediate(capture);
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            }
            return exit;
        }
    }
}
