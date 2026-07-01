<div align="center">

# 🔍 dnSpy Analyzer

**命令行 .NET 程序集逆向分析工具**  
把 DLL 扔给它，JSON 结果秒出 — 游戏 Mod 利器，AI Agent 友好

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4.svg)]()

</div>

---

## ✨ 特性

- **🔬 程序集扫描** — 一键扫描目录，自动识别 .NET 程序集，跳过非托管 DLL
- **📋 元数据分析** — 版本、依赖、命名空间分布、类型数量即时获取
- **📂 类型浏览** — 按命名空间过滤、分页查看，基类/接口/成员一目了然
- **🛠️ 方法反编译** — 基于 ILSpy 引擎，将 IL 还原为可读 C# 源码
- **🔎 智能搜索** — 按类型名、方法名、字段名、属性名快速定位目标
- **🤖 AI Agent 原生** — JSON 标准输出，零配置，直接集成

## 🔌 即放即用

**这个文件夹是独立的**，不依赖原始 dnSpy 源码，可以放在任何位置直接使用。

最常见的用法是直接扔到游戏目录下：

```
E:/game/
├── game.exe
├── Managed/                     ← 游戏的 DLL 文件夹
│   ├── Assembly-CSharp.dll
│   └── ...
└── analyzer/                    ← 直接把 analyzer 整个文件夹放这里
    ├── build.ps1
    ├── src/
    ├── README.md
    └── ...
```

然后在游戏目录下直接分析：

```bash
cd E:/game

# 扫描 DLL
dotnet run --project analyzer/src/DnSpy.Analyzer.Cli -- scan-folder Managed

# 分析程序集结构
dotnet run --project analyzer/src/DnSpy.Analyzer.Cli -- analyze-assembly Managed/Assembly-CSharp.dll

# 反编译方法
dotnet run --project analyzer/src/DnSpy.Analyzer.Cli -- decompile-method Managed/Assembly-CSharp.dll Game.Core.Player TakeDamage
```

> 如果装了 .NET 8 SDK，甚至不用构建，`dotnet run` 直接跑。

## 前置条件

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（装一次，所有项目通用）
- Windows 7+

## ⚡ 快速开始

### 方式一：直接运行（推荐，无需构建）

```bash
cd 游戏目录
dotnet run --project analyzer/src/DnSpy.Analyzer.Cli -- scan-folder Managed
```

### 方式二：构建一次，全局调用

```bash
cd analyzer
.\build.ps1

# 编译产物：src/DnSpy.Analyzer.Cli/bin/Release/net8.0/analyzer.exe
# 把这个 exe 加到 PATH 后，随处可用：
analyzer scan-folder E:/game/Managed
```

## 📖 使用指南

### 子命令一览

| 命令 | 必填参数 | 可选参数 | 功能 |
|------|---------|---------|------|
| `scan-folder` | `<path>` | `--no-recursive` | 扫描文件夹中的 .NET 程序集 |
| `analyze-assembly` | `<path>` | — | 程序集元数据、依赖、命名空间 |
| `list-types` | `<path>` | `--namespace`, `--offset`, `--limit` | 类型列表（支持过滤+分页） |
| `get-type` | `<path> <type-name>` | — | 类型详情（基类、接口、全部成员） |
| `get-methods` | `<path> <type-name>` | — | 方法签名列表 |
| `decompile-method` | `<path> <type-name> <method-name>` | — | **反编译方法为 C# 源码** |
| `decompile-type` | `<path> <type-name>` | — | 反编译整个类型 |
| `search` | `<path> <query>` | `--kind`, `--max-results` | 搜索类型/方法/字段/属性 |
| `help` | — | — | 显示帮助信息 |

### 完整工作流示例

```bash
# 1️⃣ 扫描目录，看看有哪些 DLL
analyzer scan-folder ./Managed

# 2️⃣ 分析主程序集结构
analyzer analyze-assembly ./Managed/Assembly-CSharp.dll

# 3️⃣ 浏览特定命名空间下的类型
analyzer list-types ./Managed/Assembly-CSharp.dll --namespace Game.Core

# 4️⃣ 深入了解某个类
analyzer get-type ./Managed/Assembly-CSharp.dll Game.Core.PlayerController

# 5️⃣ 反编译关键方法
analyzer decompile-method ./Managed/Assembly-CSharp.dll Game.Core.PlayerController TakeDamage

# 6️⃣ 搜索特定关键词
analyzer search ./Managed/Assembly-CSharp.dll health --kind field
```

### 输出格式

所有结果输出到 **stdout**，错误输出到 **stderr**，统一 JSON：

```json
// 成功
{"success":true,"data":{...},"elapsedMs":12,"error":null}

// 失败
{"success":false,"data":null,"elapsedMs":5,"error":"File not found: xxx"}
```

### AI Agent 调用

Agent 直接通过 bash 调用，无需任何配置：

```bash
scan-folder  →  得到 DLL 列表
    ↓
analyze-assembly  →  程序集结构
    ↓
list-types --namespace Game.Core  →  浏览类型
    ↓
get-type Game.Core.Player  →  类型详情
    ↓
decompile-method Player TakeDamage  →  得到 C# 源码
```

## 🏗️ 架构

```
┌─────────────────────────────────────┐
│    AI Agent / 终端                    │
└──────────────┬──────────────────────┘
       bash / CLI
┌──────────────▼──────────────────────┐
│  DnSpy.Analyzer.Cli                  │
│  CLI 入口 · 子命令路由 · JSON 序列化  │
└──────────────┬──────────────────────┘
        引用
┌──────────────▼──────────────────────┐
│  DnSpy.Analyzer.Core                 │
│  ┌──────────┐ ┌───────────┐ ┌──────┐ │
│  │Assembly  │ │Decompil.  │ │Search│ │
│  │Analyzer  │ │Helper     │ │Svc   │ │
│  └────┬─────┘ └─────┬─────┘ └──┬───┘ │
│       │             │          │      │
│  System.Reflection  ICSharpCode│      │
│  .Metadata (内置)   .Decompiler│      │
└──────────────────────────────────────┘
```

**关键依赖**：

| 组件 | 用途 | 许可证 |
|------|------|--------|
| [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) | C# 反编译引擎 | MIT |
| System.Reflection.Metadata | PE/元数据读取（.NET 内置） | MIT |
| Newtonsoft.Json | JSON 序列化 | MIT |

> 本工具**不依赖、不修改、不包含**原始 dnSpy GUI 的任何代码，仅借鉴了其反编译的技术思路。原始 dnSpy ([GitHub](https://github.com/dnSpy/dnSpy)) 是 GPLv3 开源项目。

## 🎮 游戏 Mod 速查表

常见 Unity 游戏逆向目标：

| 目标 | 搜索关键词 | 典型位置 |
|------|-----------|---------|
| ❤️ 血量 | `Health`, `hp`, `TakeDamage` | 玩家/敌人控制器 |
| ⚔️ 伤害 | `Damage`, `AttackPower`, `OnHit` | 战斗系统 |
| 💰 经济 | `Gold`, `Coin`, `Money`, `Price` | 商店/掉落系统 |
| 📦 物品 | `Item`, `Inventory`, `Drop` | 背包系统 |
| 🎯 经验 | `Exp`, `Level`, `XP` | 升级系统 |
| ⏱️ 冷却 | `Cooldown`, `CD`, `Timer` | 技能系统 |
| 💾 存档 | `Save`, `Load`, `Serialize` | 存档管理 |
| 🏃 移动 | `MoveSpeed`, `JumpForce` | 角色控制器 |

## 🛠️ 构建

```bash
.\build.ps1                     # Release 构建
.\build.ps1 -Configuration Debug # Debug 构建
```

编译产物：`src/DnSpy.Analyzer.Cli/bin/Release/net8.0/analyzer.exe`

### 项目结构

```
analyzer/
├── src/
│   ├── DnSpy.Analyzer.Core/       # 核心分析库
│   │   ├── Models/                # 数据模型
│   │   ├── AssemblyAnalyzer.cs    # 程序集扫描/分析
│   │   ├── DecompilationHelper.cs # C# 反编译
│   │   └── SearchService.cs       # 搜索服务
│   └── DnSpy.Analyzer.Cli/        # CLI 命令行工具
│       └── Program.cs             # 入口 + 8 个子命令
├── build.ps1                      # 构建脚本
├── .gitignore
├── CONTRIBUTING.md                # 贡献指南
├── LICENSE                        # MIT 许可证
└── README.md                      # 本文件
```

## ❓ 常见问题

<details>
<summary><b>decompile-method 提示 "ICSharpCode.Decompiler not available"？</b></summary>

确保 NuGet 包已还原：

```bash
dotnet restore src/DnSpy.Analyzer.Core/DnSpy.Analyzer.Core.csproj
```
</details>

<details>
<summary><b>能分析非托管（native）DLL 吗？</b></summary>

不能。`scan-folder` 会自动检测 PE 头中的 CLI 标志，跳过所有非 .NET 文件。
</details>

<details>
<summary><b>支持哪些 .NET 版本？</b></summary>

全部支持。使用 `System.Reflection.Metadata` 读取 PE 元数据，兼容 .NET Framework 2.0~4.8、.NET Core、.NET 5/6/7/8。
</details>

<details>
<summary><b>和 dnSpy GUI 有什么区别？</b></summary>

dnSpy GUI 是完整的桌面应用程序（WPF），提供交互式浏览、编辑、调试。本工具是轻量级命令行工具，专注于程序化分析和 AI Agent 集成，没有 GUI。
</details>

## 📄 许可证

本项目采用 [MIT 许可证](LICENSE)。  
反编译引擎 [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) 基于 MIT 许可证。

---

<div align="center">
Made with ❤️ for the game modding & reverse engineering community
</div>
