namespace FunnyProject
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            string? phpSessionId = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--phpsessid", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        MessageBox.Show("Falta el valor del argumento --phpsessid.");
                        return;
                    }
                    phpSessionId = args[++i];
                }
            }

            ApplicationConfiguration.Initialize();
            // PacketBot.Core raises alerts without UI; Windows keeps showing them as dialogs.
            FunnyProject.Abstractions.UserAlerts.Handler = message => MessageBox.Show(message);
            Application.Run(new Form1(phpSessionId));
        }
    }
}