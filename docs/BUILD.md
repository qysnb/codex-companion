# 构建

需要 Windows x64 和 .NET SDK 8 或更新版本。没有第三方 NuGet 依赖。

公开仓库包含运行时所需的七套 `registered.png`、图标和台词；原始参考图、绘图记录与 `sheet.png` 不包含在内。普通构建不依赖这些原始图。下文的 `--prepare-art` 是开发阶段处理自己的源图的可选命令，不是运行或构建的必要步骤。

```powershell
dotnet restore tests/Companion.Tests/Companion.Tests.csproj --configfile NuGet.Config
dotnet run --project tests/Companion.Tests/Companion.Tests.csproj --no-restore
dotnet restore src/Companion.App/Companion.App.csproj --configfile NuGet.Config
dotnet build src/Companion.App/Companion.App.csproj --no-restore
dotnet publish src/Companion.App/Companion.App.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=8.0.32 -p:RestoreSources=https://api.nuget.org/v3/index.json -o dist/app
dotnet publish src/Companion.Cli/Companion.Cli.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=8.0.32 -p:RestoreSources=https://api.nuget.org/v3/index.json -o dist/cli
```

最后一步只从微软 NuGet 源取得运行时包。不要禁用 TLS 验证。如果仅需要利用机器已有的 .NET 8 Desktop Runtime，使用 `--self-contained false -o dist/lite` 发布。

将 dist/cli 中以 `CodexCompanion.Cli` 开头的四个文件复制进 dist/app，复用相同微软运行时。它是短时控制台客户端，不常驻。Lite 版的主程序和客户端都以 `--self-contained false` 发布。

将 plugin 的 `.codex-plugin` 和 `skills` 文件夹拷贝到 `marketplace/plugins/codex-companion`，将发布文件拷贝到该插件的 `bin` 子目录，再将 `packaging/marketplace.json` 放入 `marketplace/.agents/plugins/marketplace.json`。只分发此目录和说明文档，不分发原型 PowerShell 脚本、测试程序、登录信息或开发缓存。

素材校验使用伴侣自己的 `--check-assets` 命令，离线检查七套透明精灵图和 630 条台词。`--render-preview <输出PNG>` 只渲染自身界面，不捕获其他应用或聊天。

测试失败会打印一行 FAIL 并返回 1。`--verify-failure-exit` 可验证这个退出路径，不触发未处理异常弹窗。

0.2.0提供 `--verify-ui` 离屏原生窗口测试，使用明确标记的合成缓存检查位置不变、6DIP气泡间距和150ms缓存显示目标。`--verify-ui-live` 通过官方API后台预取真实额度后运行相同测试，网络预取耗时与UI显示耗时分开输出。两种检查均不捕获聊天窗口。

0.2.1扩展上述检查：覆盖三角色×三语言×三尺寸的27组额度自动关闭／问候重开路径，注入旧的215DIP高度验证重新测量；同时检查气泡的实际绘制边缘、剩余空白、角色动画不越界，以及普通／减少动画模式下真实计时器连续点击后的完整恢复。可加 `--verify-ui --preview <输出PNG>` 生成仅包含本程序角色和合成问候的静止／压缩对比图，不读取宿主聊天。

0.3.0的 `--check-assets` 验证三角色在180、240、270、300、320、360、480、640、960像素下的三帧对齐、透明度及缓存预算。`--verify-ui` 验证待机／问候／用量使用三张不同的完整图，气泡到期恢复待机，32次中途打断的像素连续性、一张快照的上限、连续戳击起点连续、真实计时器停止及素材释放／重载，同时保留27组气泡布局检查。`--preview` 输出更新为三角色×三种状态的3×3对比图。

素材对齐只在开发阶段进行：Debug程序 `--prepare-art <项目绝对路径>` 读取 `assets/characters/*/sheet.png`，输出同目录的 `registered.png`，保留原图。再构建一次使新素材进入输出目录。Release没有该写入命令，也不包含轮廓分析方法；运行时仅加载三个切片共享的已对齐精灵图，分发包不需要原始sheet.png。

0.4.0中可用 `--prepare-art <项目绝对路径> <角色ID>` 只准备一位新角色，避免改写已有对齐图。`--check-assets` 根据四档尺寸及100%／125%／150%／200%／300%解码比例生成17档不同像素高度（最高960），覆盖七角色。`--verify-ui` 覆盖84组角色／语言／尺寸，预览高度随角色数量变化。此处解码尺寸检查不等于所有物理多屏组合已实测。

应用图标为 `assets/companion.ico`，由Luna第一帧生成16／32／48／64／128／256尺寸透明ICO，并通过ApplicationIcon嵌入EXE。项目根目录 `.lnk` 直接指向稳定的 `dist/marketplace/plugins/codex-companion/bin/CodexCompanion.exe`，无命令参数和脚本。
