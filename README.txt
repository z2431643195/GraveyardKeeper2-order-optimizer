OrderOptimizer 订单系统优化
---------------------------

Graveyard Keeper 2 的小镇订单优化 mod：让搬运站（交付站）只送【你真的接了订单】的东西，
不再有库存就无脑发货、把原料白白变成没人要的箱子。
Keeps the delivery (porter) stations of Graveyard Keeper 2 shipping only what your accepted town
orders actually need, instead of sending every box you happen to have in stock.

改动内容 What it changes
------------------------
原版：搬运站装什么货，只看站点界面上那排逐项开关，完全不看订单 —— 只要开着，
没人订的箱子也会一直往小镇送。
Vanilla: what a porter station loads depends only on the per-item toggles in its window and never
on your orders. As long as a toggle is on, boxes nobody ordered keep going to town.

装上本 mod：每 2 秒（可调）按已接订单刷新那排开关。
With this mod: the toggles are refreshed from your accepted orders every 2 seconds (adjustable).

缺口怎么算 How the gap is computed
----------------------------------
缺口 等于 订单要的数量 减 已经交付的数量 减 小镇托盘上已有的数量。
紧急订单按整批算，因为游戏只在一整批都到齐的时候才结算。
gap equals order amount minus already delivered amount minus what is already on the town palettes.
Urgent orders count as one whole batch, because the game only resolves them when the full batch
has arrived.

凑齐整批发车 Only start once the whole amount is ready
-----------------------------------------------------
默认设置下，只有当交付站所在的补给仓库里已经备齐这一单还需要的数量时，才会允许装货。
不够就一个都不搬，所以每一单都一定能完成、拿到好感奖励，不会再出现送一半、白跑一趟的情况。
By default a station only starts loading once the supply warehouse of that station holds the whole
amount the order still needs. Below that nothing is moved, so every accepted order is guaranteed to
complete and pay its happiness reward instead of being delivered in useless dribbles.
（数的是补给仓库里那批箱子，也就是搬运工实际装货的地方；想让判断看整个基地的库存，
把配置里的 StockScope 改成 WholeBase。）
(The mod counts the boxes inside that supply warehouse, which is exactly where the porters load from.
Set StockScope to WholeBase in the config if you want the check to look at the whole base instead.)

这条规则有两道闸门：除了站点的箱种开关，装货那一步还会再判一次 ——
就算你在站点界面手动把开关点开，只要这批没凑齐就装不上车，所以不会零散发货。
This rule has two gates: besides the per-item switches of the station, the loading step itself checks
again. Even if you flip a switch on by hand, nothing gets loaded until the whole batch is ready, so
the goods can never leave in useless dribbles.

同时接多张订单时，本 mod 会按【小镇托盘还剩多少空格】排队发车：紧急订单要整批同时躺在托盘上才
结算（游戏源码 97499），而托盘格数有限，所以只放行"还差的量装得下"的订单，并优先跑最接近完成的
那一单 —— 一单结算释放空间，再喂下一单。这样同时跑的订单会少一些，但每一单都一定能完成，不会再
出现"几张半成品把托盘占满、谁都凑不齐、生产全停"的情况。
With several orders in flight the mod queues them against the free space of the town palettes: urgent
orders only settle once their whole batch sits on the palettes (game source 97499) and that space is
limited, so only orders whose remaining amount fits are released, closest-to-finished first - one order
settles and frees its room before the next is fed. Fewer orders run at a time, but each one completes,
and several half-finished batches can no longer fill the palettes and stall everything.

补给仓库的装货货架：画面只会画 4 个箱子，但它实际能放下 40 个 —— 多出来的箱子只是**不画出来**，
不会丢失（已实测确认：存档来回之后箱子仍然在，订单照常计数）。
The supply-warehouse loading shelves only DRAW 4 boxes, but each shelf actually holds 40 - the extra boxes
are simply not rendered on it, and nothing is lost (verified in game: they survive saving and reloading and
still count towards your orders).

并且每一趟只会装【这一单还差的数量】，不会顺手把多余的也搬过去。
And each trip only loads the amount the order still needs - it never sends extra boxes along.

注意：一箱补给占搬运工 2 格、原版背包只有 4 格，所以原版一趟最多搬 2 箱。
本 mod 默认把搬运工背包扩到 64 格，一趟最多搬 32 箱，最大订单（30 箱）可以一次搬完。
把 PorterInventorySize 填 4 即回到原版容量。
Note: one supply box takes 2 slots and the vanilla porter bag only has 4, so vanilla carries at most
2 boxes per trip. This mod enlarges the bag to 64 slots by default, so a porter carries up to 32
boxes per trip - the largest order (30 boxes) fits in one trip. Set PorterInventorySize to 4 to go
back to the vanilla capacity.

只管补给仓库这条线 Only the supply line is touched
--------------------------------------------------
本 mod 只作用于把补给箱送往小镇的交付站，以及这些站上挂着的搬运工。
矿场、采石场、葡萄园、伐木场、吊车等其它搬运站不受影响：它们的开关与背包都不会被改动。
订单方面本 mod 不会替你接单、也不会代替你领取奖励；它只在下面【解除每周限制】那一节说明的范围内
修改订单数据（清掉"本周已完成"标记、可选的槽位数量与一次性订单重复）。
On the order side the mod never accepts an order for you and never collects a reward for you; it only
edits order data in the ways described in the "removing the weekly limits" section below (clearing
the finished-this-week flag, the optional slot count and the optional repeat of one-off orders).

只管订单物品 Only orderable items
---------------------------------
只有订单目录里出现过的箱种（那 22 种 box）会被接管。
矿石、石料、木材、农作物这些非订单物品永远不碰，所以矿场、采石场、葡萄园那些搬运站不受影响。
Only the box types that appear in the order catalogue (the 22 box items) are managed. Ore, stone,
wood, crops and every other non-order item are never touched, so the mine, quarry, vineyard and
other porter stations keep working exactly as before.

默认只管小镇交付站 Only town delivery stations by default
--------------------------------------------------------
默认只接管【目的地含小镇托盘】的交付站，例如把箱子送到 crates_small 的那台。
Default: only delivery stations whose destination contains a town palette, for example the one that
ships boxes to crates_small.

解除每周限制（默认关） Removing the weekly limits (off by default)
------------------------------------------------------------------
原版同一个订单每周只能交一次：结算时打上"本周已完成"标记，之后它在订单列表里变灰、点不了，
要等到 day_pride（每周固定的一天，界面提示 ui_order_possible_days 说的就是它）才统一清零。
把 ResetWeeklyOrderLock 改成 true（默认 false，即保持原版）后，清零会提前到"完成的那一刻"——
每次交完订单立刻清掉那个标记，于是可再生订单可以马上再接、再交、再拿好感奖励，不用再等一周。
Vanilla allows each order to be delivered only once per week (a "finished this week" flag until the
weekly reset on the pride day - the ui_order_possible_days hint). Set ResetWeeklyOrderLock to true
(default false = vanilla) and that reset happens the moment an order is completed instead, so
renewable orders can be accepted and paid again right away.

订单窗口的槽位数量（原版 2 个）可增可减，由 OrderSlotsEnabled 控制（默认 false）：
  · false = 收回原版数量（通常是 2 个）—— 多出来的槽位会被移除；被移除的槽位上如果挂着订单，
    那个订单会被取消（槽位清空、订单回到可再接状态；已经送到小镇的货留在托盘上，下次接单照样算数）。
    这就是"把 12 格退回 2 格"的路径。
  · true  = 按 OrderSlots（2 至 12）管理：多于目标就加空槽、少于目标就收回，同样会取消被收掉槽位上的订单。
  · 槽位数量写在存档里，所以"收回"是真实删除，再加回来也只是空槽位。
槽位多于 2 个时 mod 会让它**自动换行**：按画布可用宽度尽量多放、最多 6 个一排，沿用原来的格子尺寸和
间距，所以不会挤变形。（想固定每行几个、或彻底不换行，见下面高级配置里的 OrderSlotsPerRow。）
The slot count (2 in vanilla) can be changed both ways, controlled by OrderSlotsEnabled (false by
default): false shrinks the slots back to the vanilla number, removing the extras - and any order
sitting in a removed slot is cancelled (the slot is cleared and the order becomes acceptable again;
goods already shipped to town stay on the palettes and still count). true follows OrderSlots (2..12),
growing and shrinking to that number with the same cancellation rule. More than 2 slots wrap
automatically: as many per row as the canvas width allows, up to 6, keeping the original cell size
and spacing. (To pin the number per row or switch wrapping off, see OrderSlotsPerRow in the advanced
config further below.)

另外，交完订单、点掉奖励之后，界面会立刻刷新 —— 刚刚交的那条订单马上就能再接，不用关掉窗口再打开。
On top of that, the window is refreshed right after you collect an order reward, so the order you just
finished can be accepted again immediately instead of requiring you to close and reopen the window.

一次性订单（isRenewable = 0）默认仍然是一次性的；打开 RepeatNonRenewableOrders 后它们也能重复交，
等于把一次性订单变成无限的好感/科技点来源（奖励领完、槽位空出来之后才会重置回去）。
One-off orders (isRenewable = 0) stay one-off by default. Turn RepeatNonRenewableOrders on and they can
be repeated as well, which turns them into an endless source of happiness/tech points (they are only
put back once the reward has been collected and the slot freed).

配置 Configuration
------------------
配置文件由 BepInEx 自动生成，路径是游戏目录下的 BepInEx\config\gk2.OrderOptimizer.cfg：
The config file is generated by BepInEx at BepInEx\config\gk2.OrderOptimizer.cfg inside your game
folder:

  Enabled                总开关，默认开。关掉即完全停手，恢复原版纯手动。
                         Master switch, on by default. Turn it off and the mod does nothing.

  RequireFullBatch       默认开：凑齐整批才发车，保证拿到好感奖励。
                         改成 false 就是有多少送多少，原版那种零散发货。
                         On by default: only start once the whole amount is ready, so the reward is
                         guaranteed. Set it to false to ship whatever is available instead.

  PorterInventorySize    搬运工背包格数，默认 64（原版 4）。64 格等于一趟最多 32 箱。
                         填 4 回到原版容量；上限 200。
                         ★ 作用范围只有一处：补给仓库里那台交付站上的搬运僵尸（你在那儿安排的专职搬运工）。
                           矿场、采石场、葡萄园、伐木场、吊车等别的搬运站与它无关，那些站的僵尸背包
                           保持原版 4 格，完全不受影响。
                         The porter's bag size, 64 by default (vanilla 4). 64 slots means up to 32
                         boxes per trip. Set 4 for the vanilla capacity, maximum 200.
                         ★ Scope: only the porter zombies attached to the delivery station inside your
                           supply warehouse (the dedicated porters you staffed there). Other stations -
                           mine, quarry, vineyard, sawmill, cargo lift - keep their vanilla 4-slot bag
                           and are not affected at all.

  ResetWeeklyOrderLock   默认关（保持原版每周限制）。改成 true 即解除"同一订单每周只能交一次"，
                         完成那一刻就把标记清零。
                         Off by default (vanilla weekly limit). Set it to true to remove the
                         one-delivery-per-order-per-week limit.

  OrdersEveryDay         默认关（保持原版）。改成 true 即**直接解除"只有 day_pride 那天能接单"的限制**：
                         原版靠两条按天触发的 GameLogic（定义表 gameLogicsDefs.tsv）在 day_pride 当天 1% 把
                         粉笔板设 1、99% 又设回 0，而接单要求粉笔板大于 0（源码 208885）—— 于是只有那一天能接。
                         打开本项后粉笔板一直保持开启（按游戏自己的写法，源码 248618），哪天都能接单；
                         同时把订单上的"本周已完成"标记也清掉，刚交完的单当天就能再接。
                         只影响【可再生】订单；一次性订单仍需 RepeatNonRenewableOrders。
                         Off by default. Set it to true to remove the vanilla "pride day only" restriction:
                         two day-triggered game logics set chalk_board_enabled to 1 at 1% of the pride day
                         and back to 0 at 99% of it, while accepting an order needs it above 0 (source
                         208885). With this on the chalk board stays enabled (the way the game sets it,
                         source 248618), so orders can be accepted any day; the "finished this week" flag
                         is cleared as well. Renewable orders only; one-off orders still need
                         RepeatNonRenewableOrders.

  OrderSlotsEnabled      默认关。false = 把槽位收回原版数量（多出来的槽位移除，其上的订单被取消）；
                         true = 按下面的 OrderSlots 管理（可增可减，取消规则相同）。
                         Off by default. false shrinks the slots back to the vanilla count, removing
                         the extras and cancelling any order that sat on them; true follows OrderSlots
                         below (growing or shrinking, same cancellation rule).

  OrderSlots             订单窗口槽位数量，默认 2（原版），可调 2 至 12；仅在 OrderSlotsEnabled = true 时生效。
                         Order slot count, 2 by default (vanilla), range 2 to 12; only used when
                         OrderSlotsEnabled = true.

高级配置（不自动生成，普通用户用不到）
Advanced config (not created automatically, most players never need it)
-----------------------------------------------------------------------
上面这些之外的项（库存口径、刷新间隔、换行、一次性订单重复、托盘空间排队、托盘扩容、详细日志）
被刻意从主配置里隐藏，放在同目录的 gk2.OrderOptimizer.advanced.cfg 里：
【删掉该文件或某些键 = 用默认值】，需要时自己新建并只写要改的键。
Everything else (stock scope, refresh interval, wrapping, repeatable one-off orders, the palette
space queue, palette enlargement, verbose logging) is deliberately hidden from the main config and
lives in gk2.OrderOptimizer.advanced.cfg in the same folder. Delete the file (or individual keys) to
use the defaults; create it and write only the keys you want to change.

  StockScope = StationZone           库存口径：StationZone = 只数补给仓库（默认），WholeBase = 整个基地
  UpdateIntervalSeconds = 2          刷新间隔（0.5 ~ 60 秒）
  OrderSlotsPerRow = 0               每行几个槽位；0 = 自动（按画布宽度尽量多放，最多 6 个一排）、
                                     1 = 不换行（保持游戏原排列）、2 至 6 = 固定每行几个
  RepeatNonRenewableOrders = false   true = 一次性订单也能重复交
  PaletteSpaceGuard = true           默认开：订单按托盘空闲格数排队，装不下就等（详见上面那一节）
  LoadingShelfSize = 40              给装货货架按 4 格基准放大：t2（4 格）→ 40、t1（2 格）→ 20。
                                     原版货架只有 2/4 格，塞不下整批订单，这正是"永远凑不齐、生产全停"
                                     的根源；填 0 = 关闭并还原成原版格数。
                                     Scales the loading shelves from a 4-slot baseline: t2 (4 slots) to 40,
                                     t1 (2 slots) to 20. The vanilla shelves are too small to stage a whole
                                     batch - that is the root cause of "never reaching a full order". 0 = off,
                                     which restores the vanilla sizes.
                                     画面仍然只画 4 个箱子，但实际能放 40 个；多出来的箱子只是不画出来，不会丢失。
                                     The shelf model still draws only 4 boxes - it really holds 40, the extras are
                                     simply not rendered, and nothing is lost.
  VerboseLogging = false             详细日志（发布版默认关，不往日志里写记录）

  StockScope / UpdateIntervalSeconds / OrderSlotsPerRow / RepeatNonRenewableOrders /
  PaletteSpaceGuard / LoadingShelfSize / VerboseLogging
  - same defaults as listed above.
  （RepeatNonRenewableOrders 打开后一次性订单也能重复接、重复交，等于无限刷好感/科技点；
     VerboseLogging 打开后可看到每次开关变动、当前缺口与库存，发布版默认不记录。）

安装 Installation
-----------------
1. 装好 BepInEx 5，游戏目录内应有 winhttp.dll 和 BepInEx 文件夹。
   Install BepInEx 5. winhttp.dll and the BepInEx folder should be next to the game exe.

2. 把 OrderOptimizer.dll 放进游戏目录的 BepInEx\plugins\ 文件夹。
   Put OrderOptimizer.dll into the BepInEx\plugins\ folder of your game folder.

3. 启动一次游戏，配置文件会自动生成；之后改配置保存，重启游戏即可生效。
   Run the game once so the config file is generated; after editing it, restart the game.

兼容 Compatibility
------------------
与原版搬运站以及跨区物流类 mod 兼容，例如 Zombie Cross-Zone Logistics 与 Shared Base Storage：
本 mod 写的是游戏自己的站点开关，原版装货和跨区装货两条路径读的都是同一个开关。
Compatible with the vanilla porter stations and with cross-zone logistics mods such as Zombie
Cross-Zone Logistics and Shared Base Storage. The mod writes the game's own station toggles, and
both the vanilla loading path and the cross-zone loading path read that same toggle.

注意 Notes
----------
本 mod 会覆盖你在交付站界面上手动改的那排开关，这正是它的作用。
如果你想要完全手动控制，把 Enabled 改成 false 即可。
The mod overwrites the per-item toggles you changed manually in a delivery station window - that is
exactly its purpose. If you want full manual control, set Enabled to false.
