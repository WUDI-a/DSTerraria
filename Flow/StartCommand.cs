using Terraria.ModLoader;

namespace TerraBlind
{
	// /speedrun,原 /start 的全流程(纯代码速通,不经过 AI)。名字让给 AI 自主模式了
	public class StartCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat;
		public override string Command => "speedrun";
		public override string Description => "纯代码速通全流程:砍树 → 收火把 → 盖房子 → 下地狱 → 打肉山(不经 AI)";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (!AgentNetwork.CanAutomate) { caller.Reply("请用 /ai 向 AI 玩家下达指令。"); return; }
			if (args.Length > 0 && args[0] == "stop")
			{
				StartRun.Stop();
				caller.Reply("停了。");
				return;
			}
			if (!StartRun.Start(out string why)) caller.Reply($"起不来:{why}");
			else caller.Reply("开工。");
		}
	}
}
