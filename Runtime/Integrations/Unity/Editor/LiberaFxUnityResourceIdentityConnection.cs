#if UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity
{
    /// <summary>
    /// Explicit Unity Editor client for the desktop's existing certificate-pinned PreviewHost.
    /// Credentials live only in this window's memory and are never logged or saved to EditorPrefs.
    /// </summary>
    public sealed class LiberaFxUnityResourceIdentityConnection : EditorWindow
    {
        private const int HeaderSize = 48;
        private const int MaxPayload = 1024 * 1024;
        private const int MaxPreviewFrame = 16 * 1024 * 1024;
        private string endpoint = "wss://127.0.0.1:8766/preview?protocol=3";
        private string accessToken = "";
        private string certificateFingerprint = "";
        private string status = "Disconnected";
        private ClientWebSocket socket;
        private CancellationTokenSource cancellation;
        private readonly ConcurrentQueue<byte[]> requests = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> previewFrames = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<string> diagnostics = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private bool connecting;
        private bool disposed;
        private bool showLivePreview;
        private int queuedPreviewBytes;
        private LiberaFxUnityWssPreview preview;
        internal GameObject PreviewObject => preview?.PreviewObject;
        internal string Status => status;

        [MenuItem("Tools/LiberaFx/Resources/Connect to LIBERA FX")]
        private static void Open()
        {
            var window = GetWindow<LiberaFxUnityResourceIdentityConnection>();
            window.titleContent = new GUIContent("LIBERA FX Resource Identity");
            window.minSize = new Vector2(460, 220);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Connect this Unity Editor to the LIBERA FX PreviewHost.", EditorStyles.wordWrappedLabel);
            endpoint = EditorGUILayout.TextField("WSS endpoint", endpoint);
            accessToken = EditorGUILayout.PasswordField("Preview token", accessToken);
            certificateFingerprint = EditorGUILayout.TextField("TLS SHA-256 fingerprint", certificateFingerprint);
            using (new EditorGUI.DisabledScope(socket != null || connecting))
            {
                showLivePreview = EditorGUILayout.ToggleLeft("Render desktop preview in Unity", showLivePreview);
            }
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(socket != null || connecting))
                    if (GUILayout.Button("Connect")) _ = ConnectAsync();
                using (new EditorGUI.DisabledScope(socket == null))
                    if (GUILayout.Button("Disconnect")) Disconnect("Disconnected");
            }
            EditorGUILayout.HelpBox(status, MessageType.Info);
            EditorGUILayout.HelpBox(
                "Copy the Preview WSS endpoint, preview-only token, and SHA-256 certificate fingerprint from LIBERA FX. " +
                "The certificate is accepted only when its exact fingerprint matches. The token is not persisted.",
                MessageType.None);
            if (preview != null)
            {
                preview.Draw(GUILayoutUtility.GetRect(200, 240, GUILayout.ExpandWidth(true)));
            }
        }

        private async Task ConnectAsync()
        {
            if (socket != null || connecting || disposed) return;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "wss" ||
                uri.AbsolutePath != "/preview" || uri.Query != "?protocol=3" ||
                accessToken.Length != 64 || !IsHex(accessToken) ||
                certificateFingerprint.Length != 64 || !IsHex(certificateFingerprint))
            {
                status = "Supply the protocol-3 WSS endpoint, preview token, and 64-character SHA-256 fingerprint.";
                Repaint();
                return;
            }

            var candidate = new ClientWebSocket();
            var stop = new CancellationTokenSource();
            var expectedFingerprint = NormalizeHex(certificateFingerprint);
            connecting = true;
            status = "Connecting…";
            Repaint();
            try
            {
                candidate.Options.SetRequestHeader("X-LiberaFx-Preview-Token", accessToken);
                candidate.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    certificate != null && FingerprintMatches(certificate, expectedFingerprint);
                var connectionUri = showLivePreview
                    ? new UriBuilder(uri) { Query = "protocol=3&unityPackagePreview=1" }.Uri : uri;
                await candidate.ConnectAsync(connectionUri, stop.Token);
                var acceptedCapabilities = showLivePreview
                    ? "\"resourceIdentity\",\"unityPackagePreview\"" : "\"resourceIdentity\"";
                var hello = Encoding.UTF8.GetBytes(
                    "{\"type\":\"hello\",\"engine\":\"Unity\",\"role\":\"editor\",\"version\":1," +
                    "\"capabilities\":[" + acceptedCapabilities + "],\"viewerName\":\"Unity Editor\"}");
                await candidate.SendAsync(new ArraySegment<byte>(hello), WebSocketMessageType.Text, true, stop.Token);
                if (disposed)
                {
                    throw new OperationCanceledException();
                }
                if (showLivePreview)
                {
                    preview = new LiberaFxUnityWssPreview();
                }
                socket = candidate;
                cancellation = stop;
                connecting = false;
                status = "Connected · Unity project identity and exact resource queries are available.";
                EditorApplication.update += Pump;
                _ = ReceiveLoopAsync(candidate, stop.Token);
                Repaint();
            }
            catch (Exception)
            {
                stop.Cancel();
                stop.Dispose();
                candidate.Dispose();
                connecting = false;
                status = "Connection failed. Verify the WSS endpoint, preview-only token, and certificate fingerprint.";
                Repaint();
            }
        }

        internal Task ConnectAsync(string previewEndpoint, string previewToken, string fingerprint,
            bool enableLivePreview = false)
        {
            endpoint = previewEndpoint;
            accessToken = previewToken;
            certificateFingerprint = fingerprint;
            showLivePreview = enableLivePreview;
            return ConnectAsync();
        }

        internal bool IsConnected => socket?.State == WebSocketState.Open;

        private async Task ReceiveLoopAsync(ClientWebSocket client, CancellationToken stop)
        {
            var buffer = new byte[MaxPreviewFrame + 1];
            try
            {
                while (!stop.IsCancellationRequested && client.State == WebSocketState.Open)
                {
                    var length = 0;
                    var messageType = WebSocketMessageType.Close;
                    var fragments = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await client.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), stop);
                        messageType = result.MessageType;
                        if (messageType == WebSocketMessageType.Close) return;
                        length += result.Count;
                        if (length > buffer.Length - 1 || ++fragments > 1024) return;
                    } while (!result.EndOfMessage);

                    if (messageType == WebSocketMessageType.Binary && TryGetIdentityRequest(buffer, length, out var payload))
                    {
                        if (requests.Count >= 16)
                        {
                            diagnostics.Enqueue("Resource identity request queue is full.");
                            continue;
                        }
                        requests.Enqueue(payload);
                    }
                    else if (showLivePreview && messageType == WebSocketMessageType.Binary &&
                        IsPreviewFrame(buffer.AsSpan(0, length)))
                    {
                        if (queuedPreviewBytes + length > MaxPreviewFrame * 2 || previewFrames.Count >= 128)
                        {
                            diagnostics.Enqueue("Unity preview queue is full; reconnect to resynchronize.");
                            return;
                        }
                        previewFrames.Enqueue(buffer.AsSpan(0, length).ToArray());
                        queuedPreviewBytes += length;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                diagnostics.Enqueue("Preview connection failed. Check the endpoint, certificate pin, and editor state.");
            }
            finally
            {
                if (socket == client)
                {
                    Disconnect("Preview connection closed.");
                }
            }
        }

        private static bool IsPreviewFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < HeaderSize || frame[0] != 'L' || frame[1] != 'F' ||
                frame[2] != 'X' || frame[3] != 'P' || frame[4] != 1 || frame[5] != 0 ||
                frame[7] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(44)) != frame.Length - HeaderSize)
            {
                return false;
            }
            return frame[6] switch
            {
                1 => frame.Length > 56 && frame[48] == 'L' && frame[49] == 'F' && frame[50] == 'X' && frame[51] == 'U',
                2 => frame.Length == HeaderSize,
                3 => frame.Length <= HeaderSize + 4096,
                _ => false
            };
        }

        private static bool TryGetIdentityRequest(byte[] frame, int length, out byte[] payload)
        {
            payload = null;
            if (length < HeaderSize || frame[0] != (byte)'L' || frame[1] != (byte)'F' ||
                frame[2] != (byte)'X' || frame[3] != (byte)'P' ||
                frame[4] != 1 || frame[5] != 0 || frame[6] != 5 || frame[7] != 0)
                return false;
            var payloadLength = frame[44] | frame[45] << 8 | frame[46] << 16 | frame[47] << 24;
            if (payloadLength <= 0 || payloadLength > MaxPayload || payloadLength != length - HeaderSize)
                return false;
            var json = Encoding.UTF8.GetString(frame, HeaderSize, payloadLength);
            UnityResourceIdentityRequest command;
            try { command = JsonUtility.FromJson<UnityResourceIdentityRequest>(json); }
            catch (ArgumentException) { return false; }
            if (command == null || string.IsNullOrEmpty(command.type) ||
                !command.type.StartsWith("resource_identity_", StringComparison.Ordinal))
                return false;
            payload = new byte[payloadLength];
            Buffer.BlockCopy(frame, HeaderSize, payload, 0, payloadLength);
            return true;
        }

        private void Pump()
        {
            while (diagnostics.TryDequeue(out var diagnostic))
            {
                status = diagnostic;
                Repaint();
            }
            var client = socket;
            var stop = cancellation;
            if (client == null || stop == null || client.State != WebSocketState.Open) return;
            while (requests.TryDequeue(out var payload))
            {
                string response;
                try
                {
                    response = LiberaFxUnityResourceIdentityAdapter.ProcessRequest(Encoding.UTF8.GetString(payload));
                }
                catch (Exception)
                {
                    UnityResourceIdentityRequest request = null;
                    try { request = JsonUtility.FromJson<UnityResourceIdentityRequest>(Encoding.UTF8.GetString(payload)); }
                    catch (ArgumentException) { }
                    var operation = request == null ? "invalid" : request.type switch
                    {
                        "resource_identity_project" => request.operation,
                        "resource_identity_probe" => "probe",
                        "resource_identity_schema" => "schema",
                        "resource_identity_deliver" => "deliver",
                        _ => "invalid"
                    };
                    response = JsonUtility.ToJson(new UnityResourceIdentityReply
                    {
                        requestId = request?.requestId ?? "",
                        operation = operation,
                        status = "QueryFailure",
                        detail = "Unity Editor resource identity request failed."
                    });
                }
                _ = SendResponseAsync(client, response, stop.Token);
            }
            while (previewFrames.TryDequeue(out var frame))
            {
                queuedPreviewBytes -= frame.Length;
                try
                {
                    var changed = preview.Apply(frame, out var sequence, out var error);
                    if (changed || error != null)
                    {
                        status = error ?? "Connected · Unity is rendering the desktop preview.";
                        Repaint();
                    }
                    if (sequence != 0)
                    {
                        _ = SendResponseAsync(client, "{\"type\":\"package_preview_ack\",\"programSequence\":" +
                            sequence.ToString(CultureInfo.InvariantCulture) + "}", stop.Token);
                    }
                }
                catch (Exception error)
                {
                    preview.Clear();
                    status = "Unity preview could not apply the package: " + error.Message;
                    Repaint();
                }
            }
        }

        private async Task SendResponseAsync(ClientWebSocket client, string response, CancellationToken stop)
        {
            var bytes = Encoding.UTF8.GetBytes(response);
            if (bytes.Length == 0 || bytes.Length > MaxPayload)
            {
                diagnostics.Enqueue("Resource identity response exceeded the transport limit.");
                return;
            }
            try
            {
                await sendGate.WaitAsync(stop);
                try { await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, stop); }
                finally { sendGate.Release(); }
            }
            catch (OperationCanceledException) { diagnostics.Enqueue("Resource identity response could not be sent."); }
            catch (WebSocketException) { diagnostics.Enqueue("Resource identity response could not be sent."); }
            catch (ObjectDisposedException) { diagnostics.Enqueue("Resource identity response could not be sent."); }
        }

        private void Disconnect(string message)
        {
            EditorApplication.update -= Pump;
            cancellation?.Cancel();
            if (socket != null)
            {
                try { socket.Abort(); } catch { }
                socket.Dispose();
            }
            socket = null;
            connecting = false;
            cancellation?.Dispose();
            cancellation = null;
            requests.Clear();
            previewFrames.Clear();
            queuedPreviewBytes = 0;
            preview?.Dispose();
            preview = null;
            status = message;
            Repaint();
        }

        private void OnDisable()
        {
            disposed = true;
            Disconnect("Disconnected");
        }

        private static bool IsHex(string value)
        {
            foreach (var character in value)
                if (!Uri.IsHexDigit(character)) return false;
            return true;
        }

        private static string NormalizeHex(string value) => value.Replace(":", "").Replace("-", "")
            .ToUpperInvariant();

        private static bool FingerprintMatches(System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            string expected)
        {
            using (var sha = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", "");
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
        }

    }
}
#endif
