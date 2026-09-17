# ProxiLock

ProxiLock 是一个 WinUI 3 隐形锁工具：程序常驻系统托盘，按蓝牙、可移动磁盘或空闲时间条件锁定交互，并用 `Ctrl+Shift+L` 全局解锁。

## 构建

要求 .NET 10 SDK、Windows 10 2004（内部版本 19041）+ 和 Windows App SDK。项目默认固定为 Windows App SDK 支持的 `x64/win-x64` 架构，直接执行下面的命令即可，无需额外传入 `-p:Platform=x64`。

```powershell
dotnet build ProxiLock.csproj -c Release
dotnet publish ProxiLock.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

发布输出位于 `bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish`。配置保存到 `%LocalAppData%\ProxiLock\config.json`。

## 说明

- 蓝牙监控使用 `BluetoothLEAdvertisementWatcher`，并同时读取已配对设备的连接状态。判定分两层：设备 20 秒没有广播即视为“已离开”，但要真正触发锁定还需再持续缺席 5 秒，以免信号抖动导致反复锁定。RSSI 读数只在新鲜（4 秒内）时才参与阈值比较，过期读数不会用连接状态代替；连接状态本身也会在 15 秒无更新后失效。适配器被系统停止时扫描器会自动重建。
- U 盘列表同时枚举可移动盘和由 WMI 识别为 USB 的固定盘（移动硬盘、移动 SSD），优先保存 WMI 暴露的 `PNPDeviceID`，不可用时依次回退到已学习的硬件关联、卷序列号。存在性检查会匹配设备暴露的全部标识，因此某次 WMI 查询失败不会误判设备已拔出；WMI 硬件表缓存 5 秒以避免每秒扫描都访问 WMI。扫描在后台线程执行，避免阻塞设置界面。
- 锁定覆盖层按显示器创建无激活、置顶、近乎完全透明的原生窗口；覆盖完整显示器矩形（含任务栏），不含标题栏/系统菜单。锁定期间每秒校对一次：新增显示器会被补上，移除的显示器对应窗口会被回收，已有覆盖不会被先销毁再重建，因此刷新过程中输入始终保持拦截。
- 首次启动会先加载配置并等待设置页完成初始化后再启用策略评估；重复启动会被单实例互斥体拒绝。
- 设置页采用 BetterLyrics 同款的 `NavigationView` + `SettingsCard` 分区结构，托盘左键/双击打开设置，右键显示带图标菜单。修改任何选项后会自动防抖保存，相同值不会重复落盘。
- 锁定/解锁提示优先使用托盘气泡通知，未打包环境下 Toast 通常不可用，失败时回退到系统提示音。
- 空闲解锁后会自动重新开始计时；蓝牙与 U 盘的解锁会保留“手动解锁”状态，直到设备重新被检测到，避免反复锁定。

## 安全边界

ProxiLock 是**输入拦截便利工具，不是安全防护**，请勿依赖它保护敏感数据：

- 覆盖层本身近乎全透明，**屏幕内容始终可见**，它锁的是输入而不是画面。
- 进程可以被任务管理器结束；`Ctrl+Alt+Del` 属于系统级安全序列，无法被拦截。
- 任何具有置顶权限的程序都可能短暂把窗口压到覆盖层之上，ProxiLock 通过每秒重新置顶来缩短这个窗口期，但无法根除。
- 中断钩子安装失败时程序会放弃锁定，而不是把用户留在一个没有解锁途径的界面后面。

## 许可证

本项目以 [GNU General Public License v3.0](LICENSE) 发布，SPDX 标识为 `GPL-3.0-only`。你可以自由使用、修改和分发本项目，但衍生作品必须同样以 GPL-3.0 开源，并提供完整源代码。

