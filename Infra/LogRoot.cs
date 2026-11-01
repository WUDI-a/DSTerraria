using System;
using System.IO;
using Terraria;

namespace DSTerraria
{
	// 【一次会话一个目录】。所有日志本来都写死在 TerraBlindLogs/ 下的同名文件里,永远追加:
	// 上一局的 plan.log 和这一局混在一行行之间,而帧号(GameUpdateCount)每次进世界从头数,
	// 于是"最新那段"只能靠坐标和 H 猜。读一次日志要先花几分钟分辨哪段是哪局。
	//
	// 现在进世界时定一个带时间戳的子目录,这一局的全部日志都在里面。latest 是指向它的软链,
	// 读日志时直接看 latest/ 就是最近这一局,不用挑。
	public static class LogRoot
	{
		static string _dir;

		// 所有日志的根。跨平台由 Main.SavePath 决定,绝不自己拼绝对路径
		public static string Root => Path.Combine(Main.SavePath, "TerraBlindLogs");

		public static string Dir
		{
			get
			{
				if (_dir != null) return _dir;
				try
				{
					string root = Root;
					_dir = Path.Combine(root, DateTime.Now.ToString("MMdd_HHmmss"));
					Directory.CreateDirectory(_dir);
					Point(root, _dir);
				}
				catch { _dir = ""; }
				return _dir;
			}
		}

		// latest 软链。软链建不了就退回写一个记着路径的文本文件 --- 至少还能一眼看出是哪个目录
		static void Point(string root, string target)
		{
			string link = Path.Combine(root, "latest");
			try
			{
				if (Directory.Exists(link) || File.Exists(link))
				{
					var fi = new FileInfo(link);
					if (fi.LinkTarget != null) fi.Delete();
					else { File.WriteAllText(Path.Combine(root, "latest.txt"), target); return; }
				}
				// Windows 上建软链要管理员或开发人员模式,没有就直接走 latest.txt,
				// 别试 -- 抛出的 IOException 会被 tML 当"静默捕获异常"打进日志刷屏
				if (!SymlinkAllowed())
				{ File.WriteAllText(Path.Combine(root, "latest.txt"), target); return; }
				Directory.CreateSymbolicLink(link, target);
			}
			catch
			{
				try { File.WriteAllText(Path.Combine(root, "latest.txt"), target); } catch { }
			}
		}

		static bool? _symlinkOk;

		// Windows:管理员直接行;否则查开发人员模式注册表(开了它普通用户也能建软链)。
		// 非 Windows(macos/linux)不做限制。结果缓存,一次会话只查一回
		static bool SymlinkAllowed()
		{
			if (_symlinkOk.HasValue) return _symlinkOk.Value;
			if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
				    System.Runtime.InteropServices.OSPlatform.Windows))
				return (_symlinkOk = true).Value;
			bool ok;
			try
			{
				using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
				ok = new System.Security.Principal.WindowsPrincipal(id)
					.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
				if (!ok)
				{
					using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
						@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
					ok = key?.GetValue("AllowDevelopmentWithoutDevLicense") as int? == 1;
				}
			}
			catch { ok = false; }
			return (_symlinkOk = ok).Value;
		}

		// 换世界/重进就该换目录。下一次取 Dir 时重新建
		public static void NewSession()
		{
			_dir = null;
			DiagLog.ResetPaths();
			EventLog.ResetPaths();
		}
	}

	// 每次进世界换一个新目录。mod 不会因为退出世界而重载,不挂这个钩子的话
	// 第二局会接着写第一局的目录 --- 那就白分了。
	public class LogRootSystem : Terraria.ModLoader.ModSystem
	{
		public override void OnWorldLoad() => LogRoot.NewSession();
	}
}
