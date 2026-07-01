# 贡献指南

感谢你对 dnSpy Analyzer 感兴趣！欢迎各种形式的贡献。

## 🐛 报告 Bug

提交 Issue 时请包含：

- 使用的命令和参数
- 完整的 JSON 输出（或错误信息）
- 目标 DLL 信息（.NET 版本、x86/x64）
- 运行环境（Windows 版本、.NET SDK 版本）

## 💡 提交功能建议

欢迎提交功能建议！包括但不限于：

- 新的分析能力（如 IL 指令级字符串搜索）
- 更好的输出格式
- 新的子命令

## 🔧 开发

### 环境要求

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 7+（目标 DLL 均为 Windows PE 格式）
- 任意代码编辑器（推荐 VS Code / Rider / Visual Studio）

### 本地开发

```bash
# 克隆
git clone <your-fork-url>
cd analyzer

# 构建
.\build.ps1 -Configuration Debug

# 运行
dotnet run --project src/DnSpy.Analyzer.Cli -- help

# 测试（扫描自身输出目录）
dotnet run --project src/DnSpy.Analyzer.Cli -- scan-folder src/DnSpy.Analyzer.Cli/bin/Debug/net8.0
```

### 代码规范

- 遵循 .NET 命名规范：PascalCase 公开成员，camelCase 局部变量
- 所有公开 API 需有 XML 文档注释
- 新增功能需保证 `dotnet build` 无错误无警告

## 📦 项目结构

```
src/
├── DnSpy.Analyzer.Core/      # 核心库 — 新增分析能力在此
│   ├── Models/                # 数据模型
│   ├── AssemblyAnalyzer.cs    # 程序集扫描 + 分析
│   ├── DecompilationHelper.cs # C# 反编译
│   └── SearchService.cs       # 搜索
└── DnSpy.Analyzer.Cli/        # CLI 入口 — 新增子命令在此
    └── Program.cs
```

## 🚀 发布流程

1. 更新 `DnSpy.Analyzer.Cli.csproj` 中的版本号
2. 更新 README 中的示例（如有 API 变更）
3. 运行 `.\build.ps1` 确保 Release 构建通过
4. 提交 PR

## 📄 许可证

贡献代码即表示你同意将你的贡献以 [MIT 许可证](LICENSE) 授权。
