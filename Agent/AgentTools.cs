using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;

namespace DSTerraria;

public static class AgentTools
{
    static readonly Dictionary<string, int> Tracking = new();
    static readonly HttpClient Wiki = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient HarvestHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    public static void NewTask() => Tracking.Clear();
    public static void Stop()
    {
        Halt.All();
        StateSnapshotPlayer.StopRemoteTask();
        FightCoordinator.Stop(); ReplaySystem.Stop(); KeyPress.Clear();
        PlayerCombat.Stop();
        PvpDuel.Stop();
        StateSpacePlanner.PreserveTerrain = false;
        HttpServerSystem.PendingControl = null;
    }

    static int Int(JsonObject args, string key, int fallback = 0) => args[key]?.GetValue<int>() ?? fallback;
    static string Text(JsonObject args, string key) => args[key]?.GetValue<string>() ?? "";
    static bool Bool(JsonObject args, string key) => args[key]?.GetValue<bool>() ?? false;
    static bool Rejected(JsonObject result) => result["error"] != null ||
        result["ok"]?.ToString() == "false" || result["accepted"]?.ToString() == "false";

    static Task<JsonObject> Call(string path, JsonObject args, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        token.ThrowIfCancellationRequested();
        var response = HttpServerSystem.Dispatch(path, args?.ToJsonString(new System.Text.Json.JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}");
        var result = JsonNode.Parse(response.Body) as JsonObject
            ?? throw new InvalidOperationException($"{path} 未返回 JSON 对象。");
        if (response.Status >= 400 && result["error"] == null) result["error"] = $"status {response.Status}";
        return result;
    }, token);

    public static Task<string> State(CancellationToken token) => NativeAgent.OnMain(() =>
    {
        var state = JsonNode.Parse(StateSerializer.ToJson(HttpServerSystem.LatestSnapshot)) as JsonObject ?? new();
        state["agent"] = new JsonObject
        {
            ["status"] = NativeAgent.Status, ["auto_dodge"] = AgentPermissions.Dodge,
            ["auto_attack"] = AgentPermissions.Attack, ["player_id"] = Main.LocalPlayer.whoAmI,
            ["pvp"] = Main.LocalPlayer.hostile, ["team"] = Main.LocalPlayer.team,
            ["instruction_player_id"] = AgentNetwork.OwnerPlayer,
            ["pvp_target"] = PvpDuel.Active ? PvpDuel.Target : PlayerCombat.Target
        };
        var players = new JsonArray();
        for (int id = 0; id < Main.maxPlayers; id++)
        {
            var other = Main.player[id];
            if (other == null || !other.active || id == Main.LocalPlayer.whoAmI) continue;
            players.Add(new JsonObject
            {
                ["id"] = id, ["name"] = other.name, ["hp"] = other.statLife, ["dead"] = other.dead,
                ["pvp"] = other.hostile, ["team"] = other.team,
                ["pos"] = new JsonObject { ["x"] = other.position.X, ["y"] = other.position.Y },
                ["pvp_block_reason"] = PlayerCombat.TargetReason(Main.LocalPlayer, id)
            });
        }
        state["other_players"] = players;
        state["combat"] = PvpDuel.Report();
        state["inventory_slots"] = PvpInventory.Snapshot();
        // 身上穿的装备/饰品(护甲栏)。/start 和每次 get_state 都带,模型才知道自己穿没穿、缺什么
        state["armor_slots"] = ArmorSnapshot();
        // 当前打开的箱子内容,供 move_item 用(没开箱就是空)
        state["open_chest"] = OpenChestSnapshot();
        return state.ToJsonString();
    }, token);

    // 身上穿的装备:armor[0]=头 1=身 2=腿 3-9=饰品 10-12=时装 13-19=时装饰品。
    // 只列非空的,每件带 part 标明是哪个部位
    static JsonArray ArmorSnapshot()
    {
        var slots = new JsonArray();
        var p = Main.LocalPlayer;
        if (p == null || p.armor == null) return slots;
        string[] part = { "head", "body", "legs", "acc1", "acc2", "acc3", "acc4", "acc5", "acc6", "acc7",
            "vanity_head", "vanity_body", "vanity_legs", "vanity_acc1", "vanity_acc2", "vanity_acc3",
            "vanity_acc4", "vanity_acc5", "vanity_acc6", "vanity_acc7" };
        for (int i = 0; i < p.armor.Length && i < part.Length; i++)
        {
            var it = p.armor[i];
            if (it == null || it.IsAir) continue;
            slots.Add(new JsonObject
            {
                ["slot"] = i, ["part"] = part[i], ["type"] = it.type,
                ["name"] = it.Name, ["prefix"] = it.prefix, ["stack"] = it.stack,
                ["defense"] = it.defense, ["damage"] = it.damage
            });
        }
        return slots;
    }

    // 当前打开的箱子(0-39 槽)内容。没开箱返回空数组
    static JsonArray OpenChestSnapshot()
    {
        var slots = new JsonArray();
        var p = Main.LocalPlayer;
        if (p == null || p.chest < 0 || p.chest >= Main.chest.Length || Main.chest[p.chest] == null)
            return slots;
        for (int slot = 0; slot < 40; slot++)
        {
            var it = Main.chest[p.chest].item[slot];
            if (it == null || it.IsAir) continue;
            slots.Add(new JsonObject
            {
                ["type"] = it.type, ["prefix"] = it.prefix, ["stack"] = it.stack,
                ["name"] = it.Name, ["slot"] = slot
            });
        }
        return slots;
    }

    static Dictionary<string, int> Inventory()
    {
        var items = new Dictionary<string, int>();
        foreach (var item in Main.LocalPlayer.inventory)
            if (item != null && !item.IsAir) items[item.Name] = items.GetValueOrDefault(item.Name) + item.stack;
        return items;
    }

    static Task<string> Result(JsonObject result, Dictionary<string, int> before, CancellationToken token) => NativeAgent.OnMain(() =>
    {
        var player = Main.LocalPlayer;
        var now = Inventory();
        var got = new JsonObject(); var lost = new JsonObject(); var progress = new JsonObject();
        foreach (var item in now)
            if (item.Value > before.GetValueOrDefault(item.Key)) got[item.Key] = item.Value - before.GetValueOrDefault(item.Key);
        foreach (var item in before)
            if (item.Value > now.GetValueOrDefault(item.Key)) lost[item.Key] = item.Value - now.GetValueOrDefault(item.Key);
        foreach (var item in Tracking) progress[item.Key] = $"{now.GetValueOrDefault(item.Key)}/{item.Value}";
        result["state"] = new JsonObject
        {
            ["pos"] = new JsonObject { ["x"] = ActExecutor.OriginCx(player), ["y"] = ActExecutor.OriginCy(player) },
            ["hp"] = player.statLife, ["on_ground"] = player.velocity.Y == 0f
        };
        result["got"] = got; result["lost"] = lost;
        if (progress.Count > 0) result["进度"] = progress;
        return result.ToJsonString();
    }, token);

    internal static async Task<JsonObject> Wait(string path, string active, string stop, int seconds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        // 执行期间的机会检测:每 OpportunityEveryMs 扫一次附近宝箱/生命水晶。
        // 发现了就中断当前原语,把机会作为结果回给模型,由它决定支线(开箱)还是继续主线。
        // 只对"采集/移动/建造"这类长原语生效;战斗/合成不扫
        bool watch = stop is not ("/fight" or "/combat_reposition");
        long lastOppScan = 0;
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(150, token);
            if (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
            {
                await Call(stop, null, token);
                return new JsonObject { ["outcome"] = "interrupted_by_death" };
            }
            if (watch && timer.ElapsedMilliseconds - lastOppScan >= OpportunityEveryMs)
            {
                lastOppScan = timer.ElapsedMilliseconds;
                var opp = await NativeAgent.OnMain(ScanOpportunity, token);
                if (opp != null)
                {
                    await Call(stop, null, token);
                    var s = await Call(path, null, token);
                    opp["outcome"] = "interrupted_by_opportunity";
                    opp["paused"] = Text(s, "outcome") == "" ? "running" : Text(s, "outcome");
                    opp["note"] = "附近发现值得注意的东西,主线原语已暂停。决定去处理(如开箱)就 nav_to 过去 interact;不处理就重新发原指令继续。";
                    return opp;
                }
            }
            var status = await Call(path, null, token);
            if (active == "nav" ? Bool(status, "done") || Text(status, "status") == "failed" : !Bool(status, active))
                return status;
        }
        await Call(stop, stop == "/fight" ? new JsonObject { ["active"] = false } : null, token);
        var final = await Call(path, null, token);
        final["outcome"] = "timeout";
        return final;
    }

    const long OpportunityEveryMs = 5000;

    // 值得拐一趟的稀有矿石(银/钨/金/铂金及以上),扫到就报。铜铁铅锡这种满大街的不报
    static readonly System.Collections.Generic.HashSet<int> RareOres = new()
    {
        TileID.Silver, TileID.Tungsten, TileID.Gold, TileID.Platinum,
        TileID.Meteorite, TileID.Demonite, TileID.Crimtane, TileID.Hellstone,
        TileID.Cobalt, TileID.Palladium, TileID.Mythril, TileID.Orichalcum,
        TileID.Adamantite, TileID.Titanium, TileID.Chlorophyte, TileID.LunarOre
    };

    // 附近(25 格直线半径)有没有没开过的宝箱 / 生命水晶 / 稀有矿石。有就返回那条机会,没有返回 null。
    // 只读扫描,不记账、不碰 GreedPickup 的顺路状态。主线程跑,最多扫 51x51 格,很快
    static JsonObject ScanOpportunity()
    {
        var p = Main.LocalPlayer;
        if (p == null) return null;
        int pcx = ActExecutor.OriginCx(p), pcy = ActExecutor.OriginCy(p);
        for (int x = pcx - 25; x <= pcx + 25; x++)
            for (int y = pcy - 25; y <= pcy + 25; y++)
            {
                if (x < 1 || y < 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1) continue;
                var t = Main.tile[x, y];
                if (!t.HasTile) continue;
                if (t.TileType == TileID.Containers || t.TileType == TileID.Containers2)
                {
                    if (t.TileFrameX % 36 != 0 || t.TileFrameY % 36 != 0) continue;
                    if (Chest.IsLocked(x, y)) continue;
                    if (t.TileType == TileID.Containers && t.TileFrameX / 36 == 16) continue;
                    return new JsonObject
                    {
                        ["kind"] = "chest",
                        ["x"] = x, ["y"] = y,
                        ["desc"] = "附近有一口能开的箱子"
                    };
                }
                if (t.TileType == TileID.Heart && t.TileFrameX % 36 == 0 && t.TileFrameY % 36 == 0)
                    return new JsonObject
                    {
                        ["kind"] = "life_crystal",
                        ["x"] = x, ["y"] = y,
                        ["desc"] = "附近有一颗生命水晶"
                    };
                // 稀有矿石:银/钨/金/铂金及以上。直接按 TileID 白名单判(不用 TileID.Ore,那个是内部字段)
                if (RareOres.Contains(t.TileType))
                    return new JsonObject
                    {
                        ["kind"] = "rare_ore",
                        ["x"] = x, ["y"] = y,
                        ["tile"] = t.TileType,
                        ["desc"] = "附近有稀有矿石"
                    };
            }
        return null;
    }

    public static async Task<string> Execute(string name, JsonObject args, CancellationToken token)
    {
        if (name is not ("say" or "ask" or "get_state" or "combat_observe" or "combat_stop")
            && await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token))
        {
            while (await NativeAgent.OnMain(() => Main.LocalPlayer.dead, token)) await Task.Delay(250, token);
            return new JsonObject { ["outcome"] = "interrupted_by_death",
                ["detail"] = "Respawned; re-evaluate this action against fresh state instead of replaying its old arguments.",
                ["state_after_respawn"] = JsonNode.Parse(await State(token)) }.ToJsonString();
        }
        bool external = name is "nav_to" or "mine" or "mine_vein" or "chop_wood" or "use_item"
            or "place_at" or "act" or "interact" or "loot_all" or "craft" or "inventory_swap" or "hotbar_place" or "combat_reposition";
        long lease = external ? await NativeAgent.OnMain(() => PvpDuel.BeginAction(name), token) : 0;
        try
        {
            string output = await ExecuteCore(name, args, token);
            if (lease != 0)
                return await NativeAgent.OnMain(() =>
                {
                    PvpDuel.EndAction(lease);
                    var result = JsonNode.Parse(output) as JsonObject ?? new JsonObject { ["action_result"] = output };
                    result["combat_after_action"] = PvpDuel.Report();
                    return result.ToJsonString();
                }, token);
            return output;
        }
        finally
        {
            if (lease != 0 && !token.IsCancellationRequested)
                await NativeAgent.OnMain(() => { PvpDuel.EndAction(lease); return true; }, token);
        }
    }

    static async Task<string> ExecuteCore(string name, JsonObject args, CancellationToken token)
    {
        await NativeAgent.OnMain(() => { DiagLog.Write($"[agent] tool {name} {args}"); return true; }, token);
        if (name is not ("say" or "ask"))
            await NativeAgent.OnMain(() => { NativeAgent.Progress("开始执行：" + name, "执行 " + name, false); return true; }, token);
        if (name == "get_state") return await State(token);
        if (name == "build_house") return (await BuildHouse(args, token)).ToJsonString();
        if (name == "say")
        {
            await NativeAgent.OnMain(() => { AgentChat.Say(AgentConversation.VisibleText(Text(args, "text"))); return true; }, token);
            return "ok";
        }
        if (name == "ask") return new JsonObject { ["player_answer"] = await NativeAgent.Ask(AgentConversation.VisibleText(Text(args, "question")), token) }.ToJsonString();
        if (name is "wiki_search" or "wiki_page") return await ReadWiki(name, args, token);
        if (name == "wiki") return await WikiFallback(args, token);
        var before = await NativeAgent.OnMain(Inventory, token);
        JsonObject result;
        switch (name)
        {
            case "combat_reposition":
                var destination = await NativeAgent.OnMain(() =>
                {
                    if (!PvpDuel.Active || args["session"]?.GetValue<long>() != PvpDuel.Session)
                        return new JsonObject { ["error"] = "session_mismatch" };
                    var report = PvpDuel.Report();
                    float x = args["x"].GetValue<float>(), y = args["y"].GetValue<float>();
                    if (!PvpTerrain.ValidStance(Main.LocalPlayer, x, y))
                        return new JsonObject { ["error"] = "stance_no_longer_valid", ["current_probes"] = report["perception"]["terrain"]["stance_probes"].DeepClone() };
                    return new JsonObject { ["x"] = (int)(x / 16),
                        ["y"] = (int)(y / 16),
                        ["before_x"] = Main.LocalPlayer.Center.X, ["before_y"] = Main.LocalPlayer.Center.Y,
                        ["before_line_clear"] = report["perception"]["terrain"]["direct_line"]["clear"].DeepClone() };
                }, token);
                if (destination["error"] != null) { result = destination; break; }
                result = await Navigate(destination, token, 8);
                result["requested_destination"] = destination;
                result["displacement_pixels"] = await NativeAgent.OnMain(() => Microsoft.Xna.Framework.Vector2.Distance(
                    Main.LocalPlayer.Center, new Microsoft.Xna.Framework.Vector2(destination["before_x"].GetValue<float>(),
                        destination["before_y"].GetValue<float>())), token);
                break;
            case "inventory_swap":
                // 【先停 NPC 战斗再等挥舞结束】。战斗每帧重写 selectedItem + controlUseItem,
                // 不停的话 itemAnimation 几乎永远不归零(等不到空闲帧),换完的布局下一帧也被改掉。
                // 窗口给 5 秒:等待(≤2s) + 操作 + 收尾;成功后再续 3 秒,选中状态和布局至少稳几秒
                await NativeAgent.OnMain(() => { CombatPause.ForSeconds(5f); return true; }, token);
                var idleClock = Stopwatch.StartNew();
                while (idleClock.Elapsed.TotalSeconds < 2 && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                    || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                    await Task.Delay(50, token);
                result = await NativeAgent.OnMain(() => PvpInventory.Swap(args), token);
                if (result["error"] == null)
                    await NativeAgent.OnMain(() => { CombatPause.ForSeconds(3f); return true; }, token);
                break;
            case "hotbar_place":
            case "organize_hotbar":
                // 已弃用:不再暴露给 AI。切武器统一走 switch_item(会自动换下快捷栏里的非武器)
                result = new JsonObject { ["error"] = "deprecated",
                    ["detail"] = "这个工具已弃用。放东西用 move_item,切武器用 switch_item。" };
                break;
            case "combat_observe":
                result = new JsonObject { ["error"] = "polling_disabled",
                    ["detail"] = "End this turn. Jev continues combat; code waits for player instructions or a bounded recovery request." };
                break;
            case "combat_update":
                result = await NativeAgent.OnMain(() => PvpDuel.UpdateDirective(args), token);
                break;
            case "combat_stop":
                result = await NativeAgent.OnMain(() =>
                {
                    if (args["session"]?.GetValue<long>() != PvpDuel.Session) return new JsonObject { ["error"] = "session_mismatch" };
                    PvpDuel.Stop("agent_stopped"); return PvpDuel.Report();
                }, token);
                break;
            case "set_combat":
                result = await NativeAgent.OnMain(() =>
                {
                    if (args["auto_dodge"] != null) AgentPermissions.Dodge = Bool(args, "auto_dodge");
                    if (args["auto_attack"] != null) AgentPermissions.Attack = Bool(args, "auto_attack");
                    if (!AgentPermissions.Attack) { Combat.Stop(); FightCoordinator.Stop(); PlayerCombat.Stop(); }
                    return new JsonObject { ["auto_dodge"] = AgentPermissions.Dodge, ["auto_attack"] = AgentPermissions.Attack };
                }, token);
                break;
            case "track":
                result = await NativeAgent.OnMain(() =>
                {
                    Tracking.Clear();
                    foreach (var item in args["items"].AsObject()) Tracking[item.Key] = item.Value.GetValue<int>();
                    return new JsonObject { ["outcome"] = "tracking" };
                }, token);
                break;
            case "item_info": case "find_biome": case "find_descent": case "descent_route":
            case "tile_names": case "find_tiles": case "recipe":
                result = await Call("/" + name, args, token);
                break;
            case "nav_to":
                result = await Navigate(args, token);
                break;
            case "mine_vein":
                result = await MineVein(args, token);
                break;
            case "chop_wood":
                int wantedWood = Int(args, "count");
                if (wantedWood < 1 || wantedWood > 9999) return "{\"error\":\"count 必须在 1 到 9999 之间。\"}";
                string key = await NativeAgent.OnMain(() => Config.I.TypeSafeKey?.Trim(), token);
                if (string.IsNullOrEmpty(key)) return "{\"error\":\"请先配置 Jev 的 TypeSafe Key。\"}";
                await NativeAgent.OnMain(() => { StateSpacePlanner.PreserveTerrain = true; return true; }, token);
                try
                {
                    var decision = new WoodHarvestDecision(HarvestHttp, key);
                    result = await WoodHarvest.Run(wantedWood, Text(args, "constraints"), new GameWoodHarvest(), async (scene, ct) =>
                    {
                        var clock = Stopwatch.StartNew();
                        string choice = await decision.Choose(scene, ct);
                        await NativeAgent.OnMain(() => { DiagLog.Write($"[harvest] jev choice={choice} ms={clock.ElapsedMilliseconds} scene={scene}"); return true; }, ct);
                        return choice;
                    }, token);
                }
                finally
                {
                    await NativeAgent.OnMain(() => { StateSpacePlanner.PreserveTerrain = false; return true; }, token);
                }
                break;
            case "mine":
                result = await Call("/mine", new JsonObject { ["dir"] = Text(args, "dir"),
                    ["target_wx"] = Int(args, "target_x"), ["target_wy"] = Int(args, "target_y") }, token);
                if (!Rejected(result)) result = await Wait("/mine_status", "running", "/mine_stop", 120, token);
                break;
            case "use_item":
                result = await Call("/item_use", new JsonObject
                {
                    ["target_wx"] = Int(args, "x"), ["target_wy"] = Int(args, "y"), ["slot"] = Int(args, "slot", -1),
                    ["duration_ticks"] = Int(args, "duration_ticks", 30), ["strict"] = Bool(args, "strict")
                }, token);
                if (!Rejected(result)) result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
                break;
            case "place_at":
                result = await Place(args, token);
                break;
            case "interact":
                result = await Call("/interact", new JsonObject { ["tile_x"] = Int(args, "x"), ["tile_y"] = Int(args, "y") }, token);
                if (!Rejected(result))
                {
                    await Task.Delay(200, token);
                    result = await NativeAgent.OnMain(() => new JsonObject { ["outcome"] = HttpServerSystem.LastInteract }, token);
                }
                break;
            case "loot_all":
                result = await Call("/loot_all", args, token);
                await Task.Delay(300, token);
                break;
            case "move_item":
                result = await Call("/move_item", args, token);
                break;
            case "craft":
                result = await Call("/craft", new JsonObject { ["item_name"] = Text(args, "name"), ["amount"] = Int(args, "amount", 1) }, token);
                break;
            case "equip_item":
                result = await Call("/equip_item", args, token);
                break;
            case "switch_item":
                // 【先暂停所有武器/工具,再等挥舞结束,再切】。被要求用某武器时,切换过程中
                // 不能有旧武器/镐/斧还在挥(会干扰选中),战斗层每帧也会重写 selectedItem。
                // CombatPause 停 NPC 战斗;ItemUseCoordinator/MineCoordinator.Stop 停采集工具
                await NativeAgent.OnMain(() =>
                {
                    CombatPause.ForSeconds(5f);
                    ItemUseCoordinator.Stop();
                    MineCoordinator.Stop();
                    return true;
                }, token);
                var swClock = Stopwatch.StartNew();
                while (swClock.Elapsed.TotalSeconds < 2 && !await NativeAgent.OnMain(() => Main.LocalPlayer.dead
                    || Main.LocalPlayer.itemAnimation == 0 && Main.LocalPlayer.itemTime == 0, token))
                    await Task.Delay(50, token);
                result = await NativeAgent.OnMain(() => PvpInventory.SwitchTo(args), token);
                if (result["error"] == null)
                    await NativeAgent.OnMain(() => { CombatPause.ForSeconds(3f); return true; }, token);
                break;
            case "store_inventory":
                result = await Call("/store_inventory", null, token);
                break;
            case "act":
                result = await Call("/act", args, token);
                if (!Rejected(result)) result = await Wait("/act_status", "active", "/act_stop", 120, token);
                break;
            case "fight":
                if (!await NativeAgent.OnMain(() => AgentPermissions.Attack, token))
                    return "{\"error\":\"玩家禁止自动攻击，请先确认是否修改许可。\"}";
                result = await Call("/fight", args, token);
                if (!Rejected(result))
                {
                    result = await Wait("/fight_active", "active", "/fight", Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120), token);
                    await Call("/fight", new JsonObject { ["active"] = false }, token);
                }
                break;
            case "combat_start":
            case "duel_player":
                int duelTarget = Int(args, "player_id", -1);
                int duelSeconds = Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 30), 1, 120);
                result = await NativeAgent.OnMain(() =>
                {
                    int weaponSlot = Int(args, "slot", Main.LocalPlayer.selectedItem);
                    if (!PvpDuel.Start(duelTarget, duelSeconds, weaponSlot, Math.Clamp(Int(args, "max_dist", 300), 1, 600), Text(args, "strategy"), out string reason,
                        untilStopped: Bool(args, "until_stopped")))
                        return new JsonObject { ["error"] = reason };
                    if (args["constraints"] != null || args["instructions"] != null)
                    {
                        var patch = new JsonObject { ["session"] = PvpDuel.Session, ["expected_revision"] = PvpDuel.Directive.Revision,
                            ["constraints"] = args["constraints"]?.DeepClone(), ["instructions"] = args["instructions"]?.DeepClone() };
                        var updated = PvpDuel.UpdateDirective(patch);
                        if (updated["error"] != null) { PvpDuel.Stop("invalid_directive"); return updated; }
                    }
                    return PvpDuel.Report();
                }, token);
                break;
            case "fight_player":
                int target = Int(args, "player_id", -1);
                int seconds = Math.Clamp((int)Math.Ceiling(args["seconds"]?.GetValue<double>() ?? 10), 1, 120);
                float range = Math.Clamp(Int(args, "max_dist", 80), 1, 200);
                result = await NativeAgent.OnMain(() =>
                {
                    if (!PlayerCombat.Start(target, seconds, range, out string reason))
                        return new JsonObject { ["error"] = reason };
                    return new JsonObject { ["target_player"] = target, ["target_hp_before"] = Main.player[target].statLife,
                        ["self_pvp_enabled"] = Main.LocalPlayer.hostile };
                }, token);
                if (!Rejected(result))
                {
                    var timer = Stopwatch.StartNew();
                    while (await NativeAgent.OnMain(() => PlayerCombat.Active, token) && timer.Elapsed.TotalSeconds < seconds + 3)
                        await Task.Delay(150, token);
                    await NativeAgent.OnMain(() =>
                    {
                        if (PlayerCombat.Active) PlayerCombat.Stop("timeout");
                        result["outcome"] = PlayerCombat.Outcome;
                        result["target_hp_after"] = Main.player[target].statLife;
                        return true;
                    }, token);
                }
                break;
            default: throw new InvalidOperationException($"未知工具: {name}");
        }
        string output = await Result(result, before, token);
        await NativeAgent.OnMain(() => { DiagLog.Write($"[agent] result {name} {output}"); return true; }, token);
        await NativeAgent.OnMain(() => { NativeAgent.Progress("执行结束：" + name + "，等待下一步决策。", "等待模型决策", false); return true; }, token);
        return output;
    }

    // 盖 NPC 房:照搬速通主线 StartRun 的建房流程,参数一个不改 —— 21x10 四间、dir=1。
    // 选址用同一把尺(Predicates.ScanHouse + 同样的宽高),走到那一带后交给 HouseBuilder 施工
    static async Task<JsonObject> BuildHouse(JsonObject args, CancellationToken token)
    {
        const int HouseW = 21, HouseH = 10, Rooms = 4, Dir = 1;
        var scan = await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            bool found = Predicates.ScanHouse(ActExecutor.OriginCx(p), ActExecutor.OriginCy(p),
                HouseW, HouseH, 200, out int sx, out int sy, out int scanned);
            return new JsonObject { ["found"] = found, ["sx"] = sx, ["sy"] = sy, ["scanned"] = scanned };
        }, token);
        if (!Bool(scan, "found"))
            return new JsonObject
            {
                ["outcome"] = "no_site",
                ["need"] = new JsonArray(HouseW, HouseH),
                ["scanned"] = Int(scan, "scanned"),
                ["hint"] = "附近 200 格内没有 21x10 净空;先清理地形、垫平台,或让玩家换个地方再说一次"
            };

        int sx = Int(scan, "sx"), sy = Int(scan, "sy");
        // 走到房址那一带就行,精准踩位是 HouseBuilder 的 Lift 阶段自己干的
        var nav = await NativeAgent.OnMain(() =>
        {
            int gap = Math.Abs(ActExecutor.OriginCx(Main.LocalPlayer) - sx);
            if (gap <= 6) return true;
            int foot = HouseBuilder.LadderFootRow(sx, sy);
            RecedingNav.Start(sx, foot == int.MinValue ? sy : foot);
            return false;
        }, token);
        if (!nav) await Wait("/nav_recede_done", "nav", "/nav_recede_stop", 300, token);

        var start = await Call("/build_house", new JsonObject
        {
            ["rooms"] = Rooms, ["dir"] = Dir, ["x"] = sx, ["y"] = sy
        }, token);
        if (Rejected(start)) return start;
        var result = await Wait("/build_house_status", "running", "/build_house_stop", 900, token);
        result["corner"] = new JsonArray(sx, sy);
        result["rooms"] = Rooms;
        return result;
    }

    internal static async Task<JsonObject> Navigate(JsonObject args, CancellationToken token, int seconds = 180)
    {
        var start = await Call("/nav_recede", new JsonObject
        {
            ["gx"] = Int(args, "x"), ["gy"] = Int(args, "y"), ["exact"] = Bool(args, "exact"), ["reach"] = Bool(args, "reach")
        }, token);
        if (Rejected(start)) return start;
        var result = await Wait("/nav_recede_done", "nav", "/nav_recede_stop", seconds, token);
        return await NativeAgent.OnMain(() =>
        {
            var p = Main.LocalPlayer;
            int distance = Math.Max(Math.Abs(ActExecutor.OriginCx(p) - Int(args, "x")), Math.Abs(ActExecutor.OriginCy(p) - Int(args, "y")));
            bool arrived = Bool(args, "reach") ? Reach.CanInteract(p, Int(args, "x"), Int(args, "y")) : distance <= 6;
            if (Bool(result, "done") && !arrived)
            { result["done"] = false; result["status"] = "stopped_short"; result["dist"] = distance; }
            return result;
        }, token);
    }

    static async Task<JsonObject> Place(JsonObject args, CancellationToken token)
    {
        int count = Math.Clamp(Int(args, "n", 1), 1, 200);
        int placed = 0;
        var cells = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            var payload = new JsonObject { ["item"] = Text(args, "item") };
            if (args["x"] != null && args["y"] != null)
                payload["world"] = new JsonArray(Int(args, "x") + i * Int(args, "step_x"), Int(args, "y") + i * Int(args, "step_y"));
            var result = await Call("/place_anywhere", payload, token);
            if (!Rejected(result)) result = await Wait("/place_anywhere_status", "running", "/place_anywhere_stop", 120, token);
            cells.Add(result);
            if (Text(result, "outcome") != "done") break;
            placed++;
        }
        return new JsonObject { ["outcome"] = placed == count ? "done" : placed > 0 ? "partial" : "stuck", ["placed"] = placed, ["wanted"] = count, ["cells"] = cells };
    }

    static async Task<JsonObject> MineVein(JsonObject args, CancellationToken token)
    {
        string name = Text(args, "name");
        int wanted = Math.Clamp(Int(args, "count", 1), 1, 1000), removed = 0;
        var skipped = new HashSet<(int, int)>();
        string outcome = "exhausted";
        for (int attempt = 0; attempt < wanted + 8 && removed < wanted; attempt++)
        {
            var found = await Call("/find_tiles", new JsonObject { ["name"] = name, ["n"] = 30, ["max_dist"] = 2000 }, token);
            var target = found["tiles"]?.AsArray().OfType<JsonObject>().FirstOrDefault(t => !skipped.Contains((Int(t, "x"), Int(t, "y"))));
            if (target == null) break;
            int x = Int(target, "x"), y = Int(target, "y");
            skipped.Add((x, y));
            await Navigate(new JsonObject { ["x"] = x, ["y"] = y, ["exact"] = true }, token);
            var reach = await Call("/mine_reach", null, token);
            if (Rejected(reach)) continue;
            int slot = await NativeAgent.OnMain(() => Enumerable.Range(0, 10)
                .OrderByDescending(i => Main.LocalPlayer.inventory[i].pick)
                .FirstOrDefault(i => Main.LocalPlayer.inventory[i].pick > 0, -1), token);
            if (slot < 0) return new JsonObject { ["outcome"] = "no_pickaxe", ["tiles_removed"] = removed };
            var nearby = await Call("/find_tiles", new JsonObject { ["name"] = name, ["n"] = 40, ["max_dist"] = 60 }, token);
            foreach (var tile in nearby["tiles"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (removed >= wanted) break;
                int tx = Int(tile, "x"), ty = Int(tile, "y");
                if (tx < Int(reach, "lx") || tx > Int(reach, "hx") || ty < Int(reach, "ly") || ty > Int(reach, "hy")) continue;
                var started = await Call("/item_use", new JsonObject { ["target_wx"] = tx, ["target_wy"] = ty, ["slot"] = slot, ["strict"] = true, ["duration_ticks"] = 0 }, token);
                if (Rejected(started)) continue;
                var result = await Wait("/item_use_status", "active", "/item_use_stop", 60, token);
                if (Text(result, "outcome") == "removed") removed++;
                if (Text(result, "reason") == "tool_weak") return new JsonObject { ["outcome"] = "tool_weak", ["tiles_removed"] = removed };
            }
        }
        if (removed >= wanted) outcome = "enough";
        return new JsonObject { ["outcome"] = outcome, ["tiles_removed"] = removed, ["wanted"] = wanted,
            ["note"] = "清除方块数不代表获得物品数，以 got 和背包实际数量为准。" };
    }

    static async Task<string> ReadWiki(string name, JsonObject args, CancellationToken token)
    {
        // site 可选:calamity 走灾厄 wiki,否则原版 wiki。两个都是 wiki.gg 的 MediaWiki API
        string site = Text(args, "site").ToLowerInvariant() == "calamity"
            ? "https://calamitymod.wiki.gg/api.php" : "https://terraria.wiki.gg/api.php";
        string query = name == "wiki_search"
            ? "action=query&list=search&srsearch=" + Uri.EscapeDataString(Text(args, "query")) + "&srlimit=5"
            : "action=parse&prop=text&disablelimitreport=1&redirects=1&page=" + Uri.EscapeDataString(Text(args, "title"));
        using var request = new HttpRequestMessage(HttpMethod.Get, site + "?format=json&" + query);
        request.Headers.UserAgent.ParseAdd("DSTerraria/1.0");
        using var response = await Wiki.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync(token);
        var data = JsonNode.Parse(text);
        if (name == "wiki_search")
        {
            var result = (data?["query"]?["search"] as JsonArray) ?? new JsonArray();
            return new JsonObject { ["site"] = site, ["results"] = result }.ToJsonString();
        }
        string html = data?["parse"]?["text"]?["*"]?.GetValue<string>();
        if (html == null) return new JsonObject { ["error"] = "wiki_not_found", ["site"] = site, ["title"] = Text(args, "title") }.ToJsonString();
        string plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));
        return new JsonObject { ["site"] = site, ["title"] = Text(args, "title"), ["text"] = plain[..Math.Min(plain.Length, 6000)] }.ToJsonString();
    }

    // 统一查资料入口:先原版 Terraria wiki,查不到(空结果/页面不存在)再自动去灾厄 Calamity wiki。
    // 玩家要求"上网查资料"、或自己遇到不确定的知识时都调这个,不用自己分该去哪个 wiki
    static async Task<string> WikiFallback(JsonObject args, CancellationToken token)
    {
        string query = Text(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return "{\"error\":\"query 不能为空\"}";
        // 先原版 wiki 搜索
        var first = JsonNode.Parse(await ReadWiki("wiki_search", new JsonObject { ["query"] = query }, token)) as JsonObject;
        var results = first?["results"] as JsonArray;
        bool hasResult = results != null && results.Count > 0;
        if (!hasResult)
        {
            var cal = JsonNode.Parse(await ReadWiki("wiki_search", new JsonObject { ["query"] = query, ["site"] = "calamity" }, token)) as JsonObject;
            return new JsonObject
            {
                ["site"] = "calamity", ["fallback"] = true,
                ["results"] = cal?["results"] ?? new JsonArray(),
                ["note"] = "原版 wiki 无结果,已转灾厄 wiki"
            }.ToJsonString();
        }
        return new JsonObject { ["site"] = "terraria", ["results"] = results }.ToJsonString();
    }
}
