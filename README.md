<div align="center">
  <img src="src/WordFlow.App/Assets/Icons/wordflow-source.png" width="360" alt="WordFlow">
  <h1>WordFlow</h1>
  <p>一个完全离线、悬浮置顶的 Windows 雅思词汇学习工具。</p>
  <p>
    <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-2563EB">
    <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4">
    <img alt="Offline" src="https://img.shields.io/badge/Mode-100%25%20Offline-10B981">
    <img alt="Tests" src="https://img.shields.io/badge/Tests-623%20passed-16A34A">
  </p>
</div>

> WordFlow 是独立开发项目，与“百词斩”官方无隶属、授权或合作关系。

## 为什么做 WordFlow

WordFlow 把单词学习压缩成一张始终可见的小卡片：不需要切换窗口，不依赖网络，按一次快捷键就完成判断并进入下一词。学习计划、复习状态、皮肤、快捷键和历史数据全部保存在本机。

## 主要功能

- **12,046 个离线学习词条**：英文、音标、简体中文释义全部随项目提供。
- **FSRS-6 复习调度**：结合遗忘风险安排每日复习；`不认识 / 模糊 / 斩` 默认对应 `F1 / F2 / F3`。
- **可靠的每日队列**：支持当天断点续学、跨日补齐、延迟重学、撤销和崩溃恢复。
- **斩词与取消斩**：斩过的词不再进入学习队列，也可在已斩词汇页恢复。
- **近义词与形近词**：离线查看释义、对比说明和容易混淆的拼写关系。
- **悬浮卡片**：可拖动、隐藏、调透明度，并可开启不抢焦点的强力置顶。
- **自定义皮肤**：支持 PNG、JPG、JPEG；背景拉伸铺满，透明度实时预览并自动保存。
- **离线发音**：使用 Windows 本机语音，自动复读次数可设为 1–10 次。
- **控制中心**：设置每日新学/复习目标、快捷键、语音、外观、置顶和学习计划。
- **长期进度**：显示已斩词数、总目标百分比以及最近 183 天学习记录。
- **隐私友好**：没有账号、云同步、遥测或联网词典，学习数据只存在本机。

## 下载与使用

前往 [Releases](https://github.com/smyhwlx/WordFlow/releases/latest) 下载 `WordFlow-Windows-x64.zip`：

1. 解压整个压缩包，不要只单独复制 EXE。
2. 双击 `WordFlow.exe`。
3. Windows SmartScreen 如提示“未知发布者”，请选择“更多信息 → 仍要运行”。当前公开构建未购买代码签名证书。

程序支持 Windows 10 19041 及以上版本、Windows 11，仅提供 x64 构建。程序运行后默认显示在屏幕右下角，也可从系统托盘打开控制中心或退出。

## 默认快捷键

| 操作 | 默认快捷键 | 说明 |
| --- | --- | --- |
| 不认识 | `F1` | 进入高优先级复习 |
| 模糊 | `F2` | 按调度算法安排复习 |
| 斩 | `F3` | 标记为已斩并切换下一词 |
| 撤销 | `Ctrl + Z` | 撤销最近一次学习操作 |
| 近义词 | 可自定义 | 打开当前词的近义词面板 |
| 形近词 | 可自定义 | 打开当前词的形近词面板 |

所有快捷键均可在控制中心修改。

## 从源码运行

### 环境

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PowerShell 5.1 或更高版本

### 构建与测试

```powershell
git clone https://github.com/smyhwlx/WordFlow.git
cd WordFlow
dotnet restore WordFlow.sln
dotnet test WordFlow.sln -c Release
dotnet run --project src\WordFlow.App\WordFlow.App.csproj -c Release
```

生成可分发的单图标版本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-public-release.ps1
```

产物位于 `artifacts/public-release/`。`WordFlow.exe` 必须和 `Data/` 文件夹放在一起。

## 项目结构

```text
WordFlow/
├─ src/
│  ├─ WordFlow.Domain/          # 学习状态、FSRS-6、队列规则
│  ├─ WordFlow.Application/     # 用例与端口
│  ├─ WordFlow.Infrastructure/  # SQLite、Windows API、语音与单实例
│  └─ WordFlow.App/             # WPF 界面与控制中心
├─ tests/                       # Domain/Application/Infrastructure/App 测试
├─ data/
│  ├─ ielts/                    # 可直接运行的离线 SQLite 词库
│  ├─ curated/                  # 人工整理的闭包、形近词与审批规则
│  └─ licenses/                 # 第三方数据许可证
├─ tools/vocabulary/            # 词库与关系表构建、校验工具
├─ scripts/                     # 构建和验证脚本
└─ docs/                        # 架构、算法、数据策略与验证记录
```

## 数据说明

雅思官方并不存在一份公开、穷尽且固定的“官方完整词表”。WordFlow 的 12,046 个词条是依据考试标签、学术词汇、词频和必需的形近词闭包构建的质量门控学习集合：

- 中英文词条内容主要来自 [ECDICT](https://github.com/skywind3000/ECDICT)（MIT）。
- 词义关系证据来自 [Open English WordNet 2025](https://en-word.net/)（CC BY 4.0，并包含 Princeton WordNet 许可要求）。
- 项目内人工整理的易混词组、中文对比说明和构建规则可在 `data/curated/` 与 `tools/vocabulary/` 查看。

详细来源、哈希与许可信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)、`data/curated/source_registry.json` 和 `data/licenses/`。

## 本机数据位置

用户学习记录、设置和导入皮肤默认保存在：

```text
%LOCALAPPDATA%\WordFlow\
```

卸载或替换程序不会主动删除该目录。需要重置时，请先退出 WordFlow 并自行备份后再处理。

## 贡献与问题反馈

欢迎通过 [Issues](https://github.com/smyhwlx/WordFlow/issues) 报告崩溃、词条错误、形近词遗漏或交互建议。提交问题时建议附上 Windows 版本、复现步骤和相关单词；请勿上传包含个人学习记录的数据库。

## 许可说明

当前仓库未为 WordFlow 自有源代码授予额外的开源许可证；公开可见不等于自动获得复制、修改或商业分发授权。第三方词典与关系数据分别遵循其原始许可证，详见第三方声明。后续如确定项目许可证，将在仓库根目录单独增加 `LICENSE`。

## ☕ 请作者喝杯咖啡

如果 WordFlow 对你有帮助，欢迎支持作者继续维护词库、修复问题和改进 Windows 体验。感谢每一位大气老板！

<div align="center">
  <img src="docs/assets/donate-qr.png" width="420" alt="请作者喝杯咖啡">
</div>
