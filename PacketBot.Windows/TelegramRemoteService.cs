using System;
using System.Threading.Tasks;
using FunnyProject.Telegram;

namespace FunnyProject
{
    // Windows host for the shared Telegram service: loads the DPAPI-protected
    // settings and runs every command on the UI thread of Form1.
    internal sealed class TelegramRemoteService : IDisposable
    {
        private readonly Form1 form;
        private readonly TelegramBotService service;

        private TelegramRemoteService(Form1 form, string token, long allowedChat)
        {
            this.form = form;
            service = new TelegramBotService(token, allowedChat, DispatchAsync);
        }

        public static TelegramRemoteService? Start(Form1 form)
        {
            (string Token, long ChatId)? saved;
            try { saved = TelegramSettingsStore.Load(); }
            catch (Exception) { Console.WriteLine("[Telegram] No se pudo leer la configuracion local."); return null; }
            if (saved == null) return null;
            var remote = new TelegramRemoteService(form, saved.Value.Token, saved.Value.ChatId);
            remote.service.Start();
            return remote;
        }

        private Task<string> DispatchAsync(string message)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (form.IsDisposed || !form.IsHandleCreated)
            {
                completion.SetResult("La interfaz no esta disponible.");
                return completion.Task;
            }
            try
            {
                form.BeginInvoke((Action)(async () =>
                {
                    try { completion.TrySetResult(await form.HandleTelegramCommandAsync(message)); }
                    catch (Exception ex) { completion.TrySetResult("Error: " + ex.Message); }
                }));
            }
            catch (InvalidOperationException) { completion.TrySetResult("La interfaz se esta cerrando."); }
            return completion.Task;
        }

        public Task NotifyAsync(string message) => service.NotifyAsync(message);

        public void Dispose() => service.Dispose();
    }
}
