#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LibraFluid2DAllVerification
    {
        public static async void Run()
        {
            var exit = 0;
            var shaders = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_SHADER_FIXTURES");
            try
            {
                RequireDirectory(shaders);
                Environment.SetEnvironmentVariable("LIBERAFX_FLUID_SETTINGS", Path.Combine(shaders, "fluid-settings.json"));
                exit |= LibraFluid2DVerification.Verify();
                await DrainFrames();
                Environment.SetEnvironmentVariable("LIBERAFX_FLUID_SETTINGS", Path.Combine(shaders, "fluid-vorticity-settings.json"));
                exit |= LibraFluid2DVorticityVerification.Verify();
                await DrainFrames();
                exit |= LibraFluid2DModelsVerification.Verify();
                await DrainFrames();
                exit |= await LibraFluid2DGeneratedVerification.Verify();
                await DrainFrames();
                exit |= await LibraFluid2DDrawConsumersVerification.Verify();
                await DrainFrames();
                exit |= await LibraFluid2DFieldConsumersVerification.Verify();
                await DrainFrames();
                exit |= LibraFluid2DVerification.VerifyCompiledScopes();
                await DrainFrames();
                Debug.Log("LIBERAFX FLUID COMBINED VERIFICATION EXIT=" + exit);
            }
            catch (Exception error) { Debug.LogException(error); exit = 1; }
            EditorApplication.Exit(exit);
        }
        private static void RequireDirectory(string directory)
        {
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory))
                throw new InvalidOperationException("Absolute shader fixture directory required.");
        }
        private static async Task DrainFrames()
        {
            for (var frame = 0; frame < 4; frame++)
            {
                var completion = new TaskCompletionSource<bool>();
                EditorApplication.delayCall += () => completion.SetResult(true);
                EditorApplication.QueuePlayerLoopUpdate();
                await completion.Task;
            }
        }
    }
}
#endif
