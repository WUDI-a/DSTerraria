using Terraria;
using Terraria.ModLoader;

namespace DSTerraria;

// 自主游玩:不等人开口,agent 空闲时自动把"下一步目标"递给 NativeAgent。
// 只负责派活和接管战斗反射层开关,执行仍走原有工具循环。
public class AutoPilot : ModSystem
{
	// 空闲 2 秒后才派下一个目标,给玩家打断和收尾留窗口
	const int IdleDelayTicks = 120;
	// 首次(/start 后第一轮)只等 1 秒,尽快把装备和背包状况交给他
	const int FirstDelayTicks = 60;
	// 失败后至少等这么久再试,免得没配 key 时刷屏
	const int RetryDelayTicks = 1200;
	const string Goal =
		"自主游玩模式:玩家不在,没有人会回答,禁止使用 ask,也永远不要停下等待玩家。" +
		"数量、路线、取舍全部自己定,拿不准就选更保守的那个,这也覆盖系统提示里所有要先问玩家的规则。" +
		"总目标:把这个角色从零持续发展,击败你当前装备能挑战的 boss,然后继续发展挑战下一个。" +
		"参考顺序:木材与基地 -> 挖矿升级镐/武器/护甲(攒矿用 mine_vein) -> 建合格 NPC 房 -> " +
		"探索洞穴、开箱、拿生命水晶 -> 用 wiki 查并合成 boss 召唤物 -> 战前准备(药水、场地、平台)后召唤 boss。" +
		"boss 战的走位和挥击由战斗层自动负责,你负责召唤、补给、战前准备和撤离恢复。" +
		"一次只推进一小步:先看 state 和 track 台账再动手。同一件事失败两次就换更早的一步,不要原地重试。" +
		"开局第一件事:看 state.armor_slots(身上穿的装备饰品)和 inventory_slots(快捷栏+背包),搞清楚自己现在穿什么、有什么武器和材料,再决定第一步做什么。后续每轮也先扫一眼 armor_slots,发现光着或装备老旧就优先补上。" +
		"要回地面、回基地、回出生点时,用 use_item 对自己用魔镜(魔镜固定在快捷栏最后一格 slot=9),不要走路爬绳梯。" +
		"挖矿/赶路时系统每 3 秒扫附近,发现宝箱/生命水晶/稀有矿石会中断原语提醒你(outcome=interrupted_by_opportunity):顺路就近开箱(interact+loot_all)、挖水晶、mine_vein 挖稀有矿,不值得就继续主线。" +
		"长期习惯:背包快满就魔镜回家,interact 开箱后 store_inventory 一键存(自动保留银矿金矿铂金矿及以上,低级矿和杂物进箱);星标物品一律不搬进箱子;快捷栏 0-9 一般不动,除非合成/捡到更高级同类再换;地表以上的箱子是家里存储箱,不许挖只能放东西,地表以下的野外宝箱才能开完挖走;要用武器就 switch_item 切过去(武器不在快捷栏会自动换进去,没空位就把非武器换下来)。" +
		"刚做出或捡到护甲/饰品/更好的武器工具,立刻 equip_item 穿上或挪快捷栏,别裸奔。" +
		"每轮的正文都必须说一句话,一两句就够,哪怕只是'在挖矿';连续沉默不允许。";

	static long _idleAt = -1;
	// 【不能给 int.MinValue】now - MinValue 会溢出成负数,守卫永远过不去,autopilot 就永远不派发
	static long _lastSubmit = -1_000_000;
	// /start 之后的第一轮用短延迟(1s),之后恢复常规 5s 窗口
	static bool _firstSubmit;
	static bool _wasOn;
	static bool _warned;
	// Status 拦截的诊断只打一次,变了才再打,免得刷屏
	static string _busySaid = "";
	// 任务僵死判定:距上次反馈超过这个毫秒数(> build_house 的 900s 上限)就强制重置。
	// 正常任务每轮工具执行都会刷新反馈时间,唯一不刷新的是挂死的等待(如 ask 等玩家回答)
	const long StuckResetMs = 20 * 60 * 1000;

	// 手动启动开关:聊天栏 /start 开、/start stop 关。开启后 AutoPilot 开始派活
	public static bool Launched;

	public static string Toggle(string arg)
	{
		if (arg == "stop")
		{
			Launched = false;
			NativeAgent.Stop();
			Combat.Enabled = Config.I?.FightBack == true;
			Dodge.Enabled = Config.I?.DodgeBoss == true;
			return "AI 自主模式已停。";
		}
		Launched = true;
		_idleAt = -1;
		_lastSubmit = -1_000_000;
		_firstSubmit = true;
		_busySaid = "";
		// 退出世界时 stop 设的人工残留,一并清掉
		if (Manual.On) Manual.On = false;
		return "AI 自主模式启动,交给他了。";
	}

	public override void OnWorldLoad()
	{
		// 换世界不延续上一局的启动状态
		Launched = false;
		_firstSubmit = true;
		// 退出世界时 Run("stop") 会把 Manual.On 设成 true,新世界进来战斗反射层
		// 全被"人工"挡住。这个残留无条件清掉;玩家中途按 P 不受影响
		if (Manual.On)
		{
			Manual.On = false;
			DiagLog.Write("[autopilot] 清掉人工模式残留(世界退出时 stop 设的)");
		}
	}

	public override void Unload()
	{
		_idleAt = -1;
		_wasOn = false;
		_busySaid = "";
	}

	public override void PostUpdateEverything()
	{
		if (Main.dedServ) return;
		var cfg = Config.I;
		bool on = Launched && cfg != null && cfg.AgentEnabled && AgentNetwork.CanAutomate;
		if (on != _wasOn)
		{
			_wasOn = on;
			_idleAt = -1;
			_warned = false;
			if (!on)
			{
				// 还原成配置的意图,别把反射层留在接管状态
				Combat.Enabled = cfg?.FightBack == true;
				Dodge.Enabled = cfg?.DodgeBoss == true;
			}
		}
		if (!on) return;
		if (Main.gameMenu || !Main.LocalPlayer.active || Main.LocalPlayer.dead) { _idleAt = -1; return; }

		// 自主模式下常开走位与反击:没 boss 时 Dodge 自己退出,Combat 只在目标近身时出手。
		// 工具执行时本来就会暂时接管角色,和原有的互斥机制走同一条路。
		Dodge.Enabled = true;
		Combat.Enabled = true;

		// 没配好后端就不派活,只提示一次
		if (string.IsNullOrWhiteSpace(cfg.AgentApiKey) || string.IsNullOrWhiteSpace(cfg.AgentModel)
			|| !System.Uri.TryCreate(cfg.AgentBaseUrl, System.UriKind.Absolute, out _))
		{
			if (!_warned) { DiagLog.Write("[autopilot] Agent API 未配置(Key/Base URL/Model),不派活"); _warned = true; }
			return;
		}

		if (NativeAgent.Status is not ("idle" or "failed"))
		{
			_idleAt = -1;
			// 任务僵死(如 ask 在等永远不会来的玩家回答、HTTP 挂死):距上次反馈超过阈值就强制重置。
			// 正常长任务(build_house 最长 15 分钟)虽然 Wait 不刷新反馈,但 20 分钟 > 900s 上限,不会误杀
			if (NativeAgent.FeedbackIdleMs > StuckResetMs)
			{
				DiagLog.Write($"[autopilot] 任务僵死 {NativeAgent.FeedbackIdleMs / 60000} 分钟无反馈(status={NativeAgent.Status}),强制重置");
				NativeAgent.Stop();
			}
			else if (NativeAgent.Status != _busySaid)
			{
				_busySaid = NativeAgent.Status;
				DiagLog.Write($"[autopilot] agent 忙(status={NativeAgent.Status}),不派发;僵死 {NativeAgent.FeedbackIdleMs / 1000}s/{StuckResetMs / 1000}s");
			}
			return;
		}
		_busySaid = "";
		long now = Main.GameUpdateCount;
		if (now - _lastSubmit < RetryDelayTicks) return;
		if (_idleAt < 0) { _idleAt = now; return; }
		// 首轮等 1 秒就派(尽快把装备/背包状况交出去),之后每轮等 2 秒留打断窗口
		int delay = _firstSubmit ? FirstDelayTicks : IdleDelayTicks;
		if (now - _idleAt < delay) return;
		_idleAt = -1;
		_lastSubmit = now;
		_firstSubmit = false;
		DiagLog.Write("[autopilot] 派发自主目标");
		NativeAgent.Submit(Goal);
	}
}

// /start 启动 AI 自主模式,/start stop 停止。原纯代码速通挪到了 /speedrun
public class AutoStartCommand : Terraria.ModLoader.ModCommand
{
	public override Terraria.ModLoader.CommandType Type => Terraria.ModLoader.CommandType.Chat;
	public override string Command => "start";
	public override string Description => "启动 AI 自主游玩(DeepSeek 决策)。/start stop 停止;纯代码速通在 /speedrun";

	public override void Action(Terraria.ModLoader.CommandCaller caller, string input, string[] args)
	{
		if (!AgentNetwork.CanAutomate) { caller.Reply("请用 /ai 向 AI 玩家下达指令。"); return; }
		var cfg = Config.I;
		if (cfg == null || !cfg.AgentEnabled) { caller.Reply("内置 agent 已关闭。"); return; }
		if (string.IsNullOrWhiteSpace(cfg.AgentApiKey) || string.IsNullOrWhiteSpace(cfg.AgentModel)
			|| !System.Uri.TryCreate(cfg.AgentBaseUrl, System.UriKind.Absolute, out _))
		{ caller.Reply("先在模组配置里填好 Agent API Key、Base URL 和模型名。"); return; }
		caller.Reply(AutoPilot.Toggle(args.Length > 0 ? args[0] : ""));
	}
}
