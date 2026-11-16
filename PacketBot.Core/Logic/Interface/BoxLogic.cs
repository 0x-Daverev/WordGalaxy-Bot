using FunnyProject.Commands.Write;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FunnyProject.Logic.Interface
{
    public class BoxLogic : LogicInterface
    {
        public bool Running { get; set; }
        public int TargetMapId { get; set; }
        public int MapWidth { get; set; } = 21000;
        public int MapHeight { get; set; } = 13100;

        private string[] selectedTypes = Array.Empty<string>();
        private DateTime nextRoamUtc = DateTime.MinValue;
        private DateTime nextCollectUtc = DateTime.MinValue;
        private string lastCollectHash = "";

        public BoxLogic(Api api) : base(api) { }

        public void UpdateTypes(IEnumerable<string> types)
        {
            Volatile.Write(ref selectedTypes, types
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        public async Task StartMethod(CancellationToken token = default)
        {
            while (Running && !token.IsCancellationRequested)
            {
                try
                {
                    if (Api.User.PacketManager?.Connected != true ||
                        (TargetMapId > 0 && Api.User.UserData.MapId != TargetMapId))
                    {
                        Api.User.ActionState.Current = BotAction.Idle;
                        await Task.Delay(500, token);
                        continue;
                    }

                    string[] choices = Volatile.Read(ref selectedTypes);
                    var box = Api.User.Boxes.Snapshot()
                        .Where(b => choices.Contains(b.Type, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(b => CalculateDistance(b.X, b.Y))
                        .FirstOrDefault();

                    if (box != null)
                    {
                        Api.User.SelectedBox = box;
                        Api.User.ActionState.Current = BotAction.Run;
                        double distance = CalculateDistance(box.X, box.Y);
                        if (distance > 190)
                        {
                            if (!Api.User.Position.Moving ||
                                Math.Abs(Api.User.Position.TargetX - box.X) > 80 ||
                                Math.Abs(Api.User.Position.TargetY - box.Y) > 80)
                                FlyToCorndinates(box.X, box.Y);
                        }
                        else
                        {
                            if (lastCollectHash != box.Hash || DateTime.UtcNow >= nextCollectUtc)
                            {
                                Api.User.PacketManager?.Send(new CollectBox(box.Hash,
                                    Api.User.Position.X, Api.User.Position.Y, box.X, box.Y));
                                lastCollectHash = box.Hash;
                                nextCollectUtc = DateTime.UtcNow.AddSeconds(1);
                            }
                            // Wait for the server's box-remove packet rather than
                            // deleting a box locally before collection is confirmed.
                        }
                    }
                    else
                    {
                        Api.User.SelectedBox = null;
                        Api.User.ActionState.Current = BotAction.Run;
                        if (!Api.User.Position.Moving && DateTime.UtcNow >= nextRoamUtc)
                        {
                            int maxX = Math.Max(501, MapWidth - 500);
                            int maxY = Math.Max(501, MapHeight - 500);
                            FlyToCorndinates(Random.Shared.Next(500, maxX),
                                Random.Shared.Next(500, maxY));
                            nextRoamUtc = DateTime.UtcNow.AddSeconds(2);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine("[Collect] " + ex.Message);
                    await Task.Delay(500, token);
                }
                await Task.Delay(150, token);
            }
            Api.User.SelectedBox = null;
            Api.User.ActionState.Current = BotAction.Idle;
        }
    }
}
