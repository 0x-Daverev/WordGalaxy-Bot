using System;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Runtime
{
    // A bot activity (Collect, NPC Killer, Galaxy Gates) that BotRunner can
    // start and cancel. Configuration is passed in the constructor as plain
    // values, never read from UI controls.
    public interface IBotModule
    {
        string Name { get; }

        // Runs until cancelled. Returning (or throwing) without cancellation
        // means the module has finished, and the runner stops the bot.
        Task RunAsync(BotModuleContext context, CancellationToken cancellationToken);
    }

    public sealed class BotModuleContext
    {
        private readonly Action<string> report;

        internal BotModuleContext(Api api, Action<string> report)
        {
            Api = api;
            this.report = report;
        }

        public Api Api { get; }

        // Human-readable progress (Windows shows it in the status label,
        // the server logs it and Telegram reports it on /status).
        public void Report(string status) => report(status);
    }

    // A module made from a delegate (small or ad-hoc tasks). The lifecycle is
    // still owned by BotRunner.
    public sealed class DelegateBotModule : IBotModule
    {
        private readonly Func<BotModuleContext, CancellationToken, Task> run;

        public DelegateBotModule(string name, Func<BotModuleContext, CancellationToken, Task> run)
        {
            Name = name;
            this.run = run;
        }

        public string Name { get; }

        public Task RunAsync(BotModuleContext context, CancellationToken cancellationToken) =>
            run(context, cancellationToken);
    }
}
