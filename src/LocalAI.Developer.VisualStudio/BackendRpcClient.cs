using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAI.Developer.VisualStudio
{
    internal sealed class BackendRpcClient : IDisposable
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> pending =
            new ConcurrentDictionary<long, TaskCompletionSource<JToken>>();
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private Process process;
        private long nextId;
        private bool disposed;

        public event Action<string, JObject> Notification;
        public event Action<string> Log;

        public async Task StartAsync(JObject settings)
        {
            if (process != null && !process.HasExited) return;

            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string dll = Path.Combine(assemblyDirectory, "Backend", "LocalAI.Developer.Backend.dll");
            if (!File.Exists(dll))
            {
                string development = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..",
                    "LocalAI.Developer.Backend", "bin", "Debug", "net8.0", "LocalAI.Developer.Backend.dll"));
                if (File.Exists(development)) dll = development;
            }
            if (!File.Exists(dll))
                throw new FileNotFoundException("The AI Code Generator backend was not included in the VSIX.", dll);

            string dotnet = Environment.GetEnvironmentVariable("LOCALAI_DOTNET_PATH");
            if (string.IsNullOrWhiteSpace(dotnet)) dotnet = "dotnet";
            var startInfo = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = "\"" + dll + "\"",
                WorkingDirectory = assemblyDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnExited;
            if (!process.Start()) throw new InvalidOperationException("The AI Code Generator backend could not be started.");
            _ = ReadOutputAsync(process.StandardOutput);
            _ = ReadErrorsAsync(process.StandardError);
            await RequestAsync("initialize", settings).ConfigureAwait(false);
        }

        public async Task<JToken> RequestAsync(string method, JObject parameters)
        {
            if (process == null || process.HasExited)
                throw new InvalidOperationException("The AI Code Generator backend is not running.");

            long id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate JSON-RPC request id.");
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(request.ToString(Formatting.None)).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
            return await completion.Task.ConfigureAwait(false);
        }

        private async Task ReadOutputAsync(StreamReader reader)
        {
            string line;
            while (!disposed && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                try
                {
                    JObject message = JObject.Parse(line);
                    JToken method = message["method"];
                    if (method != null && message["id"] == null)
                    {
                        var handler = Notification;
                        if (handler != null) handler((string)method, message["params"] as JObject ?? new JObject());
                        continue;
                    }

                    long? id = (long?)message["id"];
                    TaskCompletionSource<JToken> completion;
                    if (!id.HasValue || !pending.TryRemove(id.Value, out completion)) continue;
                    JObject error = message["error"] as JObject;
                    if (error != null)
                        completion.TrySetException(new InvalidOperationException((string)error["message"] ?? "Backend request failed."));
                    else
                        completion.TrySetResult(message["result"] ?? JValue.CreateNull());
                }
                catch (Exception error)
                {
                    RaiseLog("Invalid backend message: " + error.Message + Environment.NewLine + line);
                }
            }
        }

        private async Task ReadErrorsAsync(StreamReader reader)
        {
            string line;
            while (!disposed && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                RaiseLog(line);
        }

        private void OnExited(object sender, EventArgs e)
        {
            FailAll(new InvalidOperationException("AI Code Generator backend exited with code " + process.ExitCode + "."));
        }

        private void RaiseLog(string message)
        {
            var handler = Log;
            if (handler != null) handler(message);
        }

        private void FailAll(Exception error)
        {
            foreach (var item in pending)
            {
                TaskCompletionSource<JToken> completion;
                if (pending.TryRemove(item.Key, out completion)) completion.TrySetException(error);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            FailAll(new ObjectDisposedException(nameof(BackendRpcClient)));
            try
            {
                if (process != null && !process.HasExited) process.Kill();
            }
            catch
            {
                // Best-effort shutdown during Visual Studio exit.
            }
            if (process != null) process.Dispose();
            writeLock.Dispose();
        }
    }
}
