using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

// 聊天栏输入 "/" + 自然语言(如 /挖矿、/回家、/打肉山)时,把整句交给 DeepSeek 决策。
// 游戏自己的命令(/start /tb /vis /halt /speedrun /bossbook /ai /help 等)不受影响:
// 它们走 CommandLoader,这里只在"提交的那一帧"检测并过滤掉已知命令。
public class SlashChatSystem : ModSystem
{
	// 已知命令清单:这些是玩家点给游戏/模组的,不喂给 AI。
	// 用 CommandLoader 判不掉(它是 internal),这里静态列一份,和注册处保持一致
	static readonly System.Collections.Generic.HashSet<string> Known = new()
	{
		"start", "speedrun", "tb", "ai", "vis", "halt", "dyn", "woffight",
		"bossbook", "tbdiag", "help", "playing", "me", "p", "party", "emoji"
	};

	static string _prev = "";

	public override void PostUpdateEverything()
	{
		if (Main.dedServ) return;
		if (Main.gameMenu) { _prev = ""; return; }
		string now = Main.chatText ?? "";
		// 提交瞬间:上一帧有内容、这一帧被清空(回车提交 → ClosePlayerChat 清空)
		bool submitted = _prev.Length > 0 && now.Length == 0;
		string last = _prev;
		_prev = now;
		if (!submitted) return;
		if (!Config.I.AgentEnabled || !AgentNetwork.CanAutomate) return;
		if (string.IsNullOrWhiteSpace(last) || last[0] != '/') return;

		// 抽出命令名(第一个空格前,去掉斜杠),已知命令不喂 AI
		string cmd = last.Substring(1).Trim();
		int sp = cmd.IndexOf(' ');
		if (sp >= 0) cmd = cmd.Substring(0, sp);
		cmd = cmd.ToLowerInvariant();
		if (Known.Contains(cmd)) return;

		// 玩家的自然语言指令:去掉斜杠,交给 DeepSeek 决定立即执行还是延后
		string text = last.Substring(1).Trim();
		if (string.IsNullOrWhiteSpace(text)) return;
		DiagLog.Write($"[slash] 玩家自然语言指令: {text}");
		AgentChat.Display($"<you → TB> {text}", 120, 200, 255);
		NativeAgent.Submit(text);
	}
}
