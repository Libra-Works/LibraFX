using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    /// <summary>Graphics-enabled isolated test. GPU readback is confined to this verification code.</summary>
    public static class LibraFluid2DVorticityVerification
    {
        public static void Run() => EditorApplication.Exit(Verify());
        internal static int Verify()
        {
            ComputeShader compute = null;
            ComputeBuffer input = null, dye = null, output = null;
            var exitCode = 0;
            try
            {
                var json = File.ReadAllText(Environment.GetEnvironmentVariable("LIBERAFX_FLUID_SETTINGS"));
                var settings = JsonUtility.FromJson<LibraFluid2DSettings>(json);
                settings.Validate();
                Require(settings.vorticity > 0, "The compiler fixture must enable vorticity.");
                var id = Guid.NewGuid().ToString("D");
                var manifest = "{\"version\":2,\"programs\":[],\"fluidPrograms\":[{\"nodeId\":\"" + id +
                    "\",\"settings\":" + json + ",\"renderer\":{\"material\":\"builtin:fluid-alpha\",\"sizeScale\":1}}]}";
                LibraFluid2DManifestValidation.Validate(manifest);
                var oldSettings = JsonUtility.FromJson<LibraFluid2DSettings>(json.Replace("\"vorticity\": 12.5,", ""));
                Require(oldSettings.vorticity == 0, "Legacy settings must default to disabled confinement.");

                var width = settings.width; var height = settings.height;
                var count = width * height;
                var cell = new Vector2(settings.domainWidth / width, settings.domainHeight / height);
                var source = new Vector4[count];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var p = new Vector2((x + .5f) / width - .5f, (y + .5f) / height - .5f);
                    var envelope = Mathf.Exp(-12 * p.sqrMagnitude);
                    source[y * width + x] = new Vector4(-p.y * envelope, p.x * envelope, 0, 0);
                }
                input = new ComputeBuffer(count, 16); dye = new ComputeBuffer(count, 16); output = new ComputeBuffer(count, 16);
                input.SetData(source); dye.SetData(new Vector4[count]);
                compute = UnityEngine.Object.Instantiate(LibraFluid2DInstance.ValidateAssets());
                var kernel = compute.FindKernel("LibraFluidApplyForces");
                compute.SetBuffer(kernel, "LibraFluidRead0", input);
                compute.SetBuffer(kernel, "LibraFluidRead1", dye);
                compute.SetBuffer(kernel, "LibraFluidWrite0", output);
                compute.SetVector("LibraFluidDomain", new Vector4(settings.domainWidth, settings.domainHeight, 0, 0));
                compute.SetVector("LibraFluidSource", new Vector4(.5f, .5f, .1f, 0));
                compute.SetVector("LibraFluidForce", Vector4.zero);
                var measured = new Vector4[count];
                var maximumError = 0f; var maximumEffect = 0f;
                foreach (var strength in new[] { 0f, settings.vorticity })
                {
                    compute.SetVector("LibraFluidGrid", new Vector4(width, height, settings.maximumStep, strength));
                    compute.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                    output.GetData(measured);
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var original = Read(x, y);
                        var gradient = new Vector2((Math.Abs(Curl(x + 1, y)) - Math.Abs(Curl(x - 1, y))) / (2 * cell.x),
                            (Math.Abs(Curl(x, y + 1)) - Math.Abs(Curl(x, y - 1))) / (2 * cell.y));
                        var direction = gradient / Math.Max(gradient.magnitude, .000001f);
                        var force = strength * Math.Min(cell.x, cell.y) * Curl(x, y) * new Vector2(direction.y, -direction.x);
                        var expected = original + settings.maximumStep * force;
                        if (x == 0 || x == width - 1) expected.x = 0;
                        if (y == 0 || y == height - 1) expected.y = 0;
                        var value = measured[y * width + x];
                        Require(!float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && value.z == 0 && value.w == 0,
                            "Confinement produced a nonfinite or invalid field.");
                        var error = Vector2.Distance(expected, new Vector2(value.x, value.y));
                        maximumError = Math.Max(maximumError, error);
                        Require(error < .00005f, "GPU confinement differs from the independent finite-difference reference: " + error);
                        if (strength > 0 && x > 0 && y > 0 && x < width - 1 && y < height - 1)
                            maximumEffect = Math.Max(maximumEffect, Vector2.Distance(original, new Vector2(value.x, value.y)));
                    }
                }
                Require(maximumEffect > .0001f, "Enabled confinement must change the seeded vortex relative to the disabled control.");
                input.SetData(new Vector4[count]);
                compute.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                output.GetData(measured);
                foreach (var value in measured) Require(value == Vector4.zero, "An empty field must not acquire noise or nonfinite forces.");
                Debug.Log("PASS Fluid2D vorticity: " + width + "x" + height + ", max error=" + maximumError +
                    ", max effect=" + maximumEffect + ", zero-strength and zero-field controls; device=" +
                    SystemInfo.graphicsDeviceName + ", API=" + SystemInfo.graphicsDeviceType);

                Vector2 Read(int x, int y)
                {
                    var value = source[Mathf.Clamp(y, 0, height - 1) * width + Mathf.Clamp(x, 0, width - 1)];
                    return new Vector2(x < 0 || x >= width ? -value.x : value.x, y < 0 || y >= height ? -value.y : value.y);
                }
                float Curl(int x, int y) => (Read(x + 1, y).y - Read(x - 1, y).y) / (2 * cell.x) -
                    (Read(x, y + 1).x - Read(x, y - 1).x) / (2 * cell.y);
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally
            {
                input?.Release(); dye?.Release(); output?.Release();
                if (compute) UnityEngine.Object.DestroyImmediate(compute);
            }
            return exitCode;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
