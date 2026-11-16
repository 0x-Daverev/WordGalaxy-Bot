using FunnyProject.Commands.Write;
using FunnyProject.GUIManagers;
using FunnyProject.Logic;
using FunnyProject.Logic.Interface;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FunnyProject.Forms
{
    public partial class UserWindow : Form
    {
        Api Api { get; init; }
        public string SessionPhpId => Api.User.UserData.PHPSESSID ?? "";
        // WordGalaxy account ID: the key of this session in the InstanceManager.
        public int UserId => Api.User.UserData.ID;
        public bool TelegramTcpConnected => Api.User.PacketManager?.Connected == true;
        public int TelegramMapId => Api.User.UserData.MapId;
        public bool TelegramBotRunning => botStarted;
        public FunnyProject.Runtime.BotRunner TelegramRunner => Api.Runner;
        public string TelegramNpcTargets(string instanceName, int instanceNumber)
        {
            int mapId = botSettings.NpcKillerMapId;
            var selected = botSettings.NpcKillerTargetsByMap.TryGetValue(mapId, out var names)
                ? names : new List<string>();
            return FunnyProject.Telegram.BotStatusText.NpcTargets(instanceName, instanceNumber, mapId,
                botSettings.NpcKillerLaser, selected, Api.Observations.NpcNames(mapId));
        }

        // /npc N ...: same effect as ticking the NPC list of this window.
        public string TelegramChangeNpcTargets(string argument)
        {
            if (sessionClosed || IsDisposed) return "Instancia cerrada.";
            var (map, selected, error) = FunnyProject.Telegram.NpcTargetSelection.Change(
                botSettings, Api.Observations.NpcNames, argument);
            if (map == null) return "Selecciona primero el mapa de NPC Killer.";
            if (selected == null) return error!;
            int mapId = map.id;
            botSettings.NpcKillerTargetsByMap[mapId] = selected;
            ScheduleSettingsSave();
            bool live = npcKiller != null && npcKiller.Map.id == mapId && botStarted;
            if (npcKiller != null && npcKiller.Map.id == mapId) npcKiller.UpdateTargets(selected);
            if (npcMapBox?.SelectedItem is FunnyProject.Navigation.MapGraph.Map shown && shown.id == mapId)
                RefreshNpcTargetList(force: true);
            return FunnyProject.Telegram.NpcTargetSelection.Confirmation(map.name, selected, live);
        }
        // ---- NPC Killer preferred zones ----

        // Drawing mode of the minimap (NPC Killer panel checkbox).
        private bool zoneDrawing;
        private Point? zoneDragStart;
        private Point zoneDragEnd;

        public string TelegramShipItems(string instanceName) => Api.User.Items.Text(instanceName);

        // ---- "Nave" tab: what the ship has (Core ShipItems) ----

        private ListView? itemsList;
        private TabPage? itemsPage;
        private DateTime nextItemsRefreshUtc = DateTime.MinValue;

        private void AddShipItemsTab()
        {
            itemsPage = new TabPage("Nave") { UseVisualStyleBackColor = true, Padding = new Padding(3) };
            itemsList = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowGroups = true
            };
            itemsList.Columns.Add("Objeto", 95);
            itemsList.Columns.Add("Cantidad", 62, HorizontalAlignment.Right);
            itemsPage.Controls.Add(itemsList);
            InfoTab.TabPages.Add(itemsPage);
        }

        // Every 2 s while the tab is visible.
        private void RefreshShipItemsTab()
        {
            if (itemsList == null || InfoTab.SelectedTab != itemsPage || DateTime.UtcNow < nextItemsRefreshUtc) return;
            nextItemsRefreshUtc = DateTime.UtcNow.AddSeconds(2);
            var items = Api.User.Items.Snapshot();
            itemsList.BeginUpdate();
            try
            {
                itemsList.Items.Clear();
                itemsList.Groups.Clear();
                if (items.Length == 0)
                {
                    itemsList.Items.Add(new ListViewItem(new[] { "Sin datos todavía", "" }));
                    return;
                }
                foreach (var group in items.GroupBy(i => i.Category))
                {
                    var g = itemsList.Groups.Add(group.Key, group.Key);
                    foreach (var i in group)
                        itemsList.Items.Add(new ListViewItem(new[] { i.Name, FunnyProject.UserCollections.ShipItems.Amount(i) }, g));
                }
            }
            finally { itemsList.EndUpdate(); }
        }

        public string TelegramNpcZones(string instanceName, int instanceNumber) =>
            FunnyProject.Logic.PreferredZones.Status(botSettings, instanceName, instanceNumber);

        // /zona N borrar ...: same as deleting on this window's minimap.
        public string TelegramChangeNpcZones(string argument)
        {
            if (sessionClosed || IsDisposed) return "Instancia cerrada.";
            var (change, reply) = FunnyProject.Logic.PreferredZones.ParseCommand(botSettings, argument);
            if (change == null) return reply;
            change(botSettings);
            return reply + ZonesChanged(botSettings.NpcKillerMapId);
        }

        // Saves and hands the zones to a running NPC Killer on that map.
        private string ZonesChanged(int mapId)
        {
            ScheduleSettingsSave();
            if (npcKiller == null || npcKiller.Map.id != mapId) return "";
            npcKiller.UpdateZones(FunnyProject.Logic.PreferredZones.For(botSettings, mapId));
            return botStarted ? " Aplicado al bot en marcha." : "";
        }

        // Minimap pixel -> game coordinates of the current map.
        private Point MinimapToGame(Point pixel)
        {
            var mapId = Api.User.UserData.MapId;
            var map = travel != null && travel.Graph.Maps.TryGetValue(mapId, out var bounds) ? bounds : null;
            double cx = (double)MinimapPanel.Width / (map?.width ?? 21000);
            double cy = (double)MinimapPanel.Height / (map?.height ?? 13100);
            return new Point((int)(pixel.X / cx), (int)(pixel.Y / cy));
        }

        private void ZoneMouseMove(object? sender, MouseEventArgs e)
        {
            if (zoneDragStart != null) zoneDragEnd = e.Location;
        }

        private void ZoneMouseUp(object? sender, MouseEventArgs e)
        {
            if (zoneDragStart is not Point startPixel) return;
            zoneDragStart = null;
            int mapId = Api.User.UserData.MapId;
            var a = MinimapToGame(startPixel);
            var b = MinimapToGame(e.Location);
            bool drag = Math.Abs(e.X - startPixel.X) + Math.Abs(e.Y - startPixel.Y) > 8;
            var (ok, reply) = drag
                ? FunnyProject.Logic.PreferredZones.Add(botSettings, mapId, a.X, a.Y, b.X, b.Y)
                : FunnyProject.Logic.PreferredZones.RemoveAt(botSettings, mapId, b.X, b.Y);
            if (ok) reply += ZonesChanged(mapId);
            ShowZoneMessage(reply);
        }

        private void ShowZoneMessage(string text)
        {
            if (zoneHint != null && !zoneHint.IsDisposed) zoneHint.Text = text;
        }

        private Label? zoneHint;

        // What the minimap draws: zones of the current map and the one being dragged.
        private FunnyProject.Logic.PreferredZone[] MinimapZones() =>
            FunnyProject.Logic.PreferredZones.For(botSettings, Api.User.UserData.MapId).ToArray();

        private Rectangle? MinimapZonePreview()
        {
            if (zoneDragStart is not Point start) return null;
            var a = MinimapToGame(start);
            var b = MinimapToGame(zoneDragEnd);
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        public string TelegramAction => Api.User.ActionState.Current.ToString();
        public string TelegramDetailedStatus
        {
            get
            {
                string module = moduleSelector?.SelectedItem?.ToString() ?? "Sin seleccionar";
                string activity = module == "GGs"
                    ? (ggProgress?.Text ?? "Sin datos de GG")
                    : (botStarted ? "En ejecucion" : "Sin iniciar");
                return FunnyProject.Telegram.BotStatusText.Details(botStarted, TelegramAction, module, activity,
                    (ggList?.SelectedItem as GalaxyGateInfo)?.name, botSettings.AttackMode, ggSelectedAmmo,
                    Api.User.ShipInfo.Hp, Api.User.ShipInfo.MaxHp);
            }
        }

        public string TelegramStartBot()
        {
            if (sessionClosed || IsDisposed) return "Instancia cerrada.";
            if (botStarted) return "El bot ya esta activo.";
            if (moduleSelector?.SelectedItem?.ToString() == "NPC Killer")
            {
                if (npcMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map)
                    return "Selecciona el mapa antes de iniciar NPC Killer.";
                StartSelectedModule();
                return "Inicio de NPC Killer solicitado.";
            }
            if (moduleSelector?.SelectedItem?.ToString() == "Collect")
            {
                if (collectMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map)
                    return "Selecciona el mapa antes de iniciar Collect.";
                StartSelectedModule();
                return "Inicio de Collect solicitado.";
            }
            if (moduleSelector?.SelectedItem?.ToString() != "GGs")
                return "El modulo seleccionado aun no permite ejecucion.";
            if (ggList?.SelectedItem is not GalaxyGateInfo || spinsBox == null)
                return "Selecciona una GG y los spins antes de iniciarlo.";
            StartSelectedModule();
            return "Inicio del bot solicitado.";
        }

        public string TelegramStopBot()
        {
            if (sessionClosed || IsDisposed) return "Instancia cerrada.";
            if (!botStarted) return "El bot ya esta parado.";
            StopSelectedModule();
            return "Bot detenido.";
        }

        public async Task TelegramRestartTcpAsync()
        {
            if (sessionClosed || restartingSession)
                throw new InvalidOperationException("La instancia no esta disponible para reiniciar TCP.");
            var packetManager = Api.User.PacketManager
                ?? throw new InvalidOperationException("Gestor TCP no disponible.");
            await packetManager.RestartTcpAsync();
        }

        LogicInterface NavigationMovement { get; set; }
        MapTravelModule? travel;
        ComboBox? moduleSelector;
        Button? restartSessionButton;
        private bool restartingSession;
        Button? botButton;
        ComboBox? collectMapBox;
        CheckedListBox? collectTypesList;
        FunnyProject.Modules.CollectBotModule? collector;
        private bool updatingCollectList;
        ComboBox? npcMapBox;
        CheckedListBox? npcTargetsList;
        FunnyProject.Modules.NpcKillerBotModule? npcKiller;
        private System.Windows.Forms.Timer? npcDiscoveryTimer;
        private bool updatingNpcList;
        ComboBox? ggList;
        ComboBox? spinsBox;
        ComboBox? ggAmmoBox;
        ComboBox? ggAttackModeBox;
        private string ggSelectedAmmo = "ucb-100";
        private ComboBox? ggFinalFormationBox;

        private TabPage? generalSettingsPage;
        private ComboBox? runFormationBox;
        private ComboBox? attackFormationBox;
        private System.Windows.Forms.Timer? formationSyncTimer;
        private string[]? lastShownFormationIds;
        // Formation per action, booster polling and auto-buy run in Core; the
        // window's UI timers only tick them.
        private readonly FunnyProject.Services.FormationService formations;
        private readonly FunnyProject.Services.BoosterPollingService boosterPolling;
        private readonly FunnyProject.Services.BoosterAutoBuyService boosterAutoBuy;
        private readonly FunnyProject.Services.EnergyLeechService energyLeech;
        private bool settingFormationControls;
        private readonly CancellationTokenSource boosterPurchaseCancel = new();

        private readonly BotSettings botSettings;
        private readonly System.Windows.Forms.Timer settingsSaveTimer = new System.Windows.Forms.Timer { Interval = 600 };
        private bool loadingSettings = true;
        private void ScheduleSettingsSave()
        {
            if (loadingSettings || sessionClosed || IsDisposed) return;
            settingsSaveTimer.Stop();
            settingsSaveTimer.Start();
        }
        private void SaveSettings()
        {
            settingsSaveTimer.Stop();
            BotSettingsStore.Save(Api.User.Server, Api.User.Username, botSettings);
        }
        Label? spinsLabel;
        Label? ggAmmoLabel;
        Label? ggAttackLabel;
        Label? ggFinalFormationLabel;
        int currentSpinCost;
        Label? ggProgress;
        Label? ggDetails;
        Label? ggAccountSummary;
        GalaxyGateService? ggApi;
        System.Windows.Forms.Timer? ggRefreshTimer;
        bool ggRefreshBusy;
        List<GalaxyGateInfo> gatesData = new();
        // Bot state lives in Core (Api.Runner); the window only reflects it.
        bool botStarted => Api.Runner.IsRunning;
        bool botStartPending => Api.Runner.IsStarting;
        private volatile bool sessionClosed;
        private Button? manualPortalJumpButton;
        private bool manualPortalJumpBusy;

        // Core services use the window's in-memory settings (UI thread).
        private sealed class WindowSettingsAccess : FunnyProject.Services.IBotSettingsAccess
        {
            private readonly UserWindow window;
            public WindowSettingsAccess(UserWindow window) => this.window = window;
            public BotSettings Read() => window.botSettings;
            public void Update(System.Action<BotSettings> change)
            {
                change(window.botSettings);
                window.SaveSettings();
            }
        }

        public void CloseSession()
        {
            if (sessionClosed) return;
            SaveSettings();
            sessionClosed = true;
            boosterPurchaseCancel.Cancel();
            StopSelectedModule();
            travel?.Stop();
            travel?.Dispose();
            travel = null;
            Api.User.PacketManager?.Disconnect();
            Api.Dispose();
            Close();
        }

        public UserWindow(Api api)
        {
            InitializeComponent();
            MinimapPanel.MouseMove += ZoneMouseMove;
            MinimapPanel.MouseUp += ZoneMouseUp;
            AddShipItemsTab();
            Form1.SetDoubleBuffered(MinimapPanel);
            Api = api;
            botSettings = BotSettingsStore.Load(Api.User.Server, Api.User.Username);
            ggSelectedAmmo = botSettings.Laser;
            var settingsAccess = new WindowSettingsAccess(this);
            formations = new FunnyProject.Services.FormationService(Api, settingsAccess);
            boosterPolling = new FunnyProject.Services.BoosterPollingService(Api);
            boosterAutoBuy = new FunnyProject.Services.BoosterAutoBuyService(Api, settingsAccess);
            energyLeech = new FunnyProject.Services.EnergyLeechService(Api, settingsAccess);
            settingsSaveTimer.Tick += (s, e) => SaveSettings();
            FormClosed += (s, e) => { SaveSettings(); settingsSaveTimer.Dispose(); };
            NavigationMovement = new LogicInterface(Api);
            Api.Runner.StateChanged += OnRunnerStateChanged;
            Api.Runner.StatusChanged += OnRunnerStatusChanged;
            FormClosed += (s, e) =>
            {
                Api.Runner.StateChanged -= OnRunnerStateChanged;
                Api.Runner.StatusChanged -= OnRunnerStatusChanged;
            };
            Task.Run(async () => await Render());
            SetupGeneralSettingsUi();
            // The one-line tab header exposes WinForms' overflow arrows.
            // Mouse wheel also navigates to tabs that are outside the visible strip.
            InfoTab.MouseWheel += (sender, args) =>
            {
                int step = args.Delta < 0 ? 1 : -1;
                int next = InfoTab.SelectedIndex + step;
                if (next >= 0 && next < InfoTab.TabPages.Count)
                    InfoTab.SelectedIndex = next;
            };
            SetupActionStatusUi();
            SetupBoosterStatusUi();
            SetupBotStatusOverlay();
            SetupNavigationUi();
            SetupManualPortalJump();
            SetupModuleSelector();
            SetupNpcDiscovery();
            SetupBoxDiscovery();
            SetupRestartSessionButton();
            loadingSettings = false;
            // Portal inspector retained for future development but hidden from the UI.
            // SetupPortalInspector();
        }
        private async Task ApplyAutoReconnectWhenReadyAsync()
        {
            while (!sessionClosed && !IsDisposed && Api.User.PacketManager == null)
                await Task.Delay(200);
            if (!sessionClosed && !IsDisposed && Api.User.PacketManager != null)
                Api.User.PacketManager.AutoReconnectEnabled = botStarted;
        }

        private sealed class FormationChoice
        {
            public string Id { get; }
            public string Label { get; }
            public FormationChoice(string id, string label) { Id = id; Label = label; }
            public override string ToString() => Label;
        }

        private static string FormationName(string id) => id switch
        {
            "drone_formation_default" => "Default",
            "drone_formation_f-3d-dr" => "Drill",
            "drone_formation_f-3d-vt" => "Veteran",
            "drone_formation_f-3d-wl" => "Wheel",
            _ => id.StartsWith("drone_formation_", StringComparison.Ordinal)
                ? id.Substring("drone_formation_".Length) : id
        };

        private void SetupGeneralSettingsUi()
        {
            generalSettingsPage = GeneralPage;
            generalSettingsPage.AutoScroll = true;
            var title = new Label
            {
                Left = 7, Top = 12, Width = 145, Height = 44,
                Text = "Formaciones por accion"
            };
            var runLabel = new Label
            {
                Left = 7, Top = 66, Width = 145, Height = 22,
                Text = "Run (movimiento)"
            };
            runFormationBox = new ComboBox
            {
                Left = 7, Top = 90, Width = 145,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            var attackLabel = new Label
            {
                Left = 7, Top = 139, Width = 145, Height = 22,
                Text = "Attack (combate)"
            };
            attackFormationBox = new ComboBox
            {
                Left = 7, Top = 163, Width = 145,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            generalSettingsPage.Controls.AddRange(new Control[]
            {
                title, runLabel, runFormationBox, attackLabel,
                attackFormationBox
            });
            generalSettingsPage.Resize += (sender, args) =>
            {
                int width = Math.Max(95, generalSettingsPage.ClientSize.Width - 17);
                title.Width = runLabel.Width = runFormationBox.Width =
                    attackLabel.Width = attackFormationBox.Width = width;
            };

            runFormationBox.SelectedIndexChanged += (sender, args) =>
            {
                if (settingFormationControls) return;
                botSettings.RunFormation = (runFormationBox.SelectedItem as FormationChoice)?.Id ?? "";
                formations.Reset();
                ScheduleSettingsSave();
            };
            attackFormationBox.SelectedIndexChanged += (sender, args) =>
            {
                if (settingFormationControls) return;
                botSettings.AttackFormation = (attackFormationBox.SelectedItem as FormationChoice)?.Id ?? "";
                formations.Reset();
                ScheduleSettingsSave();
            };
            formationSyncTimer = new System.Windows.Forms.Timer { Interval = 500 };
            formationSyncTimer.Tick += (sender, args) =>
            {
                if (sessionClosed || IsDisposed) return;
                RefreshAvailableFormations();
                ApplyFormationForCurrentAction();
                energyLeech.Tick();
                RefreshShipItemsTab();
            };
            formationSyncTimer.Start();
            FormClosed += (sender, args) =>
            {
                formationSyncTimer.Stop();
                formationSyncTimer.Dispose();
                formationSyncTimer = null;
            };
            SetupBoosterAutoBuyUi();
            RefreshAvailableFormations();
        }

        private void SetupBoosterAutoBuyUi()
        {
            var title = new Label
            {
                Left = 7, Top = 205, Height = 34, Width = 190,
                Text = "Autocompra boosters (<6h)"
            };
            generalSettingsPage!.Controls.Add(title);
            // Collapsible groups keep the General tab compact while retaining
            // individual booster settings and their live remaining times.
            var rows = new List<(CheckBox Option, Label Remaining, string Code, int Category)>();
            var groups = new List<(Button Header, Panel Content, string Title, int Category)>();
            var expandedGroups = new HashSet<Panel>();
            int nextTop = 244;
            foreach (var category in new[] { (Id: 2, Name: "Daño"), (Id: 1, Name: "Honor"), (Id: 0, Name: "Experiencia") })
            {
                var items = BoosterPurchaseService.Items.Where(x => x.Category == category.Id).ToArray();
                var header = new Button
                {
                    Left = 7, Top = nextTop, Width = 190, Height = 29,
                    Text = "▸ " + category.Name + " (" + items.Length + ")",
                    TextAlign = ContentAlignment.MiddleLeft,
                    TabStop = false
                };
                var content = new Panel
                {
                    Left = 7, Top = nextTop + header.Height,
                    Width = 190, Height = items.Length * 30 + 4,
                    Visible = false
                };
                for (int index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    int id = item.Id;
                    int rowTop = index * 30 + 2;
                    var box = new CheckBox
                    {
                        Left = 0, Top = rowTop, Width = 100, Height = 27,
                        Text = item.Code,
                        Checked = botSettings.AutoBuyBoosters != null &&
                            botSettings.AutoBuyBoosters.TryGetValue(id, out bool enabled) && enabled
                    };
                    box.CheckedChanged += (sender, args) =>
                    {
                        botSettings.AutoBuyBoosters ??= new Dictionary<int, bool>();
                        botSettings.AutoBuyBoosters[id] = box.Checked;
                        ScheduleSettingsSave();
                    };
                    var remainingLabel = new Label
                    {
                        Left = 105, Top = rowTop + 4, Width = 85, Height = 22,
                        Text = "--:--:--",
                        TextAlign = ContentAlignment.MiddleRight
                    };
                    content.Controls.Add(box);
                    content.Controls.Add(remainingLabel);
                    rows.Add((box, remainingLabel, item.Code, item.Category));
                }
                generalSettingsPage.Controls.Add(header);
                generalSettingsPage.Controls.Add(content);
                groups.Add((header, content, category.Name, category.Id));
                nextTop += header.Height + 4;
            }
            void LayoutBoosterGroups()
            {
                int width = Math.Max(155, generalSettingsPage.ClientSize.Width - 24);
                title.Width = width;
                int top = 244;
                foreach (var group in groups)
                {
                    group.Header.Top = top;
                    group.Header.Width = width;
                    top += group.Header.Height;
                    group.Content.Top = top;
                    group.Content.Width = width;
                    foreach (Control control in group.Content.Controls)
                    {
                        if (control is Label remaining)
                        {
                            remaining.Left = Math.Max(80, width - remaining.Width);
                        }
                        else if (control is CheckBox option)
                        {
                            option.Width = Math.Max(75, width - 95);
                        }
                    }
                    if (expandedGroups.Contains(group.Content)) top += group.Content.Height;
                    top += 4;
                }
            }
            foreach (var group in groups)
            {
                var selectedGroup = group;
                selectedGroup.Header.Click += (sender, args) =>
                {
                    bool expanded = !expandedGroups.Contains(selectedGroup.Content);
                    if (expanded) expandedGroups.Add(selectedGroup.Content);
                    else expandedGroups.Remove(selectedGroup.Content);
                    selectedGroup.Content.Visible = expanded;
                    selectedGroup.Header.Text = (expanded ? "▾ " : "▸ ") +
                        selectedGroup.Title + " (" +
                        BoosterPurchaseService.Items.Count(x =>
                            x.Category == selectedGroup.Category) + ")";
                    LayoutBoosterGroups();
                };
            }
            generalSettingsPage.Resize += (sender, args) => LayoutBoosterGroups();
            LayoutBoosterGroups();
            var remainingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            remainingTimer.Tick += (sender, args) =>
            {
                if (sessionClosed || IsDisposed) return;
                foreach (var row in rows)
                {
                    if (Api.User.Boosters.TryGetRemainingSeconds(row.Code, row.Category,
                        out long remaining))
                    {
                        var time = TimeSpan.FromSeconds(remaining);
                        row.Remaining.Text = time.Days > 0
                            ? time.ToString(@"d\.hh\:mm\:ss")
                            : time.ToString(@"hh\:mm\:ss");
                    }
                    else
                    {
                        row.Remaining.Text = "--:--:--";
                    }
                }
            };
            remainingTimer.Start();
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += async (sender, args) =>
            {
                if (sessionClosed || IsDisposed) return;
                await boosterAutoBuy.CheckAsync(boosterPurchaseCancel.Token);
            };
            timer.Start();
            FormClosed += (sender, args) =>
            {
                boosterPurchaseCancel.Cancel();
                timer.Stop();
                timer.Dispose();
                remainingTimer.Stop();
                remainingTimer.Dispose();
                boosterPurchaseCancel.Dispose();
            };
        }

        private void RefreshAvailableFormations()
        {
            var ids = Api.User.AvailableDroneFormations;
            if (lastShownFormationIds != null && ids.SequenceEqual(lastShownFormationIds)) return;
            lastShownFormationIds = ids.ToArray();
            settingFormationControls = true;
            try
            {
                void Fill(ComboBox box, string selectedId)
                {
                    box.Items.Clear();
                    box.Items.Add(new FormationChoice("", "Sin cambio automatico"));
                    foreach (var id in ids)
                        box.Items.Add(new FormationChoice(id, FormationName(id)));
                    if (!string.IsNullOrEmpty(selectedId) &&
                        !ids.Contains(selectedId, StringComparer.Ordinal))
                        box.Items.Add(new FormationChoice(selectedId,
                            FormationName(selectedId) + " (no detectada)"));
                    var item = box.Items.Cast<FormationChoice>()
                        .FirstOrDefault(choice => choice.Id == selectedId);
                    box.SelectedItem = item ?? box.Items[0];
                }
                if (runFormationBox != null && attackFormationBox != null)
                {
                    Fill(runFormationBox, botSettings.RunFormation);
                    Fill(attackFormationBox, botSettings.AttackFormation);
                }
                if (ggFinalFormationBox != null && !ggFinalFormationBox.IsDisposed)
                    Fill(ggFinalFormationBox, botSettings.GgFinalNpcFormation);
            }
            finally { settingFormationControls = false; }
            formations.Reset();
        }

        private void ApplyFormationForCurrentAction() => formations.Tick();

        private void SetupActionStatusUi()
        {
            var actionLabel = new Label
            {
                Left = 7, Top = 190, Width = 190, Height = 25,
                Text = "Accion: Idle"
            };
            StatPage.Controls.Add(actionLabel);
            var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (sender, args) =>
            {
                if (!sessionClosed && !actionLabel.IsDisposed)
                    actionLabel.Text = "Accion: " +
                        (botStarted ? Api.User.ActionState.Current.ToString() : "Idle");
            };
            timer.Start();
            FormClosed += (sender, args) => { timer.Stop(); timer.Dispose(); };
        }

        private void SetupBoosterStatusUi()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (sender, args) =>
            {
                if (sessionClosed || IsDisposed) return;
                boosterPolling.Tick(DateTime.UtcNow);
            };
            timer.Start();
            FormClosed += (sender, args) => { timer.Stop(); timer.Dispose(); };
        }

        private void SetupBotStatusOverlay()
        {
            ggProgress = new Label
            {
                Left = 8,
                Top = 8,
                Width = 330,
                Height = 72,
                AutoSize = false,
                AutoEllipsis = true,
                Text = "Estado bot: Sin iniciar",
                BackColor = Color.FromArgb(38, 44, 52),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Padding = new Padding(7, 5, 7, 5),
                BorderStyle = BorderStyle.FixedSingle
            };
            MinimapPanel.Controls.Add(ggProgress);
            ggProgress.BringToFront();
            MinimapPanel.Resize += (sender, args) =>
            {
                if (ggProgress != null && !ggProgress.IsDisposed)
                    ggProgress.Width = Math.Max(120, Math.Min(330, MinimapPanel.ClientSize.Width - 55));
            };
        }

        private void SetupManualPortalJump()
        {
            manualPortalJumpButton = new Button
            {
                Text = "↪",
                Width = 28,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(38, 44, 52),
                ForeColor = Color.Silver,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                TabStop = false
            };
            manualPortalJumpButton.FlatAppearance.BorderSize = 0;
            manualPortalJumpButton.Left = Math.Max(0, MinimapPanel.ClientSize.Width - manualPortalJumpButton.Width - 7);
            manualPortalJumpButton.Top = 7;
            var tip = new ToolTip();
            tip.SetToolTip(manualPortalJumpButton, "Saltar por el portal más cercano (acércate primero)");
            MinimapPanel.Controls.Add(manualPortalJumpButton);
            manualPortalJumpButton.BringToFront();
            MinimapPanel.Resize += (s, e) =>
            {
                if (manualPortalJumpButton != null && !manualPortalJumpButton.IsDisposed)
                    manualPortalJumpButton.Left = Math.Max(0, MinimapPanel.ClientSize.Width - manualPortalJumpButton.Width - 7);
            };
            manualPortalJumpButton.Click += (s, e) =>
            {
                if (manualPortalJumpBusy || sessionClosed) return;
                if (Api.User.PacketManager?.Connected != true)
                {
                    MessageBox.Show("No hay conexión TCP con el juego.");
                    return;
                }
                FunnyProject.UserCollections.Objects.Gate? gate;
                lock (Api.User.BasesPorts.Ports)
                    gate = Api.User.BasesPorts.Ports
                        .Where(p => p.ID > 0)
                        .OrderBy(p => Math.Pow(p.X - Api.User.Position.X, 2) +
                                      Math.Pow(p.Y - Api.User.Position.Y, 2))
                        .FirstOrDefault();
                if (gate == null)
                {
                    MessageBox.Show("No hay portales disponibles en el mapa.");
                    return;
                }
                double dx = gate.X - Api.User.Position.X;
                double dy = gate.Y - Api.User.Position.Y;
                if (dx * dx + dy * dy > 200 * 200)
                {
                    MessageBox.Show("Acércate a un portal antes de saltar.");
                    return;
                }
                manualPortalJumpBusy = true;
                manualPortalJumpButton!.Enabled = false;
                try
                {
                    Api.User.PacketManager.Send(new PortalSelect(gate.ID));
                    Api.User.PacketManager.Send(new PortalJump());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("No se pudo enviar el salto: " + ex.Message);
                }
                finally
                {
                    manualPortalJumpBusy = false;
                    if (manualPortalJumpButton != null && !manualPortalJumpButton.IsDisposed)
                        manualPortalJumpButton.Enabled = true;
                }
            };
            FormClosed += (s, e) => tip.Dispose();
        }

        private void SetupRestartSessionButton()
        {
            restartSessionButton = new Button
            {
                Text = "Reiniciar instancia",
                Height = 30
            };
            ActionPage.Controls.Add(restartSessionButton);
            LayoutActionButtons();
            restartSessionButton.Click += async (sender, args) =>
            {
                if (restartingSession || sessionClosed) return;
                restartingSession = true;
                restartSessionButton.Enabled = false;
                try
                {
                    var main = Application.OpenForms.OfType<Form1>().FirstOrDefault();
                    if (main == null)
                        throw new InvalidOperationException("No se encontro la ventana principal.");
                    await main.RestartWordGalaxySessionAsync(this);
                }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                        MessageBox.Show("No se pudo reiniciar la instancia: " + ex.Message);
                }
                finally
                {
                    restartingSession = false;
                    if (!IsDisposed && restartSessionButton != null && !restartSessionButton.IsDisposed)
                        restartSessionButton.Enabled = true;
                }
            };
        }

        private void LayoutActionButtons()
        {
            const int margin = 7, gap = 6, height = 30;
            int width = Math.Max(120, ActionPage.ClientSize.Width - margin * 2);
            int bottom = ActionPage.ClientSize.Height - margin;
            if (restartSessionButton != null)
            {
                restartSessionButton.SetBounds(margin, bottom - height, width, height);
                bottom -= height + gap;
            }
            if (botButton != null)
            {
                botButton.SetBounds(margin, bottom - height, width, height);
                var settingsPanel = ActionPage.Controls.OfType<Panel>().FirstOrDefault();
                if (settingsPanel != null)
                    settingsPanel.Height = Math.Max(35, botButton.Top - settingsPanel.Top - 8);
            }
        }

        private void SetupPortalInspector()
        {
            var page = new TabPage("Portals");
            InfoTab.TabPages.Add(page);
            var count = new Label { Dock = DockStyle.Top, Height = 40,
                Text = "Portales recibidos: 0" };
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            list.Columns.Add("ID", 65);
            list.Columns.Add("Tipo", 55);
            list.Columns.Add("X", 65);
            list.Columns.Add("Y", 65);
            list.Columns.Add("Empresa", 65);
            var copy = new Button { Dock = DockStyle.Bottom, Height = 30,
                Text = "Copiar portales" };
            var details = new TextBox { Dock = DockStyle.Bottom, Height = 64,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            page.Controls.Add(list);
            page.Controls.Add(details);
            page.Controls.Add(copy);
            page.Controls.Add(count);
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(details.Text); }
                catch (System.Runtime.InteropServices.ExternalException)
                { MessageBox.Show("No se pudo copiar los portales."); }
            };
            void RefreshPortals()
            {
                if (sessionClosed || IsDisposed) return;
                int mapId = Api.User.UserData.MapId;
                FunnyProject.UserCollections.Objects.Gate[] snapshot;
                lock (Api.User.BasesPorts.Ports)
                    snapshot = Api.User.BasesPorts.Ports.ToArray();
                var lines = snapshot.OrderBy(g => g.ID).Select(g =>
                    $"ID={g.ID} tipo={g.PortalType} x={g.X} y={g.Y} empresa={g.FactionID}").ToArray();
                string data = $"Mapa ID: {mapId}; portales: {snapshot.Length}" +
                    Environment.NewLine + string.Join(Environment.NewLine, lines);
                if (data == details.Text) return;
                details.Text = data;
                count.Text = $"Mapa ID: {mapId} | Portales: {snapshot.Length}";
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var gate in snapshot.OrderBy(g => g.ID))
                {
                    var item = new ListViewItem(gate.ID.ToString());
                    item.SubItems.Add(gate.PortalType.ToString());
                    item.SubItems.Add(gate.X.ToString());
                    item.SubItems.Add(gate.Y.ToString());
                    item.SubItems.Add(gate.FactionID.ToString());
                    list.Items.Add(item);
                }
                list.EndUpdate();
            }
            var timer = new System.Windows.Forms.Timer { Interval = 750 };
            timer.Tick += (s, e) => { if (page == InfoTab.SelectedTab) RefreshPortals(); };
            timer.Start();
            FormClosed += (s, e) => { timer.Stop(); timer.Dispose(); };
            RefreshPortals();
        }

        private void SetupBoxDiscovery()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (sender, args) =>
            {
                // Box types are recorded by Api.Observations (Core).
                if (sessionClosed) return;
                RefreshCollectTypes();
            };
            timer.Start();
            FormClosed += (sender, args) => { timer.Stop(); timer.Dispose(); };
        }

        private void RefreshCollectTypes()
        {
            if (collectTypesList == null || collectTypesList.IsDisposed ||
                collectMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map map)
                return;
            botSettings.CollectTypesByMap ??= new Dictionary<int, List<string>>();
            var selected = botSettings.CollectTypesByMap.TryGetValue(map.id, out var saved)
                ? saved : new List<string>();
            var types = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            types.UnionWith(Api.Observations.BoxTypes(map.id));
            var sorted = types.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToArray();
            if (sorted.SequenceEqual(collectTypesList.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase))
                return;
            updatingCollectList = true;
            try
            {
                collectTypesList.BeginUpdate();
                collectTypesList.Items.Clear();
                foreach (string type in sorted)
                    collectTypesList.Items.Add(type, selected.Contains(type, StringComparer.OrdinalIgnoreCase));
            }
            finally
            {
                collectTypesList.EndUpdate();
                updatingCollectList = false;
            }
        }

        private void SetupNpcDiscovery()
        {
            npcDiscoveryTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            npcDiscoveryTimer.Tick += (sender, args) =>
            {
                // NPC names are recorded by Api.Observations (Core).
                if (sessionClosed) return;
                RefreshNpcTargetList();
            };
            npcDiscoveryTimer.Start();
            FormClosed += (sender, args) => { npcDiscoveryTimer.Stop(); npcDiscoveryTimer.Dispose(); };
        }

        // force: also re-apply the check marks (after a change from Telegram).
        private void RefreshNpcTargetList(bool force = false)
        {
            if (npcTargetsList == null || npcTargetsList.IsDisposed ||
                npcMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map map) return;
            int mapId = map.id;
            botSettings.NpcKillerTargetsByMap ??= new Dictionary<int, List<string>>();
            var selected = botSettings.NpcKillerTargetsByMap.TryGetValue(mapId, out var targets)
                ? targets : new List<string>();
            var available = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            available.UnionWith(Api.Observations.NpcNames(mapId));
            string[] sorted = available.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] shown = npcTargetsList.Items.Cast<string>().ToArray();
            if (!force && sorted.SequenceEqual(shown, StringComparer.OrdinalIgnoreCase)) return;
            updatingNpcList = true;
            try
            {
                npcTargetsList.BeginUpdate();
                npcTargetsList.Items.Clear();
                foreach (string name in sorted)
                    npcTargetsList.Items.Add(name, selected.Contains(name, StringComparer.OrdinalIgnoreCase));
            }
            finally
            {
                npcTargetsList.EndUpdate();
                updatingNpcList = false;
            }
        }

        private void SetupModuleSelector()
        {
            var label = new Label { Left = 7, Top = 8, Width = 148, Text = "Modulo" };
            moduleSelector = new ComboBox
            {
                Left = 7, Top = 29, Width = 148,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            moduleSelector.Items.AddRange(new object[] { "Collect", "NPC Killer", "Kill and Collect", "GGs" });
            ggRefreshTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            ggRefreshTimer.Tick += async (s, e) =>
            {
                if (!sessionClosed && moduleSelector?.SelectedItem?.ToString() == "GGs")
                    await LoadGates();
            };
            FormClosed += (s, e) => { ggRefreshTimer?.Stop(); ggRefreshTimer?.Dispose(); ggRefreshTimer = null; };
            var settings = new Panel
            {
                Left = 5, Top = 65, Width = 157, Height = 250,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            // Keep child controls inside the visible width even when the vertical
            // scrollbar reduces the panel's client area.
            bool resizingSettings = false;
            void FitSettingsWidth()
            {
                if (resizingSettings) return;
                resizingSettings = true;
                try
                {
                    int available = Math.Max(40, settings.ClientSize.Width - 6);
                    foreach (Control child in settings.Controls)
                    {
                        int width = Math.Max(40, available - child.Left);
                        if (child.Width != width)
                            child.Width = width;
                    }
                    // AutoScroll may have changed the client width after a vertical
                    // scrollbar appeared. Never reserve horizontal scrollable space.
                    settings.HorizontalScroll.Visible = false;
                }
                finally { resizingSettings = false; }
            }
            settings.ControlAdded += (sender, args) => FitSettingsWidth();
            settings.SizeChanged += (sender, args) => FitSettingsWidth();
            settings.Layout += (sender, args) => FitSettingsWidth();
            ActionPage.Controls.Add(label);
            ActionPage.Controls.Add(moduleSelector);
            ActionPage.Controls.Add(settings);
            botButton = new Button { Left = 7, Width = 148, Height = 30, Text = "Iniciar bot" };
            ActionPage.Controls.Add(botButton);
            botButton.Click += (s, e) => { if (botStarted) StopSelectedModule(); else StartSelectedModule(); };
            void LayoutAction()
            {
                int width = Math.Max(120, ActionPage.ClientSize.Width - 14);
                label.Width = width;
                moduleSelector.Width = width;
                settings.Width = width;
                botButton.Width = width;
                LayoutActionButtons();
                settings.Height = Math.Max(35, botButton.Top - settings.Top - 8);
                FitSettingsWidth();
            }
            ActionPage.Resize += (s, e) => LayoutAction();
            LayoutAction();
            void ShowSettings()
            {
                if (botStarted) StopSelectedModule();
                ggRefreshTimer?.Stop();
                settings.Controls.Clear();
                collectMapBox = null;
                collectTypesList = null;
                npcMapBox = null;
                npcTargetsList = null;
                ggList = null;
                spinsBox = null;
                ggAmmoBox = null;
                ggAttackModeBox = null;
                ggFinalFormationBox = null;
                spinsLabel = null;
                ggAmmoLabel = null;
                ggAttackLabel = null;
                ggFinalFormationLabel = null;
                if (ggProgress != null) ggProgress.Text = "Estado bot: Sin iniciar";
                ggDetails = null;
                ggAccountSummary = null;
                string selected = moduleSelector.SelectedItem?.ToString() ?? "";
                var header = new Label { Left = 2, Top = 4, Width = 150, Height = 32,
                    Text = selected + " - configuracion" };
                settings.Controls.Add(header);
                if (selected == "Collect")
                {
                    settings.Controls.Add(new Label { Left = 2, Top = 40, Width = 150,
                        Text = "Mapa de recoleccion:" });
                    collectMapBox = new ComboBox { Left = 2, Top = 64, Width = 148,
                        DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "name" };
                    foreach (var map in new FunnyProject.Navigation.MapGraph().SortedMaps)
                        collectMapBox.Items.Add(map);
                    settings.Controls.Add(collectMapBox);
                    settings.Controls.Add(new Label { Left = 2, Top = 101, Width = 150,
                        Text = "Tipos de cajas:" });
                    collectTypesList = new CheckedListBox { Left = 2, Top = 123,
                        Width = 148, Height = 145, CheckOnClick = true, IntegralHeight = false };
                    settings.Controls.Add(collectTypesList);
                    settings.Controls.Add(new Label { Left = 2, Top = 277, Width = 150,
                        Height = 66, ForeColor = SystemColors.GrayText,
                        Text = "Tipos observados en este mapa. Inicia el viaje si aun no aparecen." });
                    collectTypesList.ItemCheck += (sender, e) =>
                    {
                        if (updatingCollectList ||
                            collectMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map map)
                            return;
                        var types = new List<string>();
                        for (int i = 0; i < collectTypesList.Items.Count; i++)
                        {
                            bool enabled = i == e.Index
                                ? e.NewValue == CheckState.Checked : collectTypesList.GetItemChecked(i);
                            if (enabled && collectTypesList.Items[i] is string name) types.Add(name);
                        }
                        botSettings.CollectTypesByMap[map.id] = types;
                        collector?.UpdateTypes(types);
                        ScheduleSettingsSave();
                    };
                    collectMapBox.SelectedIndexChanged += (sender, args) =>
                    {
                        if (collectMapBox.SelectedItem is not FunnyProject.Navigation.MapGraph.Map map)
                            return;
                        botSettings.CollectMapId = map.id;
                        ScheduleSettingsSave();
                        updatingCollectList = true;
                        try { collectTypesList.Items.Clear(); }
                        finally { updatingCollectList = false; }
                        RefreshCollectTypes();
                    };
                    int mapIndex = -1;
                    for (int i = 0; i < collectMapBox.Items.Count; i++)
                        if (collectMapBox.Items[i] is FunnyProject.Navigation.MapGraph.Map map &&
                            map.id == botSettings.CollectMapId) { mapIndex = i; break; }
                    if (mapIndex < 0)
                        for (int i = 0; i < collectMapBox.Items.Count; i++)
                            if (collectMapBox.Items[i] is FunnyProject.Navigation.MapGraph.Map map &&
                                map.id == Api.User.UserData.MapId) { mapIndex = i; break; }
                    if (mapIndex >= 0) collectMapBox.SelectedIndex = mapIndex;
                    RefreshCollectTypes();
                }
                else if (selected == "NPC Killer")
                {
                    settings.Controls.Add(new Label { Left = 2, Top = 40, Width = 150, Text = "Mapa para matar:" });
                    npcMapBox = new ComboBox { Left = 2, Top = 64, Width = 148,
                        DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "name" };
                    foreach (var map in new FunnyProject.Navigation.MapGraph().SortedMaps)
                        npcMapBox.Items.Add(map);
                    settings.Controls.Add(npcMapBox);
                    settings.Controls.Add(new Label { Left = 2, Top = 101, Width = 150,
                        Text = "NPCs a atacar:" });
                    npcTargetsList = new CheckedListBox { Left = 2, Top = 123, Width = 148,
                        Height = 145, CheckOnClick = true, IntegralHeight = false };
                    settings.Controls.Add(npcTargetsList);
                    settings.Controls.Add(new Label { Left = 2, Top = 277, Width = 150, Height = 66,
                        ForeColor = SystemColors.GrayText,
                        Text = "NPCs observados en el mapa. Si la lista esta vacia, inicia el viaje para descubrirlos." });
                    // NPC Killer ammunition, independent of the Galaxy Gates one.
                    settings.Controls.Add(new Label { Left = 2, Top = 345, Width = 148, Text = "Municion laser:" });
                    var npcAmmoBox = new ComboBox { Left = 2, Top = 367, Width = 148, DropDownStyle = ComboBoxStyle.DropDownList };
                    npcAmmoBox.Items.AddRange(new object[] { "LCB-10", "MCB-25", "MCB-50", "UCB-100" });
                    npcAmmoBox.SelectedIndex = Math.Max(0, Array.IndexOf(BotSettings.LaserAmmunition, botSettings.NpcKillerLaser));
                    npcAmmoBox.SelectedIndexChanged += (s, e) =>
                    {
                        botSettings.NpcKillerLaser = BotSettings.LaserAmmunition[Math.Max(0, npcAmmoBox.SelectedIndex)];
                        npcKiller?.UpdateAmmunition(botSettings.NpcKillerLaser);
                        ScheduleSettingsSave();
                    };
                    settings.Controls.Add(npcAmmoBox);
                    // Energy Leech: applied live by EnergyLeechService.
                    var leechBox = new CheckBox { Left = 2, Top = 397, Width = 150, Height = 34,
                        Text = "Comprar Energy Leech", Checked = botSettings.NpcKillerEnergyLeech };
                    leechBox.CheckedChanged += (s, e) =>
                    {
                        botSettings.NpcKillerEnergyLeech = leechBox.Checked;
                        ScheduleSettingsSave();
                    };
                    settings.Controls.Add(leechBox);
                    // Preferred zones, drawn on the minimap of the current map.
                    zoneDrawing = false;
                    zoneDragStart = null;
                    var zoneDrawBox = new CheckBox { Left = 2, Top = 437, Width = 150, Height = 34,
                        Text = "Dibujar zonas en el minimapa" };
                    zoneDrawBox.CheckedChanged += (s, e) =>
                    {
                        zoneDrawing = zoneDrawBox.Checked;
                        zoneDragStart = null;
                        MinimapPanel.Cursor = zoneDrawing ? Cursors.Cross : Cursors.Default;
                    };
                    settings.Controls.Add(zoneDrawBox);
                    var zoneClear = new Button { Left = 2, Top = 475, Width = 148, Height = 26, Text = "Borrar zonas del mapa" };
                    zoneClear.Click += (s, e) =>
                    {
                        int mapId = Api.User.UserData.MapId;
                        if (MessageBox.Show(this, "Borrar todas las zonas preferidas de " +
                                FunnyProject.Logic.PreferredZones.MapName(mapId) + "?", "Zonas preferidas",
                                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        ShowZoneMessage(FunnyProject.Logic.PreferredZones.Clear(botSettings, mapId) + ZonesChanged(mapId));
                    };
                    settings.Controls.Add(zoneClear);
                    zoneHint = new Label { Left = 2, Top = 505, Width = 150, Height = 80, ForeColor = SystemColors.GrayText,
                        Text = "Zonas preferidas (verdes): sin objetivos busca dentro y prefiere los NPCs de dentro. " +
                               "Con la casilla marcada, arrastra para crear y haz clic en una zona para borrarla." };
                    settings.Controls.Add(zoneHint);
                    npcTargetsList.ItemCheck += (sender, e) =>
                    {
                        if (updatingNpcList || npcMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map map)
                            return;
                        var names = new List<string>();
                        for (int i = 0; i < npcTargetsList.Items.Count; i++)
                        {
                            bool enabled = i == e.Index ? e.NewValue == CheckState.Checked : npcTargetsList.GetItemChecked(i);
                            if (enabled && npcTargetsList.Items[i] is string name) names.Add(name);
                        }
                        botSettings.NpcKillerTargetsByMap[map.id] = names;
                        npcKiller?.UpdateTargets(names);
                        ScheduleSettingsSave();
                    };
                    npcMapBox.SelectedIndexChanged += (sender, args) =>
                    {
                        if (npcMapBox.SelectedItem is not FunnyProject.Navigation.MapGraph.Map map) return;
                        botSettings.NpcKillerMapId = map.id;
                        ScheduleSettingsSave();
                        // Rebuild checked states even when two maps have the same NPC names.
                        updatingNpcList = true;
                        try { npcTargetsList.Items.Clear(); }
                        finally { updatingNpcList = false; }
                        RefreshNpcTargetList();
                    };
                    int mapIndex = -1;
                    for (int i = 0; i < npcMapBox.Items.Count; i++)
                        if (npcMapBox.Items[i] is FunnyProject.Navigation.MapGraph.Map m &&
                            m.id == botSettings.NpcKillerMapId) { mapIndex = i; break; }
                    if (mapIndex < 0)
                        for (int i = 0; i < npcMapBox.Items.Count; i++)
                            if (npcMapBox.Items[i] is FunnyProject.Navigation.MapGraph.Map m &&
                                m.id == Api.User.UserData.MapId) { mapIndex = i; break; }
                    if (mapIndex >= 0) npcMapBox.SelectedIndex = mapIndex;
                    RefreshNpcTargetList();
                }
                else if (selected == "Kill and Collect")
                {
                    settings.Controls.Add(new CheckBox { Left = 2, Top = 42, Width = 150,
                        Text = "Atacar NPC" });
                    settings.Controls.Add(new CheckBox { Left = 2, Top = 72, Width = 150,
                        Text = "Recoger cajas" });
                    settings.Controls.Add(new Label { Left = 2, Top = 111, Width = 150,
                        Text = "Prioridad:" });
                    var priority = new ComboBox { Left = 2, Top = 134, Width = 148,
                        DropDownStyle = ComboBoxStyle.DropDownList };
                    priority.Items.AddRange(new object[] { "NPC", "Cajas" });
                    priority.SelectedIndex = 0;
                    settings.Controls.Add(priority);
                }
                else if (selected == "GGs")
                {
                    settings.Controls.Add(new Label { Left = 2, Top = 40, Width = 150, Text = "Galaxy Gate:" });
                    ggList = new ComboBox { Left = 2, Top = 65, Width = 148, DropDownStyle = ComboBoxStyle.DropDownList };
                    ggDetails = new Label { Left = 2, Top = 98, Width = 150, Height = 90,
                        Font = new Font("Segoe UI", 8f), Text = "Cargando..." };
                    settings.Controls.Add(ggList);
                    settings.Controls.Add(ggDetails);
                    ggAccountSummary = new Label { Left = 2, Top = 190, Width = 150, Height = 20 };
                    settings.Controls.Add(ggAccountSummary);
                    ggList.SelectedIndexChanged += (s, e) => { if (ggList.SelectedItem is GalaxyGateInfo gate) { botSettings.GalaxyGateName = gate.name; ScheduleSettingsSave(); } DisplayGate(); };
                    spinsLabel = new Label { Left = 2, Top = 213, Width = 148, Text = "Spins:" };
                    settings.Controls.Add(spinsLabel);
                    spinsBox = new ComboBox { Left = 2, Top = 235, Width = 148, DropDownStyle = ComboBoxStyle.DropDownList };
                    spinsBox.Items.AddRange(new object[] { "x1", "x5", "x10", "x100" });
                    spinsBox.SelectedIndex = botSettings.Spins switch { 1 => 0, 5 => 1, 10 => 2, _ => 3 };
                    spinsBox.SelectedIndexChanged += (s, e) => { botSettings.Spins = new[] { 1, 5, 10, 100 }[Math.Max(0, spinsBox.SelectedIndex)]; ScheduleSettingsSave(); };
                    settings.Controls.Add(spinsBox);
                    ggAmmoLabel = new Label { Left = 2, Top = 268, Width = 148, Text = "Municion laser:" };
                    settings.Controls.Add(ggAmmoLabel);
                    ggAmmoBox = new ComboBox { Left = 2, Top = 290, Width = 148, DropDownStyle = ComboBoxStyle.DropDownList };
                    ggAmmoBox.Items.AddRange(new object[] { "LCB-10", "MCB-25", "MCB-50", "UCB-100" });
                    ggAmmoBox.SelectedIndex = ggSelectedAmmo switch { "lcb-10" => 0, "mcb-25" => 1, "mcb-50" => 2, _ => 3 };
                    ggAmmoBox.SelectedIndexChanged += (s, e) => { ggSelectedAmmo = ggAmmoBox.SelectedItem?.ToString()?.ToLowerInvariant() ?? "ucb-100"; botSettings.Laser = ggSelectedAmmo; ScheduleSettingsSave(); };
                    settings.Controls.Add(ggAmmoBox);
                    ggAttackLabel = new Label { Left = 2, Top = 322, Width = 148, Text = "Tipo de ataque:" };
                    settings.Controls.Add(ggAttackLabel);
                    ggAttackModeBox = new ComboBox { Left = 2, Top = 344, Width = 148, DropDownStyle = ComboBoxStyle.DropDownList };
                    ggAttackModeBox.Items.AddRange(new object[] { "Laser", "Kamikaze", "Ambos" });
                    ggAttackModeBox.SelectedIndex = botSettings.AttackMode switch { "laser" => 0, "kami" => 1, _ => 2 };
                    ggAttackModeBox.SelectedIndexChanged += (s, e) => { botSettings.AttackMode = ggAttackModeBox.SelectedIndex switch { 0 => "laser", 1 => "kami", _ => "both" }; ScheduleSettingsSave(); };
                    settings.Controls.Add(ggAttackModeBox);
                    ggFinalFormationLabel = new Label { Left = 2, Top = 376, Width = 148,
                        Height = 20, Text = "Formacion final de GG:" };
                    settings.Controls.Add(ggFinalFormationLabel);
                    ggFinalFormationBox = new ComboBox { Left = 2, Top = 398, Width = 148,
                        DropDownStyle = ComboBoxStyle.DropDownList };
                    ggFinalFormationBox.Items.Add(new FormationChoice("", "Desactivado"));
                    foreach (string id in Api.User.AvailableDroneFormations)
                        ggFinalFormationBox.Items.Add(new FormationChoice(id, FormationName(id)));
                    if (!string.IsNullOrWhiteSpace(botSettings.GgFinalNpcFormation) &&
                        !Api.User.AvailableDroneFormations.Contains(botSettings.GgFinalNpcFormation, StringComparer.Ordinal))
                        ggFinalFormationBox.Items.Add(new FormationChoice(botSettings.GgFinalNpcFormation,
                            FormationName(botSettings.GgFinalNpcFormation) + " (sin detectar)"));
                    var formationItem = ggFinalFormationBox.Items.Cast<FormationChoice>().FirstOrDefault(x =>
                        x.Id == botSettings.GgFinalNpcFormation);
                    ggFinalFormationBox.SelectedItem = formationItem ?? ggFinalFormationBox.Items[0];
                    ggFinalFormationBox.SelectedIndexChanged += (sender, args) =>
                    {
                        if (settingFormationControls) return;
                        botSettings.GgFinalNpcFormation =
                            (ggFinalFormationBox.SelectedItem as FormationChoice)?.Id ?? "";
                        ScheduleSettingsSave();
                    };
                    settings.Controls.Add(ggFinalFormationBox);
                    if (ggProgress != null) ggProgress.Text = "Estado bot: Sin iniciar";
                    ggRefreshTimer?.Start();
                    _ = LoadGates();
                }
                if (selected != "GGs" && selected != "NPC Killer" && selected != "Collect") settings.Controls.Add(new Label { Left = 2, Top = 200, Width = 150, Height = 65,
                    ForeColor = SystemColors.GrayText,
                    Text = "Opciones visuales. La ejecucion se implementara despues." });
            }
            moduleSelector.SelectedIndexChanged += (s, e) => { botSettings.SelectedModule = moduleSelector.SelectedItem?.ToString() ?? "Collect"; ScheduleSettingsSave(); ShowSettings(); };
            int savedModuleIndex = moduleSelector.Items.IndexOf(botSettings.SelectedModule);
            moduleSelector.SelectedIndex = savedModuleIndex < 0 ? 0 : savedModuleIndex;
        }

        private async Task LoadGates()
        {
            if (sessionClosed || IsDisposed || moduleSelector?.SelectedItem?.ToString() != "GGs" || ggRefreshBusy) return;
            ggRefreshBusy = true;
            try
            {
                var cookie = Api.User.UserData.PHPSESSID;
                if (string.IsNullOrWhiteSpace(cookie)) throw new InvalidOperationException("PHPSESSID no disponible");
                ggApi ??= new GalaxyGateService(cookie);
                var data = await ggApi.ReadAsync();
                if (sessionClosed || IsDisposed || ggList == null || ggList.IsDisposed ||
                    moduleSelector?.SelectedItem?.ToString() != "GGs") return;
                int previousId = ggList.SelectedItem is GalaxyGateInfo current ? current.id : -1;
                string desiredGateName = previousId < 0 ? botSettings.GalaxyGateName : "";
                gatesData = data.gates;
                currentSpinCost = data.spinCost;
                if (ggAccountSummary != null)
                    ggAccountSummary.Text = "Uri: " + data.uridium.ToString("N0");
                ggList.Items.Clear();
                foreach (var gate in gatesData) ggList.Items.Add(gate);
                ggList.DisplayMember = "name";
                int index = gatesData.FindIndex(g => g.id == previousId);
                if (index < 0 && !string.IsNullOrWhiteSpace(desiredGateName))
                    index = gatesData.FindIndex(g => string.Equals(g.name, desiredGateName, StringComparison.OrdinalIgnoreCase));
                if (ggList.Items.Count > 0) ggList.SelectedIndex = index < 0 ? 0 : index;
                DisplayGate();
            }
            catch (Exception ex)
            {
                if (ggDetails != null && !ggDetails.IsDisposed) ggDetails.Text = "GGs: " + ex.Message;
            }
            finally
            {
                ggRefreshBusy = false;
            }
        }

        private void DisplayGate()
        {
            if (ggDetails == null || ggList?.SelectedItem is not GalaxyGateInfo gate) return;
            bool canSpin = !gate.prepared && gate.partsOwned < gate.maxParts;
            if (spinsLabel != null)
            {
                spinsLabel.Visible = canSpin;
                spinsLabel.Text = currentSpinCost > 0 ? "Spins · coste: " + currentSpinCost : "Spins:";
            }
            if (spinsBox != null) spinsBox.Visible = canSpin;
            // Compact the GG controls when spins are not applicable.
            // Visible spin controls retain their space; otherwise the laser,
            // attack and final-formation selectors move directly below GG info.
            int offset = canSpin ? 0 : -55;
            if (ggAmmoLabel != null) ggAmmoLabel.Top = 268 + offset;
            if (ggAmmoBox != null) ggAmmoBox.Top = 290 + offset;
            if (ggAttackLabel != null) ggAttackLabel.Top = 322 + offset;
            if (ggAttackModeBox != null) ggAttackModeBox.Top = 344 + offset;
            if (ggFinalFormationLabel != null) ggFinalFormationLabel.Top = 376 + offset;
            if (ggFinalFormationBox != null) ggFinalFormationBox.Top = 398 + offset;
            ggDetails.Text = "Piezas: " + gate.partsOwned + "/" + gate.maxParts
                + "\nVidas: " + gate.lives
                + "\nPreparada: " + (gate.prepared ? "Si" : "No")
                + "\nOleada: " + gate.wave
                + "\nRonda final: " + (GalaxyGateFinalWaves.TryGet(gate.name, out int verifiedFinal)
                    ? verifiedFinal.ToString() : "No verificada")
                + "\nVida extra: " + (gate.lifeQuote?.available == true ? gate.lifeQuote.price?.ToString() ?? "Disponible" : "No disponible");
        }

        private async void StartSelectedModule()
        {
            if (botStarted || botStartPending || sessionClosed) return;
            string? module = moduleSelector?.SelectedItem?.ToString();
            // Validate selections before attempting a TCP connection.
            if (module == "Collect" &&
                collectMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map)
            {
                MessageBox.Show("Selecciona un mapa para Collect.");
                return;
            }
            if (module == "NPC Killer" &&
                npcMapBox?.SelectedItem is not FunnyProject.Navigation.MapGraph.Map)
            {
                MessageBox.Show("Selecciona un mapa para NPC Killer.");
                return;
            }
            if (module == "GGs" &&
                (ggList?.SelectedItem is not GalaxyGateInfo || spinsBox == null))
            {
                MessageBox.Show("Selecciona una GG y los spins.");
                return;
            }
            if (module != "Collect" && module != "NPC Killer" && module != "GGs")
            {
                MessageBox.Show("Modulo pendiente de implementar.");
                return;
            }

            if (Api.User.PacketManager == null)
            {
                MessageBox.Show("Gestor TCP no disponible.");
                return;
            }
            if (travel == null)
            {
                try { travel = new MapTravelModule(Api); }
                catch (Exception ex)
                {
                    MessageBox.Show("La navegacion entre mapas no esta disponible: " + ex.Message);
                    return;
                }
            }

            // The UI selections are copied into the settings, then the module is
            // built by Core exactly as on the server.
            int galaxyGateId = 0;
            if (module == "Collect")
                botSettings.CollectMapId = ((FunnyProject.Navigation.MapGraph.Map)collectMapBox!.SelectedItem!).id;
            else if (module == "NPC Killer")
                botSettings.NpcKillerMapId = ((FunnyProject.Navigation.MapGraph.Map)npcMapBox!.SelectedItem!).id;
            else
            {
                var selected = (GalaxyGateInfo)ggList!.SelectedItem!;
                galaxyGateId = selected.id;
                botSettings.GalaxyGateName = selected.name;
                botSettings.Spins = new[] { 1, 5, 10, 100 }[Math.Max(0, spinsBox!.SelectedIndex)];
                botSettings.Laser = ggSelectedAmmo;
            }
            ScheduleSettingsSave();
            string cookie = Api.User.UserData.PHPSESSID ?? "";
            var built = FunnyProject.Modules.BotModuleFactory.Create(module, botSettings, travel!,
                () => ggApi ??= new GalaxyGateService(cookie), cookie, galaxyGateId);
            if (built.Module == null)
            {
                MessageBox.Show(built.Error);
                return;
            }
            var botModule = built.Module;
            collector = botModule as FunnyProject.Modules.CollectBotModule;
            npcKiller = botModule as FunnyProject.Modules.NpcKillerBotModule;
            if (botModule is FunnyProject.Modules.GalaxyGatesBotModule gg)
                gg.GatesChanged += () => RunOnUi(() => _ = LoadGates());

            // A stopped bot does not reconnect on its own: BotRunner restores TCP
            // before launching any work and enables auto-reconnect only once running.
            bool reconnecting = Api.User.PacketManager.Connected != true;
            if (reconnecting && botButton != null)
            {
                botButton.Enabled = false;
                botButton.Text = "Reconectando TCP...";
            }
            bool started;
            try
            {
                started = await Api.Runner.StartAsync(botModule);
            }
            catch (Exception ex)
            {
                if (!sessionClosed && !IsDisposed)
                    MessageBox.Show("No se pudo reconectar TCP: " + ex.Message);
                return;
            }
            finally
            {
                if (!IsDisposed && botButton != null && !botButton.IsDisposed)
                {
                    botButton.Enabled = true;
                    botButton.Text = botStarted ? "Parar bot" : "Iniciar bot";
                }
            }
            // The module may already have finished (and the UI been reset) here.
            if (!started || !botStarted || sessionClosed || IsDisposed) return;
            if (module == "Collect" && collectMapBox != null) collectMapBox.Enabled = false;
            if (module == "NPC Killer" && npcMapBox != null) npcMapBox.Enabled = false;
            if (module == "GGs")
            {
                if (ggList != null) ggList.Enabled = false;
                if (spinsBox != null) spinsBox.Enabled = false;
            }
        }

        private void RunOnUi(System.Action action)
        {
            if (sessionClosed || IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        private void StopSelectedModule() => Api.Runner.Stop();

        private void OnRunnerStatusChanged(object? sender, string status)
        {
            if (sessionClosed || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => OnRunnerStatusChanged(sender, status)); return; }
            if (ggProgress != null && !ggProgress.IsDisposed) ggProgress.Text = status;
        }

        private void OnRunnerStateChanged(object? sender, FunnyProject.Runtime.BotRunnerState state)
        {
            if (state is not (FunnyProject.Runtime.BotRunnerState.Stopped or
                              FunnyProject.Runtime.BotRunnerState.Faulted)) return;
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => OnRunnerStateChanged(sender, state)); return; }
            OnBotStopped();
        }

        // UI and per-run state reset after the runner stops, whatever the cause
        // (button, Telegram /stopbot, module finished or session closed).
        private void OnBotStopped()
        {
            formations.Reset();
            if (ggProgress != null && !ggProgress.IsDisposed)
                ggProgress.Text = "Estado bot: Detenido";
            collector = null;
            npcKiller = null;
            if (npcMapBox != null && !npcMapBox.IsDisposed) npcMapBox.Enabled = true;
            if (collectMapBox != null && !collectMapBox.IsDisposed) collectMapBox.Enabled = true;
            if (ggList != null && !ggList.IsDisposed) ggList.Enabled = true;
            if (spinsBox != null && !spinsBox.IsDisposed) spinsBox.Enabled = true;
            if (travel?.StopOnArrival == true)
            {
                travel.Stop();
                travel.StopOnArrival = false;
            }
            if (botButton != null && !botButton.IsDisposed) botButton.Text = "Iniciar bot";
        }

        private void SetupNavigationUi()
        {
            try
            {
                travel = new MapTravelModule(Api);
                var autoInvite = new CheckBox { Left = 7, Top = 35, Width = 150, Text = "Auto aceptar grupo", Checked = botSettings.AutoAcceptInvites };
                travel.AutoAcceptInvites = autoInvite.Checked;
                autoInvite.CheckedChanged += (s, e) => { if (travel != null) travel.AutoAcceptInvites = autoInvite.Checked; botSettings.AutoAcceptInvites = autoInvite.Checked; ScheduleSettingsSave(); };
                var inviterId = new TextBox { Left = 7, Top = 68, Width = 148, PlaceholderText = "ID remitente (vacio = todos)", Text = botSettings.AllowedInviterId?.ToString() ?? "" };
                inviterId.TextChanged += (s, e) =>
                {
                    if (travel == null) return;
                    string value = inviterId.Text.Trim();
                    if (value.Length == 0)
                    {
                        travel.AllowedInviterId = null;
                        botSettings.AllowedInviterId = null;
                        ScheduleSettingsSave();
                        inviterId.BackColor = SystemColors.Window;
                    }
                    else if (int.TryParse(value, out int id) && id > 0)
                    {
                        travel.AllowedInviterId = id;
                        botSettings.AllowedInviterId = id;
                        ScheduleSettingsSave();
                        inviterId.BackColor = SystemColors.Window;
                    }
                    else
                    {
                        // Invalid input never removes the previous restriction.
                        // Reject all invitations until the filter is corrected.
                        travel.AllowedInviterId = -1;
                        inviterId.BackColor = Color.MistyRose;
                    }
                };
                travel.AllowedInviterId = botSettings.AllowedInviterId;
                var copySidButton = new Button
                {
                    Left = 7, Top = 405, Width = 148, Height = 26,
                    Text = "Copiar SID"
                };
                copySidButton.Click += (s, e) =>
                {
                    string? sid = Api.User.UserData.SID;
                    if (string.IsNullOrEmpty(sid))
                    {
                        MessageBox.Show("SID no disponible en esta instancia.");
                        return;
                    }
                    try
                    {
                        Clipboard.SetText(sid);
                        // Never print or display the SID.
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        MessageBox.Show("No se pudo copiar el SID al portapapeles.");
                    }
                };
                copySidButton.Top = 60;
                StatPage.Controls.Add(copySidButton);
                var copyWebSessionButton = new Button
                {
                    Left = 7, Top = 95, Width = 148, Height = 26,
                    Text = "Copiar PHPSESSID"
                };
                copyWebSessionButton.Click += (s, e) =>
                {
                    var value = Api.User.UserData.PHPSESSID;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        MessageBox.Show("PHPSESSID no disponible.");
                        return;
                    }
                    try { Clipboard.SetText(value); }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        MessageBox.Show("No se pudo copiar PHPSESSID.");
                    }
                };
                StatPage.Controls.Add(copyWebSessionButton);
                // TCP reconnect is automatic only while a bot module is running.
                // No user toggle: stopped instances must stay disconnected.
                // PacketManager may still be null if the session is being created.
                _ = ApplyAutoReconnectWhenReadyAsync();
                GroupPage.Controls.Add(autoInvite);
                GroupPage.Controls.Add(inviterId);

                FormClosed += (s, e) => { sessionClosed = true; travel?.Dispose(); Api.User.PacketManager?.Disconnect(); };
            }
            catch (Exception ex) { Console.WriteLine("[Navigation] " + ex.Message); }
        }
        private async Task Render()
        {
            while (!sessionClosed && !IsDisposed)
            {
                try
                {
                    var bit = new Bitmap(MinimapPanel.Width, MinimapPanel.Height);
                    using (Graphics g = Graphics.FromImage(bit))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        RenderManager rd = new RenderManager(g, Api, MinimapPanel.Width, MinimapPanel.Height)
                        {
                            PreferredZones = MinimapZones(),
                            ZonePreview = MinimapZonePreview()
                        };
                        await rd.Draw();
                        var oldimage = MinimapPanel.BackgroundImage;


                        if (MinimapPanel.InvokeRequired)
                        {
                            Invoke((MethodInvoker)delegate
                            {
                                MinimapPanel.BackgroundImage = bit;
                            });
                        }
                        else
                        {
                            MinimapPanel.BackgroundImage = bit;
                        }

                        await Task.Delay(50);
                        if (oldimage != null)
                        {
                            oldimage.Dispose();
                            rd.Dispose();
                        }
                        if (UriLbl.InvokeRequired)
                        {
                            Invoke((MethodInvoker)delegate
                            {
                                UriLbl.Text = $"Uri: {Api.User.Statistics.CollectedUridium}/{Api.User.Statistics.Uridium}";
                            });
                        }
                        else
                        {
                            UriLbl.Text = $"Uri: {Api.User.Statistics.CollectedUridium}/{Api.User.Statistics.Uridium}";
                        }

                    }
                }
                catch (Exception)
                {
                    await Task.Delay(50);
                   
                }

            }



        }

        private void MinimapPanel_MouseDown(object sender, MouseEventArgs e)
        {
            if (zoneDrawing)
            {
                // Drawing preferred zones: drag to add, click to delete.
                zoneDragStart = e.Location;
                zoneDragEnd = e.Location;
                return;
            }
            if (NavigationMovement == null) return;
            var mapId = Api.User.UserData.MapId;
            var map = travel != null && travel.Graph.Maps.TryGetValue(mapId, out var bounds) ? bounds : null;
            var ConvertX = ((double)MinimapPanel.Width / (map?.width ?? 21000));
            var ConvertY = ((double)MinimapPanel.Height / (map?.height ?? 13100));

            NavigationMovement.FlyToCorndinates((int)((double)e.X / ConvertX), (int)((double)e.Y / ConvertY));
            
        }

    }
}
