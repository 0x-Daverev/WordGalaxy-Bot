using FunnyProject.Commands.Write;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.Interfaces
{
    public class MoveInterface
    {
        protected Api Api { get; set; }

        public MoveInterface(Api api)
        {
            Api = api;
        }
        private void FlyWithAnimation(int x, int y)
        {
            // Barriers: go around them, or stop where the game stops the ship.
            var (planX, planY) = Api.User.Zones.PlanMove(Api.User.Position.X, Api.User.Position.Y, x, y);
            bool rerouted = planX != x || planY != y;
            if (rerouted && Api.User.Position.Moving &&
                Api.User.Position.TargetX == planX && Api.User.Position.TargetY == planY)
                return; // already flying to this detour corner
            (x, y) = (planX, planY);
            Api.User.Position.TargetX = x;
            Api.User.Position.TargetY = y;

            Api.User.PacketManager?.Send(new Move(Api.User.Position.X, Api.User.Position.Y, Api.User.Position.TargetX, Api.User.Position.TargetY));
            Api.User.Position.Moving = true;


            double distance = Math.Sqrt(Math.Pow((Api.User.Position.TargetX - Api.User.Position.X), 2) + Math.Pow((Api.User.Position.TargetY - Api.User.Position.Y), 2));
            double duration = (distance / Api.User.Position.Speed);
            float durationMs = (float)duration * 1000;

            Api.Tweener.TargetCancel(Api.User.Position);
            Api.Tweener.Tween(Api.User.Position, new { X = Api.User.Position.TargetX, Y = Api.User.Position.TargetY }, durationMs)
                .OnComplete(new System.Action(() => Api.User.Position.Moving = false));

        }
        public void FlyToCorndinates(int x, int y)
        {
            FlyWithAnimation(x, y);
        }

    }
}
