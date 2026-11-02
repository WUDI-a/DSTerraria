using System.Text.Json.Nodes;
using Terraria;
using Terraria.ID;

namespace DSTerraria;

public static class PvpInventory
{
    public static JsonArray Snapshot()
    {
        var slots = new JsonArray();
        for (int slot = 0; slot < 50; slot++)
        {
            var item = ItemState(Main.LocalPlayer.inventory[slot]); item["slot"] = slot; slots.Add(item);
        }
        return slots;
    }

    static JsonObject ItemState(Item item) => new()
    { ["type"] = item.type, ["prefix"] = item.prefix, ["stack"] = item.stack, ["name"] = item.Name };

    // 整背包操作(Swap/PlaceInHotbar)共用的忙闸门:挥舞动画进行中、采集/放置动作占用中、死亡。
    // 挥舞中换格子会把手上那件换成别的物品,原版虽不崩但语义全乱;coordinator 每帧按槽号重读
    // inventory,中途换位会让它拿错件 —— 所以一律等空闲。
    static bool InventoryBlocked(Player self) => self.dead || self.itemAnimation != 0 || self.itemTime != 0
        || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning;

    public static JsonObject Swap(JsonObject args)
    {
        int src = args["src"]?.GetValue<int>() ?? -1, dst = args["dst"]?.GetValue<int>() ?? -1;
        var self = Main.LocalPlayer;
        if (src < 0 || src >= 50 || dst < 0 || dst >= 50 || src == dst) return new JsonObject { ["error"] = "invalid_inventory_slots" };
        if (InventoryBlocked(self))
            return new JsonObject { ["error"] = "inventory_busy" };
        bool Matches(Item item, JsonNode expected) => expected is JsonObject
            && expected["type"]?.GetValue<int>() == item.type && expected["prefix"]?.GetValue<int>() == item.prefix
            && expected["stack"]?.GetValue<int>() == item.stack;
        if (!Matches(self.inventory[src], args["expected_src"]) || !Matches(self.inventory[dst], args["expected_dst"]))
            return new JsonObject { ["error"] = "stale_inventory", ["src_item"] = ItemState(self.inventory[src]), ["dst_item"] = ItemState(self.inventory[dst]) };
        return SwapSlots(src, dst);
    }

    public static JsonObject SwapSlots(int src, int dst)
    {
        var self = Main.LocalPlayer;
        if (src < 0 || src >= self.inventory.Length || dst < 0 || dst >= self.inventory.Length || src == dst)
            return new JsonObject { ["error"] = "invalid_inventory_slots" };
        var before = new JsonObject { ["src"] = ItemState(self.inventory[src]), ["dst"] = ItemState(self.inventory[dst]) };
        (self.inventory[src], self.inventory[dst]) = (self.inventory[dst], self.inventory[src]);
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            NetMessage.SendData(MessageID.SyncEquipment, number: self.whoAmI, number2: src);
            NetMessage.SendData(MessageID.SyncEquipment, number: self.whoAmI, number2: dst);
        }
        PvpDuel.InventoryChanged();
        return new JsonObject { ["ok"] = true, ["src"] = src, ["dst"] = dst, ["before"] = before,
            ["after"] = new JsonObject { ["src"] = ItemState(self.inventory[src]), ["dst"] = ItemState(self.inventory[dst]) } };
    }

    // hotbar_place:把指定物品放进指定快捷栏格(0-9)。目标格原有物品与源位置对调
    // (源是背包格则被顶出来的进背包),放完选中该格;纯武器顺手记为常用武器(与 switch_item 同语义)。
    // 和 Swap 的关键差别:【不需要 expected_src/expected_dst】—— 源槽在主线程执行那一刻按名字现查,
    // 模型只报"名字+目标格"即可,从根上消掉 stale_inventory 这道模型最常翻车的闸。
    public static JsonObject PlaceInHotbar(JsonObject args)
    {
        int target = args["slot"]?.GetValue<int>() ?? -1;
        var self = Main.LocalPlayer;
        if (self == null) return new JsonObject { ["error"] = "no_player" };
        if (target < 0 || target > 9)
            return new JsonObject { ["error"] = "bad_target_slot", ["hint"] = "slot 是目标快捷栏格,取 0-9" };

        // 定位源物品:优先 src 显式给槽号,否则按名字在 0-49 里找第一个(逻辑同 /switch_item)
        int src = args["src"]?.GetValue<int>() ?? -1;
        string want = args["name"]?.GetValue<string>() ?? args["item"]?.GetValue<string>();
        if (src < 0 && want != null)
            for (int i = 0; i < 50 && i < self.inventory.Length; i++)
            {
                var it = self.inventory[i];
                if (it == null || it.IsAir) continue;
                if ((it.Name ?? "") == want
                    || (ItemID.Search.ContainsName(want) && ItemID.Search.GetId(want) == it.type))
                { src = i; break; }
            }
        if (src < 0 || src >= self.inventory.Length)
            return new JsonObject { ["error"] = "item_not_found", ["hint"] = "给 src(背包槽号)或 name/item(物品名)" };

        if (InventoryBlocked(self))
            return new JsonObject { ["error"] = "inventory_busy" };

        var item = self.inventory[src];
        if (item == null || item.IsAir)
            return new JsonObject { ["error"] = "empty_slot", ["slot"] = src };

        var before = new JsonObject { ["src"] = ItemState(item), ["target"] = ItemState(self.inventory[target]) };
        if (src != target)
        {
            (self.inventory[src], self.inventory[target]) = (self.inventory[target], self.inventory[src]);
            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                NetMessage.SendData(MessageID.SyncEquipment, number: self.whoAmI, number2: src);
                NetMessage.SendData(MessageID.SyncEquipment, number: self.whoAmI, number2: target);
            }
            PvpDuel.InventoryChanged();
        }
        self.selectedItem = target;
        // 与 switch_item 同语义:纯武器记常用,organize_hotbar 会优先把它排进武器区
        if (item.damage > 0 && item.pick == 0 && item.axe == 0 && item.hammer == 0)
            HttpServerSystem.CommonWeaponType = item.type;

        return new JsonObject { ["ok"] = true, ["placed"] = src != target, ["slot"] = target, ["name"] = item.Name,
            ["before"] = before,
            ["after"] = new JsonObject { ["src"] = ItemState(self.inventory[src]), ["target"] = ItemState(self.inventory[target]) },
            ["selected"] = true };
    }
}
