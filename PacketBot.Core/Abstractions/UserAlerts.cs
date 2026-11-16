using System;

namespace FunnyProject.Abstractions
{
    // Blocking, user-facing alerts raised by the engine (e.g. license denied).
    // Core never shows UI itself: each host installs its own handler at start-up.
    // Windows shows a MessageBox; headless hosts keep the console default.
    public static class UserAlerts
    {
        private static Action<string> handler = Default;

        public static Action<string> Handler
        {
            get => handler;
            set => handler = value ?? Default;
        }

        public static void Show(string message) => handler(message);

        private static void Default(string message) =>
            Console.WriteLine("[Alerta] " + message);
    }
}
