namespace ServiceLib.Models.Configs;

public class Config
{
    #region property

    public string IndexId { get; set; }
    public string SubIndexId { get; set; }

    #endregion property

    #region other entities

    public CoreBasicItem CoreBasicItem { get; set; }
    public TunModeItem TunModeItem { get; set; }
    public KcpItem KcpItem { get; set; }
    public GrpcItem GrpcItem { get; set; }
    public RoutingBasicItem RoutingBasicItem { get; set; }
    public GUIItem GuiItem { get; set; }
    public MsgUIItem MsgUIItem { get; set; }
    public UIItem UiItem { get; set; }
    public ConstItem ConstItem { get; set; }
    public SpeedTestItem SpeedTestItem { get; set; }
    public Mux4RayItem Mux4RayItem { get; set; }
    public Mux4SboxItem Mux4SboxItem { get; set; }
    public HysteriaItem HysteriaItem { get; set; }
    public ClashUIItem ClashUIItem { get; set; }
    public SystemProxyItem SystemProxyItem { get; set; }
    public WebDavItem WebDavItem { get; set; }
    public CheckUpdateItem CheckUpdateItem { get; set; }
    public Fragment4RayItem? Fragment4RayItem { get; set; }
    public List<InItem> Inbound { get; set; }
    public List<KeyEventItem> GlobalHotkeys { get; set; }
    public List<CoreTypeItem> CoreTypeItem { get; set; }
    public SimpleDNSItem SimpleDNSItem { get; set; }
    public HappyEyeballs4RayItem HappyEyeballs4RayItem { get; set; }
    public ZapretItem ZapretItem { get; set; }
    public TgWsProxyItem TgWsProxyItem { get; set; }
    public WarpItem WarpItem { get; set; }
    public BlockMapItem BlockMapItem { get; set; }
    public List<ConnectionRule> ConnectionRules { get; set; }
    public List<ConnectionRuleProfile> ConnectionRuleProfiles { get; set; }

    /// <summary>Block internet if the VPN core dies while connected.</summary>
    public bool KillSwitchEnabled { get; set; }

    /// <summary>Switch to another server of the group when the active one stops passing traffic.</summary>
    public bool AutoFailover { get; set; }

    /// <summary>Reload the core after sleep or a network change (null = on).</summary>
    public bool? ResumeReconnect { get; set; }

    /// <summary>While a game runs, never switch servers on the fly (null = on).</summary>
    public bool? GameMode { get; set; }

    /// <summary>Extra game exe names (without path) on top of the built-in list.</summary>
    public List<string>? GameProcesses { get; set; }

    /// <summary>Test both cores on a server the first time it is picked and keep the better one (null = on).</summary>
    public bool? AutoPickCore { get; set; }

    /// <summary>
    /// First-run wizard: null = not shown yet (shown when there are no servers), false = in progress
    /// (it restarted the app as administrator and shows again), true = closed, never shown again.
    /// </summary>
    public bool? FirstRunDone { get; set; }

    /// <summary>Password-protect the local proxy port in TUN mode (null = on).</summary>
    public bool? ProtectLocalProxy { get; set; }

    /// <summary>When set, every rule (except Block) uses this mode: a quick "all via VPN / all via zapret" switch.</summary>
    public EConnectionMode? ConnectionRulesOverride { get; set; }

    #endregion other entities
}
