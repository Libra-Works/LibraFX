using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class GodotPackagePreviewTests
{
    public static void Run()
    {
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        Require(!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root),
            "Set an absolute isolated LIBERAFX_DATA_ROOT for Godot preview checks.");
        RunWithDispatcher(VerifyWebSocketAsync());
    }

    public static void RunActualGodot(string godotExecutable, string existingExtensionDll)
    {
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        Require(!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root),
            "Set an absolute isolated LIBERAFX_DATA_ROOT for Godot WSS checks.");
        Require(Path.IsPathFullyQualified(godotExecutable) && File.Exists(godotExecutable),
            "Pass an absolute path to the Godot console executable.");
        Require(Path.IsPathFullyQualified(existingExtensionDll) && File.Exists(existingExtensionDll),
            "Pass an absolute path to the existing Godot extension DLL.");
        RunWithDispatcher(VerifyActualGodotWebSocketAsync(godotExecutable, existingExtensionDll),
            TimeSpan.FromSeconds(75));
    }

    private static async Task VerifyActualGodotWebSocketAsync(string godotExecutable, string existingExtensionDll)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        var graph = model.Graph;
        graph.DeleteNodes(graph.Nodes.ToArray());
        graph.AutoDestroyWhenIdle = false;
        var signal = graph.CreateNode(GraphNodeKind.Signal);
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.EnterGraph(group), "Cannot enter the actual Godot WSS fixture template.");
        var lifeTime = graph.CreateNode(new GraphNodeKind("LifeTime"));
        graph.SetParameter(lifeTime, "seconds", JsonSerializer.SerializeToElement(1d));
        var start = graph.Nodes.Single(node => node.Definition.Kind.Value == "OnStart");
        var childSignal = graph.CreateNode(new GraphNodeKind("SpawnOutputSignal"));
        Require(graph.TryConnectNodes(start, childSignal, out var error, sourcePortId: "then"),
            "Cannot connect actual Godot child Signal route: " + error);
        graph.NavigateToGraph(0);
        signal = graph.GetNode(signal.Id);
        group = graph.GetNode(group.Id);
        var output = graph.CreateNode(new GraphNodeKind("OutputSignal"));
        graph.SetParameter(output, "name", JsonSerializer.SerializeToElement("godot-send-routed"));
        Require(graph.TryConnectNodes(signal, group, out error, "spawn") &&
            graph.TryConnectNodes(group, output, out error, "signal",
                SpawnableGroupSchema.OutputPortId(childSignal.Id)),
            "Cannot connect actual Godot root Signal route: " + error);
        model.Playback.Pause();
        model.Playback.Frame = 0;
        var view = new GraphNodeView { DataContext = signal };
        var window = new Window { Width = 400, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var sendButton = view.FindControl<Button>("PreviewSignalSendButton")!;

        var projectRoot = Path.Combine(Path.GetTempPath(), "LiberaFx-Godot-WSS-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);
        Process? godot = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var godotSource = Path.Combine(repositoryRoot, "Runtime", "Integrations", "Godot");
            Directory.CreateDirectory(Path.Combine(projectRoot, "bin"));
            File.Copy(Path.Combine(godotSource, "liberafx.gdextension"),
                Path.Combine(projectRoot, "liberafx.gdextension"));
            File.Copy(Path.Combine(godotSource, "liberafx_live_preview.gd"),
                Path.Combine(projectRoot, "liberafx_live_preview.gd"));
            File.Copy(Path.Combine(godotSource, "liberafx_package_preview.gd"),
                Path.Combine(projectRoot, "liberafx_package_preview.gd"));
            File.Copy(Path.Combine(godotSource, "Tests", "godot_wss_editor_send_smoke.gd"),
                Path.Combine(projectRoot, "godot_wss_editor_send_smoke.gd"));
            var extensionHash = SHA256.HashData(File.ReadAllBytes(existingExtensionDll));
            File.Copy(existingExtensionDll, Path.Combine(projectRoot, "bin", "liberafx_godot.dll"));
            File.WriteAllText(Path.Combine(projectRoot, "project.godot"),
                "[application]\nconfig/name=\"LIBERA FX isolated WSS test\"\n\n" +
                "[rendering]\nrenderer/rendering_method=\"gl_compatibility\"\n");

            var certificatePath = Path.Combine(projectRoot, "preview-certificate.pem");
            var readyPath = Path.Combine(projectRoot, "package-ready.json");
            var resultPath = Path.Combine(projectRoot, "signal-result.json");
            var errorPath = resultPath + ".error";
            var invitation = JsonSerializer.Serialize(new
            {
                type = "librafx_preview_connection",
                version = 1,
                host = "localhost",
                port,
                accessToken = host.AccessToken,
                certificateFingerprint = host.Fingerprint
            });
            File.WriteAllText(certificatePath, host.PublicCertificatePem);

            await host.StartAsync(model);
            var startInfo = new ProcessStartInfo(godotExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectRoot
            };
            foreach (var argument in new[]
            {
                "--headless", "--path", projectRoot, "--script", "res://godot_wss_editor_send_smoke.gd",
                "--", certificatePath, readyPath, resultPath, signal.Id.ToString("D")
            })
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(projectRoot, "appdata")).FullName;
            startInfo.Environment["LIBERAFX_GODOT_TEST_INVITATION"] = invitation;
            godot = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the isolated Godot process.");
            stdoutTask = godot.StandardOutput.ReadToEndAsync();
            stderrTask = godot.StandardError.ReadToEndAsync();

            await WaitForMarkerAsync(readyPath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Require(sendButton.IsEffectivelyEnabled &&
                    ReferenceEquals(sendButton.Command, signal.SendPreviewSignalCommand),
                    "The editor Send button is unavailable in the actual Godot WSS fixture.");
                new ButtonAutomationPeer(sendButton).GetProvider<IInvokeProvider>()!.Invoke();
                Dispatcher.UIThread.RunJobs();
            });
            var resultText = await WaitForMarkerAsync(resultPath);
            using (var result = JsonDocument.Parse(resultText))
            {
                Require(result.RootElement.GetProperty("nodeId").GetGuid() == signal.Id &&
                    result.RootElement.GetProperty("sequence").GetString() == "1" &&
                    result.RootElement.GetProperty("gameIoRouted").GetBoolean(),
                    "Actual Godot did not dispatch the authored Signal and route its child output.");
            }
            await godot.WaitForExitAsync(cancellation.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Require(godot.ExitCode == 0,
                $"Godot WSS process exited with {godot.ExitCode}.\n{stdout}\n{stderr}");
            Require(CryptographicOperations.FixedTimeEquals(extensionHash,
                SHA256.HashData(File.ReadAllBytes(existingExtensionDll))),
                "The existing GDExtension DLL was modified during verification.");
            Console.WriteLine("Actual Godot WebSocketPeer WSS PASS: pinned certificate, package admission, Editor Send dispatch and child output-signal route.");

            async Task<string> WaitForMarkerAsync(string path)
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (File.Exists(errorPath))
                        throw new InvalidOperationException("Godot reported: " + File.ReadAllText(errorPath));
                    if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancellation.Token);
                    if (godot.HasExited)
                    {
                        var standardOutput = await stdoutTask;
                        var standardError = await stderrTask;
                        throw new InvalidOperationException(
                            $"Godot exited before writing {Path.GetFileName(path)} (exit {godot.ExitCode}).\n" +
                            standardOutput + "\n" + standardError);
                    }
                    await Task.Delay(40, cancellation.Token);
                }
                throw new TimeoutException($"Timed out waiting for Godot marker {Path.GetFileName(path)}.");
            }
        }
        catch (Exception ex)
        {
            if (godot is { HasExited: false })
            {
                try { godot.Kill(); await godot.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
            }
            if (godot is not null && stdoutTask is not null && stderrTask is not null)
            {
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                throw new InvalidOperationException($"{ex.Message}\nGodot stdout:\n{stdout}\nGodot stderr:\n{stderr}", ex);
            }
            throw;
        }
        finally
        {
            if (godot is { HasExited: false })
            {
                try { godot.Kill(); await godot.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
            }
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
            model.DisposeStorageFiles();
            try { Directory.Delete(projectRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            godot?.Dispose();
        }
    }

    private static async Task VerifyWebSocketAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        using var rejectedSocket = new ClientWebSocket();
        using var socket = new ClientWebSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Window? window = null;
        var graph = model.Graph;
        graph.DeleteNodes(graph.Nodes.ToArray());
        var signal = graph.CreateNode(GraphNodeKind.Signal);
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.EnterGraph(group), "Cannot enter the Godot preview Spawnable fixture.");
        var lifeTime = graph.CreateNode(new GraphNodeKind("LifeTime"));
        graph.SetParameter(lifeTime, "seconds", JsonSerializer.SerializeToElement(1d));
        graph.NavigateToGraph(0);
        signal = graph.GetNode(signal.Id);
        group = graph.GetNode(group.Id);
        Require(graph.TryConnectNodes(signal, group, out var error, "spawn"),
            "Cannot connect the Godot preview Send fixture: " + error);
        model.Playback.Pause();
        model.Playback.Frame = 0;
        var view = new GraphNodeView { DataContext = signal };
        window = new Window { Width = 400, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var sendButton = view.FindControl<Button>("PreviewSignalSendButton")!;

        try
        {
            await host.StartAsync(model);
            var pemBody = string.Concat(host.PublicCertificatePem
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).SkipLast(1));
            var pemFingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(pemBody)));
            Require(host.PublicCertificatePem.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) &&
                pemFingerprint == host.Fingerprint,
                "Preview host certificate PEM and published fingerprint differ.");

            var uri = new Uri($"wss://localhost:{port}/preview?protocol=3&godotPackagePreview=1");
            rejectedSocket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            var wrongFingerprint = Convert.FromHexString(host.Fingerprint);
            wrongFingerprint[0] ^= 0xFF;
            rejectedSocket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), wrongFingerprint);
            using (var rejectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    await rejectedSocket.ConnectAsync(uri, rejectTimeout.Token);
                    throw new InvalidOperationException("Godot preview accepted a certificate with the wrong pin.");
                }
                catch (Exception ex) when (ex is WebSocketException or AuthenticationException)
                {
                    // A certificate-pin mismatch must fail before the WSS request is accepted.
                }
            }

            socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            var fingerprint = Convert.FromHexString(host.Fingerprint);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await socket.ConnectAsync(uri, cancellation.Token);

            var program = await ReceiveAsync(CompiledPreviewKind.Program, cancellation.Token);
            var programSequence = BinaryPrimitives.ReadInt64LittleEndian(program.AsSpan(8));
            Require(program.AsSpan(48, 4).SequenceEqual("LFXP"u8),
                "Godot package sender did not produce an .lfxp package frame.");

            await socket.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"hello","engine":"Godot","role":"editor","version":1,"capabilities":["godotPackagePreview"]}"""),
                WebSocketMessageType.Text, true, cancellation.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { type = "package_preview_ack", programSequence })),
                WebSocketMessageType.Text, true, cancellation.Token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Require(sendButton.IsEffectivelyEnabled &&
                    ReferenceEquals(sendButton.Command, signal.SendPreviewSignalCommand),
                    "The actual editor Send button is not available in the Godot preview fixture.");
                new ButtonAutomationPeer(sendButton).GetProvider<IInvokeProvider>()!.Invoke();
                Dispatcher.UIThread.RunJobs();
            });

            var send = await ReceiveAsync(CompiledPreviewKind.Control, cancellation.Token);
            using var request = JsonDocument.Parse(send.AsMemory(48));
            var payload = request.RootElement;
            var sendSequence = long.Parse(payload.GetProperty("sequence").GetString()!,
            NumberStyles.None, CultureInfo.InvariantCulture);
            Require(send.AsSpan(8, 36).IndexOfAnyExcept((byte)0) < 0 &&
                payload.GetProperty("type").GetString() == "godot_preview_signals" &&
            sendSequence == programSequence + 1 &&
                payload.GetProperty("timeSeconds").GetDouble() == 0 &&
            payload.GetProperty("baseSequence").GetString() == "0" &&
                payload.GetProperty("requests").GetArrayLength() == 1 &&
            payload.GetProperty("requests")[0].GetProperty("sequence").GetString() == "1" &&
                payload.GetProperty("requests")[0].GetProperty("nodeId").GetGuid() == signal.Id,
                "Godot WSS did not route the editor Send node ID and monotonic request sequence.");

            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.StepForwardCommand.Execute(null));
            var playback = await ReceiveAsync(CompiledPreviewKind.Playback, cancellation.Token);
            Require(BinaryPrimitives.ReadInt64LittleEndian(playback.AsSpan(16)) ==
                sendSequence,
                "Godot playback did not use the Send publication as its next baseline.");
            Console.WriteLine("Godot package preview WSS PASS: pinned TLS, .lfxp snapshot, editor Send routing, and playback baseline.");

            async Task<byte[]> ReceiveAsync(CompiledPreviewKind expected, CancellationToken token)
            {
                using var stream = new MemoryStream();
                var buffer = new byte[8192];
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), token);
                    Require(result.MessageType == WebSocketMessageType.Binary &&
                        stream.Length + result.Count <= 16 * 1024 * 1024,
                        "Invalid or oversized Godot preview WSS frame.");
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                var bytes = stream.ToArray();
                Require(bytes.Length >= 48 &&
                    (CompiledPreviewKind)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) == expected,
                    $"Godot preview expected {expected} WSS frame.");
                return bytes;
            }
        }
        finally
        {
            rejectedSocket.Abort();
            socket.Abort();
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close());
            model.DisposeStorageFiles();
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Runtime", "Integrations", "Godot",
                        "liberafx.gdextension")))
                    return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the LIBERA FX repository for the isolated Godot project.");
    }

    private static void RunWithDispatcher(Task task, TimeSpan? timeout = null)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(35));
            using var registration = timeoutSource.Token.Register(() =>
                Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false),
                TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        Require(task.IsCompleted, "The isolated Godot preview WSS check timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
