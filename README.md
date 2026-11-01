# DSTerraria

一个 tModLoader 模组：让 AI 接管泰拉瑞亚，自主游玩。

把泰拉瑞亚的感知、寻路、建造、战斗拆成一套可调用的工具，交给大模型（DeepSeek）决策——从零开始探索、挖矿、合成、建房子、打 boss，全程不用人操作。

## 特性

- **AI 自主决策**：接入 DeepSeek（OpenAI 兼容 API），模型自主调用 30+ 游戏工具完成任务
- **工具原语**：寻路、挖矿、砍树、合成、放置、开箱、建造、战斗，每个原语交回真实结果与失败现场
- **执行期机会感知**：长操作期间每 3 秒扫一次附近，发现宝箱 / 生命水晶 / 稀有矿石就交给模型决定是否拐一趟
- **自动装备与整理**：捡到更好的护甲/武器自动穿戴、镐子归位 2 号位、武器按强弱排 3/4/5 号位
- **背包管理**：背包满自动回家存箱子，一键存入并保留高价值矿石
- **Boss 战**：走位与反击反射层（60Hz）驱动，模型只出意图、代码负责瞄准与时机
- **中文人格**：内置鲸鱼娘人格，说话带脾气

## 启动

1. 下载 release 的 `.tmod` 放进 `tModLoader/Mods/`，游戏内启用；或源码放进 `tModLoader/ModSources/DSTerraria/` 后构建重载
2. 模组配置 → 填写 Agent API Key / Base URL / 模型名（DeepSeek 三件套）
3. 进世界，聊天栏输入 `/start` 启动 AI 自主游玩，`/start stop` 停止
4. 聊天栏输入 `/` + 自然语言（如 `/回家` `/挖矿`）可直接下指令

## 配置

| 配置项 | 说明 |
|---|---|
| Agent API Key | 大模型服务商密钥（如 DeepSeek 的 `sk-...`） |
| Agent Base URL | OpenAI 兼容根地址，如 `https://api.deepseek.com/v1` |
| Agent 模型名 | 模型 ID，如 `deepseek-chat` |

API Key 只存本机 `ModConfigs/DSTerraria.json`，不进源码、不上传。

## 环境

- Terraria 1.4.4.9
- tModLoader（对应 1.4.4.9）
- （可选）DragonLens：关闭刷怪 / 无敌 / 倍速，方便调试

## License

MIT
