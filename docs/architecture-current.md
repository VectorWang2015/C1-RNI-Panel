# RNI Palette：Windows 调用架构与边界

核实日期：2026-09-19 至 2026-09-20。本文是能力依据与实现约束；具体现场通过项与未完成项见 README/发布记录，不从静态实现推断验收。

## 结论

RNI Palette 是 Capture One 外部的 Windows 面板，不是接入官方选区/样式业务 API 的插件。当前可行路线是 Windows UI Automation 观察/操作原生控件；原生 WinForms 模式菜单未公开 UIA Toggle 时，以 Windows MSAA 读取精确可见菜单项的实际状态；必要时 SendInput 触发原生鼠标动作或已配置样式快捷键，只读 SQLite 校对照片身份。它依赖 C1 的 GUI 控件和布局，无法把一组 GUI 观察与后续动作变成数据库事务。

不写在线图库、不修改原片、不注入 C1 进程、不加载并调用厂商私有方法。私有程序集的静态元数据仅用来理解原生界面和已有命令语义，不是外部 SDK。

## 官方接口核实

Capture One 官方 Developer Portal 明确提供插件 SDK、API 文档、教程及 Windows/macOS 示例。因此“Windows 完全没有 SDK”不准确。但其公开页面没有证实获取实时选区、查询已应用样式、按 UUID 幂等 ApplyStyle 或仅移除指定样式的 Windows 接口；本轮没有获取并验证完整注册开发者 SDK。准确结论是“需要的业务能力尚未验证”，不是“只要有 SDK 就能调用”。[官方开发者入口](https://www.captureone.com/en/partnerships/developer)

官方自动化文档描述 AppleScript/JXA、macOS Script Editor 和 `~/Library/Scripts/Capture One Scripts/`。它不能作为 Windows 脚本接口依据。[官方 AppleScript 自动化文档](https://support.captureone.com/hc/en-us/articles/360002681418-Capture-One-Workflow-Automation-with-AppleScript)

本机 16.7.8 的 `Plugins/OpenWith/PhaseOne.Plugin.dll` 只读类型检查可见 `IEditingPlugin`、`IOpenWithPlugin`、`IPublishingPlugin`、`IFileHandlingPlugin`、处理设置及文件路径任务。已检查的契约中没有发现实时选区或样式操作方法；`P1.C1.PluginContracts.dll` 可见的是 Catalog Import 契约。这个有限检查不能证明其他或未来 SDK 永远不支持，但也没有提供替代当前 GUI 桥接的依据。

## 当前三个数据来源

| 来源 | 能用于什么 | 不能代表什么 |
|---|---|---|
| Windows UI Automation | 当前可见浏览器摘要、主查看器、原生样式控件和可用模式 | 厂商支持的选区事务 API；控件未提供属性不等于属性为 false |
| 原生 UIA 动作 / SendInput | 用户原生菜单、勾选和样式快捷键的操作入口 | 按键被系统接收不等于 C1 已应用；同一入口可能是 toggle |
| 只读 Catalog SQLite | 从界面文件名与浏览器变体信息核对唯一照片身份 | 运行中实时选区、当前未刷盘的样式状态、在线写入接口 |

空样式列表必须是“确实找到可读、已展开的样式工具，并读到零行”。控件不在当前页面、折叠、隐藏、失效或提供器超时均是独立错误，不能转换为零行再发送样式。

旧版两秒超时还会留下未取消的后台 UIA 调用。当前实现限定已找到的工具子树、批量缓存一次观察中的属性、复用控件引用而非复用旧样式值，仅允许一个未完成样式读取；超时不再启动重复全树扫描。原生菜单先定位 MenuBar/同进程可见 popup，再匹配直接菜单子项，避免深入全部样式子菜单。顶层菜单只缓存经重新校验的元素引用，不缓存模式值。适当延长限时只是配套措施，不是根因修复的替代。

开发联调还暴露过一个实际迟发缺陷：发送前的模式校验阻塞超过八秒，外层已经超时，但后台任务在校验返回后继续发送。当前修复把慢模式观察移到限时发送 worker 外，随后再做最终照片身份检查；在阻塞校验返回之后，以及真正 Toggle/SendInput 之前再次检查本次操作是否过期。**这能阻止尚未提交的过期操作开始发送，但不能取消已经进入原生 UIA 提供器的调用。** 后者仍可能晚返回或晚完成，所以保留未完成 worker 的门禁、不自动重试，不能把超时写成“照片一定未改变”或“已经回滚”。

## 全胶片执行

索引到的 1680 个本地样式不是 1680 个可执行入口的证明。原型只有 Portra 160/400 八条快捷键；扩展应走本地完整路径对应的原生样式树，或经过原生配置加载验证的映射，而不是仅取消面板按钮限制。

原生样式树中的标准版和颗粒版可能同名，因此请求身份应采用 UUID 和完整目录路径。已应用列表的显示名称不能独立确认版本。现场发现已有 Portra 样式但两个同名叶节点均为 Off；只读 IL 进一步发现 `StyleTreeStyleViewModel` 构造函数只订阅 `CheckmarkChangedEvent`，没有初始化其 `m_styleChecked`，后续事件才写入。懒构建控件可能错过旧状态更新是与现场相符的推断，而不是已证明的事件时序。因此勾选 Off 不能当作“这个 UUID 未应用”的事实；滚动可见也不保证修复它。

实用路线是按已应用列表的精确 RNI 行调用原生清除，确认这行消失且非 RNI 行不变，再按完整目录路径应用目标并回读原生列表。无法从源树可靠消歧时，内部 `rni-name:<显示名称>` 仅表示“此名称与本地已索引 RNI 匹配”，**不表示初始样式 UUID 已确认**。清除动作由该精确原生行持有的 Style 对象执行，不从名称猜标准版/颗粒版 UUID；多个同名已应用行不能唯一定位时停止。如果用户另建一个与 RNI 完全同名的非 RNI 样式，单凭这个名称也无法区分。

本次请求完成明确的源路径操作后，可将“本次指定路径”与“原生已应用名称回读”关联确认。此证据只属于这次操作，不应改写成“原生已应用列表提供了 UUID”，也不能跨越用户后来在 C1 里的手动修改永久复用。重复同名请求不能盲目 toggle；已能独立确认目标时跳过发送，身份仍不明时明确清除/重设，因此重复点击保护不一定意味着零发送。

这仍是多个 GUI 步骤：若清除后应用失败，应明确报告“旧 RNI 已移除、目标未确认”，不能声称原效果仍在。只读静态 BAML 证据如下（是否在具体布局公开相应 UIA 模式仍须现场确认）：

- `tools/styles/styletoolview.baml` 的四个树绑定 `UserStyles`、`BuiltInStyles`、`UserPresets`、`BuiltInPresets`；没有发现四树独立命名的 AutomationId。
- 文件夹 `TreeViewItem` 的 Expanded 与 Children 双向/单向绑定；模板有 `Expander`、`PART_Header`、`ItemsHost`。
- 样式叶 `StyleCheckBox` 内容绑定 DisplayName，勾选绑定 StyleChecked；点击绑定 CheckStyleCommand，所以仍需前后回读，不能认为它是无条件 Apply。
- `ListViewAppliedStyles` 行显示 DisplayName 与 Comment，未发现 UUID 的界面属性绑定。其左键动作打开动态行菜单，`CLEAR_FROM_BACKGROUND` 为“从背景中清除”/“Clear from Background”。注意该动态菜单只添加显示字符串，实际业务挂在 **ContextMenu.PreviewMouseDown**：通过菜单 DataContext 调用该行真实 Style 的 RemoveStyleCommand。UIA MenuItem.Invoke 可能只关闭菜单而未清除；这里需要对已唯一定位菜单项的实际矩形发送一次鼠标点击，再读回确认。BAML 中另有 Command/PlacementTarget.SelectedItem 绑定的右键菜单，不能与左键动态菜单混为一谈。两者都不是整图 Reset。

`StyleTreeStyleViewModel.set_StyleChecked` 在合法目标上会调用 `ToggleStyleOnBackground`，true 分支调用 ApplyToBackground，false 分支调用 ClearFromBackground；UIA Toggle 不仅改变外观。但前提仍是当前勾选状态可信且目标合法，不能用这个静态实现掩盖上述初始化/状态问题。

官方也明确说明再次点击已应用 Style 会移除它，且已应用样式列在工具顶部。[官方 Applying a Style](https://support.captureone.com/hc/en-us/articles/360002611857-Applying-a-Style)

当前本机快捷键静态语义见仓库 `shortcut-format-readonly-20260919.md`：所有受影响变体已有某 UUID 时，快捷键会转为 Remove；ReplaceTopIfSameType 比较的是 StyleSource，不是胶片家族。其他普通样式也可能同属 Styles。为保留其他调整，不应靠全局重置、拷贝整图调整或不加判断地替换顶部来清除 RNI。

## 多选方案及明确限制

官方说明关闭 Edit Selected Variants / Edit All Selected Variants 后，适用操作只影响主变体；开启后 Styles and Presets 可影响其他选中变体。因此可行实现方向是保留整个选区、暂时只编辑主图，用原生 Select First/Next 在已选图中移动主图，每张独立读取、判断、发送一次并确认。它不是把主图勾选当作整组选区状态。[官方多图调整说明](https://support.captureone.com/hc/en-us/articles/360002480877-Adjusting-multiple-images-at-a-time)、[官方主图与选中变体说明](https://support.captureone.com/hc/en-us/articles/360002481917-Primary-and-selected-variants)

本轮实现使用预扫描形成 N 个不同 UUID 的有序目标列表，然后再按该顺序逐张执行；每步检查文档、进程、主图身份与选区数量，每次真正发送样式前重新观察实时 EditMultiple 关闭状态。慢模式读取不重复塞入每个导航/观察或限时发送 worker。完成后恢复原主图和原编辑模式。取消、外部介入或原生结果不明时停止，保留当前现场供检查，不盲目导航或重发。

本机静态菜单定位：

| 动作 | 原生命令/控件名 | 简体中文菜单 |
|---|---|---|
| 多图编辑模式 | `editPrimaryOnlyToolStripMenuItem`；工具栏 `toolStripButtonEditMultiple` | 图像 → 编辑所有已选项 |
| 移至首个已选主图 | `selectFirstToolStripMenuItem` | 选择 → 第一个 |
| 移至下个已选主图 | `selectNextToolStripMenuItem` | 选择 → 下一个 |

虽然菜单内部名称含 PrimaryOnly，其 Checked 绑定实际为 EditMultiple：选中表示同时编辑多图。不能从名字推断反向含义。原生界面还可能提示“您选择了多个变体，但‘编辑所有已选变体’选项为关闭”，其选项包括“仅限主变体”和“所有已选变体”。遇未明确处理的弹窗应停止，不自动批准全部照片。

实际模式菜单可能位于同一 C1 进程的独立 popup HWND，且没有 UIA TogglePattern。此时只对已唯一定位并可见的菜单项中心调用 `AccessibleObjectFromPoint`，使用 API 返回的 child ID 读取 `IAccessible.accName`、`accRole`、`accState`；要求名称与 UIA 项一致、角色为 MenuItem、该坐标窗口属于同一 C1 进程，才解释 `STATE_SYSTEM_CHECKED=0x10`。状态不支持、混合、不可用或被遮挡一律停止。模式在菜单打开时读取，关闭菜单后不把旧 Checked 当作实时值；实际切换对重新定位的可见命令发送一次鼠标点击，再重新打开回读，不做失败后的动作重试。这仍是 Windows 辅助功能接口，不是 Capture One SDK。[微软 AccessibleObjectFromPoint](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfrompoint)、[微软 MSAA 状态常量](https://learn.microsoft.com/en-us/windows/win32/winauto/object-state-constants)

打开原生菜单时，`Process.MainWindowHandle/MainWindowTitle` 可能暂时指向 popup。菜单期间的守卫核对先前锁定 HWND 的实际窗口标题、所属 PID 与进程启动代次，同时要求焦点仍在同一 C1 进程；不使用动态 MainWindowTitle 误判换库，也不是仅凭 PID 放行。完整文档路径和照片身份仍在操作前后重新核对。

### 自动准备主查看器

`NativeViewer.cs` 将连接与照片执行分开：连接只读取当前图库完整路径和选中数量，不解析照片 UUID、不改变查看器、更不应用样式。明确的 Apply/Clear 请求先走原来的 `InspectPrimary`；已经是唯一主图时不读取或操作视图菜单。仅 `MultipleViewerException`（确实读到多个可见照片标题）才进入准备路径，且再次核对同一图库与 N≥2；数据库、文件名、变体歧义和一般 UIA 错误均不会触发切视图。

准备使用“查看 → 自定义查看器 → 多视图”的真实原生勾选状态。对应 ID 为 `viewToolStripMenuItem` → `customizeViewerToolStripMenuItem` → `viewerModeShowAllToolStripMenuItem`；Checked=true 表示显示多图。只在它确实为 true 时点击关闭，回读后再识别主图。该命令只改 ViewerShowOnlyPrimary，不改选区；它与 Y 键的 VariantViewModeToggle/前后比较模式不同。完整批量成功后恢复本次改过的视图，失败保留当前现场并提示可能仍为仅主图。实机通过范围以 README 对应结果为准，不因为存在代码就称通过。

恢复多视图时，查看器结构正常重建曾使最后的纯图库/数量观察误报“界面仍在变化”。现在 `InspectSelection` 只取得新鲜 DocumentSelector、MainItemsControl、SummaryText，不读取或缓存查看器标题，也不因无关查看器结构噪声拒绝已读到的三控件；它不能用于证明照片身份。真正应用/清除前的严格 `InspectPrimary`、唯一变体核对及最终目标验证保持不变。

两张照片的实测批量应用、重复和清除均已完成，但分别约115、76、109秒，仍有显著GUI成本。后续已展开菜单小范围查询减少了一个已测出的全窗口回退热点，未对套用/清除流程重新计时，不能把优化代码存在写成新的性能保证。这里不以牺牲最终目标校验换取速度。

仍有 GUI 边界：UIA 不直接提供整个浏览器的可靠 SelectionItem 状态时，摘要数量与遍历身份是组合证据；它们不能检测并原子锁定每一瞬间的同数量替换选区。未经现场证明的导航和开关模式必须报告未验证/不可读，不能把单元测试通过称为批量实机成功。

## 结果语言

- “已发送”：原生输入/动作已提交，尚未证明目标结果。
- “原生已确认”：在同一已核对照片上读回目标名称/空列表，并说明样式身份依据是原生明确身份，还是本次精确源路径操作加名称回读。不能将初始 `rni-name:` 回退写成 UUID 已确认。
- “完整可用”：所声称的用户流程已在授权现场完成；编译、静态分析、模拟测试、控件定位成功都不单独等于此结果。

日志应保留阶段、耗时、目标身份和停止原因。交付说明只报告确实完成的验证；不重做照片迁移，不将本次调研误写为图库修改授权。
