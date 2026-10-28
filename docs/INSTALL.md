# 安装与卸载

## 直接运行

从 [GitHub Releases](https://github.com/qysnb/codex-companion/releases/latest) 下载运行包。请使用Release附件中的两个运行ZIP；GitHub自动生成的Source code ZIP仅包含源码，需要自行构建。程序使用普通用户权限，不经过控制台脚本。

解压 `codex-companion-win-x64.zip`，双击包中 `marketplace/plugins/codex-companion/bin/CodexCompanion.exe`。此包带 .NET 8 Desktop Runtime，无需开发 SDK 或管理员安装。请保留 exe 旁边的运行时和 assets 文件夹。

若使用 `codex-companion-lite-win-x64.zip`，需事先安装 .NET 8 Desktop Runtime x64，建议使用微软提供的最新安全修订版本。Lite 版磁盘包更小，运行行为相同。程序不会替你安装系统运行时。

Lite 包的解压根目录名为 `marketplace-lite`；下文安装源路径相应指向这个目录。

首次启动默认 Q 版、简体中文、240 DIP。Codex 在后台时角色自动隐藏；在 Codex 前台等待最多约 1 秒即可出现。显示区域位于客户端右下角，拖动可调整位置。

当前版本0.4.5，提供Astra、Sol、Luna、Fable、Haiku、Pro、Flash，前三位的内部形象ID保持不变。待机使用闭嘴图，问候使用眯眼图，用量使用张嘴图；气泡关闭后回到待机。三张完整人物经过对齐，表情约140毫秒过渡，减少动画模式约70毫秒。左键点击时面部与身体统一压缩回弹，约420毫秒后恢复原状，减少动画模式约180毫秒。按住角色至少350毫秒后再拖动，才会调整相对Codex的位置。点击、表情和气泡不改变角色保存的位置。

右键“大小”提供小杯180、中杯240、大杯320和超大杯420逻辑像素。旧设置继续有效；默认档位不变。较小工作区会限制实际显示尺寸，额度气泡在上下空间不足时可放在侧边。

右键“额度刷新间隔”可选择15、30、60、120、300秒，或输入5–3600秒的自定义值，默认30秒。启动时预取一次，后台按间隔更新；任一缓存重置时间到达后也触发读取。缓存显示与网络同步分开，气泡始终标出读取时间。

右键“额度重置提醒”默认勾选，关闭后记住选择。在程序运行期间，同一已验证账户的五小时／周限额从实际 0% 恢复为 100% 时，出现一次右下角提醒，8秒自动关闭，也可点击关闭。不抢走键盘焦点、不播放声音、不移动桌宠。两种限额同时恢复合并提醒。提醒复用现有刷新；它在真实响应抵达后触发，不在预计重置时刻推测恢复。退出期间或未曾读到0%的恢复不会追溯提醒。

额度读取超时会丢弃该临时连接，按5／15／60秒退避后重新连接。气泡会显示读取状态和重试倒计时；确认同一账户时保留上次真实额度及原始读取时间。账户暂时无法验证时隐藏旧值，确认仍是原账户后可恢复缓存；退出、换账户或身份不可比较时不复用旧账户缓存。

0.4.2将账户与额度只读请求在传输层、服务层的截止时间统一为30秒，避免将仍能成功完成的慢响应在10秒时提前取消。缓存显示不等待网络；初次预取仍可能需要十几秒。更新时先退出旧桌宠，将新包完整解压到单独目录后启动，原有设置会自动沿用。

## 安装 Codex 插件

将解压目录长期保留，然后在普通终端执行，将示例绝对路径换成自己解压得到的 marketplace 目录：

```powershell
codex plugin marketplace add 'F:\你的目录\marketplace'
codex plugin add codex-companion@codex-companion-local
```

重新打开 Codex 聊天后，可让它显示／隐藏伴侣、切换形象或语言、刷新额度、读取状态。插件仅提供调用入口，实际桌面功能由伴侣 exe 执行。

无需 PowerShell 启动脚本、执行策略覆盖、白名单或关闭安全软件。本次开发没有自动注册用户插件或启用开机启动。

## 命令与诊断

在普通终端从 exe 所在目录执行：

```powershell
./CodexCompanion.Cli.exe --command show
./CodexCompanion.Cli.exe --command set-character --value elegant
./CodexCompanion.Cli.exe --command set-language --value ja
./CodexCompanion.Cli.exe --command set-refresh-interval --value 60
./CodexCompanion.Cli.exe --command get-status
./CodexCompanion.Cli.exe --command exit
./CodexCompanion.exe --probe-quota
./CodexCompanion.exe --check-assets
```

日常控制命令通过 `.Cli.exe` 输出 JSON 并退出，不常驻。GUI exe 的 `--probe-quota` 和 `--check-assets` 为开发诊断入口，在某些 PowerShell 宿主下可能不等待退出；需要完整诊断输出时，可使用 `Start-Process` 的 `-Wait` 与 `-RedirectStandardOutput` 将结果保存到自己指定的普通文件。

设置位于 `%LOCALAPPDATA%\CodexCompanion\settings.json`，不含登录信息。开机启动只有在右键菜单中主动开启后才写入当前用户的 `Run\CodexCompanion` 项。

## 卸载

先在角色右键菜单关闭“开机启动”（如果曾开启），再退出伴侣。在终端运行 `codex plugin remove codex-companion@codex-companion-local`，通过 `codex plugin marketplace remove --help` 查看本机版本对应的移除源命令。最后删除自己解压的文件夹；可选择保留或删除上述 settings.json。卸载不涉及 Codex 登录文件、聊天或角色原始参考图。

右键角色 → 主题，可分别设置额度文字和进度条颜色。选择预设，或输入 #RRGGBB，查看预览后保存；取消不会修改当前配色。
