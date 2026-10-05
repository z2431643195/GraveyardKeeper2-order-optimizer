# OrderOptimizer / 订单系统优化

Graveyard Keeper 2（守墓人 2）的 BepInEx 插件：让小镇交付站（搬运站）只送【你真的接了订单】的箱种，不再有库存就无脑发货。
A BepInEx plugin for Graveyard Keeper 2: the town delivery stations ship only what your accepted orders need, instead of sending every box they happen to have in stock.

---

## 功能 Features

原版：交付站装什么货，只看站点界面上那排逐项开关，**完全不看订单** —— 只要开着，没人订的箱子也会一直往小镇送：原料被做成没人要的箱子，占着产线与搬运工时，还换不来钱和好感。
In vanilla a delivery station only looks at the per-item toggles on its own panel and never at your orders: as long as a toggle is on, boxes nobody ordered keep being shipped to town, burning materials, production time and porter trips for nothing.

装上本插件后：

- 每 2 秒（可调）按【已接订单】刷新那排开关：有缺口才打开，送完自动关闭 | Every 2 seconds (adjustable) the toggles are realigned with your accepted orders: a box type is enabled only while a gap remains, otherwise off.
- 【凑齐整批才发车】：补给仓库里备齐这一单还差的数量才允许装货，保证每单都能完成并拿到好感奖励 | Only start once the whole amount is ready: the station begins loading only when the supply warehouse holds everything the order still needs, so every accepted order completes and pays its happiness reward.
- 【只装缺口量】：一趟最多搬这一单还差的数量，不会顺手把多余的也搬过去 | At most the remaining amount is loaded per trip - no overshipping.
- 可选（默认关）：解除【同一订单每周只能交一次】的限制、订单窗口槽位数可调并可自动换行、一次性订单可重复 | Optional (off by default): lift the one-delivery-per-order-per-week limit, make the order slot count configurable with automatic row wrapping, and make one-off orders repeatable.

**默认完全不动原版玩法**：每周限制、槽位数量、一次性订单都保持原版，只有上面三项核心功能在工作。
**A fresh install changes nothing about vanilla pacing**: the weekly limit, the slot count and one-off orders all stay vanilla; only the three core features above are active.

## 它怎么做 How it works

缺口 = 订单要的数量 − 已经交付的数量 − 小镇托盘上已有的数量。紧急订单按整批算，因为游戏只在一整批都到齐时才结算。
The gap equals the amount the order still needs, minus what has already been delivered, minus what already sits on the town palettes. Urgent orders count as one full batch, because the game only settles them once the whole batch has arrived.

实现路线只有一条：写的是**玩家自己在站点界面上点的那一个标记**（站点 `WgoData` 上的逐项 `GameRes`），不动任何游戏逻辑。
There is only one implementation route: it writes the very flag you toggle by hand in the station UI (the per-item `GameRes` on the station `WgoData`), and touches no game logic.

### 两道闸门 Two gates

除了开关那一层，装货那一步还会再判一次：就算你手动把开关点开，只要这批没凑齐就装不上车，所以既催不了单、也破不了整批。
Besides the toggle layer, the loading step itself checks again: even if you flip a toggle by hand, nothing is loaded until the whole batch is ready - so manual toggling can neither force a shipment nor defeat the whole-batch rule.

判据两边完全一致：`库存 + 在途 ≥ 缺口`。用【在途】是为了让判据在装货过程中保持恒定（每装走一箱，库存 −1、在途 +1）。
Both gates use exactly the same rule: `stock + in transit >= gap`. "In transit" keeps the rule stable while a trip is loading, because every box taken moves one unit from stock into the porter's hands.

### 多张订单同时进行：按托盘空间排队 Queueing by palette space

紧急订单要【整批同时躺在托盘上】才结算（源码 97499：托盘现有量 ≥ 需求总量才推进），而小镇托盘总格数 = `warehouse` + `warehouse_cellar` 两区里所有 `TownPalette` 的格数之和（97488）。所以多张订单一起发车时，托盘会被几张半成品分着占满 ⇒ 谁都凑不齐整批 ⇒ 托盘永不释放、补给仓被撑满、上游跟着停。

本插件默认按【托盘空闲格数】排队（高级项 `PaletteSpaceGuard`，默认开）：只放行"还差的量装得进当前空闲格数"的订单，并按**最接近完成**的顺序喂 —— 一单结算释放空间，再喂下一单。代价是同时推进的订单变少，但每一单都能完成。被暂缓时会写一行 `[palette] 托盘空间不足，暂缓 …`（详细日志下）。

若存档**已经卡死**（托盘被几单半成品占满、谁都凑不够格数结算），把托盘上的货清掉即可解锁（原版托盘本身有 999 格，正常情况下碰不到这种情况）。

Urgent orders only settle once their whole batch sits on the palettes (source 97499), and the total palette space is the sum of every `TownPalette`'s size across `warehouse` and `warehouse_cellar` (97488). With several orders in flight the palettes fill up with half-finished batches, nobody ever reaches a full batch, nothing is released, the supply warehouse backs up and upstream production stalls with it.

The plugin queues by free palette space (advanced `PaletteSpaceGuard`, on by default): only orders whose remaining amount fits the free space are released, closest-to-finished first, so one order settles and frees its space before the next is fed. Fewer orders run at a time, but each one completes. Deferrals are logged as `[palette] 托盘空间不足，暂缓 …` (verbose logging).

If a save is **already stuck** (palettes packed with several half-finished batches), clear the palettes to unlock it - vanilla palettes hold 999 slots each, so this is rare.

### 一趟能搬多少 Trip capacity

一箱补给是 `ItemSize.Big`、要占搬运工 2 格，而原版背包是硬编码 4 格，所以原版一趟最多 2 箱。本插件把【补给仓库交付站上】搬运僵尸的背包默认扩到 64 格（一趟最多 32 箱，够最大订单一次搬完）；填 4 即恢复原版。
A supply box is `ItemSize.Big` and takes 2 of the porter's slots, and the vanilla bag is a hard-coded 4 slots, so vanilla carries at most 2 boxes per trip. The plugin enlarges the bag of the porter zombies attached to the supply-warehouse delivery station to 64 slots by default (up to 32 boxes per trip, enough for the largest order); set it to 4 to go back to vanilla.

## 订单面板 Order-window features

| 原版限制 | 本插件 | 配置 |
|---|---|---|
| 同一个订单每周只能交一次（要等 day_pride 那天统一重置）| 完成那一刻就清掉【本周已完成】标记，可再生订单交完马上能再接、再交、再拿奖励 | `ResetWeeklyOrderLock = false`（默认关；改 `true` 生效）|
| 订单窗口只有 2 个槽位（常量 `start_orders_count`）| 可增可减：`true` 时按 `OrderSlots` 管理；`false` 时收回原版数量。收回槽位时，其上的订单会被取消（槽位清空、订单回到可再接；已送到小镇的货留在托盘上，下次接单照样算数）| `OrderSlotsEnabled = false`、`OrderSlots = 2` |
| 一次性订单（`isRenewable = 0`）交完就永久结束 | 可选：奖励领完、槽位空出来之后把状态重置回可接 | `RepeatNonRenewableOrders = false`（高级项）|

| Vanilla limit | What the plugin does | Config |
|---|---|---|
| One delivery per order per week (reset on the pride day) | Clears the "finished this week" flag the moment an order is completed, so renewable orders can be accepted and paid again right away | `ResetWeeklyOrderLock = false` (off by default; set `true` to enable) |
| Only 2 order slots (the constant `start_orders_count`) | Grow or shrink: `true` follows `OrderSlots`; `false` shrinks back to the vanilla count. Any order sitting in a removed slot is cancelled (slot cleared, order acceptable again; goods already on the town palettes still count) | `OrderSlotsEnabled = false`, `OrderSlots = 2` |
| One-off orders (`isRenewable = 0`) end forever | Optional: once the reward is collected and the slot freed, the order is put back to acceptable | `RepeatNonRenewableOrders = false` (advanced) |

领奖之后界面会**立刻刷新**（下一帧重绘订单窗口与选单窗口），所以刚交完的单当场就能再接，不用关窗重开。
The window is refreshed immediately after you collect a reward (the orders window and the selection window are redrawn on the next frame), so an order you just finished can be accepted again right away - no need to close and reopen.

槽位多于 2 个时**自动换行**：按画布可用宽度尽量多放、最多 6 个一排（想固定每行几个或彻底不换行，用高级项 `OrderSlotsPerRow`：0 = 自动（默认）、1 = 不换行、2 至 6 = 固定）。实现**不依赖 Unity 的布局组件**：停掉槽位容器的布局组与自适应、写死容器尺寸、自己算每格坐标，外观尺寸沿用原来的。
Slots wrap automatically once there are more than 2: as many per row as the canvas width allows, up to 6 (advanced `OrderSlotsPerRow` overrides it: 0 = automatic, the default; 1 = no wrapping; 2 to 6 = fixed). The implementation does not rely on Unity layout components: the container's layout group and size fitter are switched off, the container size is fixed and every cell position is computed directly, reusing the original cell size and spacing.

## 作用范围 What it touches

**它只操作两样东西：① 补给仓库里那台交付站（往小镇送货的那台）与它上面的搬运僵尸；② 小镇仓库的订单面板（订单数据）。其它东西一律不碰。**
**It only ever operates on two things: (1) the delivery station that ships to the town palette (the one in your supply warehouse) and the porter zombies attached to it; (2) the order data of the town warehouse order panel. Nothing else.**

| 只做这些 | 细节 |
|---|---|
| 交付站的逐项开关 | 写站点 `WgoData` 上的逐项 `GameRes`（玩家在界面上点的那一排勾）|
| 搬运工背包 | 只换管辖交付站上搬运僵尸的背包（仅在【比目标小】时换，货原样搬过去）|
| 装货量闸门 | 装货方法的头尾加限流：一趟最多装缺口量（非管辖站一路放行）|
| 订单数据（订单面板）| 完成即清【本周已完成】标记、按配置增减槽位并取消被收回槽位上的订单、一次性订单可重复 —— 都受配置控制，默认全关。不会替你接单，也不会替你领奖 |

| Does | Detail |
|---|---|
| Station toggles | Writes the per-item `GameRes` on the managed station (the very flags you click in its panel) |
| Porter bags | Resizes only the porters attached to a managed station, and only when their bag is smaller than the target; cargo is moved over unchanged |
| Loading gate | Throttles the loading method: at most the order's gap per trip (unmanaged stations pass straight through) |
| Order data | Clears the finished-this-week flag, grows/shrinks slots and cancels orders on removed slots, repeats one-off orders - all config-gated and off by default. Never accepts an order or collects a reward for you |

| 完全不碰 | 说明 |
|---|---|
| 其它搬运站 | 矿场、采石场、葡萄园、伐木场、吊车等站 —— 开关、背包、僵尸全都不动（代码里根本不进那个分支）|
| 非订单物品 | 矿石、石料、木材、农作物等永不接管 |
| 小镇托盘里的货 | 只读计数，不替游戏搬货 |
| 游戏逻辑 | 只挂 3 个 Harmony 补丁：装货方法的头尾（限流）、`VendorOrderData.MarkFinishedThisWeek`（完成即清零，可选）、`UIVendorOrdersWindow.Redraw`（槽位换行排布，可选）。不复制任何游戏逻辑 |
| 物品 / 背包系统 | 只反射 `ZombieWgoData.porterInventory` 这一个字段，其余全走公开 API |

| Never touches | Detail |
|---|---|
| Other porter stations | Mine, quarry, vineyard, sawmill, cargo lift and so on - toggles, bags and zombies stay untouched (the code never enters that branch) |
| Non-order items | Ore, stone, wood, crops and everything else are never managed |
| Goods on the town palettes | Read-only counting, never moved by the plugin |
| Game logic | Only three Harmony patches: the loading method's head/tail (throttle), `VendorOrderData.MarkFinishedThisWeek` (optional instant reset) and `UIVendorOrdersWindow.Redraw` (optional slot wrapping). No game logic is copied |
| Item / inventory systems | Only one reflected field (`ZombieWgoData.porterInventory`); everything else uses public APIs |

## 安装 Installation

1. 装好 BepInEx 5（游戏目录内应有 winhttp.dll 与 BepInEx 文件夹）。| Install BepInEx 5 (winhttp.dll and the BepInEx folder next to the game exe).
2. 把 `OrderOptimizer.dll` 放进游戏目录的 `BepInEx\plugins\`。| Put `OrderOptimizer.dll` into the `BepInEx\plugins\` folder of your game folder.
3. 启动一次游戏，配置会自动生成。| Start the game once; the config is generated automatically.

## 配置 Configuration

主配置（`BepInEx\config\gk2.OrderOptimizer.cfg`）只放用户级开关：
The main config (`BepInEx\config\gk2.OrderOptimizer.cfg`) only exposes the user-facing switches:

| 节 | 项 | 默认 | 说明 |
|---|---|---|---|
| General | `Enabled` | `true` | 总开关；关掉即完全停手，站点开关回到纯手动 |
| General | `RequireFullBatch` | `true` | 凑齐整批才发车；`false` = 有多少送多少 |
| General | `PorterInventorySize` | `64` | 搬运工背包格数（原版 4）；只影响补给仓库交付站上的搬运僵尸 |
| Orders | `ResetWeeklyOrderLock` | `false` | 解除【同一订单每周只能交一次】（默认关，保持原版）|
| Orders | `OrderSlotsEnabled` | `false` | 是否接管槽位数量；`false` = 收回原版数量 |
| Orders | `OrderSlots` | `2` | 槽位数量（2~12），仅在上一项为 `true` 时生效 |
| Orders | `OrdersEveryDay` | `false` | vendor 天天可接订单：**直接解除"只有 day_pride 那天能接单"的原版限制**（粉笔板常开）+ 清掉"本周已完成"标记 ⇒ 哪天都能接、刚交完的单当天就能再接 |

| Section | Key | Default | Notes |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch; turn it off and the mod does nothing |
| General | `RequireFullBatch` | `true` | Whole-batch gate; `false` = ship whatever is available |
| General | `PorterInventorySize` | `64` | Porter bag size (vanilla 4); only the porters of the supply-warehouse delivery station |
| Orders | `ResetWeeklyOrderLock` | `false` | Lift the one-delivery-per-order-per-week limit (off by default) |
| Orders | `OrdersEveryDay` | `false` | Vendors accept orders every day: removes the vanilla "pride day only" restriction (the chalk board is kept enabled) and clears the "finished this week" flag, so an order can be picked any day - and re-picked the same day it was paid |
| Orders | `OrderSlotsEnabled` | `false` | Whether the slot count is managed; `false` shrinks back to the vanilla count |
| Orders | `OrderSlots` | `2` | Slot count (2..12); only used when the key above is `true` |

高级项藏在同目录的 `gk2.OrderOptimizer.advanced.cfg` 里（**不会自动生成**，删掉即用默认值）：`StockScope`（库存口径，默认 `StationZone` 只数补给仓库）、`UpdateIntervalSeconds`（默认 2 秒）、`OrderSlotsPerRow`（默认 0 = 按画布宽度自动换行、最多 6 个一排；1 = 不换行；2 至 6 = 固定）、`PaletteSpaceGuard`（默认 true = 按小镇托盘空闲格数排队）、`LoadingShelfSize`（**默认 40**；给**装货货架**按 4 格基准放大 —— t2 4→40、t1 2→20；填 0 = 关闭并还原成原版）、`RepeatNonRenewableOrders`（默认 false）、`VerboseLogging`（默认 false）。
Advanced keys live in `gk2.OrderOptimizer.advanced.cfg` in the same folder (**not created automatically**; delete it to use defaults): `StockScope` (`StationZone` = count the supply warehouse only, the default), `UpdateIntervalSeconds` (default 2 s), `OrderSlotsPerRow` (default 0 = wrap automatically to the canvas width, up to 6 per row; 1 = no wrapping; 2 to 6 = fixed), `PaletteSpaceGuard` (on by default), `LoadingShelfSize` (**default 40**; scales the loading shelves from a 4-slot baseline - t2 4 to 40, t1 2 to 20; 0 turns it off and restores the vanilla sizes), `RepeatNonRenewableOrders` (default false), `VerboseLogging` (default false).

## 日志 Log lines

```
GK2 OrderOptimizer 1.0.0 已加载（订单驱动交付站开关 / order-driven station toggles）
订单目录收录 22 种物品 …                                  order catalogue loaded
已挂钩装货上限 / 已挂钩订单每周重置 / 已挂钩订单窗口排版      the three Harmony hooks
按订单更新了 N 个箱种开关 …                                toggles realigned after an order change
已把补给仓库交付站上的 N 个搬运工背包扩到 64 格 …            porter bag(s) enlarged
```

详细日志（高级项 `VerboseLogging = true`）才会出现：
Only with verbose logging (`VerboseLogging = true` in the advanced file):

```
[toggle] <站>#<实例短id>@<区域> <箱种> → 开/关 …   哪一台站写了哪一项（#id 用来区分同类的多台站）
[batch] 整批未齐，拦下这一趟 …                     整批没齐时被拦下的那一趟
[orders] 缺口/库存 need(have): …                  每种箱种当前缺口与库存（仅在内容变化时输出）
```

## 构建与打包 Build & pack

```powershell
dotnet build OrderOptimizer.csproj -c Debug      # 或 / or -c Release
.\pack.bat                                       # 输出 / outputs release\OrderOptimizer-v1.0.0.zip
```

发布包含 `BepInEx\plugins\OrderOptimizer.dll`、`README.txt`、`CHANGELOG.txt`、`LICENSE.txt`；**不含配置文件**（一律由 BepInEx 生成）。
The release zip contains `BepInEx\plugins\OrderOptimizer.dll`, `README.txt`, `CHANGELOG.txt` and `LICENSE.txt`; no config file is shipped - BepInEx generates it.

游戏路径默认为 `E:\SteamLibrary\steamapps\common\Graveyard Keeper 2`，可用 `-p:GameDir="..."` 覆盖。
The game path defaults to `E:\SteamLibrary\steamapps\common\Graveyard Keeper 2`; override it with `-p:GameDir="..."`.

## 兼容与取舍 Notes & limits

- 站点开关在插件启用期间由它维护；想完全手动控制就把 `Enabled` 设为 `false`。| While enabled the plugin owns the station toggles; set `Enabled = false` for fully manual control.
- 跨区物流（Zombie Cross-Zone Logistics）读的是同一排开关、调的是同一个装货方法，所以两条装货路径都受同一道闸门约束，不会互相打架。| The cross-zone logistics mod reads the same toggles and calls the same loading method, so both loading paths pass the same gate and never fight.
- 槽位数量写在存档里：`OrderSlotsEnabled` 从 `true` 改回 `false`（或把 `OrderSlots` 调小）会**真实删除**多出来的槽位，其上的订单被取消；若那单已交付但还没领奖，奖励会作废 —— 这种情况会打一行带 ⚠ 的日志明确提示，不会静默处理。| The slot list lives in your save: switching `OrderSlotsEnabled` back to `false` (or lowering `OrderSlots`) really removes the extra slots and cancels the orders that sat on them; if such an order was already delivered but not yet paid, its reward is forfeited - that case prints an explicit ⚠ log line instead of failing silently.
- 游戏更新若重命名被挂钩的三个方法，对应功能会停止生效，但日志会明确写 `找不到 …`，不会静默出错。| If a game update renames one of the three patched methods, the matching feature stops working but logs a clear `找不到 …` line instead of failing silently.
- **补给仓库的装货货架：画面只画 4 个箱子，但它实际能放下 40 个 —— 多出来的箱子只是不画出来，不会丢失**（已实测：存档来回之后箱子仍在，订单照常计数）。| The supply-warehouse loading shelves only DRAW 4 boxes but each shelf actually holds 40 - the extra boxes are simply not rendered on it, and nothing is lost (verified in game: they survive a save and reload and still count towards your orders).
- 托盘空间是硬上限：紧急订单要【整批同时躺在托盘上】才结算（源码 97499），总格数 = warehouse + warehouse_cellar 里所有 TownPalette 的 `inventory_size` 之和（97488）。插件默认按空间排队（`PaletteSpaceGuard`，默认开 ⇒ 不会再死锁）；**已经卡死**的存档把托盘上的货清掉即可解锁。详细日志会打一行 `[palette] 小镇托盘：N 个容器，共 M 格（已用 X，空闲 Y）`，可以照着它判断同时接几单。| Palette space is a hard cap: urgent orders need their whole batch on the palettes at once (source 97499) and the total is the sum of every TownPalette's `inventory_size` in warehouse plus warehouse_cellar (97488). The plugin queues by space by default (`PaletteSpaceGuard`, on), so it no longer deadlocks; a save that is **already stuck** is unlocked by clearing the palettes. Verbose logging prints a `[palette]` line with the container/slot/used/free totals so you can judge how many orders to run at once.

## 状态 Status

- 版本 v1.0.1，已验证：站点开关随订单开合、凑齐整批才发车、只装缺口量、两台交付站都被管辖、领奖后即时刷新、槽位数量与换行、**装货货架扩容（实测 8 架 32 格 → 320 格）**、订单天天可接（粉笔板保持开启）。
- Version v1.0.1, verified: toggles follow orders, whole-batch gate, no overshipping, both delivery stations managed, instant refresh after collecting a reward, slot count and row wrapping, **loading shelves enlarged (measured: 8 shelves went 32 to 320 slots)**, orders accepted every day (chalk board kept enabled).
