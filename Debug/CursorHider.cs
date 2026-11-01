using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ModLoader;

namespace DSTerraria;

// AI 接管时把游戏指针藏起来。瞄准本身是写 Main.mouseX/Y 的功能,藏的只是那支箭的贴图。
// 恢复条件:真实鼠标一动、开背包、开聊天、回主菜单,立刻还原,人随时能接管。
public class CursorHider : ModSystem
{
	public static bool Enabled;

	// 真实硬件鼠标的位置,不受 Main.mouseX 写入影响
	static int _lastMx = -1, _lastMy = -1;
	static uint _movedAtTick;
	const uint ReappearDelayTicks = 60;

	public override void Load()
	{
		if (Main.dedServ) return;
		On_Main.DrawInterface_36_Cursor += Hook;
	}

	public override void Unload()
	{
		if (Main.dedServ) return;
		On_Main.DrawInterface_36_Cursor -= Hook;
	}

	static void Hook(On_Main.orig_DrawInterface_36_Cursor orig)
	{
		if (Enabled && ShouldHide()) return;
		orig();
	}

	static bool ShouldHide()
	{
		if (Main.gameMenu || Main.playerInventory || Main.drawingPlayerChat) return false;
		var ms = Mouse.GetState();
		if (ms.X != _lastMx || ms.Y != _lastMy)
		{
			_lastMx = ms.X;
			_lastMy = ms.Y;
			_movedAtTick = Main.GameUpdateCount;
		}
		if (Main.GameUpdateCount - _movedAtTick < ReappearDelayTicks) return false;
		var p = Main.LocalPlayer;
		if (p == null || !p.active) return false;
		// 键盘有人按就当人工接管(不带 controlUseItem:那是 AI 挥武器也在写的)
		bool manual = p.controlLeft || p.controlRight || p.controlUp || p.controlDown
			|| p.controlJump || p.controlHook || p.controlMount || p.controlInv;
		return !manual;
	}
}
