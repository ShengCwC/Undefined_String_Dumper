# Undefined String Dumper

Undefined SS Community 的 Windows 内存字符串取证工具。程序直接读取 `java.exe` / `javaw.exe` 的可读内存区域，按照 Process Hacker 2.39 的规则流式提取 ASCII 与零字节分隔宽字符串，用一个界面完成原来需要多步执行的流程。

当前版本是 **v0.3.1**：进程发现、完整进程属性预览、兼容扫描、进度、结果预览、紧凑本地导出、取消和隐私边界均已实现；远端归档尚未接入。

## 当前能力

- 自动发现正在运行的 `java.exe` 与 `javaw.exe`，展示 PID、说明、私有内存占用及启动时间。
- 目标详情包含签名与签名者、文件版本、映像路径、已脱敏命令行、当前目录、启动时间、PEB 地址、映像类型、父进程、缓解策略和保护级别；同一组属性也会写入完整导出文件。
- 命令行中的令牌、密码、Secret 与 API Key 等敏感参数在进入界面和导出层前会被替换为 `[REDACTED]`。
- 默认使用与参考流程一致的扫描规则：最短长度 4、ASCII + Process Hacker 兼容 Unicode、Private + Mapped、排除 Image。
- “Unicode（PH 兼容）”只识别 `可打印单字节 + 00` 形式的宽字符串；不会把任意两个相邻字节误判为 UTF-16。
- 直接调用 Windows 内存接口，不依赖或自动操作 Process Hacker。
- 以 1 MiB 分块读取，不创建完整进程 dump，也不创建临时 txt。
- 扫描结果通过批次接口流出；当前只在界面内存中保留前 20,000 条预览，完整数量仍会统计。
- 支持扫描中止、结果筛选和主动清除内存预览。
- 可选择“完整导出”，重新扫描当前目标并把全部字符串流式写入带 BOM 的 UTF-8 文本；结果行采用紧凑的 `0x地址 (字节长度): 内容` 格式，与 Process Hacker 导出结构对齐。
- 导出先写入同目录的唯一临时文件，成功后再原子替换目标；取消或失败不会破坏已有目标文件。
- 启动时申请管理员权限，并在扫描前要求工作人员确认已取得玩家授权。
- EXE 与窗口使用社区 Logo；发布产物支持 Authenticode SHA-256 签名和时间戳。

## 使用

1. 启动 Minecraft 并进入游戏主界面。
2. 以管理员权限启动 `UndefinedStringDumper.exe`（程序会自动弹出 UAC）。
3. 在左侧确认目标进程及 PID；多个 Java 进程并存时，不要只依赖进程名称。
4. 保持默认扫描配置，确认已获得玩家授权。
5. 点击“开始一键扫描”进行纯内存预览；需要保存全部结果时点击“完整导出”并选择路径。

本版本不会自动保存或上传数据。关闭程序、清除预览或发起新扫描后，旧预览不会形成可恢复的本地结果文件。Windows 本身仍可能将进程内存换页到系统分页文件，因此“零持久化”指应用不主动写入业务数据文件，而不是对操作系统换页作绝对保证。

“完整导出”属于工作人员明确触发的例外：导出文件包含潜在敏感内存字符串，应放在受控位置并按案件保留策略及时删除。

## 构建

需要 Windows 10/11 x64 与 .NET 8 SDK：

```powershell
dotnet build Undefined.StringDumper.sln -c Release
dotnet run --project tests/Undefined.StringDumper.Core.Tests -c Release
```

生成可独立运行的 x64 单文件版本：

```powershell
dotnet publish src/Undefined.StringDumper.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

使用当前用户证书存储中的代码签名证书：

```powershell
.\sign.ps1 -CertificateThumbprint "<thumbprint>"
```

也可通过 `-PfxPath` 与 `-PfxPassword` 使用受保护的 PFX。不要把证书私钥或密码提交到仓库。当前社区自签名证书可以验证发布文件的签名和完整性，但不具备公共 CA 信任链或 SmartScreen 信誉；公共分发仍建议更换为受信任代码签名机构签发的证书。

## 项目结构

- `src/Undefined.StringDumper.Core`：进程发现、Windows 内存读取、区域过滤、字符串流式提取和结果输出接口。
- `src/Undefined.StringDumper.App`：WPF 桌面 UI、扫描编排、内存预览与会话状态。
- `tests/Undefined.StringDumper.Core.Tests`：无第三方测试框架的核心和真实子进程读取验证。
- `tests/Undefined.StringDumper.App.VisualTests`：离屏渲染默认窗口，用于发现布局截断与资源加载问题。
- `docs/upload-architecture.md`：下一阶段 R2 或 Telegram Bot 远端存档构思。
- `sign.ps1`：对发布 EXE 进行 Authenticode SHA-256 签名、时间戳和校验文件生成。

## 已知边界

- 当前仅构建 x64 版本；目标是常见的 64 位 Minecraft Java 客户端。
- Java 进程在扫描中仍会分配、释放内存，少量区域读取失败属于正常动态变化，界面会给出数量。
- 结果只代表在采集时刻可读取到的字符串，不能单独作为作弊定论；需要结合进程来源、模组、文件哈希和工作人员判断。
- 每条结果保留完整起始地址和字节长度；极长连续字符串的显示内容最多保留 8,191 个字符，与 Process Hacker 2.39 的显示缓冲上限一致。

本项目使用 [GNU GPL v3](LICENSE)。
