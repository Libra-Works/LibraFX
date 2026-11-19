using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LiberaFx.Unity
{
    public sealed partial class LiberaFxSpawnableComponent
    {
        private const byte OutputSignalKind = 3; // NamedIoKind.OutputSignal
        private static readonly UTF8Encoding ScriptPortEncoding = new UTF8Encoding(false, true);
        private readonly Dictionary<string, (Func<GameIoEvent, double[]> Handler, string ResultProperty)> scriptActions =
            new Dictionary<string, (Func<GameIoEvent, double[]>, string)>(StringComparer.Ordinal);
        private int scriptActionDepth;

        /// <summary>Binds a root action signal to a main-thread handler on this component.</summary>
        public bool BindScriptAction(string signalName, Func<GameIoEvent, double[]> handler, string resultProperty = null)
        {
            UnityInstanceSession.RequireMainThread();
            if (!IsScriptAction(signalName) || !IsScriptPortName(signalName) || handler == null ||
                (!string.IsNullOrEmpty(resultProperty) && !IsScriptPortName(resultProperty)) ||
                scriptActions.ContainsKey(signalName))
            {
                return false;
            }
            scriptActions.Add(signalName, (handler, resultProperty));
            return true;
        }

        public bool UnbindScriptAction(string signalName)
        {
            UnityInstanceSession.RequireMainThread();
            return signalName != null && scriptActions.Remove(signalName);
        }

        private static bool IsScriptAction(string name) =>
            name != null && name.Length > 7 && name.StartsWith("action.", StringComparison.Ordinal);

        private static bool IsScriptPortName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name[0] == ' ' || name[name.Length - 1] == ' ')
            {
                return false;
            }
            foreach (var character in name)
            {
                if (char.IsControl(character))
                {
                    return false;
                }
            }
            try
            {
                return ScriptPortEncoding.GetByteCount(name) <= 128;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        private void InvokeScriptAction(GameIoEvent item, NativeSpawnableHost current)
        {
            if (item.Kind != OutputSignalKind || !IsWholeEffect(item.ContainerId, item.InstanceId) ||
                !IsScriptAction(item.PortName))
            {
                return;
            }
            if (!scriptActions.TryGetValue(item.PortName, out var action))
            {
                Debug.LogWarning("No LIBERA FX script action is bound to " + item.PortName + ".", this);
                return;
            }
            ++scriptActionDepth;
            try
            {
                var request = new GameIoEvent((byte[])item.ContainerId.Clone(), item.InstanceId, item.PortIndex,
                    item.Kind, item.Width, item.IsColor, (double[])item.Values.Clone(), item.PortName);
                var values = action.Handler(request);
                if (host != current || effectForced || string.IsNullOrEmpty(action.ResultProperty))
                {
                    return;
                }
                var result = values == null ? NativeResult.InvalidArgument : current.SetGameProperty(action.ResultProperty, values);
                if (result != NativeResult.Ok)
                {
                    Debug.LogWarning("LIBERA FX script action " + item.PortName +
                        " could not write " + action.ResultProperty + ": " + result, this);
                }
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
            }
            finally
            {
                --scriptActionDepth;
            }
        }
    }
}
