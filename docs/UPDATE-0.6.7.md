# 0.6.7：根目录启动与英文便携路径

```text
CodexCompanion/
  CodexCompanion.exe
  README.md
  app/
    bin/
    skills/companion/
    .codex-plugin/
  docs/
  licenses/
  .agents/plugins/marketplace.json
```

根目录是带现有Luna图标的原生启动文件；实际WPF程序及完整运行时位于app/bin。启动器只启动固定的相对路径，立即退出，无额外常驻进程，不调用Shell/PowerShell，不自解压、不联网。它按自身文件位置定位程序，不依赖当前工作目录，并正确处理带空格的目录。

app仍是完整技能插件，CLI相对位置不变。本地源通过根目录的目录清单指向./app。所有分发文件路径必须为ASCII英文，打包时会校验。移动时整体移动目录，保留app文件夹。Lite同样使用根目录入口，但实际WPF程序仍需要.NET8 Desktop Runtime x64。

偏好保存在原有用户设置目录，版本更新和布局变化不会重置设置。开启开机启动的用户在下次手动启动时修复自身已有Run路径；关闭偏好不写启动项，不重建用户从系统中移除的Run值。缓存安装的插件没有根目录启动文件时仍使用自身GUI可执行文件。

验证包含路径/空格/移动/缺失入口的核心测试、原生启动器的无联网fixture测试、根目录和插件相对路径/全英文文件名/CRC检查，以及实际根目录启动后的版本、设置和单实例验证。0.6.6资源报告保留为历史测量，本版没有改变额度网络策略。
