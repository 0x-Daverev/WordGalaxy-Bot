using System.Diagnostics;
using Unglide;

namespace FunnyProject
{
    // Per-instance context shared by packet handlers, modules and services.
    // WordGalaxy sessions are created by Sessions.WordGalaxySession, which sets
    // User.UserData and User.PacketManager.
    public class Api : IDisposable
    {
        public User User { get; set; }
        public Tweener Tweener;
        // Start/stop state of this instance's bot, shared by every frontend.
        public FunnyProject.Runtime.BotRunner Runner { get; }
        // NPC names and box types seen per map (target and type pickers).
        public FunnyProject.UserCollections.MapObservations Observations { get; } = new();
        private readonly CancellationTokenSource lifetime = new();

        public Api(User user)
        {
            User = user;
            Runner = new FunnyProject.Runtime.BotRunner(this);
            Task.Run(async () => await UpdateTweener(lifetime.Token));
            Task.Run(async () => await SampleObservations(lifetime.Token));
            Tweener = new();
        }

        private async Task UpdateTweener(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Stopwatch s = new Stopwatch();
                    s.Start();
                    await Task.Delay(15, token);
                    Tweener.Update(s.ElapsedMilliseconds);
                    s.Stop();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {

                }
            }
        }

        private async Task SampleObservations(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token);
                    Observations.Sample(User);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine("[Observaciones] " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public bool IsDisposed => lifetime.IsCancellationRequested;

        // Stops the bot and the instance's background loop. Closing TCP is the session's job.
        public void Dispose()
        {
            Runner.Dispose();
            if (!lifetime.IsCancellationRequested)
                lifetime.Cancel();
        }
    }
}
