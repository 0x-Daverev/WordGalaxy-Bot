using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Services
{
    // Drone formation per bot action: while the bot runs, send
    // RunFormation when roaming and AttackFormation when attacking, only on a
    // change and only if the formation is available. Idle never changes it.
    // During the last GG wave the final formation sent by the GG module wins.
    // A module that sends its own formation at some point (the GG final wave).
    internal interface IFinalFormationSource
    {
        bool FinalFormationActive { get; }
        string FinalFormation { get; }
    }

    internal sealed class FormationService
    {
        private readonly Api api;
        private readonly IBotSettingsAccess settings;
        private readonly object sync = new();
        private string lastApplied = "";
        private bool wasConnected;
        private bool finalWasActive;

        public FormationService(Api api, IBotSettingsAccess settings)
        {
            this.api = api;
            this.settings = settings;
        }

        // Forces the next applicable formation to be sent again (settings
        // changed, formations list refreshed, bot stopped).
        public void Reset()
        {
            lock (sync) lastApplied = "";
        }

        public void Tick()
        {
            lock (sync)
            {
                bool connected = api.User.PacketManager?.Connected == true;
                if (!connected)
                {
                    wasConnected = false;
                    lastApplied = "";
                    return;
                }
                if (!wasConnected)
                {
                    wasConnected = true;
                    lastApplied = "";
                }
                if (!api.Runner.IsRunning)
                {
                    finalWasActive = false;
                    return;
                }

                var final = api.Runner.Module as IFinalFormationSource;
                bool finalActive = final != null && final.FinalFormationActive &&
                    !string.IsNullOrWhiteSpace(final.FinalFormation);
                if (finalActive && !finalWasActive)
                    lastApplied = final!.FinalFormation; // already sent by the module
                finalWasActive = finalActive;

                var current = settings.Read();
                string desired = finalActive
                    ? final!.FinalFormation
                    : api.User.ActionState.Current switch
                    {
                        BotAction.Run => current.RunFormation,
                        BotAction.Attack => current.AttackFormation,
                        _ => ""
                    };
                if (string.IsNullOrWhiteSpace(desired) ||
                    !api.User.AvailableDroneFormations.Contains(desired, StringComparer.Ordinal) ||
                    desired == lastApplied)
                    return;
                api.User.PacketManager!.Send(new FunnyProject.Commands.Write.Action(desired));
                api.User.RecordFormationRequest(desired);
                lastApplied = desired;
            }
        }

        // Headless hosts; Windows calls Tick from its 500 ms UI timer.
        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { Console.WriteLine("[Formaciones] " + ex.GetType().Name + ": " + ex.Message); }
                try { await Task.Delay(500, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
