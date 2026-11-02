using System.Threading;
using Terraria;

namespace DSTerraria
{
	// 【换装窗口】。swap/hotbar_place 这类整背包操作期间,NPC 战斗输入按下暂停:
	// Combat/FightCoordinator/PlayerCombat 每帧重写 selectedItem + controlUseItem,
	// 不暂停的话,刚摆好的快捷栏布局和选中状态下一帧就被战斗改掉,玩家看着就是"放不住"。
	// 时钟用 GameUpdateCount(只有主线程递增),写入可能来自 agent 线程或 HTTP 线程,
	// 全部走 Interlocked;只延长不缩短,晚到的写入不会吃掉别人开的窗口。
	public static class CombatPause
	{
		private static long _until;   // 截止时刻(GameUpdateCount)

		public static bool Active => (long)Main.GameUpdateCount < Interlocked.Read(ref _until);

		// 从现在起暂停 seconds 秒。已经在暂停中且剩余更长则保持不变
		public static void ForSeconds(float seconds)
		{
			long deadline = (long)Main.GameUpdateCount + (long)(seconds * 60f);
			long cur;
			do
			{
				cur = Interlocked.Read(ref _until);
				if (deadline <= cur) return;
			}
			while (Interlocked.CompareExchange(ref _until, deadline, cur) != cur);
		}
	}
}
