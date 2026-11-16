using FunnyProject;
using FunnyProject.Logic;
using FunnyProject.Modules;
using FunnyProject.Runtime;
using FunnyProject.Services;
using FunnyProject.Sessions;
using FunnyProject.Telegram;

namespace PacketBot.Server;

// One headless game instance as seen by Telegram. Module parameters (map,
// box types, NPCs) come from the account's saved BotSettings (same file as the
// Windows app), read again on every start so edits apply without restarting.
public sealed class ServerBotInstance : IBotInstance, IConfigurableBotInstance, IDisposable
{
    private readonly MapTravelModule travel;
    // Last module started in this session ("Collect" / "NPC Killer"). /startbot
    // reuses it, so a bot started with --start collect restarts as Collect even
    // if the settings file (shared with Windows) has another module selected.
    private string? lastModule;
    // NPC Killer currently or last started, for live target changes.
    private NpcKillerBotModule? npcKiller;
    private CollectBotModule? collect;
    private GalaxyGatesBotModule? galaxyGates;
    // Tests replace the Galaxy Gates HTTP API.
    internal Func<string, IGalaxyGateApi> GalaxyGateApiFactory { get; set; } = cookie => new GalaxyGateService(cookie);
    private IGalaxyGateApi? galaxyGateApi;

    private readonly ServerSettingsAccess settings;
    private readonly CancellationTokenSource background = new();

    public ServerBotInstance(WordGalaxySession session, LogTag? tag = null)
    {
        Session = session;
        Tag = tag ?? new LogTag(session.UserId.ToString());
        this.settings = new ServerSettingsAccess(session.Api.User.Server, session.Api.User.Username);
        var settings = LoadSettings();
        // Navigation also accepts group invites, as in the Windows window.
        travel = new MapTravelModule(session.Api)
        {
            AutoAcceptInvites = settings.AutoAcceptInvites,
            AllowedInviterId = settings.AllowedInviterId
        };
        // Same periodic work as the Windows window timers, without a window.
        Formations = new FormationService(session.Api, this.settings);
        BoosterPolling = new BoosterPollingService(session.Api);
        BoosterAutoBuy = new BoosterAutoBuyService(session.Api, this.settings);
        EnergyLeech = new EnergyLeechService(session.Api, this.settings);
        var token = background.Token;
        _ = Task.Run(() => { LogTag.Enter(Tag); return Formations.RunAsync(token); });
        _ = Task.Run(() => { LogTag.Enter(Tag); return BoosterPolling.RunAsync(token); });
        _ = Task.Run(() => { LogTag.Enter(Tag); return BoosterAutoBuy.RunAsync(token); });
        _ = Task.Run(() => { LogTag.Enter(Tag); return EnergyLeech.RunAsync(token); });
    }

    // Log label of this account (its ID), shared with the TCP tasks of the session.
    public LogTag Tag { get; }

    internal FormationService Formations { get; }
    internal BoosterPollingService BoosterPolling { get; }
    internal BoosterAutoBuyService BoosterAutoBuy { get; }
    internal EnergyLeechService EnergyLeech { get; }

    public WordGalaxySession Session { get; }
    private User User => Session.Api.User;
    private BotRunner Runner => Session.Api.Runner;

    public object Key => Session;
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(User.UserData.InGameName) ? User.UserData.InGameName.Trim()
        : !string.IsNullOrWhiteSpace(Session.AccountName) ? Session.AccountName!
        : Session.UserId.ToString();
    public string PhpSessionId => Session.PhpSessionId;
    public bool BotRunning => Runner.IsRunning;
    public bool BotActive => Runner.IsActive;
    public BotStopReason LastStopReason => Runner.LastStopReason;
    public int BotStopCount => Runner.StopCount;
    public string? LastError => Runner.LastError?.Message;
    public bool TcpConnected => Session.PacketManager.Connected;
    public int MapId => User.UserData.MapId;
    public string SettingsPath => settings.Path;

    public string DetailedStatus
    {
        get
        {
            string module = Runner.ModuleName ?? lastModule ?? LoadSettings().SelectedModule;
            string activity = BotRunning
                ? (string.IsNullOrWhiteSpace(Runner.Status) ? "En ejecucion" : Runner.Status)
                : Runner.State == BotRunnerState.Faulted ? "Error: " + Runner.LastError?.Message
                : "Sin iniciar";
            var settings = LoadSettings();
            var gg = galaxyGates?.Options;
            return BotStatusText.Details(BotRunning, User.ActionState.Current.ToString(), module, activity,
                    gg?.GateName ?? settings.GalaxyGateName, gg?.AttackMode ?? settings.AttackMode,
                    gg?.Ammunition ?? settings.Laser, User.ShipInfo.Hp, User.ShipInfo.MaxHp) +
                "\n" + Supervision(DateTime.UtcNow);
        }
    }

    // Unattended operation: is the game still talking to us, how often TCP
    // dropped and the last error. Shown by /status and in the periodic log.
    internal string Supervision(DateTime nowUtc)
    {
        var tcp = Session.PacketManager;
        string text = "Ultimo paquete: " + (tcp.Connected && tcp.LastReceivedUtc != default
            ? "hace " + Ago(nowUtc - tcp.LastReceivedUtc) : "-");
        text += "\nReconexiones TCP: " + Math.Max(0, tcp.ConnectionCount - 1);
        if (tcp.LastCloseUtc is { } closed)
            text += "\nUltima desconexion: hace " + Ago(nowUtc - closed) + " (" + tcp.LastCloseReason + ")";
        if (LastError is { } error)
            text += "\nUltimo error: " + error.Replace("\r", "").Replace("\n", " | ");
        return text;
    }

    // One line for the journal every few minutes.
    internal string Heartbeat(DateTime nowUtc) =>
        "[Estado] TCP " + (TcpConnected ? "conectado" : "desconectado") +
        " | Bot " + (BotRunning ? (Runner.ModuleName ?? "?") + " en marcha" : Runner.State.ToString()) +
        " | " + Supervision(nowUtc).Replace("\n", " | ");

    internal static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 120) return (int)span.TotalSeconds + " s";
        if (span.TotalMinutes < 120) return (int)span.TotalMinutes + " min";
        if (span.TotalHours < 48) return (int)span.TotalHours + " h";
        return (int)span.TotalDays + " d";
    }

    public string NpcTargetsStatus(int instanceNumber)
    {
        var settings = LoadSettings();
        int mapId = settings.NpcKillerMapId;
        var selected = settings.NpcKillerTargetsByMap.TryGetValue(mapId, out var names)
            ? names : new List<string>();
        return BotStatusText.NpcTargets(DisplayName, instanceNumber, mapId, settings.NpcKillerLaser,
            selected, Session.Api.Observations.NpcNames(mapId));
    }

    // /npc N ...: saved to the settings file and applied to a running NPC Killer.
    public string ChangeNpcTargets(int instanceNumber, string argument)
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        var (map, selected, error) = NpcTargetSelection.Change(LoadSettings(), Session.Api.Observations.NpcNames, argument);
        if (map == null)
            return "Selecciona primero el mapa de NPC Killer (no hay mapa guardado en " + SettingsPath + ").";
        if (selected == null) return error!;
        int mapId = map.id;
        settings.Update(s => (s.NpcKillerTargetsByMap ??= new())[mapId] = selected);
        var running = npcKiller;
        bool live = running != null && running.Map.id == mapId && Runner.IsActive && Runner.ModuleName == running.Name;
        if (running != null && running.Map.id == mapId) running.UpdateTargets(selected);
        return NpcTargetSelection.Confirmation(map.name, selected, live);
    }

    private BotSettings LoadSettings() => settings.Read();

    // ---- NPC Killer preferred zones ----

    // /nave N
    public string ShipItemsStatus(int instanceNumber) => User.Items.Text(DisplayName);

    // /zonas N
    public string NpcZonesStatus(int instanceNumber) =>
        PreferredZones.Status(LoadSettings(), DisplayName, instanceNumber);

    // /zona N borrar <numero|todas>, on the NPC Killer map.
    public string ChangeNpcZones(int instanceNumber, string argument)
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        var (change, reply) = PreferredZones.ParseCommand(LoadSettings(), argument);
        if (change == null) return reply;
        settings.Update(change);
        return reply + ApplyZones(LoadSettings().NpcKillerMapId);
    }

    // Zones of the map the ship is on (the one the minimap shows).
    internal IReadOnlyList<PreferredZone> CurrentMapZones() =>
        PreferredZones.For(LoadSettings(), User.UserData.MapId);

    // Web panel: drawn on the minimap, so on the ship's current map.
    internal FunnyProject.Logic.PreferredZoneReply ChangeCurrentMapZones(Func<BotSettings, int, (bool Ok, string Reply)> change)
    {
        if (Session.IsClosed) return new(false, "Instancia cerrada.");
        int mapId = User.UserData.MapId;
        (bool Ok, string Reply) result = (false, "");
        settings.Update(s => result = change(s, mapId));
        return new(result.Ok, result.Reply + (result.Ok ? ApplyZones(mapId) : ""));
    }

    // A running NPC Killer on that map uses the new zones at once.
    private string ApplyZones(int mapId)
    {
        var running = npcKiller;
        if (running == null || running.Map.id != mapId) return "";
        running.UpdateZones(PreferredZones.For(LoadSettings(), mapId));
        return Runner.IsActive && Runner.ModuleName == running.Name ? " Aplicado al bot en marcha." : "";
    }

    // /ajustes N
    public string SettingsStatus(int instanceNumber) =>
        SettingsCommands.Describe(LoadSettings(), DisplayName, instanceNumber,
            User.AvailableDroneFormations, SettingsPath);

    // /ajuste N <clave> <valor>: saved at once; formations and auto-buy apply
    // live (they read the settings every tick), the rest on the next /startbot,
    // except the NPC Killer ammunition, which also changes a running NPC Killer.
    public string ChangeSetting(int instanceNumber, string key, string value)
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        var (change, reply) = SettingsCommands.Parse(key, value, User.AvailableDroneFormations);
        if (change == null) return reply;
        settings.Update(change);
        string k = key.Trim().ToLowerInvariant();
        if (k == "modulo") lastModule = null; // /startbot uses the new module
        if (k.StartsWith("formacion")) Formations.Reset();
        string when = k.StartsWith("formacion") || k == "autocompra" || k == "leech"
            ? " Aplicado."
            : Runner.IsActive ? " Se aplica en el proximo /startbot." : "";
        if (k == "municion_npc" && npcKiller != null && Runner.IsActive && Runner.ModuleName == npcKiller.Name)
        {
            npcKiller.UpdateAmmunition(LoadSettings().NpcKillerLaser);
            when = " Aplicado al bot en marcha.";
        }
        return "Guardado. " + reply + when;
    }

    // Web panel: everything the settings page shows, with the choices.
    internal Web.SettingsView WebSettings()
    {
        var s = LoadSettings();
        List<string> Saved(Dictionary<int, List<string>>? byMap, int id) =>
            byMap != null && byMap.TryGetValue(id, out var v) ? v : new List<string>();
        Web.CheckView[] Checks(IReadOnlyList<string> all, IReadOnlyCollection<string> chosen)
        {
            var set = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);
            return all.Select(n => new Web.CheckView(n, set.Contains(n))).ToArray();
        }
        var boxes = Saved(s.CollectTypesByMap, s.CollectMapId);
        var npcs = Saved(s.NpcKillerTargetsByMap, s.NpcKillerMapId);
        return new Web.SettingsView(
            BotModuleFactory.Normalize(s.SelectedModule),
            s.CollectMapId, Checks(SettingsCommands.BoxList(boxes, Session.Api.Observations.BoxTypes(s.CollectMapId)), boxes),
            s.NpcKillerMapId, s.NpcKillerLaser,
            Checks(NpcTargetSelection.Build(npcs, Session.Api.Observations.NpcNames(s.NpcKillerMapId)), npcs),
            s.NpcKillerEnergyLeech,
            s.GalaxyGateName, s.Spins, s.Laser, s.AttackMode,
            s.RunFormation, s.AttackFormation, s.GgFinalNpcFormation,
            BoosterPurchaseService.Items.Select(i => new Web.CheckView(i.Code,
                s.AutoBuyBoosters != null && s.AutoBuyBoosters.TryGetValue(i.Id, out bool on) && on)).ToArray(),
            s.AutoAcceptInvites, SettingsPath,
            travel.Graph.SortedMaps.Select(m => new Web.OptionView(m.id.ToString(), m.name)).ToArray(),
            BotSettings.LaserAmmunition, SettingsCommands.GateNames, new[] { 1, 5, 10, 100 },
            User.AvailableDroneFormations);
    }

    // Web panel checklists: the full selection instead of numbers to toggle.
    // Only names from the shown list are kept.
    internal string SetChecklist(string kind, IReadOnlyCollection<string> names)
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        var current = LoadSettings();
        bool boxes = kind == "boxes";
        int mapId = boxes ? current.CollectMapId : current.NpcKillerMapId;
        if (!travel.Graph.Maps.TryGetValue(mapId, out var map))
            return boxes ? "Selecciona primero el mapa de Collect." : "Selecciona primero el mapa de NPC Killer.";
        var byMap = boxes ? current.CollectTypesByMap : current.NpcKillerTargetsByMap;
        var saved = byMap != null && byMap.TryGetValue(mapId, out var v) ? v : new List<string>();
        var list = boxes
            ? SettingsCommands.BoxList(saved, Session.Api.Observations.BoxTypes(mapId))
            : NpcTargetSelection.Build(saved, Session.Api.Observations.NpcNames(mapId));
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var selected = list.Where(wanted.Contains).ToList();
        if (boxes)
        {
            settings.Update(s => (s.CollectTypesByMap ??= new())[mapId] = selected);
            if (collect != null && collect.Map.id == mapId) collect.UpdateTypes(selected);
            return "Cajas de Collect en " + map.name + ": " +
                (selected.Count == 0 ? "ninguna" : string.Join(", ", selected)) + ".";
        }
        settings.Update(s => (s.NpcKillerTargetsByMap ??= new())[mapId] = selected);
        if (npcKiller != null && npcKiller.Map.id == mapId) npcKiller.UpdateTargets(selected);
        return NpcTargetSelection.Confirmation(map.name, selected, false);
    }

    // /cajas N
    public string BoxTypesStatus(int instanceNumber)
    {
        var current = LoadSettings();
        int mapId = current.CollectMapId;
        var selected = current.CollectTypesByMap.TryGetValue(mapId, out var types) ? types : new List<string>();
        return SettingsCommands.BoxTypes(DisplayName, instanceNumber, mapId, selected,
            Session.Api.Observations.BoxTypes(mapId));
    }

    // /caja N ...: saved and applied to a running Collect on that map.
    public string ChangeBoxTypes(int instanceNumber, string argument)
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        var current = LoadSettings();
        int mapId = current.CollectMapId;
        if (!travel.Graph.Maps.TryGetValue(mapId, out var map))
            return "Selecciona primero el mapa de Collect: /ajuste " + instanceNumber + " mapa_collect 1-1.";
        var saved = current.CollectTypesByMap.TryGetValue(mapId, out var types) ? types : new List<string>();
        var list = SettingsCommands.BoxList(saved, Session.Api.Observations.BoxTypes(mapId));
        var (selected, error) = NpcTargetSelection.Apply(list, saved, argument, "/cajas", "/caja", "cajas vistas");
        if (selected == null) return error!;
        settings.Update(s => (s.CollectTypesByMap ??= new())[mapId] = selected);
        var running = collect;
        bool live = running != null && running.Map.id == mapId && Runner.IsActive && Runner.ModuleName == running.Name;
        if (running != null && running.Map.id == mapId) running.UpdateTypes(selected);
        return "Cajas de Collect en " + map.name + ": " +
            (selected.Count == 0 ? "ninguna" : string.Join(", ", selected)) + "." +
            (live ? " Aplicado al bot en marcha." : "");
    }

    public string StartBot() => Start(null);

    // moduleOverride: "collect" / "npc" force Collect / NPC Killer (--start);
    // null reuses the last module started in this session, or else the saved one.
    public string Start(string? moduleOverride)
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        if (Runner.IsActive) return "El bot ya esta activo.";
        var settings = LoadSettings();
        string module = moduleOverride != null ? BotModuleFactory.Normalize(moduleOverride)
            : lastModule ?? settings.SelectedModule;
        var built = BotModuleFactory.Create(module, settings, travel,
            () => galaxyGateApi ??= GalaxyGateApiFactory(Session.PhpSessionId), Session.PhpSessionId,
            settingsLocation: SettingsPath);
        if (built.Module == null) return built.Error!;
        if (built.Warning != null) Console.WriteLine("[Bot] Aviso: " + built.Warning);
        var botModule = built.Module;
        module = BotModuleFactory.Normalize(module);
        collect = botModule as CollectBotModule;
        if (botModule is NpcKillerBotModule killer) npcKiller = killer;
        if (botModule is GalaxyGatesBotModule gg)
        {
            galaxyGates = gg;
            gg.FinalFormationSent += formation =>
                Console.WriteLine("[GG] " + DisplayName + ": formacion final " + formation + " enviada.");
        }
        string reply = module switch
        {
            BotModuleFactory.Collect => "Inicio de Collect solicitado.",
            BotModuleFactory.NpcKiller => "Inicio de NPC Killer solicitado.",
            _ => "Inicio del bot solicitado."
        };

        lastModule = module;
        Console.WriteLine("[Bot] " + DisplayName + ": iniciando " + botModule.Name + ".");
        _ = StartInBackgroundAsync(botModule);
        return reply;
    }

    private async Task StartInBackgroundAsync(IBotModule module)
    {
        LogTag.Enter(Tag); // the module and its reconnects log under this account
        try
        {
            if (!await Runner.StartAsync(module))
                Console.WriteLine("[Bot] " + DisplayName + ": el bot ya estaba activo o se paro durante el arranque.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Bot] " + DisplayName + ": no se pudo reconectar TCP: " + ex.Message);
        }
    }

    public string StopBot()
    {
        if (Session.IsClosed) return "Instancia cerrada.";
        if (!Runner.IsActive) return "El bot ya esta parado.";
        Runner.Stop();
        return "Bot detenido.";
    }

    public async Task RestartTcpAsync()
    {
        if (Session.IsClosed)
            throw new InvalidOperationException("La instancia no esta disponible para reiniciar TCP.");
        LogTag.Enter(Tag);
        await Session.PacketManager.RestartTcpAsync();
    }

    public void Dispose()
    {
        background.Cancel();
        travel.Dispose();
    }
}

// Telegram's view of the InstanceManager: one ServerBotInstance per session,
// in opening order. Thread-safe: Telegram and the session worker call it
// concurrently.
public sealed class ServerInstanceHost : IBotInstanceHost, IDisposable
{
    private readonly InstanceManager manager;
    private readonly object sync = new();
    private readonly Dictionary<WordGalaxySession, ServerBotInstance> adapters = new();
    private readonly Dictionary<WordGalaxySession, LogTag> tags = new();

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> problems = new();

    public ServerInstanceHost(InstanceManager manager) => this.manager = manager;

    // Problems that need the user but are not about a running bot (an
    // account that could not be opened). Telegram sends them if enabled.
    public void ReportProblem(string message) => problems.Enqueue(message);

    public bool TryTakeProblem(out string message) => problems.TryDequeue(out message!);

    public InstanceManager Manager => manager;

    public IReadOnlyList<IBotInstance> Instances => Typed;

    public IReadOnlyList<ServerBotInstance> Typed
    {
        get
        {
            var sessions = manager.Instances;
            lock (sync)
            {
                foreach (var gone in adapters.Keys.Where(s => !sessions.Contains(s)).ToArray())
                {
                    adapters[gone].Dispose();
                    adapters.Remove(gone);
                }
                foreach (var gone in tags.Keys.Where(s => !sessions.Contains(s)).ToArray())
                    tags.Remove(gone);
                return sessions.Where(s => !s.IsClosed).Select(s =>
                {
                    if (!adapters.TryGetValue(s, out var adapter))
                        adapters[s] = adapter = new ServerBotInstance(s, tags.GetValueOrDefault(s));
                    return adapter;
                }).ToArray();
            }
        }
    }

    // tag: log label of the caller; renamed to the account ID once known.
    public async Task<ServerBotInstance> OpenAsync(string phpSessionId, CancellationToken token, LogTag? tag = null)
    {
        tag ??= new LogTag("cuenta");
        LogTag.Enter(tag); // the TCP tasks of the new session inherit it
        var session = await manager.OpenAsync(phpSessionId, token);
        tag.Name = session.UserId.ToString();
        lock (sync) tags[session] = tag;
        return Typed.First(i => i.Session == session);
    }

    // The replacement session starts with the bot stopped, as on Windows.
    public async Task RestartInstanceAsync(IBotInstance instance)
    {
        var old = (ServerBotInstance)instance;
        LogTag.Enter(old.Tag);
        await old.Session.Api.Runner.StopAsync(TimeSpan.FromSeconds(5));
        var session = await manager.RestartAsync(old.Session.UserId);
        lock (sync) tags[session] = old.Tag;
        _ = Typed; // releases the old adapter and creates the new one
    }

    public async Task StopAllAsync(TimeSpan timeout)
    {
        await Task.WhenAll(Typed.Select(i => i.Session.Api.Runner.StopAsync(timeout)));
    }

    public void Dispose()
    {
        lock (sync)
        {
            foreach (var adapter in adapters.Values) adapter.Dispose();
            adapters.Clear();
        }
        manager.Dispose();
    }
}
