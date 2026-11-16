using System;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Runtime
{
    public enum BotRunnerState
    {
        Stopped,
        // Restoring TCP before the module starts.
        Starting,
        Running,
        // Stopped because the module ended with an error. Can be started again.
        Faulted
    }

    // Why the bot last stopped. Telegram only alerts on stops nobody asked for.
    public enum BotStopReason
    {
        None,
        // Stop/StopAsync/Dispose: the user, Telegram or a session close.
        Requested,
        // The module returned by itself (e.g. a GG run that finished).
        Completed,
        // The module threw.
        Faulted,
        // TCP could not be restored before starting.
        StartFailed
    }

    // Single source of truth for "is the bot running" of one game instance.
    // Owns the TCP reconnect policy:
    //  - Start with TCP down: reconnect first, then run the module.
    //  - Automatic reconnect only while Running.
    //  - Stop: cancel the module and disable automatic reconnects at once.
    // The module runs on the caller's synchronization context (the UI thread in
    // Windows, the thread pool in the server).
    public sealed class BotRunner : IDisposable
    {
        private readonly Api api;
        private readonly object sync = new();
        private BotRunnerState state = BotRunnerState.Stopped;
        private CancellationTokenSource? current;
        private Task completion = Task.CompletedTask;
        private IBotModule? module;
        private string status = "";
        private Exception? lastError;
        private BotStopReason lastStopReason;
        private int stopCount;
        private bool disposed;

        public BotRunner(Api api) => this.api = api;

        // Raised on the thread that caused the change (UI thread for Windows actions).
        public event EventHandler<BotRunnerState>? StateChanged;
        public event EventHandler<string>? StatusChanged;

        public BotRunnerState State { get { lock (sync) return state; } }
        public bool IsRunning => State == BotRunnerState.Running;
        public bool IsStarting => State == BotRunnerState.Starting;
        public bool IsActive => State is BotRunnerState.Starting or BotRunnerState.Running;
        public string? ModuleName { get { lock (sync) return module?.Name; } }
        // The module of the current or last run.
        public IBotModule? Module { get { lock (sync) return module; } }
        public string Status { get { lock (sync) return status; } }
        public Exception? LastError { get { lock (sync) return lastError; } }
        public BotStopReason LastStopReason { get { lock (sync) return lastStopReason; } }
        // Incremented on every stop, so a poller cannot miss a short run.
        public int StopCount { get { lock (sync) return stopCount; } }
        // Completes when the last started module has really finished.
        public Task Completion { get { lock (sync) return completion; } }

        // Returns false if the bot was already active or was stopped while TCP
        // was being restored. Throws if TCP cannot be restored.
        public async Task<bool> StartAsync(IBotModule module)
        {
            ArgumentNullException.ThrowIfNull(module);
            var run = new CancellationTokenSource();
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(BotRunner));
                if (state is BotRunnerState.Starting or BotRunnerState.Running) return false;
                current = run;
                this.module = module;
                lastError = null;
                lastStopReason = BotStopReason.None;
                status = "";
                state = BotRunnerState.Starting;
            }
            RaiseState(BotRunnerState.Starting);

            var packets = api.User.PacketManager;
            try
            {
                if (packets == null) throw new InvalidOperationException("Gestor TCP no disponible.");
                // A stopped bot does not reconnect on its own: restore TCP explicitly.
                if (!packets.Connected)
                {
                    await packets.RestartTcpAsync();
                    if (!packets.Connected)
                        throw new InvalidOperationException("La conexion TCP no se ha restablecido.");
                }
            }
            catch (Exception ex)
            {
                if (Finish(run, BotRunnerState.Stopped, ex, BotStopReason.StartFailed)) RaiseState(BotRunnerState.Stopped);
                throw;
            }

            lock (sync)
            {
                // Stop() during the reconnect wins.
                if (!ReferenceEquals(current, run) || run.IsCancellationRequested) return false;
                packets.AutoReconnectEnabled = true;
                api.User.ActionState.Current = BotAction.Idle;
                state = BotRunnerState.Running;
            }
            RaiseState(BotRunnerState.Running);
            var task = RunModuleAsync(module, run);
            lock (sync)
            {
                if (ReferenceEquals(current, run)) completion = task;
            }
            return true;
        }

        private async Task RunModuleAsync(IBotModule module, CancellationTokenSource run)
        {
            var token = run.Token;
            Exception? error = null;
            try
            {
                await module.RunAsync(new BotModuleContext(api, text => SetStatus(run, text)), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                error = ex;
                Console.WriteLine("[Bot] " + module.Name + " termino con error: " + ex.Message);
            }
            finally
            {
                // The module ended by itself (not via Stop): stop the bot.
                if (!token.IsCancellationRequested &&
                    Finish(run, error == null ? BotRunnerState.Stopped : BotRunnerState.Faulted, error,
                        error == null ? BotStopReason.Completed : BotStopReason.Faulted))
                    RaiseState(error == null ? BotRunnerState.Stopped : BotRunnerState.Faulted);
                run.Dispose();
            }
        }

        // Immediate stop: the bot is Stopped when this returns; the module
        // observes cancellation and finishes in the background (see Completion).
        public void Stop()
        {
            CancellationTokenSource? run;
            lock (sync) run = current;
            if (run == null) return;
            if (Finish(run, BotRunnerState.Stopped, null, BotStopReason.Requested)) RaiseState(BotRunnerState.Stopped);
        }

        // Stop and wait until the module has finished (headless shutdown).
        public async Task StopAsync(TimeSpan timeout)
        {
            Stop();
            var task = Completion;
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
                Console.WriteLine("[Bot] El modulo no termino en " + timeout.TotalSeconds + " s tras parar.");
        }

        private bool Finish(CancellationTokenSource run, BotRunnerState final, Exception? error,
            BotStopReason reason)
        {
            lock (sync)
            {
                if (!ReferenceEquals(current, run)) return false;
                current = null;
                state = final;
                lastStopReason = reason;
                stopCount++;
                if (error != null) lastError = error;
                if (api.User.PacketManager is { } packets) packets.AutoReconnectEnabled = false;
                api.User.ActionState.Current = BotAction.Idle;
            }
            try { run.Cancel(); }
            catch (ObjectDisposedException) { }
            return true;
        }

        private void SetStatus(CancellationTokenSource run, string text)
        {
            lock (sync)
            {
                // A cancelled module must not overwrite the status of a newer run.
                if (!ReferenceEquals(current, run)) return;
                status = text;
            }
            StatusChanged?.Invoke(this, text);
        }

        private void RaiseState(BotRunnerState value) => StateChanged?.Invoke(this, value);

        public void Dispose()
        {
            Stop();
            lock (sync) disposed = true;
        }
    }
}
