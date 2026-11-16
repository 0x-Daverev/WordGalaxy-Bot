using FunnyProject.Forms;
using FunnyProject.GUIManagers;
using FunnyProject.Interfaces;
using FunnyProject.Logic;
using FunnyProject.Logic.Interface;
using FunnyProject.PacketHandlers;

namespace FunnyProject
{
    public partial class Form1 : Form, FunnyProject.Telegram.IBotInstanceHost
    {
        private ComboBox? authMode;
        private TelegramRemoteService? telegramRemote;
        private readonly System.Windows.Forms.Timer telegramAlertTimer =
            new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly FunnyProject.Telegram.TelegramAlertMonitor telegramAlerts = new();
        // Same session registry as the server: one session per account.
        private readonly FunnyProject.Sessions.InstanceManager instances = new();

        private Button? telegramSettingsButton;
        private ComboBox? savedAccounts;
        private Button? saveAccountButton;
        private Button? deleteAccountButton;
        public Form1(string? startupPhpSessionId = null)
        {
            InitializeComponent();
            Text = "WordGalaxy PacketBot " + FunnyProject.BotVersion.Current;
            this.DoubleBuffered = true;
            MainTabPage.DrawMode = TabDrawMode.OwnerDrawFixed;
            MainTabPage.SizeMode = TabSizeMode.Fixed;
            MainTabPage.ItemSize = new Size(165, 28);
            MainTabPage.DrawItem += DrawAccountTab;
            MainTabPage.MouseDown += AccountTabMouseDown;
            authMode = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            authMode.Items.AddRange(new object[] { "PHPSESSID", "Usuario y contraseña" });
            authMode.SelectedIndex = 0;
            savedAccounts = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            saveAccountButton = new Button { Width = 102, Height = 25, Text = "Guardar cuenta" };
            deleteAccountButton = new Button { Width = 102, Height = 25, Text = "Eliminar cuenta" };
            LoginPage.Controls.Add(savedAccounts);
            LoginPage.Controls.Add(saveAccountButton);
            LoginPage.Controls.Add(deleteAccountButton);
            savedAccounts.SelectedIndexChanged += (s, e) =>
            {
                if (savedAccounts.SelectedItem is not string username) return;
                try
                {
                    UsernameBox.Text = username;
                    PasswordBox.Text = SavedAccounts.Load(username);
                }
                catch (Exception ex) { MessageBox.Show("No se pudo cargar la cuenta: " + ex.Message); }
            };
            saveAccountButton.Click += (s, e) =>
            {
                try
                {
                    SavedAccounts.Save(UsernameBox.Text, PasswordBox.Text);
                    RefreshSavedAccounts(UsernameBox.Text.Trim());
                }
                catch (Exception ex) { MessageBox.Show("No se pudo guardar la cuenta: " + ex.Message); }
            };
            deleteAccountButton.Click += (s, e) =>
            {
                if (savedAccounts.SelectedItem is not string username) return;
                try
                {
                    SavedAccounts.Delete(username);
                    RefreshSavedAccounts(null);
                    UsernameBox.Clear();
                    PasswordBox.Clear();
                }
                catch (Exception ex) { MessageBox.Show("No se pudo eliminar la cuenta: " + ex.Message); }
            };
            RefreshSavedAccounts(null);
            telegramSettingsButton = new Button { Text = "Configurar Telegram", Width = 210, Height = 28 };
            telegramSettingsButton.Click += (s, e) => ConfigureTelegram();
            LoginPage.Controls.Add(telegramSettingsButton);
            LoginPage.Controls.Add(authMode);
            authMode.SelectedIndexChanged += (s, e) => UpdateAuthMode();
            UpdateAuthMode();
            telegramAlertTimer.Tick += (s, e) => CheckTelegramAlerts();
            Shown += (s, e) =>
            {
                telegramRemote = TelegramRemoteService.Start(this);
                telegramAlertTimer.Start();
                if (!string.IsNullOrWhiteSpace(startupPhpSessionId))
                {
                    // Reuse the existing web login and TCP initialization path.
                    authMode!.SelectedIndex = 0;
                    PasswordBox.Text = startupPhpSessionId;
                    LoginBtn_Click(LoginBtn, EventArgs.Empty);
                }
            };
            FormClosed += (s, e) =>
            {
                telegramAlertTimer.Stop();
                telegramAlertTimer.Dispose();
                telegramRemote?.Dispose();
                instances.Dispose();
            };
        }

      
       
        public static void SetDoubleBuffered(System.Windows.Forms.Control c)
        {
          
            if (System.Windows.Forms.SystemInformation.TerminalServerSession)
                return;

            System.Reflection.PropertyInfo? aProp =
                  typeof(System.Windows.Forms.Control).GetProperty(
                        "DoubleBuffered",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

            aProp?.SetValue(c, true, null);
        }

   

        private void CheckTelegramAlerts()
        {
            var alerts = telegramAlerts.Check(this);
            if (telegramRemote == null) return;
            foreach (var alert in alerts) _ = telegramRemote.NotifyAsync(alert);
        }

        // Telegram view of the open tabs, numbered from 1 in tab order.
        public IReadOnlyList<FunnyProject.Telegram.IBotInstance> Instances =>
            MainTabPage.TabPages.Cast<TabPage>()
                .Where(p => p != LoginPage)
                .Select(p => (Page: p, Window: p.Controls.OfType<UserWindow>().FirstOrDefault()))
                .Where(x => x.Window != null)
                .Select(x => (FunnyProject.Telegram.IBotInstance)new WindowBotInstance(x.Page, x.Window!))
                .ToArray();

        public Task RestartInstanceAsync(FunnyProject.Telegram.IBotInstance instance) =>
            RestartWordGalaxySessionAsync(((WindowBotInstance)instance).Window);

        private sealed class WindowBotInstance : FunnyProject.Telegram.IBotInstance
        {
            private readonly TabPage page;
            public WindowBotInstance(TabPage page, UserWindow window) { this.page = page; Window = window; }
            public UserWindow Window { get; }
            public object Key => Window;
            public string DisplayName => page.Text;
            public string PhpSessionId => Window.SessionPhpId;
            public bool BotRunning => Window.TelegramBotRunning;
            public bool BotActive => Window.TelegramRunner.IsActive;
            public FunnyProject.Runtime.BotStopReason LastStopReason => Window.TelegramRunner.LastStopReason;
            public int BotStopCount => Window.TelegramRunner.StopCount;
            public string? LastError => Window.TelegramRunner.LastError?.Message;
            public bool TcpConnected => Window.TelegramTcpConnected;
            public int MapId => Window.TelegramMapId;
            public string DetailedStatus => Window.TelegramDetailedStatus;
            public string NpcTargetsStatus(int instanceNumber) => Window.TelegramNpcTargets(page.Text, instanceNumber);
            public string ChangeNpcTargets(int instanceNumber, string argument) => Window.TelegramChangeNpcTargets(argument);
            public string ShipItemsStatus(int instanceNumber) => Window.TelegramShipItems(page.Text);
            public string NpcZonesStatus(int instanceNumber) => Window.TelegramNpcZones(page.Text, instanceNumber);
            public string ChangeNpcZones(int instanceNumber, string argument) => Window.TelegramChangeNpcZones(argument);
            public string StartBot() => Window.TelegramStartBot();
            public string StopBot() => Window.TelegramStopBot();
            public Task RestartTcpAsync() => Window.TelegramRestartTcpAsync();
        }

        private void ConfigureTelegram()
        {
            using var dialog = new Form
            {
                Text = "Telegram - acceso remoto", Width = 405, Height = 260,
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false, MinimizeBox = false
            };
            var tokenLabel = new Label { Left = 16, Top = 18, Width = 355,
                Text = "Token de BotFather (dejar vacio para conservar el actual):" };
            var tokenBox = new TextBox { Left = 16, Top = 41, Width = 355, UseSystemPasswordChar = true };
            var chatLabel = new Label { Left = 16, Top = 78, Width = 355, Text = "Chat ID autorizado:" };
            var chatBox = new TextBox { Left = 16, Top = 101, Width = 355 };
            try
            {
                var saved = TelegramSettingsStore.Load();
                if (saved != null) chatBox.Text = saved.Value.ChatId.ToString();
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, "No se pudo leer la configuracion: " + ex.Message);
            }
            var save = new Button { Left = 16, Top = 145, Width = 112, Text = "Guardar" };
            var delete = new Button { Left = 137, Top = 145, Width = 112, Text = "Eliminar" };
            var close = new Button { Left = 258, Top = 145, Width = 112, Text = "Cerrar" };
            save.Click += (sender, args) =>
            {
                try
                {
                    if (!long.TryParse(chatBox.Text.Trim(), out long chat) || chat <= 0)
                        throw new ArgumentException("Chat ID invalido.");
                    string token = tokenBox.Text.Trim();
                    if (token.Length == 0)
                        token = TelegramSettingsStore.Load()?.Token ?? "";
                    TelegramSettingsStore.Save(token, chat);
                    telegramRemote?.Dispose();
                    telegramRemote = TelegramRemoteService.Start(this);
                    MessageBox.Show(dialog, telegramRemote != null ? "Telegram configurado y activado." : "Configuracion guardada.");
                    dialog.Close();
                }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
            };
            delete.Click += (sender, args) =>
            {
                try
                {
                    telegramRemote?.Dispose();
                    telegramRemote = null;
                    TelegramSettingsStore.Delete();
                    MessageBox.Show(dialog, "Configuracion de Telegram eliminada.");
                    dialog.Close();
                }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
            };
            close.Click += (sender, args) => dialog.Close();
            dialog.Controls.AddRange(new Control[] { tokenLabel, tokenBox, chatLabel, chatBox, save, delete, close });
            dialog.ShowDialog(this);
        }

        private void DrawAccountTab(object? sender, DrawItemEventArgs e)
        {
            var page = MainTabPage.TabPages[e.Index];
            var rect = MainTabPage.GetTabRect(e.Index);
            e.Graphics.FillRectangle(SystemBrushes.Control, rect);
            TextRenderer.DrawText(e.Graphics, page.Text, MainTabPage.Font,
                new Rectangle(rect.X + 6, rect.Y + 3, Math.Max(0, rect.Width - 24), rect.Height - 6),
                SystemColors.ControlText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (page != LoginPage)
            {
                var close = new Rectangle(rect.Right - 20, rect.Y + 3, 16, rect.Height - 6);
                TextRenderer.DrawText(e.Graphics, "×", MainTabPage.Font, close,
                    SystemColors.ControlText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void AccountTabMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < MainTabPage.TabPages.Count; i++)
            {
                var page = MainTabPage.TabPages[i];
                if (page == LoginPage) continue;
                var rect = MainTabPage.GetTabRect(i);
                if (!new Rectangle(rect.Right - 20, rect.Y + 3, 16, rect.Height - 6).Contains(e.Location)) continue;
                foreach (UserWindow window in page.Controls.OfType<UserWindow>().ToArray())
                {
                    window.CloseSession();
                    instances.Close(window.UserId);
                }
                MainTabPage.TabPages.Remove(page);
                page.Dispose();
                MainTabPage.SelectedTab = LoginPage;
                return;
            }
        }

        private void RefreshSavedAccounts(string? selected)
        {
            if (savedAccounts == null) return;
            savedAccounts.Items.Clear();
            foreach (string name in SavedAccounts.Names()) savedAccounts.Items.Add(name);
            if (selected != null) savedAccounts.SelectedItem = selected;
        }

        private void UpdateAuthMode()
        {
            bool credentials = authMode?.SelectedIndex == 1;
            if (authMode != null) authMode.Visible = true;
            UsernameBox.Visible = credentials;
            PasswordBox.PlaceholderText = credentials ? "Password" : "PHPSESSID";
            if (savedAccounts != null) savedAccounts.Visible = credentials;
            if (saveAccountButton != null) saveAccountButton.Visible = credentials;
            if (deleteAccountButton != null) deleteAccountButton.Visible = credentials;
            LoginPage_Resize(this, EventArgs.Empty);
        }

        private void LoginPage_Resize(object sender, EventArgs e)
        {
            int x = LoginPage.Width / 2 - 105;
            bool credentials = authMode?.SelectedIndex == 1;
            int top = LoginPage.Height / 2 - (credentials ? 144 : 60);
            if (authMode != null) authMode.Location = new Point(x, top + 30);
            UsernameBox.Location = new Point(x, top + 60);
            PasswordBox.Location = new Point(x, top + (credentials ? 90 : 60));
            if (savedAccounts != null) savedAccounts.Location = new Point(x, top + 120);
            if (saveAccountButton != null) saveAccountButton.Location = new Point(x, top + 149);
            if (deleteAccountButton != null) deleteAccountButton.Location = new Point(x + 108, top + 149);
            LoginBtn.Location = new Point(LoginPage.Width / 2 - LoginBtn.Width / 2,
                top + (credentials ? 181 : 98));
            if (telegramSettingsButton != null)
                telegramSettingsButton.Location = new Point(Math.Max(8, LoginPage.ClientSize.Width - 218), 12);
        }

        private void BindHeroTabName(PacketManager manager, TabPage page)
        {
            EventHandler<FunnyProject.Commands.Read.UserInitialize>? handler = null;
            handler = (sender, hero) =>
            {
                if (string.IsNullOrWhiteSpace(hero.Username)) return;
                void Update()
                {
                    if (page.IsDisposed || page.Parent == null) return;
                    page.Text = hero.Username.Trim();
                }
                try
                {
                    if (IsDisposed || Disposing) return;
                    if (InvokeRequired) BeginInvoke((Action)Update);
                    else Update();
                }
                catch (InvalidOperationException) { }
            };
            manager.OnHeroInit += handler;
            page.Disposed += (sender, args) => manager.OnHeroInit -= handler;
        }

        // Runs on the UI thread (TelegramRemoteService uses BeginInvoke).
        internal Task<string> HandleTelegramCommandAsync(string command) =>
            new FunnyProject.Telegram.TelegramCommandDispatcher(this).HandleAsync(command);

        public async Task RestartWordGalaxySessionAsync(UserWindow window)
        {
            string phpSessionId = window.SessionPhpId;
            if (string.IsNullOrWhiteSpace(phpSessionId))
                throw new InvalidOperationException("La instancia no tiene un PHPSESSID valido.");
            var page = window.Parent as TabPage
                ?? throw new InvalidOperationException("No se encontro la pestaña de la instancia.");

            // The manager closes the old session only after /map has been validated.
            var session = await instances.RestartAsync(window.UserId);

            window.CloseSession();
            page.Controls.Remove(window);
            window.Dispose();

            var newWindow = new UserWindow(session.Api)
            {
                Dock = DockStyle.Fill,
                FormBorderStyle = FormBorderStyle.None,
                TopLevel = false
            };
            page.Controls.Add(newWindow);
            newWindow.Show();
            MainTabPage.SelectedTab = page;

            BindHeroTabName(session.PacketManager, page);
        }

        private async void LoginBtn_Click(object sender, EventArgs e)
        {

        bool credentials = authMode?.SelectedIndex == 1;
        string phpSessionId = credentials ? "" : PasswordBox.Text.Trim();
        if (!credentials && !FunnyProject.Sessions.WordGalaxyWebClient.IsValidPhpSessionId(phpSessionId))
        {
            MessageBox.Show("Introduce un PHPSESSID valido.");
            return;
        }
        if (credentials && (string.IsNullOrWhiteSpace(UsernameBox.Text) ||
            string.IsNullOrWhiteSpace(PasswordBox.Text)))
        {
            MessageBox.Show("Introduce usuario y contraseña.");
            return;
        }

        LoginBtn.Enabled = false;
        try
        {
            var session = credentials
                ? await instances.OpenWithCredentialsAsync(UsernameBox.Text, PasswordBox.Text)
                : await instances.OpenAsync(phpSessionId);
            var api = session.Api;
            int userId = session.UserId;

            Console.WriteLine($"[WordGalaxy] UserID: {userId}");
            string accountName = credentials ? UsernameBox.Text.Trim() : "";
            if (string.IsNullOrEmpty(accountName))
                accountName = session.AccountName ?? "";
            if (string.IsNullOrWhiteSpace(accountName)) accountName = userId.ToString();
            // TCP is already connected: the hero may have arrived before the tab.
            string heroName = api.User.UserData.InGameName ?? "";
            var wordGalaxyPage = new TabPage(string.IsNullOrWhiteSpace(heroName) ? accountName : heroName.Trim());
            BindHeroTabName(session.PacketManager, wordGalaxyPage);
            session.PacketManager.Disconnected += (s, args) =>
                Console.WriteLine("[WordGalaxy] Conexion TCP cerrada.");

            var wordGalaxyWindow = new UserWindow(api);
            wordGalaxyWindow.Dock = DockStyle.Fill;
            wordGalaxyWindow.FormBorderStyle = FormBorderStyle.None;
            wordGalaxyWindow.TopLevel = false;
            wordGalaxyPage.Controls.Add(wordGalaxyWindow);
            wordGalaxyWindow.Show();
            MainTabPage.TabPages.Add(wordGalaxyPage);
            MainTabPage.SelectedTab = wordGalaxyPage;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WordGalaxy] Error: " + ex.Message);
            MessageBox.Show("No se pudo iniciar sesion en WordGalaxy: " + ex.Message);
        }
        finally
        {
            LoginBtn.Enabled = true;
        }
        return;
        
        }
    }
}
