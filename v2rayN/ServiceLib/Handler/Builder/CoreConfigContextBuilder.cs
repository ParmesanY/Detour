namespace ServiceLib.Handler.Builder;

public record CoreConfigContextBuilderResult(CoreConfigContext Context, NodeValidatorResult ValidatorResult)
{
    public bool Success => ValidatorResult.Success;
}

/// <summary>
///     Holds the results of a full context build, including the main context and an optional
///     pre-socks context (e.g. for TUN protection or pre-socks chaining).
/// </summary>
public record CoreConfigContextBuilderAllResult(
    CoreConfigContextBuilderResult MainResult,
    CoreConfigContextBuilderResult? PreSocksResult)
{
    /// <summary>True only when both the main result and (if present) the pre-socks result succeeded.</summary>
    public bool Success => MainResult.Success && (PreSocksResult?.Success ?? true);

    /// <summary>
    ///     Merges all errors and warnings from the main result and the optional pre-socks result
    ///     into a single <see cref="NodeValidatorResult" /> for unified notification.
    /// </summary>
    public NodeValidatorResult CombinedValidatorResult => new(
        [.. MainResult.ValidatorResult.Errors, .. PreSocksResult?.ValidatorResult.Errors ?? []],
        [.. MainResult.ValidatorResult.Warnings, .. PreSocksResult?.ValidatorResult.Warnings ?? []]);
}

public class CoreConfigContextBuilder
{
    /// <summary>
    ///     Builds a <see cref="CoreConfigContext" /> for the given node, resolves its proxy map,
    ///     and processes outbound nodes referenced by routing rules.
    /// </summary>
    /// <param name="customMain">For the pre-core in front of a custom profile: that profile. Its rules may
    /// then pick other custom profiles as their servers (<see cref="CustomConfigRuleServers"/>).</param>
    public static async Task<CoreConfigContextBuilderResult> Build(Config config, ProfileItem node, ProfileItem? customMain = null)
    {
        var runCoreType = AppManager.Instance.GetCoreType(node, node.ConfigType);
        var coreType = runCoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
        var context = new CoreConfigContext
        {
            Node = node,
            RunCoreType = runCoreType,
            AllProxiesMap = [],
            AppConfig = config,
            FullConfigTemplate = await AppManager.Instance.GetFullConfigTemplateItem(coreType),
            IsTunEnabled = config.TunModeItem.EnableTun,
            SimpleDnsItem = config.SimpleDNSItem,
            ProtectDomainList = [],
            RawDnsItem = await AppManager.Instance.GetDNSItem(coreType),
            RoutingItem = ConnectionRulesManager.Instance.ApplyToRouting(config, await ConfigHandler.GetDefaultRouting(config)),
            IsWindows = Utils.IsWindows(),
            IsMacOS = Utils.IsMacOS(),
            IsLinux = Utils.IsLinux(),
            HasGlobalIPv6Address = Utils.HasGlobalIPv6Address(),
            ProtectCoreTypeList = config.TunModeItem.EnableTun ? [ECoreType.Xray, ECoreType.sing_box] : []
        };
        var validatorResult = NodeValidatorResult.Empty();
        var (actNode, nodeValidatorResult) = await ResolveNodeAsync(context, node);
        if (!nodeValidatorResult.Success)
        {
            return new CoreConfigContextBuilderResult(context, nodeValidatorResult);
        }
        context = context with { Node = actNode };
        validatorResult.Warnings.AddRange(nodeValidatorResult.Warnings);
        if (!(context.RoutingItem?.RuleSet.IsNullOrEmpty() ?? true))
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(context.RoutingItem?.RuleSet) ?? [];
            foreach (var ruleItem in rules.Where(ruleItem =>
                         ruleItem.Enabled && !Global.OutboundTags.Contains(ruleItem.OutboundTag)))
            {
                if (ruleItem.OutboundTag.IsNullOrEmpty())
                {
                    validatorResult.Warnings.Add(string.Format(ResUI.MsgRoutingRuleEmptyOutboundTag, ruleItem.Remarks));
                    ruleItem.OutboundTag = Global.ProxyTag;
                    continue;
                }
                if (ruleItem.OutboundTag == Global.WarpOutboundRemarks)
                {
                    // WARP rules: the WARP page's local SOCKS proxy; through the VPN while it isn't running.
                    if (WarpProxyManager.Instance.IsRunning)
                    {
                        context.AllProxiesMap[$"remark:{ruleItem.OutboundTag}"] = WarpProxyManager.LocalProxyNode(config);
                    }
                    else
                    {
                        ruleItem.OutboundTag = Global.ProxyTag;
                    }
                    continue;
                }
                if (ruleItem.OutboundTag == Global.RecordsTag)
                {
                    // Windows zapret rules in "TLS records" mode: a direct outbound of the core itself.
                    continue;
                }
                if (ruleItem.OutboundTag == Global.ZapretOutboundRemarks)
                {
                    // macOS/Linux zapret rules: the tpws SOCKS proxy; direct while it isn't running (as winws off).
                    if (ZapretManager.Instance.IsRunning)
                    {
                        context.AllProxiesMap[$"remark:{ruleItem.OutboundTag}"] = ZapretManager.TpwsProxyNode(config);
                    }
                    else
                    {
                        ruleItem.OutboundTag = Global.DirectTag;
                    }
                    continue;
                }
                ProfileItem? ruleOutboundNode;
                if (ruleItem.OutboundTag.StartsWith(ServiceServerResolver.TagPrefix, StringComparison.Ordinal))
                {
                    // A connection rule with its own server ("servers per service").
                    if (actNode.ConfigType == EConfigType.Custom || config.EmergencyItem?.Active == true)
                    {
                        // The custom config runs as is; its pre-core context resolves the rule servers.
                        ruleItem.OutboundTag = Global.ProxyTag;
                        continue;
                    }
                    var customMainXray = customMain != null && AppManager.Instance.GetCoreType(customMain, customMain.ConfigType) == ECoreType.Xray;
                    if (context.RunCoreType == ECoreType.sing_box && ServiceServerResolver.IsList(ruleItem.OutboundTag))
                    {
                        // "= A; B": every server goes into the config behind a selector; RuleServerFailover
                        // moves it down the list when the current one dies.
                        await RegisterServerListAsync(context, ruleItem.OutboundTag, customMain ?? actNode, customMainXray);
                        continue;
                    }
                    ruleOutboundNode = await ServiceServerResolver.ResolveAsync(ruleItem.OutboundTag, customMain ?? actNode, customMainXray);
                    if (ruleOutboundNode == null)
                    {
                        ruleItem.OutboundTag = Global.ProxyTag;
                        continue;
                    }
                    if (ruleOutboundNode.ConfigType == EConfigType.Custom)
                    {
                        context.AllProxiesMap[$"remark:{ruleItem.OutboundTag}"] = await CustomRuleServerNodeAsync(context, ruleOutboundNode);
                        continue;
                    }
                }
                else
                {
                    ruleOutboundNode = await AppManager.Instance.GetProfileItemViaRemarks(ruleItem.OutboundTag);
                }
                if (ruleOutboundNode == null)
                {
                    validatorResult.Warnings.Add(string.Format(ResUI.MsgRoutingRuleOutboundNodeNotFound,
                        ruleItem.Remarks, ruleItem.OutboundTag));
                    ruleItem.OutboundTag = Global.ProxyTag;
                    continue;
                }

                var (actRuleNode, ruleNodeValidatorResult) = await ResolveNodeAsync(context, ruleOutboundNode, false);
                validatorResult.Warnings.AddRange(ruleNodeValidatorResult.Warnings.Select(w =>
                    string.Format(ResUI.MsgRoutingRuleOutboundNodeWarning, ruleItem.Remarks, ruleItem.OutboundTag, w)));
                if (!ruleNodeValidatorResult.Success)
                {
                    validatorResult.Warnings.AddRange(ruleNodeValidatorResult.Errors.Select(e =>
                        string.Format(ResUI.MsgRoutingRuleOutboundNodeError, ruleItem.Remarks, ruleItem.OutboundTag,
                            e)));
                    ruleItem.OutboundTag = Global.ProxyTag;
                    continue;
                }

                context.AllProxiesMap[$"remark:{ruleItem.OutboundTag}"] = actRuleNode;
            }
        }

        if (context.IsTunEnabled && context.AppConfig.TunModeItem.RouteExcludeAddress is { Count: > 0 })
        {
            var appConfig = JsonUtils.DeepCopy(config);
            var routeExcludeAddressList = new List<string>();
            foreach (var addr in context.AppConfig.TunModeItem.RouteExcludeAddress)
            {
                try
                {
                    IPNetwork2.Parse(addr);
                    routeExcludeAddressList.Add(addr);
                }
                catch
                {
                    validatorResult.Warnings.Add(string.Format(ResUI.MsgTunRouteExcludeInvalidAddress, addr));
                }
            }
            appConfig.TunModeItem.RouteExcludeAddress = routeExcludeAddressList;
            context = context with { AppConfig = appConfig };
        }
        if (!context.AppConfig.CoreBasicItem.SendThrough.IsNullOrEmpty()
            && !Utils.IsLocalIP(context.AppConfig.CoreBasicItem.SendThrough))
        {
            validatorResult.Warnings.Add(string.Format(ResUI.MsgInvalidProperty, ResUI.TbSettingsSendThrough));
            var appConfig = JsonUtils.DeepCopy(context.AppConfig);
            appConfig.CoreBasicItem.SendThrough = string.Empty;
            context = context with { AppConfig = appConfig };
        }
        var bindInterface = context.AppConfig.CoreBasicItem.BindInterface?.TrimEx();
        if (bindInterface.IsNotEmpty())
        {
            if (NetworkUtils.IsAutoBindInterface(bindInterface))
            {
                bindInterface = NetworkUtils.GetDefaultPhysicalInterfaceName()?.TrimEx();
            }

            if (bindInterface.IsNullOrEmpty()
                || !Utils.ContainsInterfaceName(bindInterface)
                || !(context.IsTunEnabled || context.IsWindows))
            {
                validatorResult.Warnings.Add(string.Format(ResUI.MsgInvalidProperty, ResUI.TbSettingsBindInterface));
                bindInterface = string.Empty;
            }

            var appConfig = JsonUtils.DeepCopy(context.AppConfig);
            appConfig.CoreBasicItem.BindInterface = bindInterface;
            context = context with { AppConfig = appConfig };
        }

        return new CoreConfigContextBuilderResult(context, validatorResult);
    }

    /// <summary>
    ///     Registers the servers of a rule's server list ("= A; B") in order. A server that can't be used is
    ///     skipped; with none left the rule takes the main server.
    /// </summary>
    private static async Task RegisterServerListAsync(CoreConfigContext context, string outboundTag, ProfileItem main, bool allowCustom)
    {
        var keys = new List<string?>();
        var members = await ServiceServerResolver.ResolveListAsync(outboundTag, main, allowCustom);
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member == null)
            {
                keys.Add(null); // the main server
                continue;
            }
            var key = $"remark:{outboundTag}#{i}";
            if (member.ConfigType == EConfigType.Custom)
            {
                context.AllProxiesMap[key] = await CustomRuleServerNodeAsync(context, member);
                keys.Add(key);
                continue;
            }
            var (actMember, result) = await ResolveNodeAsync(context, member, false);
            if (!result.Success)
            {
                Logging.SaveLog($"ServiceServer: {member.Remarks} skipped: {string.Join("; ", result.Errors)}");
                continue;
            }
            context.AllProxiesMap[key] = actMember;
            keys.Add(key);
        }
        if (keys.Count > 0)
        {
            context.RuleServerLists[outboundTag] = keys;
        }
    }

    /// <summary>
    ///     A rule server that is a custom profile: a local SOCKS port that the main custom config will
    ///     open for it (one port per server, shared by every rule picking it).
    /// </summary>
    private static async Task<ProfileItem> CustomRuleServerNodeAsync(CoreConfigContext context, ProfileItem server)
    {
        var port = context.CustomRuleServers.FirstOrDefault(kv => kv.Value.IndexId == server.IndexId).Key;
        if (port == 0)
        {
            port = Utils.GetFreePort(AppManager.Instance.GetLocalPort(EInboundProtocol.socks) + 40 + context.CustomRuleServers.Count);
            while (context.CustomRuleServers.ContainsKey(port))
            {
                port++;
            }
            context.CustomRuleServers[port] = server;
        }

        // The pre-core runs the TUN: the rule server's own domains must resolve directly, as the main's.
        try
        {
            var path = File.Exists(server.Address) ? server.Address : Utils.GetConfigPath(server.Address);
            if (File.Exists(path))
            {
                foreach (var domain in CustomConfigDomains.Extract(await File.ReadAllTextAsync(path)))
                {
                    context.ProtectDomainList.Add(domain);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CoreConfigContextBuilder", ex);
        }

        return new ProfileItem
        {
            IndexId = $"detour-srv{port}",
            ConfigType = EConfigType.SOCKS,
            Address = Global.Loopback,
            Port = port,
            Remarks = server.Remarks,
        };
    }

    /// <summary>
    ///     Builds the main <see cref="CoreConfigContext" /> for <paramref name="node" /> and, when
    ///     the main build succeeds, also builds the optional pre-socks context required for TUN
    ///     protection or pre-socks proxy chaining.
    /// </summary>
    public static async Task<CoreConfigContextBuilderAllResult> BuildAll(Config config, ProfileItem node)
    {
        // Emergency mode: Tor's local SOCKS port stands in for the (blocked) server.
        if (config.EmergencyItem?.Active == true && TorManager.Instance.IsReady)
        {
            node = TorManager.Instance.LocalProxyNode();
        }
        var mainResult = await Build(config, node);
        if (!mainResult.Success)
        {
            return new CoreConfigContextBuilderAllResult(mainResult, null);
        }

        var preResult = await BuildPreSocksIfNeeded(mainResult.Context);
        if (preResult is null)
        {
            return new CoreConfigContextBuilderAllResult(mainResult, null);
        }

        var resolvedMainResult = mainResult with
        {
            Context = mainResult.Context with
            {
                IsTunEnabled = false,
                // main core doesn't handle tun directly when pre-socks is used
                ProtectDomainList = [.. mainResult.Context.ProtectDomainList, .. preResult.Context.ProtectDomainList],
                CustomRuleServers = preResult.Context.CustomRuleServers,
            },
        };
        if (mainResult.Context.IsTunEnabled
            && mainResult.Context.AppConfig.TunModeItem.StrictRoute)
        {
            var appConfig = JsonUtils.DeepCopy(mainResult.Context.AppConfig);
            appConfig.CoreBasicItem.BindInterface = string.Empty;
            appConfig.CoreBasicItem.SendThrough = string.Empty;
            resolvedMainResult = resolvedMainResult with
            {
                Context = resolvedMainResult.Context with
                {
                    AppConfig = appConfig,
                },
            };
        }
        return new CoreConfigContextBuilderAllResult(resolvedMainResult, preResult);
    }

    /// <summary>
    ///     Determines whether a pre-socks context is required for <paramref name="nodeContext" />
    ///     and, if so, builds and returns it. Returns <c>null</c> when no pre-socks core is needed.
    /// </summary>
    private static async Task<CoreConfigContextBuilderResult?> BuildPreSocksIfNeeded(CoreConfigContext nodeContext)
    {
        var config = nodeContext.AppConfig;
        var node = nodeContext.Node;
        var coreType = AppManager.Instance.GetCoreType(node, node.ConfigType);

        var preSocksItem = ConfigHandler.GetPreSocksItem(config, node, coreType);
        if (preSocksItem != null)
        {
            var preSocksResult = await Build(nodeContext.AppConfig, preSocksItem,
                node.ConfigType == EConfigType.Custom ? node : null);

            var protectCoreTypeList = new HashSet<ECoreType>(nodeContext.ProtectCoreTypeList) { nodeContext.RunCoreType };

            return preSocksResult with
            {
                Context = preSocksResult.Context with
                {
                    ProtectDomainList =
                    [.. nodeContext.ProtectDomainList, .. preSocksResult.Context.ProtectDomainList],
                    ProtectCoreTypeList = protectCoreTypeList,
                },
            };
        }

        return null;
    }

    /// <summary>
    ///     Resolves a node into the context, optionally wrapping it in a subscription-level proxy chain.
    ///     Returns the effective (possibly replaced) node and the validation result.
    /// </summary>
    public static async Task<(ProfileItem, NodeValidatorResult)> ResolveNodeAsync(CoreConfigContext context,
        ProfileItem node,
        bool includeSubChain = true)
    {
        if (node.IndexId.IsNullOrEmpty())
        {
            return (node, NodeValidatorResult.Empty());
        }

        if (includeSubChain)
        {
            var (virtualChainNode, chainValidatorResult) = await BuildSubscriptionChainNodeAsync(node);
            if (virtualChainNode != null)
            {
                context.AllProxiesMap[virtualChainNode.IndexId] = virtualChainNode;
                var (resolvedNode, resolvedResult) = await ResolveNodeAsync(context, virtualChainNode, false);
                resolvedResult.Warnings.InsertRange(0, chainValidatorResult.Warnings);
                return (resolvedNode, resolvedResult);
            }
            // Chain not built but warnings may still exist (e.g. missing profiles)
            if (chainValidatorResult.Warnings.Count > 0)
            {
                var fillResult = await RegisterNodeAsync(context, node);
                fillResult.Warnings.InsertRange(0, chainValidatorResult.Warnings);
                return (node, fillResult);
            }
        }

        var registerResult = await RegisterNodeAsync(context, node);
        return (node, registerResult);
    }

    /// <summary>
    ///     If the node's subscription defines prev/next profiles, creates a virtual
    ///     <see cref="EConfigType.ProxyChain" /> node that wraps them together.
    ///     Returns <c>null</c> as the chain item when no chain is needed.
    ///     Any warnings (e.g. missing prev/next profile) are returned in the validator result.
    /// </summary>
    private static async Task<(ProfileItem? ChainNode, NodeValidatorResult ValidatorResult)>
        BuildSubscriptionChainNodeAsync(ProfileItem node)
    {
        var result = NodeValidatorResult.Empty();

        if (node.Subid.IsNullOrEmpty() || node.ConfigType == EConfigType.Custom)
        {
            return (null, result);
        }

        var subItem = await AppManager.Instance.GetSubItem(node.Subid);
        if (subItem == null)
        {
            return (null, result);
        }

        ProfileItem? prevNode = null;
        ProfileItem? nextNode = null;

        if (!subItem.PrevProfile.IsNullOrEmpty())
        {
            prevNode = await AppManager.Instance.GetProfileItemViaRemarks(subItem.PrevProfile);
            if (prevNode == null)
            {
                result.Warnings.Add(string.Format(ResUI.MsgSubscriptionPrevProfileNotFound, subItem.PrevProfile));
            }
        }
        if (!subItem.NextProfile.IsNullOrEmpty())
        {
            nextNode = await AppManager.Instance.GetProfileItemViaRemarks(subItem.NextProfile);
            if (nextNode == null)
            {
                result.Warnings.Add(string.Format(ResUI.MsgSubscriptionNextProfileNotFound, subItem.NextProfile));
            }
        }

        if (prevNode is null && nextNode is null)
        {
            return (null, result);
        }

        // Build new proxy chain node
        var chainNode = new ProfileItem
        {
            IndexId = $"inner-{Utils.GetGuid(false)}",
            ConfigType = EConfigType.ProxyChain,
            CoreType = AppManager.Instance.GetCoreType(node, node.ConfigType),
            Remarks = node.Remarks,
        };
        List<string?> childItems = [prevNode?.IndexId, node.IndexId, nextNode?.IndexId];
        var chainExtraItem = chainNode.GetProtocolExtra() with
        {
            GroupType = chainNode.ConfigType.ToString(),
            ChildItems = string.Join(",", childItems.Where(x => !x.IsNullOrEmpty())),
        };
        chainNode.SetProtocolExtra(chainExtraItem);
        return (chainNode, result);
    }

    /// <summary>
    ///     Dispatches registration to either <see cref="RegisterGroupNodeAsync" /> or
    ///     <see cref="RegisterSingleNodeAsync" /> based on the node's config type.
    /// </summary>
    private static async Task<NodeValidatorResult> RegisterNodeAsync(CoreConfigContext context, ProfileItem node)
    {
        if (node.ConfigType.IsGroupType())
        {
            return await RegisterGroupNodeAsync(context, node);
        }
        return await RegisterSingleNodeAsync(context, node);
    }

    /// <summary>
    ///     Validates a single (non-group) node and, on success, adds it to the proxy map
    ///     and records any domain addresses that should bypass the proxy.
    /// </summary>
    private static async Task<NodeValidatorResult> RegisterSingleNodeAsync(CoreConfigContext context, ProfileItem node)
    {
        if (node.ConfigType.IsGroupType())
        {
            return NodeValidatorResult.Empty();
        }

        var nodeValidatorResult = NodeValidator.Validate(node, context.RunCoreType);

        if (node.ConfigType == EConfigType.Outbound)
        {
            var addressFileName = node.Address;
            if (!File.Exists(addressFileName))
            {
                addressFileName = Utils.GetConfigPath(addressFileName);
            }
            if (!File.Exists(addressFileName))
            {
                nodeValidatorResult.Errors.Add(string.Format(ResUI.MsgCustomOutboundFileNotFound, node.Remarks, addressFileName));
            }
            try
            {
                var fileContent = await File.ReadAllTextAsync(addressFileName);
                context.CustomOutboundContent[node.IndexId] = fileContent;
            }
            catch
            {
                nodeValidatorResult.Errors.Add(string.Format(ResUI.MsgCustomOutboundFileNotFound, node.Remarks, addressFileName));
            }
        }

        var msgs = new List<string>([.. nodeValidatorResult.Errors, .. nodeValidatorResult.Warnings]);
        if (msgs.Count > 0)
        {
            Logging.SaveLog($"{node.Remarks}: {string.Join("; ", msgs)}");
        }
        if (!nodeValidatorResult.Success)
        {
            return nodeValidatorResult;
        }

        context.AllProxiesMap[node.IndexId] = node;

        var address = node.Address;
        if (Utils.IsDomain(address))
        {
            context.ProtectDomainList.Add(address);
        }

        // Custom profiles: Address is the config file name; the server names are inside the JSON.
        if (node.ConfigType == EConfigType.Custom && address.IsNotEmpty())
        {
            try
            {
                var path = File.Exists(address) ? address : Utils.GetConfigPath(address);
                if (File.Exists(path))
                {
                    foreach (var domain in CustomConfigDomains.Extract(await File.ReadAllTextAsync(path)))
                    {
                        context.ProtectDomainList.Add(domain);
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("CoreConfigContextBuilder", ex);
            }
        }

        // ech query server name protect
        if (!node.EchConfigList.IsNullOrEmpty())
        {
            var echQuerySni = node.Sni;
            if (node.StreamSecurity == Global.StreamSecurity
                && node.EchConfigList.Contains("://"))
            {
                var idx = node.EchConfigList.IndexOf('+');
                echQuerySni = idx > 0 ? node.EchConfigList[..idx] : node.Sni;
            }

            if (Utils.IsDomain(echQuerySni))
            {
                context.ProtectDomainList.Add(echQuerySni);
            }
        }

        // xhttp downloadSettings address protect
        var xhttpExtra = node.GetTransportExtra().XhttpExtra;
        if (!string.IsNullOrEmpty(xhttpExtra)
            && JsonUtils.ParseJson(xhttpExtra) is JsonObject extra
            && extra.TryGetPropertyValue("downloadSettings", out var dsNode)
            && dsNode is JsonObject downloadSettings
            && downloadSettings.TryGetPropertyValue("address", out var dAddrNode)
            && dAddrNode is JsonValue dAddrValue
            && dAddrValue.TryGetValue(out string? dAddr)
            && !string.IsNullOrEmpty(dAddr)
            && Utils.IsDomain(dAddr))
        {
            context.ProtectDomainList.Add(dAddr);
        }

        return nodeValidatorResult;
    }

    /// <summary>
    ///     Entry point for registering a group node. Initialises the visited/ancestor sets
    ///     and delegates to <see cref="TraverseGroupNodeAsync" />.
    /// </summary>
    private static async Task<NodeValidatorResult> RegisterGroupNodeAsync(CoreConfigContext context,
        ProfileItem node)
    {
        if (!node.ConfigType.IsGroupType())
        {
            return NodeValidatorResult.Empty();
        }

        HashSet<string> ancestors = [node.IndexId];
        HashSet<string> globalVisited = [node.IndexId];
        return await TraverseGroupNodeAsync(context, node, globalVisited, ancestors);
    }

    /// <summary>
    ///     Recursively walks the children of a group node, registering valid leaf nodes
    ///     and nested groups. Detects cycles via <paramref name="ancestorsGroup" /> and
    ///     deduplicates shared nodes via <paramref name="globalVisitedGroup" />.
    /// </summary>
    private static async Task<NodeValidatorResult> TraverseGroupNodeAsync(
        CoreConfigContext context,
        ProfileItem node,
        HashSet<string> globalVisitedGroup,
        HashSet<string> ancestorsGroup)
    {
        var (groupChildList, _) = await GroupProfileManager.GetChildProfileItems(node);
        List<string> childIndexIdList = [];
        var childNodeValidatorResult = NodeValidatorResult.Empty();
        foreach (var childNode in groupChildList)
        {
            if (ancestorsGroup.Contains(childNode.IndexId))
            {
                childNodeValidatorResult.Errors.Add(
                    string.Format(ResUI.MsgGroupCycleDependency, node.Remarks, childNode.Remarks));
                continue;
            }

            if (globalVisitedGroup.Contains(childNode.IndexId))
            {
                childIndexIdList.Add(childNode.IndexId);
                continue;
            }

            if (!childNode.ConfigType.IsGroupType())
            {
                var childNodeResult = await RegisterSingleNodeAsync(context, childNode);
                childNodeValidatorResult.Warnings.AddRange(childNodeResult.Warnings.Select(w =>
                    string.Format(ResUI.MsgGroupChildNodeWarning, node.Remarks, childNode.Remarks, w)));
                childNodeValidatorResult.Errors.AddRange(childNodeResult.Errors.Select(e =>
                    string.Format(ResUI.MsgGroupChildNodeError, node.Remarks, childNode.Remarks, e)));
                if (!childNodeResult.Success)
                {
                    continue;
                }

                globalVisitedGroup.Add(childNode.IndexId);
                childIndexIdList.Add(childNode.IndexId);
                continue;
            }

            var newAncestorsGroup = new HashSet<string>(ancestorsGroup) { childNode.IndexId };
            var childGroupResult =
                await TraverseGroupNodeAsync(context, childNode, globalVisitedGroup, newAncestorsGroup);
            childNodeValidatorResult.Warnings.AddRange(childGroupResult.Warnings.Select(w =>
                string.Format(ResUI.MsgGroupChildGroupNodeWarning, node.Remarks, childNode.Remarks, w)));
            childNodeValidatorResult.Errors.AddRange(childGroupResult.Errors.Select(e =>
                string.Format(ResUI.MsgGroupChildGroupNodeError, node.Remarks, childNode.Remarks, e)));
            if (!childGroupResult.Success)
            {
                continue;
            }

            globalVisitedGroup.Add(childNode.IndexId);
            childIndexIdList.Add(childNode.IndexId);
        }

        if (childIndexIdList.Count == 0)
        {
            childNodeValidatorResult.Errors.Add(string.Format(ResUI.MsgGroupNoValidChildNode, node.Remarks));
            return childNodeValidatorResult;
        }
        childNodeValidatorResult.Warnings.AddRange(childNodeValidatorResult.Errors);
        childNodeValidatorResult.Errors.Clear();

        node.SetProtocolExtra(node.GetProtocolExtra() with { ChildItems = Utils.List2String(childIndexIdList) });
        context.AllProxiesMap[node.IndexId] = node;
        return childNodeValidatorResult;
    }
}
