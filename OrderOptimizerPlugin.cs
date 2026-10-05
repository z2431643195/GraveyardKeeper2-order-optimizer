using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.UI;

namespace OrderOptimizer
{
    /// <summary>
    /// 订单系统优化 / Order system optimization —— 让小镇交付站只送「有订单要的东西」。
    ///
    /// 原版机制（已核对源码）：
    ///   · 交付站（<c>porter_station_conveyor_storage</c> 等）装什么货，只由站点上那排「逐项开关」
    ///     决定 —— 每个箱种在站点 <c>WgoData</c> 上存一个 GameRes 标记；
    ///   · 玩家在站点界面点一下就是 <c>station.SetGameRes(id, !flag)</c>（源码 200002）；
    ///   · 装货时读它：原版 <c>PorterCheckDeliveryStart</c>（源码 121590）与补齐空位那条
    ///     <c>IsPorterEnabledRegularItem</c>（源码 121838）都要求标记为 1；
    ///   · **它完全不看订单** —— 所以只要开着开关，没人订的箱子也会一直往小镇送：
    ///     原料被做成没人要的箱子、占用产线与搬运工时、换不来钱和好感。
    ///   · 订单结算 <c>VendorSystem.TryResolveOrders</c>（源码 97478）只处理**玩家接进槽位**的单，
    ///     从 warehouse + warehouse_cellar 两个区域的小镇托盘（TownPalette，含 crates_small）上扣货。
    ///
    /// 本 mod 做的事：周期性把交付站的逐项开关**对齐到已接订单** ——
    ///     缺口 = 订单需要量 − 已交付量（非紧急单为 order.Count；紧急单按 0，游戏只在整批到位时结算）
    ///            − 小镇托盘上已有的数量
    ///     装货条件（默认 <c>RequireFullBatch = true</c>）：缺口 &gt; 0 **且基地里已备齐整批** → 打开该箱种；
    ///     否则关闭 —— 凑不够就一个都不搬。这样每一单都一定能完成、拿到好感奖励；
    ///     也可以改成"有多少送多少"（<c>RequireFullBatch = false</c>，原版那种零散发货）。
    ///
    ///     ⚠ 物理上限：一箱补给是 <c>ItemSize.Big</c>，要占 2 格（物品 + fake_porter_slot_filler，
    ///       源码 121743-121759），而搬运工背包只有 4 格（119598）⇒ **一趟最多 2 箱**。
    ///       所以"整批"是"凑齐之后分连续几趟送完"，不可能单趟搬完 5~30 箱。
    ///
    /// 为什么这条路最稳：
    ///   · 写的是**玩家自己在界面上写的那一个标记**，不碰任何游戏逻辑、不需要 Harmony 补丁；
    ///   · 原版两条装货路径都读它；跨区物流（Zombie Cross-Zone Logistics）的装货同样读
    ///     <c>AttachedWgoData.GetGameResInt(item.id)</c>，所以两条路径自动都被管住，不会打架；
    ///   · 只接管「订单目录里出现过的物品」（那 22 种 box_*），矿石 / 石料 / 木材等非订单物品
    ///     永不触碰 —— 矿场、采石场、葡萄园这些搬运站不会被误停；
    ///   · 默认只作用于「目的地含小镇托盘」的交付站，其它搬运站一概不碰。
    ///
    /// 关掉 <c>Enabled</c> 即完全停手，站点开关回到纯手动（原版行为）。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class OrderOptimizerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "gk2.OrderOptimizer";
        public const string PluginName = "GK2 OrderOptimizer";
        public const string PluginVersion = "1.0.1";

        /// <summary>槽位数量上限（配置范围与内部 clamp 共用同一个值）。</summary>
        internal const int MaxOrderSlots = 12;

        internal static OrderOptimizerPlugin Instance;
        internal static Harmony HarmonyInstance;

        /// <summary>本次 tick 里被管辖的交付站（Guid）—— 装货上限钩子靠它判断"这个搬运工该不该管"。</summary>
        internal static readonly HashSet<System.Guid> ManagedStationIds = new HashSet<System.Guid>();

        /// <summary>高级选项所在的配置文件（不随插件自动生成；见 Awake 里的说明）。</summary>
        internal static ConfigFile AdvancedConfig;

        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<bool> RequireFullBatch;
        internal static ConfigEntry<string> StockScope;
        internal static ConfigEntry<bool> PaletteSpaceGuard;
        internal static ConfigEntry<int> LoadingShelfSize;
        internal static ConfigEntry<bool> ResetWeeklyOrderLock;
        internal static ConfigEntry<bool> OrdersEveryDay;
        internal static ConfigEntry<bool> OrderSlotsEnabled;
        internal static ConfigEntry<int> OrderSlots;
        internal static ConfigEntry<int> OrderSlotsPerRow;
        internal static ConfigEntry<bool> RepeatNonRenewableOrders;
        internal static ConfigEntry<int> PorterInventorySize;
        internal static ConfigEntry<float> UpdateIntervalSeconds;
        internal static ConfigEntry<bool> VerboseLog;

        /// <summary>搬运工背包（ZombieWgoData 的私有字段）—— 原版是硬编码 4 格，源码 119598。</summary>
        private static readonly FieldInfo PorterInventoryField =
            typeof(ZombieWgoData).GetField("porterInventory", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>订单窗口已画出的槽位控件（UIVendorOrdersWindow.drawnWidgets，源码 208976）——
        /// 拿它取到槽位容器（= 控件父物体，新槽位就是 Instantiate 到这里，源码 209018）。</summary>
        private static readonly FieldInfo VendorOrdersWidgetsField =
            typeof(UIVendorOrdersWindow).GetField("drawnWidgets", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>领奖后需要立刻重绘一次订单界面（下一帧执行，避免在按钮回调里重绘自己）。</summary>
        private static bool pendingUiRefresh;

        /// <summary>上一次应用换行布局的容器 + 参数，用来避免每轮重复设置。</summary>
        private static Transform laidOutContainer;
        private static int laidOutColumns = -1;

        /// <summary>格子尺寸只量一次：记下"哪个容器"和"量到的尺寸"（防止每轮重测导致累加漂移）。</summary>
        private static Transform cellSizeOwner;
        private static Vector2 cachedCellSize;

        /// <summary>布局诊断是否已经打过（每个会话一次）。</summary>
        private static bool dumpedOrdersWindowLayout;

        /// <summary>下一帧再补一遍排版：Unity 的 ContentSizeFitter + LayoutGroup 叠在同一节点上时需要第二遍
        /// 布局才稳定（第一次打开窗口时父级链要到帧末才归位），所以打开后再补一次。</summary>
        private static bool pendingLayoutPass;

        /// <summary>上一次记录的排版签名 —— 只有几何真的变了才写详细日志。</summary>
        private static string lastLayoutSignature;

        /// <summary>排版被应用的次数（每会话累加，用于日志里区分"第一次打开"和后续）。</summary>
        private static int layoutPassCount;

        /// <summary>重入保护（调用游戏自适应时万一把 Redraw 又转回来就不会递归）。</summary>
        private static bool insideSlotLayout;

        /// <summary>`[bag]` 明细是否已经打过（每会话一次 —— 那段每轮都会跑，不能每轮都打）。</summary>
        private static bool loggedBagSummary;

        /// <summary>托盘容量诊断：第一行必打（不受详细日志开关影响），之后只在数字变化时追一行。</summary>
        private static bool loggedTownPaletteSummary;
        private static string lastPaletteTotalsSignature;
        private static float lastPaletteTotalsLogTime = -999f;

        /// <summary>上一次 `[orders]` 缺口摘要的内容签名（只有内容变了才打，避免每轮刷屏）。</summary>
        private static string lastNeedsSignature;

        /// <summary>游戏自己的窗口自适应扩展方法 `RefreshContentFitter(RectTransform)`（在 LazyBearTechnology.dll 里，
        /// 命名空间未知 → 运行期按签名找一次）。Redraw 内部也调它（源码 209048），但那时我们还没改尺寸，
        /// 所以窗口框尺寸是旧的 —— 我们必须在自己改完之后再调一次。</summary>
        private static MethodInfo refreshFitterMethod;
        private static bool refreshFitterSearched;

        /// <summary>订单目录里出现过的物品 id —— 本 mod 只接管这些种类，别的箱子/原料一律不碰。</summary>
        private static readonly HashSet<string> OrderableItems = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>站点定义 id → 是否「目的地含小镇托盘」，算一次缓存住。</summary>
        private static readonly Dictionary<string, bool> TownStationCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static bool orderableItemsReady;
        private static float nextTick;
        private static float lastErrorTime = -1000f;

        private void Awake()
        {
            Instance = this;

            // ★ 高级选项放在**单独的文件**里，主 cfg 只留用户级开关：
            //   `saveOnInit: false` ⇒ 这个文件不会被自动创建/写出来（除非有人主动改值），
            //   想调高级项的人自己新建 gk2.OrderOptimizer.advanced.cfg 填键值即可（README 里有清单）。
            AdvancedConfig = new ConfigFile(
                Path.Combine(Paths.ConfigPath, "gk2.OrderOptimizer.advanced.cfg"),
                saveOnInit: false);

            ModEnabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "总开关。true：按已接订单自动开关交付站的箱种；false：什么都不做，站点开关完全由你手动控制。\n" +
                "Master switch. true: keep the delivery stations' item toggles aligned with the orders you " +
                "accepted. false: do nothing, the toggles stay fully manual (vanilla behaviour).");

            RequireFullBatch = Config.Bind(
                "General",
                "RequireFullBatch",
                true,
                "true（默认）：只有基地库存凑够订单缺口时才允许装货 —— 凑齐整批才发车，保证这单一定能完成、\n" +
                "      拿到好感（点赞）奖励；不够就一个都不搬。\n" +
                "false：只要接了单，有多少送多少（原版那种零散发货）。\n" +
                "注意：一箱补给占 2 格、搬运工只有 4 格，所以一趟最多搬 2 箱 —— 整批是分成连续几趟送过去的，\n" +
                "      只是\"凑齐之前一趟都不发\"。\n" +
                "true (default): a station only starts loading once the base holds the full amount the order still\n" +
                "needs, so the order is guaranteed to complete and pay its happiness reward. Below that, nothing\n" +
                "is moved at all. false: keep the vanilla style of shipping whatever happens to be available.\n" +
                "Note: one supply box takes 2 of the porter's 4 slots, so a porter carries at most 2 boxes per\n" +
                "trip - a batch is delivered over consecutive trips, it just never starts before the amount is\n" +
                "complete.");

            PorterInventorySize = Config.Bind(
                "General",
                "PorterInventorySize",
                64,
                new ConfigDescription(
                    "搬运工的背包格数（原版硬编码 4 格）。一箱补给是 Big 物品、要占 2 格，所以 64 格 = 一趟最多搬 32 箱\n" +
                    "—— 最大订单（30 箱）正好能一次搬完。填 4 就是原版行为；改完立刻生效（下一轮替换背包，\n" +
                    "已经在手上的货会原样搬过去，不会丢）。\n" +
                    "★ 作用范围只有一处：【补给仓库里那台交付站】上的搬运僵尸（就是你在那儿安排的专职搬运工）。\n" +
                    "  别的搬运站（矿场、采石场、葡萄园、伐木场、吊车等）跟这里没有任何关系，它们的僵尸背包\n" +
                    "  保持原版 4 格、完全不受影响。\n" +
                    "The porter's inventory size (vanilla is a hard-coded 4 slots). A supply box is a Big item and\n" +
                    "takes 2 slots, so 64 slots = up to 32 boxes per trip, enough for the largest order (30 boxes).\n" +
                    "Set it to 4 for the vanilla behaviour. Takes effect on the next tick; cargo already carried is\n" +
                    "moved over unchanged.\n" +
                    "★ Scope: only the porter zombies attached to the delivery station inside your supply warehouse\n" +
                    "  (the dedicated porters you staffed there). Stations such as the mine, quarry, vineyard,\n" +
                    "  sawmill or cargo lift are unrelated and keep their vanilla 4-slot bag.",
                    new AcceptableValueRange<int>(4, 200)));

            StockScope = AdvancedConfig.Bind(
                "Advanced",
                "StockScope",
                "StationZone",
                new ConfigDescription(
                    "到哪里数库存来判断\"凑齐整批\"：\n" +
                    "  StationZone（默认）= 只数该交付站所在区域（补给仓库）里的容器 —— 与搬运工实际取货的口径一致；\n" +
                    "  WholeBase       = 数整个基地所有区域（旧行为）。\n" +
                    "Where the mod counts stock to decide whether a whole batch is ready:\n" +
                    "  StationZone (default) = only the containers of the delivery station's own area, which is exactly\n" +
                    "                          where the porters load from;\n" +
                    "  WholeBase             = every area of the base (the old behaviour).",
                    new AcceptableValueList<string>("StationZone", "WholeBase")));

            UpdateIntervalSeconds = AdvancedConfig.Bind(
                "Advanced",
                "UpdateIntervalSeconds",
                2f,
                new ConfigDescription(
                    "多久检查一次订单并刷新站点开关，单位秒。\n" +
                    "How often the accepted orders are checked and the station toggles refreshed, in seconds.",
                    new AcceptableValueRange<float>(0.5f, 60f)));

            PaletteSpaceGuard = AdvancedConfig.Bind(
                "Advanced",
                "PaletteSpaceGuard",
                true,
                "默认开：订单要按【小镇托盘的空闲格数】排队发车。\n" +
                "  背景：紧急订单要【整批同时躺在托盘上】才结算（源码 97499 —— 托盘现有量 >= 需求总量才推进），\n" +
                "        而托盘总格数 = warehouse + warehouse_cellar 两区里所有 TownPalette 的格数之和（源码 97488）。\n" +
                "  开着：只放行【还差的量装得进托盘空闲格数】的订单，并按【最接近完成】的顺序喂 —— 一单跑完释放空间，\n" +
                "        再喂下一单。多张订单同时推进时不再互相挤爆托盘（那会让谁都凑不齐整批、补给仓被撑满、上游停）。\n" +
                "  关掉：回到只看【凑齐整批】那一道闸门，不管托盘还剩多少空格。\n" +
                "On by default: orders are queued against the free space of the town palettes.\n" +
                "  Urgent orders only settle once their whole batch sits on the palettes (source 97499), and the\n" +
                "  total palette space is the sum of every TownPalette's size across warehouse + warehouse_cellar\n" +
                "  (source 97488). With this on, only orders whose remaining amount fits the free palette space\n" +
                "  are released, closest-to-finished first, so several orders can no longer fill the palettes with\n" +
                "  half-finished batches (which used to stall everything). Turn it off to go back to the plain\n" +
                "  whole-batch gate.");

            LoadingShelfSize = AdvancedConfig.Bind(
                "Advanced",
                "LoadingShelfSize",
                40,
                new ConfigDescription(
                    "给【搬运工装货的货架】扩容（默认 40；填 0 = 关闭并还原成原版格数）。这批容器 = 交付站所在区域的 MultiInventoryWgoDatas，\n" +
                    "也正是本 mod 数【凑齐整批】的同一批容器；原版 conveyor_storage_t1 只有 2 格、t2 只有 4 格\n" +
                    "（定义表 wgoDefs.tsv），而订单要 5 至 30 箱同时备齐 ⇒ 判据永远不成立、开关永不打开、上游被顶停。\n" +
                    "  比例规则（以原版 4 格为基准）：目标 = max(原版格数, min(该值, 原版格数 × 该值 / 4))。\n" +
                    "  所以填 40 时：t2（4 格）→ 40、t1（2 格）→ 20 —— 升级到 t2 正好翻倍，符合直觉。\n" +
                    "  原版就比目标大的容器一律【只增不减】（例如 999 格的箱子保持 999，绝不被缩小）。\n" +
                    "  改回 0 会还原成定义表里的原版格数（只在容器里现有的东西放得下时才缩，不会丢货）。\n" +
                    "Enlarges the shelves the porters load from (default 40; set it to 0 to turn it off and restore the\n" +
                    "vanilla sizes). Those containers are the delivery station's zone (MultiInventoryWgoDatas) - the very\n" +
                    "set the whole-batch check counts. In vanilla\n" +
                    "conveyor_storage_t1 holds 2 slots and t2 holds 4 (see wgoDefs.tsv), while an order needs 5 to 30\n" +
                    "boxes staged at once, so the gate can never open and upstream production stalls.\n" +
                    "  Scaling rule (4 slots = baseline): target = max(vanilla, min(this value, vanilla * value / 4)).\n" +
                    "  With 40 that gives t2 (4 slots) to 40 and t1 (2 slots) to 20 - upgrading to t2 doubles it.\n" +
                    "  Containers already bigger than the target are never shrunk (a 999-slot crate stays 999).\n" +
                    "Setting it back to 0 restores the data-defined sizes, and only once a shelf actually has room,\n" +
                    "so nothing is ever lost.",
                    new AcceptableValueRange<int>(0, 999)));

            ResetWeeklyOrderLock = Config.Bind(
                "Orders",
                "ResetWeeklyOrderLock",
                false,
                "false（默认）：保持原版的每周重置 —— 同一个订单每周只能交一次，要等 day_pride 那天统一重置\n" +
                "      （源码 97546 / 97554）。发布版默认不动原版玩法。\n" +
                "true：解除这个限制 —— 每次完成订单后立刻清掉\"本周已完成\"标记，可再生订单交完马上就能再接、\n" +
                "      再做、再拿好感奖励；一次性订单是否也能重复，由下面的 RepeatNonRenewableOrders 单独控制。\n" +
                "false (default): keep the vanilla weekly reset - each order can be delivered once per week until\n" +
                "the pride day resets the flags (source 97546 / 97554). The released defaults change nothing.\n" +
                "true: lift that limit - the finished-this-week flag is cleared the moment an order is completed,\n" +
                "so renewable orders can be re-accepted and paid again right away.");

            OrdersEveryDay = Config.Bind(
                "Orders",
                "OrdersEveryDay",
                false,
                "false（默认）：保持原版 —— 原版用两条**按天触发**的 GameLogic 卡死接单时机（定义表 gameLogicsDefs.tsv）：\n" +
                "      enable_chalk_board  → day_pride 当天 1%  时把 chalk_board_enabled 设成 1；\n" +
                "      disable_chalk_board → day_pride 当天 99% 时把它设回 0；\n" +
                "      而接单判定要求 chalk_board_enabled > 0（源码 208885）⇒ 实际效果就是【只有那一天能接单】。\n" +
                "true：**直接解除这道日期限制** —— 按游戏自己的写法把 chalk_board_enabled 一直保持为 1，哪天都能接单；\n" +
                "      同时把订单上的\"本周已完成\"标记也清掉（领奖那一刻 + 每轮兜底），于是刚交完的单当天就能再接。\n" +
                "      只影响【可再生】订单的重复接取；一次性订单仍需下面的 RepeatNonRenewableOrders。\n" +
                "false (default): vanilla behaviour - two day-triggered game logics (gameLogicsDefs.tsv) set\n" +
                "chalk_board_enabled to 1 at 1% of the pride day and back to 0 at 99% of it, while accepting an\n" +
                "order requires chalk_board_enabled > 0 (source 208885) - so orders really are limited to that\n" +
                "one day. true: that date restriction is removed - the chalk board is kept at 1 exactly the way\n" +
                "the game itself sets it (source 248618), and the \"finished this week\" flag is cleared as well,\n" +
                "so an order can be picked again the same day. Renewable orders only; one-off orders still need\n" +
                "RepeatNonRenewableOrders.");

            OrderSlotsEnabled = Config.Bind(
                "Orders",
                "OrderSlotsEnabled",
                false,
                "是否接管订单窗口的槽位数量（默认 false）。\n" +
                "  false：把槽位**收回原版数量**（常量 start_orders_count，通常是 2）—— 多出来的槽位会被移除；\n" +
                "         被移除的槽位如果挂着订单，那个订单会被取消（槽位清空、订单回到可再接状态，\n" +
                "         已经送到小镇的货留在托盘上，下次接单时照样算数）。\n" +
                "  true ：按下面的 OrderSlots 管理（多于目标就加、少于目标就收，同样会取消被收掉槽位上的订单）。\n" +
                "  ⚠ 槽位写在存档里，所以\"收回\"是真实删除；一旦收掉再加回来也只是重新加回空槽位。\n" +
                "Whether the mod manages the order window's slot count (false by default).\n" +
                "  false: slots are shrunk back to the vanilla count (the constant start_orders_count, usually 2).\n" +
                "         Any order sitting in a removed slot is cancelled: the slot is cleared and the order becomes\n" +
                "         acceptable again - goods already shipped to town stay on the palettes and still count.\n" +
                "  true : slots follow OrderSlots below (grown or shrunk to that number, same cancellation rule).\n" +
                "  Note: the slot list lives in your save, so shrinking really removes them.");

            OrderSlots = Config.Bind(
                "Orders",
                "OrderSlots",
                2,
                new ConfigDescription(
                    "订单窗口的槽位数量（原版 = 常量 start_orders_count = 2，源码 97433 / 88520）。\n" +
                    "只在 OrderSlotsEnabled = true 时生效；界面是按槽位数动态生成的（源码 209004-209046）。\n" +
                    "多于目标就加空槽、少于目标就收回（收回时取消其上的订单，见 OrderSlotsEnabled 的说明）。\n" +
                    "How many order slots the orders window has (vanilla = the constant start_orders_count = 2,\n" +
                    "source 97433 / 88520). Only used when OrderSlotsEnabled = true. The window builds one widget\n" +
                    "per slot (source 209004-209046); slots are grown or shrunk to this number, and orders sitting\n" +
                    "in removed slots are cancelled (see OrderSlotsEnabled).",
                    new AcceptableValueRange<int>(2, MaxOrderSlots)));

            OrderSlotsPerRow = AdvancedConfig.Bind(
                "Advanced",
                "OrderSlotsPerRow",
                0,
                new ConfigDescription(
                    "订单窗口一排放几个槽位（多于这个数就换行）。\n" +
                    "  0（默认）= **自动**：按画布可用宽度尽量多放（最多 6 个一排），所以打开更多槽位就会自动换行，\n" +
                    "              不用手填；\n" +
                    "  1        = 不换行，保持游戏原来的排列（不接管排版）；\n" +
                    "  2 至 6   = 固定每行几个。\n" +
                    "只在槽位多于 2 个时才有意义；原版 2 格的外观永远不动。\n" +
                    "How many slots per row the orders window uses (extra ones wrap to the next row).\n" +
                    "  0 (default) = automatic: as many as fit the canvas width, up to 6 per row - so enabling more\n" +
                    "                slots wraps them by itself, nothing to configure;\n" +
                    "  1           = no wrapping at all, keep whatever arrangement the game does;\n" +
                    "  2 to 6      = fixed number per row.\n" +
                    "Only meaningful when there are more than 2 slots; the vanilla 2-slot look is never touched.",
                    new AcceptableValueRange<int>(0, 6)));

            RepeatNonRenewableOrders = AdvancedConfig.Bind(
                "Advanced",
                "RepeatNonRenewableOrders",
                false,
                "true：连\"一次性\"（isRenewable = 0）的订单也能重复接、重复交 —— 领完奖励、槽位空出来之后，\n" +
                "      它的状态会被重置回可接。注意这等于让一次性订单变成无限刷（好感/科技点也会重复给）。\n" +
                "false（默认）：一次性订单照原版，交完就结束。\n" +
                "true: even one-off orders (isRenewable = 0) can be accepted and delivered again - once the reward\n" +
                "has been collected and the slot freed, the order is put back to accept-able. This effectively\n" +
                "turns one-off orders into an infinite source of happiness/tech points.\n" +
                "false (default): one-off orders stay one-off.");

            VerboseLog = AdvancedConfig.Bind(
                "Advanced",
                "VerboseLogging",
                false,
                "详细日志：每次开关变化都打一行（含该箱种当前缺口），便于确认它在按订单动作。\n" +
                "Verbose log: one line per toggle change including the current order gap, so you can verify " +
                "that it follows your orders.");

            try
            {
                HarmonyInstance = new Harmony(PluginGuid);

                // 显式挂钩（不靠 PatchAll 扫描，避免类可见性之类的问题静默失败），并且把结果写进日志，
                // 这样"装货上限到底有没有生效"在日志里一眼可查。
                MethodInfo takeMethod = AccessTools.Method(
                    typeof(ZombieWgoData),
                    "PorterTryTakeItemFromInventory",
                    new[] { typeof(Inventory), typeof(NeedItemData) });

                if (takeMethod == null)
                {
                    Logger.LogError("找不到装货方法 PorterTryTakeItemFromInventory —— 只装缺口量不会生效 / target method not found");
                }
                else
                {
                    MethodInfo prefixMethod = AccessTools.Method(typeof(Patch_PorterTakeCapped), "Prefix");
                    MethodInfo postfixMethod = AccessTools.Method(typeof(Patch_PorterTakeCapped), "Postfix");

                    if (prefixMethod == null || postfixMethod == null)
                    {
                        Logger.LogError("找不到补丁方法 Prefix/Postfix —— 只装缺口量不会生效 / patch methods not found");
                    }
                    else
                    {
                        HarmonyInstance.Patch(takeMethod,
                            new HarmonyMethod(prefixMethod),
                            new HarmonyMethod(postfixMethod));
                        Logger.LogInfo($"已挂钩装货上限 / loading cap patched on {takeMethod.DeclaringType.Name}.{takeMethod.Name}");
                    }
                }

                // 每周限制：结算那一刻就把"本周已完成"标记清零（不等到 day_pride）
                MethodInfo markMethod = AccessTools.Method(typeof(VendorOrderData), "MarkFinishedThisWeek");
                MethodInfo noLockPostfix = markMethod != null
                    ? AccessTools.Method(typeof(Patch_NoWeeklyLock), "Postfix")
                    : null;

                if (markMethod == null || noLockPostfix == null)
                {
                    Logger.LogError("找不到 MarkFinishedThisWeek —— 每周限制只剩每轮扫描兜底 / target method not found");
                }
                else
                {
                    HarmonyInstance.Patch(markMethod, null, new HarmonyMethod(noLockPostfix));
                    Logger.LogInfo("已挂钩订单每周重置 / weekly order lock patched on VendorOrderData.MarkFinishedThisWeek");
                }

                // 订单窗口重绘后重排槽位（换行）
                MethodInfo ordersRedrawMethod = AccessTools.Method(typeof(UIVendorOrdersWindow), "Redraw");
                MethodInfo slotLayoutPostfix = ordersRedrawMethod != null
                    ? AccessTools.Method(typeof(Patch_OrdersWindowLayout), "Postfix")
                    : null;

                if (ordersRedrawMethod == null || slotLayoutPostfix == null)
                {
                    Logger.LogError("找不到 UIVendorOrdersWindow.Redraw —— 槽位换行只剩每轮扫描兜底 / target method not found");
                }
                else
                {
                    HarmonyInstance.Patch(ordersRedrawMethod, null, new HarmonyMethod(slotLayoutPostfix));
                    Logger.LogInfo("已挂钩订单窗口排版 / orders window layout patched on UIVendorOrdersWindow.Redraw");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"Harmony 补丁失败（装货上限将不生效）/ patching failed: {e.GetType().Name}: {e.Message}");
            }

            Logger.LogInfo(
                $"{PluginName} {PluginVersion} 已加载（订单驱动交付站开关 / order-driven station toggles）");
        }

        private void Update()
        {
            try
            {
                if (!ModEnabled.Value) return;

                // 领奖后下一帧刷新一次订单界面 —— 让"刚交完的单"立刻能再接（不必关窗重开）
                if (pendingUiRefresh)
                {
                    pendingUiRefresh = false;
                    RefreshOrdersUi();
                }

                // 订单窗口打开后，下一帧再补一遍排版 + 布局重建：
                // 引擎自己的布局/自适应要到帧末才跑完，补这一遍之后"第一次打开"才会和后续一致。
                if (pendingLayoutPass)
                {
                    pendingLayoutPass = false;
                    UIVendorOrdersWindow orders = LazyUI.GetWindow<UIVendorOrdersWindow>();
                    if (orders != null && orders.IsShown) ApplySlotLayout(orders, false, "deferred");
                }

                float now = Time.realtimeSinceStartup;
                if (now < nextTick) return;
                nextTick = now + Mathf.Max(0.5f, UpdateIntervalSeconds.Value);

                Tick();
            }
            catch (Exception e)
            {
                LogErrorThrottled(e);
            }
        }

        /// <summary>一轮：把交付站的逐项开关对齐到当前已接订单。</summary>
        private static void Tick()
        {
            if (MainGame.Instance == null || MainGame.Instance.GameSave == null) return;   // 主菜单还没进存档

            GameBalance balance = GameBalance.Me;
            WorldData world = MainGame.WorldData;
            if (balance == null || world == null) return;

            if (!orderableItemsReady) BuildOrderableItemSet(balance);
            if (!orderableItemsReady) return;                                              // 平衡表还没好，下一轮再来

            ReportTownPalettes(world);           // 托盘容量：第一行必打，之后数字变化时（详细日志）追一行
            DumpTownContainers(world);           // 详细日志：一次性列出全世界所有 TownPalette 容器

            VendorSystem vendorSystem = MainGame.Instance.GameSave.vendorSystem;
            if (vendorSystem == null) return;

            KeepChalkBoardEnabled();             // ★ OrdersEveryDay：解除"只有 day_pride 那天能接单"

            Dictionary<string, int> needed = CollectNeededByOrders(vendorSystem);
            if (needed.Count > 0)
            {
                SubtractStockOnTownPalettes(world, needed);                                // 小镇托盘上已有的算已交付
            }

            UpdatePaletteGuard(world, needed);   // ★ 托盘空间闸门：决定这一轮允许推进哪些订单

            // 库存按"区域"算（默认就是交付站所在区域 = 补给仓库），同一区域只算一遍
            Dictionary<WorldZoneData, Dictionary<string, int>> stockByZone =
                (RequireFullBatch.Value && needed.Count > 0)
                    ? new Dictionary<WorldZoneData, Dictionary<string, int>>()
                    : null;

            Dictionary<string, int> lastStock = new Dictionary<string, int>(StringComparer.Ordinal);

            List<PorterStationDef> stationDefs = balance.porterStationDefs;
            if (stationDefs == null) return;

            // 本次实际被管辖的交付站（只有"往小镇托盘送货"的那台）—— 每轮重建
            ManagedStationIds.Clear();

            int changed = 0;
            for (int d = 0; d < stationDefs.Count; d++)
            {
                PorterStationDef def = stationDefs[d];
                if (def == null || string.IsNullOrEmpty(def.id)) continue;
                if (def.items == null || def.items.Count == 0) continue;

                List<WgoData> stations = world.GetWgoDataList(def.id);
                if (stations == null || stations.Count == 0) continue;

                // ★ 范围写死：只接管"往小镇托盘送货"的交付站（默认就是补给仓库那台）。
                //   矿场 / 采石场 / 葡萄园 / 伐木场 / 吊车等其它搬运站一律不进这个循环。
                if (!IsTownDeliveryStation(world, def)) continue;

                for (int s = 0; s < stations.Count; s++)
                {
                    WgoData station = stations[s];
                    if (station == null) continue;

                    ManagedStationIds.Add(station.UniqueId.Guid);    // 只给这些站上的搬运工扩容/限流

                    // 这个站自己区域里的库存（默认口径）—— 搬运工也正是从这里装货的
                    WorldZoneData stationZone = station.WorldZoneData;
                    Dictionary<string, int> stock = StockFor(stationZone, needed, stockByZone);
                    if (stock.Count > 0) lastStock = stock;

                    // 日志里带上"站 #实例 @ 区域"：同一种站建了多台时（例如补给仓库里那两台交付站），
                    // 靠这个短 id 就能看出每条日志是哪一台写的。
                    string stationTag = def.id + "#" +
                        station.UniqueId.Guid.ToString("N").Substring(0, 4) + "@" +
                        (stationZone != null && !string.IsNullOrEmpty(stationZone.id) ? stationZone.id : "?");

                    for (int k = 0; k < def.items.Count; k++)
                    {
                        NeedItemData item = def.items[k];
                        if (item == null || string.IsNullOrEmpty(item.id)) continue;

                        // 非订单物品（矿石、石料、木材、农作物…）永不接管：
                        // 万一被误关，那些搬运站就整条停了。
                        if (!OrderableItems.Contains(item.id)) continue;

                        int gap;
                        needed.TryGetValue(item.id, out gap);
                        int have;
                        stock.TryGetValue(item.id, out have);

                        // 已经在搬运工手上的货算"已派出" —— 否则刚装走的那几箱会让 base 库存下跌，
                        // 判据失真、开关被误关（大订单会被卡住）。
                        int carried = gap > 0 ? Patch_PorterTakeCapped.CarriedByManagedPorters(item.id) : 0;
                        int stillToSend = Mathf.Max(0, gap - carried);

                        // 装货条件（RequireFullBatch = true 时）：
                        //   还有东西没派出 且 基地里备齐了这批 —— 凑不够就一个都不搬（避免零散发货，
                        //   那种运输既拿不到好感奖励，还占着搬运工）。
                        // 托盘空间闸门（PaletteSpaceGuard）：这一单"还差的量"装不进托盘空闲格数就暂缓，
                        // 先让别的单跑完、空间释放出来再继续（否则多张单会把托盘挤满半成品，谁都完不成）。
                        bool paletteOk = PaletteAllowed(item.id);

                        bool wanted = stillToSend > 0 && paletteOk
                            && (!RequireFullBatch.Value || have >= stillToSend);
                        int desired = wanted ? 1 : 0;

                        if (station.GetGameResInt(item.id) == desired) continue;

                        station.SetGameRes(item.id, desired);
                        changed++;

                        if (VerboseLog.Value)
                        {
                            Instance.Logger.LogInfo(
                                $"[toggle] {stationTag} {item.id} → {(desired == 1 ? "开 on" : "关 off")}" +
                                $"（缺口 gap={gap}, 在途 carried={carried}, {StockLabel()} have={have}）");
                        }
                    }
                }
            }

            if (changed > 0)
            {
                Instance.Logger.LogInfo(
                    $"按订单更新了 {changed} 个箱种开关 / {changed} station toggle(s) aligned with accepted orders.");
            }

            // 站点的收货/取货容器诊断放在站点循环**之后**：那时世界的容器名单肯定已经建好，
            // 免得像 2026-10-05 那次一样，在世界还没就绪时先判一次、把 false 缓存进 TownStationCache。
            DumpStationTargets(world, balance);                       // 详细日志：交付站的收货容器（含非托盘）
            EnsureLoadingShelfSize(world, balance);                   // ★ 装货货架扩容（LoadingShelfSize，0 = 关闭）
            DumpStationSources(world, balance);                       // 详细日志：取货容器（**扩容之后**再打，显示的是实际格数）

            EnsurePorterInventory(ManagedStationIds);                 // 只扩被管辖站上的搬运工

            MaintainOrderSystem(vendorSystem);                        // 每周限制 / 槽位（订单系统本身）
            MaintainOrdersWindowLayout("tick");                       // 槽位换行（OrderSlots > 2 时）

            if (VerboseLog.Value)
            {
                // ★ 只在缺口内容真的变化时打：这段每轮（默认 2 秒）都跑，之前每轮一条会把日志刷爆
                string needsText = DescribeNeeds(needed, lastStock);
                if (needsText != lastNeedsSignature)
                {
                    lastNeedsSignature = needsText;
                    Instance.Logger.LogInfo(needsText);
                }
            }
        }

        /// <summary>已接订单 → 每种物品还差多少（紧急单按整批算，因为游戏只在整批到位时结算）。</summary>
        private static Dictionary<string, int> CollectNeededByOrders(VendorSystem vendorSystem)
        {
            var needed = new Dictionary<string, int>(StringComparer.Ordinal);

            List<(VendorOrderData, Vendor)> orders = vendorSystem.GetCurrentOrders();
            if (orders == null) return needed;

            for (int i = 0; i < orders.Count; i++)
            {
                VendorOrderData order = orders[i].Item1;
                if (order == null) continue;                                     // 空槽位
                if (order.State == VendorOrderState.Finished) continue;

                VendorOrderDef def = order.Definition;
                if (def == null || string.IsNullOrEmpty(def.itemId) || def.count <= 0) continue;

                // 紧急单（isUrgent）：游戏要求托盘上凑满整批才结算，order.Count 一直是 0，
                // 所以已交付量按 0 算（托盘存量会在下一步统一扣掉）。
                int delivered = def.isUrgent ? 0 : order.Count;
                int remaining = def.count - delivered;
                if (remaining <= 0) continue;

                int cur;
                needed.TryGetValue(def.itemId, out cur);
                needed[def.itemId] = cur + remaining;
            }

            return needed;
        }

        /// <summary>把小镇托盘上已有的同种货物从缺口里扣掉，避免重复发货。</summary>
        private static bool dumpedTownContainers;

        /// <summary>
        /// 详细日志下打**一次**：把全世界所有 `interactionType == TownPalette` 的容器逐条列出来
        /// （容器 id @ 区域 · 已用/格数）。
        ///
        /// 为什么需要：游戏结算（`TryResolveOrders` 97488）**只数 warehouse + warehouse_cellar 两个区域**里的托盘，
        /// 而玩家在画面上看到的"托盘货架"可能属于别的区域或别的容器类型。这一行用来核实
        /// "看得见的托盘"和"结算时数的托盘"是不是同一批 —— 对不上就得改统计范围。
        /// 只打一次（世界建好、且确实找到容器之后），只在详细日志下。
        /// </summary>
        private static void DumpTownContainers(WorldData world)
        {
            if (!VerboseLog.Value || dumpedTownContainers || world == null) return;

            List<GameSceneData> scenes = world.gameSceneDataList;
            if (scenes == null) return;

            List<string> found = new List<string>();
            for (int s = 0; s < scenes.Count; s++)
            {
                GameSceneData scene = scenes[s];
                if (scene == null || scene.worldZones == null) continue;

                for (int z = 0; z < scene.worldZones.Count; z++)
                {
                    WorldZoneData zone = scene.worldZones[z];
                    if (zone == null || zone.wgoDataList == null) continue;

                    for (int i = 0; i < zone.wgoDataList.Count; i++)
                    {
                        WgoData wgo = world.GetWgoData(zone.wgoDataList[i]);
                        if (wgo == null || wgo.Inventory == null || wgo.Inventory.Data == null) continue;
                        if (!IsTownPalette(wgo)) continue;

                        found.Add((wgo.Definition != null ? wgo.Definition.id : "?") + "@" + zone.id + " " +
                                  wgo.Inventory.Data.InventoryFillSize + "/" + wgo.Inventory.Data.InventorySize);
                    }
                }
            }

            if (found.Count == 0) return;                 // 世界还没建好，下一轮再来
            dumpedTownContainers = true;

            Instance.Logger.LogInfo(
                $"[palette] 全世界 TownPalette 容器共 {found.Count} 个（已用/格数）：{string.Join("、", found.ToArray())}" +
                $" / every TownPalette container in the world (used/size)");
        }

        /// <summary>
        /// 数一遍 warehouse + warehouse_cellar 里所有小镇托盘（TownPalette）的格数 / 已用 / 空闲，并汇报。
        ///
        /// 为什么需要：紧急订单（`isUrgent`）在游戏里要求**整批同时待在托盘上**才结算 ——
        /// `VendorSystem.TryResolveOrders` 源码 97499：紧急单的待结算量 = (托盘现有量 >= 需求总量) ? 总量 : 0，
        /// 而托盘总容量 = 两个区域里所有 TownPalette 的 inventory_size 之和（源码 97488 / 39888）。
        /// 于是"托盘还能放多少箱"就是同时推进多个订单时的硬上限 —— 这一行把它变成看得见的数字。
        ///
        /// 打印时机：**第一行必打**（不受详细日志开关影响 —— 排查问题时最需要的就是它）；
        /// 之后只在（容器数 / 格数 / 已用 / 空闲）任一变化时补一行，且只在 `VerboseLogging = true` 下。
        /// 每轮（默认 2 秒）核对一次，开销只是遍历两个区域里的少数容器。
        /// ※ 之前写成"只打一次"，于是它永远是**进存档那一刻**的快照（用户实测：接单前打的
        ///   已用 0，后面送到托盘上的货完全没反映出来）—— 所以改成跟着数字变化走。
        /// </summary>
        private static void ReportTownPalettes(WorldData world)
        {
            int palettes = 0, slots = 0, free = 0, used = 0;
            SumTownPalettes(world, "warehouse", ref palettes, ref slots, ref free, ref used);
            SumTownPalettes(world, "warehouse_cellar", ref palettes, ref slots, ref free, ref used);

            if (palettes == 0) return;                                   // 世界还没建好，下一轮再来

            string signature = palettes + "|" + slots + "|" + used + "|" + free;
            bool first = !loggedTownPaletteSummary;
            if (!first && signature == lastPaletteTotalsSignature) return;

            lastPaletteTotalsSignature = signature;
            loggedTownPaletteSummary = true;

            if (!first)
            {
                if (!VerboseLog.Value) return;                            // 后续变化：只在详细日志下追
                float now = Time.realtimeSinceStartup;
                if (now - lastPaletteTotalsLogTime < 2f) return;           // 连续到货时防抖
                lastPaletteTotalsLogTime = now;
            }

            Instance.Logger.LogInfo(
                $"[palette] 小镇托盘：{palettes} 个容器，共 {slots} 格（已用 {used}，空闲 {free}）" +
                $" —— 紧急订单要整批同时躺在托盘上才结算，空闲格数决定同时能推进几单" +
                $" / town palettes: {palettes} containers, {slots} slots ({used} used, {free} free)");
        }

        private static void SumTownPalettes(WorldData world, string zoneId,
            ref int palettes, ref int slots, ref int free, ref int used)
        {
            WorldZoneData zone = world.GetWorldZoneDataById(zoneId);
            if (zone == null || zone.wgoDataList == null) return;

            for (int i = 0; i < zone.wgoDataList.Count; i++)
            {
                WgoData wgo = world.GetWgoData(zone.wgoDataList[i]);
                if (wgo == null || wgo.Inventory == null || !IsTownPalette(wgo)) continue;

                int size = wgo.Inventory.Data.InventorySize;
                int fill = wgo.Inventory.Data.InventoryFillSize;

                palettes++;
                slots += size;
                used += fill;
                free += Mathf.Max(0, size - fill);
            }
        }

        // ---------------------------------------------------------------- 托盘空间闸门（B）与托盘扩容（A）

        /// <summary>这一轮被放行的订单箱种（托盘空间够装下）；`paletteGuardOn` 为 false 时全部放行。</summary>
        private static readonly HashSet<string> paletteAllowed = new HashSet<string>(StringComparer.Ordinal);
        private static bool paletteGuardOn;
        private static string lastPaletteGuardSignature;

        /// <summary>
        /// 托盘空间闸门（高级项 `PaletteSpaceGuard`，默认开）—— 决定"这一轮允许推进哪些订单"。
        ///
        /// 为什么需要：紧急订单要【整批同时躺在托盘上】才结算（`TryResolveOrders` 源码 97499），
        /// 托盘总格数又有限（97488）。多张订单同时发车时，托盘会被几张半成品分着占满 ⇒ 谁都凑不齐整批
        /// ⇒ 托盘永不释放 ⇒ 补给仓被撑满、上游跟着停。所以这里按"还差的量"从小到大排队，
        /// 只放行【装得进当前空闲格数】的那些 —— 先跑完最接近完成的单，它结算后释放空间，再喂下一单。
        ///
        /// 开销：每轮（默认 2 秒）算一次，遍历的是 warehouse + warehouse_cellar 里的托盘（数量很少）。
        /// 装货层的 Prefix 只查这个缓存集合（不重算），所以最多晚一拍生效。
        /// </summary>
        private static void UpdatePaletteGuard(WorldData world, Dictionary<string, int> needed)
        {
            paletteGuardOn = false;
            paletteAllowed.Clear();

            if (!PaletteSpaceGuard.Value) return;                  // 关掉：完全不管托盘空间
            if (world == null || needed.Count == 0) return;

            paletteGuardOn = true;

            int free = TownPaletteFreeSlots(world);
            if (free <= 0) WarnPaletteFull();          // ⚠ 托盘满了：这行不受详细日志开关影响

            // 按"还差多少"从小到大排队（相同则按箱种名，保证顺序稳定）；能装下就放行
            List<KeyValuePair<string, int>> queue = new List<KeyValuePair<string, int>>(needed);
            queue.Sort((a, b) => a.Value != b.Value
                ? a.Value.CompareTo(b.Value)
                : string.CompareOrdinal(a.Key, b.Key));

            int budget = free;
            for (int i = 0; i < queue.Count; i++)
            {
                int amount = queue[i].Value;
                if (amount <= 0) continue;                         // 没缺口：本来就不会开
                if (amount > budget) continue;                     // 装不下：这一轮暂缓，等空间释放
                budget -= amount;
                paletteAllowed.Add(queue[i].Key);
            }

            LogPaletteDeferred(needed, free);
        }

        /// <summary>这一轮是否允许装/发该箱种（闸门关闭时恒为 true）。</summary>
        private static bool PaletteAllowed(string itemId)
        {
            if (!paletteGuardOn) return true;
            return paletteAllowed.Contains(itemId);
        }

        /// <summary>小镇托盘当前的空闲格数（warehouse + warehouse_cellar 两区所有 TownPalette）。</summary>
        private static int TownPaletteFreeSlots(WorldData world)
        {
            int palettes = 0, slots = 0, free = 0, used = 0;
            SumTownPalettes(world, "warehouse", ref palettes, ref slots, ref free, ref used);
            SumTownPalettes(world, "warehouse_cellar", ref palettes, ref slots, ref free, ref used);
            return free;
        }

        /// <summary>
        /// 有订单被托盘空间暂缓时打一行（详细日志；同一状态只打一次，避免每轮刷屏）。
        /// 只写这一行、不动别的：让人一眼看出"不是 mod 卡了，是托盘满了在排队"。
        /// </summary>
        private static void LogPaletteDeferred(Dictionary<string, int> needed, int free)
        {
            if (!VerboseLog.Value) return;

            List<string> deferred = new List<string>();
            int deferredTotal = 0;

            foreach (KeyValuePair<string, int> kv in needed)
            {
                if (kv.Value <= 0 || paletteAllowed.Contains(kv.Key)) continue;
                deferred.Add(kv.Key + "(" + kv.Value + ")");
                deferredTotal += kv.Value;
            }

            if (deferred.Count == 0)
            {
                lastPaletteGuardSignature = null;
                return;
            }

            string signature = free + "|" + string.Join(",", deferred.ToArray());
            if (signature == lastPaletteGuardSignature) return;
            lastPaletteGuardSignature = signature;

            Instance.Logger.LogInfo(
                $"[palette] 托盘空间不足，暂缓 {deferred.Count} 种（共需 {deferredTotal} 格，当前空闲 {free}）：" +
                $"{string.Join("、", deferred.ToArray())} —— 先跑完已放行的那几单，空间释放后自动继续" +
                $" / deferred (need {deferredTotal} slots, {free} free): {string.Join(", ", deferred.ToArray())}");
        }

        private static float lastPaletteFullWarnTime = -9999f;

        /// <summary>
        /// 托盘一个空格都没有、而还有订单没送完时的 ⚠ 警告（**不受 `VerboseLogging` 影响**，5 分钟最多一行）。
        ///
        /// 为什么必须报出来：托盘上可能压着【没人订的旧货】—— 未接单的货**不会结算也不会消失**
        /// （`TryResolveOrders` 97478 只遍历已接单），于是它们永久占位。这时任何订单都装不进托盘，
        /// 空间闸门会一直暂缓 ⇒ 看起来像"mod 卡住了"，所以要让人一眼看见、自己去清托盘。
        /// </summary>
        private static void WarnPaletteFull()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastPaletteFullWarnTime < 300f) return;      // 5 分钟最多一行，不刷屏
            lastPaletteFullWarnTime = now;

            Instance.Logger.LogWarning(
                "[palette] ⚠ 小镇托盘已满（空闲 0 格），订单装不进去 —— 托盘上可能压着没人订的旧货" +
                "（未接单的货不会结算也不会消失，会一直占位）。把托盘清一清就能继续。" +
                " / town palettes are completely full: clear them to continue");
        }

        private static string lastShelfSizeSignature;

        /// <summary>
        /// 装货货架扩容（高级项 `LoadingShelfSize`，默认 0 = 关闭）。
        ///
        /// 目标容器 = 每个"往小镇送货"的交付站所在区域的 `MultiInventoryWgoDatas`
        /// —— 也就是搬运工装货的来源、同时是本 mod "凑齐整批"判据数的同一批容器。
        /// 原版 `conveyor_storage_t1` = 2 格、`t2` = 4 格（`wgoDefs.tsv` 实测），而订单要 5~30 箱同时备齐，
        /// 于是判据永远不成立 ⇒ 开关永不打开 ⇒ 补给仓被撑满、上游停（N 网反馈的现象）。
        ///
        /// 比例规则（以原版 4 格为基准）：target = max(原版, min(该值, ceil(原版 × 该值 / 4)))。
        ///   ⇒ 填 40 时：t2（4 格）→ 40 ✓、t1（2 格）→ 20 ✓（升级到 t2 正好翻倍）。
        ///   ⇒ 原版比目标大的（999 格箱子等）保持不动 ✓ **绝不缩小** ✓。
        /// 关闭时还原成定义表里的原版格数，同样只在容器里现有的东西放得下时才缩（不丢货）。
        /// </summary>
        private static void EnsureLoadingShelfSize(WorldData world, GameBalance balance)
        {
            if (world == null || balance == null) return;

            int size = Mathf.Max(0, LoadingShelfSize.Value);

            List<PorterStationDef> defs = balance.porterStationDefs;
            if (defs == null) return;

            Dictionary<string, int> resized = new Dictionary<string, int>(StringComparer.Ordinal);   // "id|目标" → 个数
            Dictionary<string, int> restoredMap = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int d = 0; d < defs.Count; d++)
            {
                PorterStationDef def = defs[d];
                if (def == null || string.IsNullOrEmpty(def.id) || def.items == null) continue;
                if (!IsTownDeliveryStation(world, def)) continue;

                List<WgoData> stations = world.GetWgoDataList(def.id);
                if (stations == null) continue;

                for (int s = 0; s < stations.Count; s++)
                {
                    WgoData station = stations[s];
                    if (station == null) continue;

                    WorldZoneData zone = station.WorldZoneData;
                    List<WgoData> containers = zone != null ? zone.MultiInventoryWgoDatas : null;
                    if (containers == null) continue;

                    for (int c = 0; c < containers.Count; c++)
                    {
                        WgoData wgo = containers[c];
                        if (wgo == null || wgo.Inventory == null || wgo.Inventory.Data == null) continue;
                        if (wgo.Definition == null) continue;

                        int vanilla = wgo.Definition.inventorySize;
                        int now = wgo.Inventory.Data.InventorySize;
                        string id = !string.IsNullOrEmpty(wgo.Definition.id) ? wgo.Definition.id : "?";

                        if (size <= 0)                                        // 关闭：还原成原版
                        {
                            if (now <= vanilla) continue;
                            if (wgo.Inventory.Data.InventoryFillSize > vanilla) continue;   // 放不下就先不缩
                            wgo.Inventory.Data.InventorySize = vanilla;
                            CountUp(restoredMap, id + "|" + vanilla);
                            continue;
                        }

                        int scaled = Mathf.CeilToInt(vanilla * (float)size / 4f);
                        int target = Mathf.Max(vanilla, Mathf.Min(size, scaled));
                        if (now >= target) continue;                          // 只扩不缩

                        wgo.Inventory.Data.InventorySize = target;
                        CountUp(resized, id + "|" + target);
                    }
                }
            }

            // 汇报：按"容器 id + 目标格数"归并计数 —— 同类货架有 8 个就写 t2 ×8 → 40，
            // （之前用 HashSet 去重，8 个同类货架只会显示 1 条，看着像只改了一个）。
            Dictionary<string, int> map = size > 0 ? resized : restoredMap;
            List<string> keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);

            string signature = size + "|" + keys.Count + "|" + string.Join(",", keys.ToArray());
            if (signature == lastShelfSizeSignature) return;
            lastShelfSizeSignature = signature;
            if (keys.Count == 0) return;

            int total = 0;
            List<string> lines = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                string[] parts = keys[i].Split('|');
                int count = map[keys[i]];
                total += count;
                lines.Add(parts[0] + " ×" + count + " → " + parts[1]);
            }

            if (size > 0)
            {
                Instance.Logger.LogInfo(
                    $"[shelf] 装货货架已扩容（LoadingShelfSize = {size}，共 {total} 个）：" +
                    $"{string.Join("、", lines.ToArray())} / loading shelves enlarged");
            }
            else
            {
                Instance.Logger.LogInfo(
                    $"[shelf] 装货货架已还原为原版格数（共 {total} 个）：{string.Join("、", lines.ToArray())}" +
                    $" / loading shelves restored to the data-defined sizes");
            }
        }

        /// <summary>把"某个 key"的计数 +1（日志归并用）。</summary>
        private static void CountUp(Dictionary<string, int> map, string key)
        {
            int count;
            map.TryGetValue(key, out count);
            map[key] = count + 1;
        }

        private static void SubtractStockOnTownPalettes(WorldData world, Dictionary<string, int> needed)
        {
            WorldZoneData warehouse = world.GetWorldZoneDataById("warehouse");
            WorldZoneData cellar = world.GetWorldZoneDataById("warehouse_cellar");

            List<string> keys = new List<string>(needed.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                string itemId = keys[i];

                int onPalettes = 0;
                if (warehouse != null) onPalettes += warehouse.CountItemsOnTownPalettes(itemId);
                if (cellar != null) onPalettes += cellar.CountItemsOnTownPalettes(itemId);

                int gap = needed[itemId] - onPalettes;
                needed[itemId] = gap > 0 ? gap : 0;
            }
        }

        /// <summary>
        /// 重绘订单相关界面（如果正开着）—— 解决"交完一单必须关窗重开才能再接"：
        ///   · 订单窗口 `UIVendorOrdersWindow` —— 槽位列表（刚交完的槽位立刻变回可点）
        ///   · 选单窗口 `UIVendorOrdersSelectionWindow` —— 供应商订单列表（刚交完那条立刻不再是灰的）
        /// 顺带把槽位换行布局应用一次。
        /// </summary>
        private static void RefreshOrdersUi()
        {
            try
            {
                UIVendorOrdersWindow orders = LazyUI.GetWindow<UIVendorOrdersWindow>();
                if (orders != null && orders.IsShown) orders.Redraw();

                UIVendorOrdersSelectionWindow selection = LazyUI.GetWindow<UIVendorOrdersSelectionWindow>();
                if (selection != null && selection.IsShown) selection.Redraw();
            }
            catch (Exception e)
            {
                LogErrorThrottled(e);
            }

            MaintainOrdersWindowLayout("refresh");
        }

        /// <summary>
        /// 槽位换行：把订单窗口的槽位容器换成 `GridLayoutGroup`（每行 `OrderSlotsPerRow` 个）。
        ///
        /// 只在 `OrderSlots > 2` 且窗口正开着时动手 —— 原版 2 格的外观完全不碰。
        /// 容器怎么找：槽位控件都是 `Instantiate(prefab, prefab.transform.parent)`（源码 209018），
        /// 所以随便取一个已画出的槽位控件、它的父物体就是容器 ✓。
        ///
        /// 格子尺寸沿用槽位控件**当前的实际大小**、间距与对齐沿用原布局组，所以外观不会变形；
        /// 原布局组会被 `enabled = false`（两个布局组同时生效会互相打架）。
        /// </summary>
        /// <summary>
        /// 订单窗口布局诊断（每个会话一次，`VerboseLogging = true` 时输出）：
        /// 把"槽位容器"往上的父链逐层打出来 —— 每层的实际尺寸 / sizeDelta / 锚点 / 轴心，
        /// 以及该节点上挂了哪些布局组件（LayoutGroup 会标 on/off）。用来判断这个界面到底靠什么排版：
        /// 布局组？ContentSizeFitter？还是代码写死位置。有了它才能对症下药地做换行。
        /// </summary>
        private static void DumpOrdersWindowLayout(RectTransform container, RectTransform sample)
        {
            if (dumpedOrdersWindowLayout) return;
            dumpedOrdersWindowLayout = true;

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("订单窗口布局诊断 / orders window layout dump:");

                Transform t = container;
                for (int depth = 0; t != null && depth < 7; depth++, t = t.parent)
                {
                    RectTransform rt = t as RectTransform;
                    sb.Append('\n').Append("  [").Append(depth).Append("] ").Append(t.name);
                    if (rt != null)
                    {
                        sb.Append(" rect=").Append(rt.rect.size.x.ToString("0")).Append('x').Append(rt.rect.size.y.ToString("0"));
                        sb.Append(" sizeDelta=").Append(rt.sizeDelta.x.ToString("0")).Append('x').Append(rt.sizeDelta.y.ToString("0"));
                        sb.Append(" anchor=")
                          .Append(rt.anchorMin.x.ToString("0.##")).Append(',').Append(rt.anchorMin.y.ToString("0.##"))
                          .Append("..")
                          .Append(rt.anchorMax.x.ToString("0.##")).Append(',').Append(rt.anchorMax.y.ToString("0.##"));
                        sb.Append(" pivot=").Append(rt.pivot.x.ToString("0.##")).Append(',').Append(rt.pivot.y.ToString("0.##"));
                    }
                    sb.Append(" comps=[").Append(DescribeLayoutComponents(t.gameObject)).Append(']');
                }

                sb.Append('\n').Append("  容器子物体数 = ").Append(container.childCount);

                if (sample != null)
                {
                    sb.Append('\n').Append("  槽位样本 ").Append(sample.name)
                      .Append(" rect=").Append(sample.rect.size.x.ToString("0")).Append('x').Append(sample.rect.size.y.ToString("0"))
                      .Append(" sizeDelta=").Append(sample.sizeDelta.x.ToString("0")).Append('x').Append(sample.sizeDelta.y.ToString("0"))
                      .Append(" anchoredPos=").Append(sample.anchoredPosition.x.ToString("0")).Append(',').Append(sample.anchoredPosition.y.ToString("0"))
                      .Append(" comps=[").Append(DescribeLayoutComponents(sample.gameObject)).Append(']');
                }

                Instance.Logger.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                LogErrorThrottled(e);
            }
        }

        /// <summary>
        /// 调用游戏自己的窗口自适应 `RectTransform.RefreshContentFitter()`（扩展方法，在 LazyBearTechnology.dll）。
        /// 运行期按签名在所有已加载程序集里找一次并缓存；找不到就退回 `LayoutRebuilder`（见调用处）。
        /// </summary>
        private static bool RefreshWindowFitter(RectTransform rect)
        {
            if (rect == null) return false;

            if (!refreshFitterSearched)
            {
                refreshFitterSearched = true;
                try
                {
                    Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                    for (int a = 0; a < asms.Length && refreshFitterMethod == null; a++)
                    {
                        Type[] types;
                        try { types = asms[a].GetTypes(); }
                        catch { continue; }

                        for (int t = 0; t < types.Length; t++)
                        {
                            Type ty = types[t];
                            if (ty == null || !ty.IsAbstract || !ty.IsSealed) continue;

                            MethodInfo m = ty.GetMethod(
                                "RefreshContentFitter",
                                BindingFlags.Public | BindingFlags.Static,
                                null,
                                new[] { typeof(RectTransform) },
                                null);

                            if (m != null)
                            {
                                refreshFitterMethod = m;
                                break;
                            }
                        }
                    }

                    Instance.Logger.LogInfo(refreshFitterMethod != null
                        ? $"已找到游戏窗口自适应 / RefreshContentFitter found in {refreshFitterMethod.DeclaringType.FullName}"
                        : "未找到 RefreshContentFitter —— 退回 LayoutRebuilder / not found, falling back");
                }
                catch (Exception e)
                {
                    LogErrorThrottled(e);
                }
            }

            if (refreshFitterMethod == null) return false;

            try
            {
                refreshFitterMethod.Invoke(null, new object[] { rect });
                return true;
            }
            catch (Exception e)
            {
                LogErrorThrottled(e);
                return false;
            }
        }

        /// <summary>列出某个物体上"值得关注"的组件（布局组 / 自适应 / 滚动 / 布局元素），布局组标注开关。</summary>
        private static string DescribeLayoutComponents(GameObject go)
        {
            Component[] comps = go.GetComponents<Component>();
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;

                string n = c.GetType().Name;
                if (n == "RectTransform" || n == "Transform" || n == "CanvasRenderer" ||
                    n == "Image" || n == "TextMeshProUGUI" || n == "TextMeshPro" ||
                    n == "LazyButton" || n == "UIItemCell")
                {
                    continue;
                }

                if (sb.Length > 0) sb.Append(',');

                LayoutGroup lg = c as LayoutGroup;
                if (lg != null)
                {
                    sb.Append(n).Append(lg.enabled ? "(on" : "(off");
                    HorizontalOrVerticalLayoutGroup linear = lg as HorizontalOrVerticalLayoutGroup;
                    if (linear != null)
                    {
                        sb.Append(",spacing=").Append(linear.spacing.ToString("0"))
                          .Append(",controlW=").Append(linear.childControlWidth ? '1' : '0')
                          .Append(",controlH=").Append(linear.childControlHeight ? '1' : '0')
                          .Append(",expandW=").Append(linear.childForceExpandWidth ? '1' : '0')
                          .Append(",expandH=").Append(linear.childForceExpandHeight ? '1' : '0');
                    }
                    sb.Append(')');
                }
                else
                {
                    sb.Append(n);
                }

                if (sb.Length > 220)
                {
                    sb.Append(",…");
                    break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 一行最多放几个槽位（`OrderSlotsPerRow = 0` 时用）：按**画布的可用宽度**算，最多 6 个一排。
        ///
        /// 为什么用画布宽度而不是窗口宽度：订单窗口在 `Redraw` 里会按内容尺寸自适应
        /// （`RefreshContentFitter`，源码 209048），也就是说窗口宽度 = 我们刚写进去的容器宽度，
        /// 拿它当基准会自我循环（越算越宽）。画布宽度才是"一排放不下就会跑出屏幕"的那条真实边界。
        /// 用 90% 留一点边距；量不到就退回最多 4 个一排，宁可保守。
        /// </summary>
        private static int AutoColumns(UIVendorOrdersWindow window, Vector2 cell, float spacing, int cellCount)
        {
            float available = 0f;
            RectTransform root = window != null ? window.transform.root as RectTransform : null;
            if (root != null) available = root.rect.width * 0.9f;
            if (available < 1f && root != null) available = root.sizeDelta.x * 0.9f;
            if (available < 1f) return Mathf.Clamp(cellCount, 2, 4);

            float step = Mathf.Max(1f, cell.x + spacing);
            int fit = Mathf.FloorToInt((available + spacing) / step);
            return Mathf.Clamp(fit, 2, Mathf.Min(6, Mathf.Max(2, cellCount)));
        }

        private static void MaintainOrdersWindowLayout(string source)
        {
            if (VendorOrdersWidgetsField == null) return;

            UIVendorOrdersWindow window = LazyUI.GetWindow<UIVendorOrdersWindow>();
            if (window == null || !window.IsShown) return;

            ApplySlotLayout(window, false, source);
        }

        /// <summary>
        /// 订单窗口的槽位换行 —— **自己算坐标摆位**，完全不依赖 Unity 的布局组件。
        ///
        /// 为什么不用 GridLayoutGroup（试过，两版都失败）：
        ///   诊断显示槽位容器 `CurrentOrders` 上挂着 `HorizontalLayoutGroup` + `ContentSizeFitter`。
        ///   想换成 GridLayoutGroup 时，`AddComponent` 在运行期直接抛空引用（mod 里加不了这个组件），
        ///   于是只剩"原布局组被停用"这一步生效 ⇒ `ContentSizeFitter` 量不到任何布局、把容器尺寸算成 0
        ///   ⇒ 整排槽位贴着那个 0 尺寸点排开 —— 表现就是"第一格在窗口中心、最后一格出界"。
        ///
        /// 现在的做法：
        ///   1. 停掉容器的布局组 + ContentSizeFitter（一个会盖掉我们的坐标，一个会量不到布局而缩成 0）；
        ///   2. **容器尺寸由我们写死**（= 内容尺寸）；
        ///   3. 逐个设置槽位的 anchor/pivot/anchoredPosition —— 每行 OrderSlotsPerRow 个，多出来的换行。
        /// 这样没有任何组件会再动这些槽位，布局稳定、可预测。
        ///
        /// 每次窗口重绘后都会重排（Harmony Postfix 挂在 `UIVendorOrdersWindow.Redraw`，源码 208984），
        /// 所以槽位增减、反复开关窗口都不会错位。
        /// </summary>
        private static void ApplySlotLayout(UIVendorOrdersWindow window, bool deferAgain, string source)
        {
            if (VendorOrdersWidgetsField == null || window == null) return;

            // 重入保护：下面会调用游戏自己的自适应，万一它内部又触发一次 Redraw（进到本方法的 Postfix），
            // 没有这道闸门就会递归。加一层标记，最外层的调用说了算，内层的直接跳过。
            if (insideSlotLayout) return;
            insideSlotLayout = true;

            try
            {
                ApplySlotLayoutCore(window, deferAgain, source);
            }
            finally
            {
                insideSlotLayout = false;
            }
        }

        private static void ApplySlotLayoutCore(UIVendorOrdersWindow window, bool deferAgain, string source)
        {

            List<UIVendorOrderWidget> widgets =
                VendorOrdersWidgetsField.GetValue(window) as List<UIVendorOrderWidget>;
            if (widgets == null || widgets.Count == 0) return;

            // 只排"正在显示"的槽位：游戏把多余槽位 SetActive(false)（源码 208991 / 208022），
            // 它们和那个隐藏的 prefab 模板一样不参与排版。
            List<RectTransform> cells = new List<RectTransform>();
            for (int i = 0; i < widgets.Count; i++)
            {
                UIVendorOrderWidget w = widgets[i];
                if (w == null || !w.gameObject.activeSelf) continue;
                RectTransform rt = w.transform as RectTransform;
                if (rt != null) cells.Add(rt);
            }
            if (cells.Count == 0) return;

            RectTransform container = cells[0].parent as RectTransform;
            if (container == null) return;

            if (VerboseLog.Value) DumpOrdersWindowLayout(container, cells[0]);        // 每个会话一次

            // OrderSlotsPerRow：0 = 自动（按画布宽度算列数，默认）、1 = 不接管排版、2 以上 = 固定列数
            int perRowSetting = Mathf.Max(0, OrderSlotsPerRow.Value);
            if (perRowSetting == 1) return;                           // 1 = 不换行（不接管排版）
            // 判断依据用"实际显示出来的槽位数"，而不是配置值：
            // 槽位是只增不减的（存档里可能还留着 12 个），配置降回 2 也不该让这些槽位挤成一行。
            if (cells.Count <= 2) return;                             // 原版 2 格：外观不动

            // ★ 格子尺寸**只量一次然后固定住**，之后永远不再测量：
            //   上一版每轮都重新读 cells[0].rect.size，只要有任何东西把格子改大一点点（父级布局、
            //   自适应、游戏自身重绘…），下一轮就会以"更大的格子"重新算一遍容器与行距 —— 每开一次
            //   窗口就累加一次，最后整块跳出屏幕。固定住尺寸 + 下面的绝对坐标写入 ⇒ 物理上不可能累加。
            if (cellSizeOwner != container)
            {
                Vector2 measured = cells[0].rect.size;
                if (measured.x < 1f || measured.y < 1f) measured = cells[0].sizeDelta;
                if (measured.x < 1f || measured.y < 1f) return;        // 尺寸还没算出来，下一轮再来

                cellSizeOwner = container;
                cachedCellSize = measured;
            }
            Vector2 cell = cachedCellSize;

            // 间距沿用原布局组（诊断里 spacing=2），拿不到就用 2
            float spacing = 2f;
            LayoutGroup old = container.GetComponent<LayoutGroup>();
            if (old != null)
            {
                HorizontalOrVerticalLayoutGroup linear = old as HorizontalOrVerticalLayoutGroup;
                if (linear != null && linear.spacing > 0f) spacing = linear.spacing;

                if (old.enabled) old.enabled = false;                  // 让位：它会把我们摆好的坐标盖掉
            }

            // ★ 容器上的 ContentSizeFitter 也必须停掉：没有布局组时它量不出首选尺寸，会把容器压成 0
            //   —— 这正是前面"整排歪到一边"的原因。
            ContentSizeFitter fitter = container.GetComponent<ContentSizeFitter>();
            if (fitter != null && fitter.enabled) fitter.enabled = false;

            // 0 = 自动（按画布宽度算）；2 至 6 = 固定列数，上限也按实际槽位数
            int columns = perRowSetting == 0
                ? AutoColumns(window, cell, spacing, cells.Count)
                : Mathf.Clamp(perRowSetting, 2, cells.Count);
            int rows = (cells.Count + columns - 1) / columns;

            Vector2 containerSize = new Vector2(
                columns * cell.x + (columns - 1) * spacing,
                rows * cell.y + (rows - 1) * spacing);

            // 布局写入封装成一段、**跑两遍**用（见下）：
            //   容器锚点/pivot 写死成"左上角"（Unity 的 SetChildAlongAxis 就是按 pivot 摆位的，
            //   点锚点 + pivot(0,1) 才能让容器左上角落在父级左上角）；
            //   容器上**所有**布局组与 ContentSizeFitter 一律停掉（一个会盖掉我们的坐标，
            //   一个在没有布局组时量不出首选尺寸、会把容器压成 0）。
            Action writeGeometry = () =>
            {
                LayoutGroup[] groups = container.GetComponents<LayoutGroup>();
                for (int g = 0; g < groups.Length; g++)
                {
                    if (groups[g] != null && groups[g].enabled) groups[g].enabled = false;
                }

                ContentSizeFitter f = container.GetComponent<ContentSizeFitter>();
                if (f != null && f.enabled) f.enabled = false;

                container.anchorMin = new Vector2(0f, 1f);
                container.anchorMax = new Vector2(0f, 1f);
                container.pivot = new Vector2(0f, 1f);
                container.sizeDelta = containerSize;                   // 点锚点 ⇒ sizeDelta 就是绝对尺寸

                for (int i = 0; i < cells.Count; i++)
                {
                    int col = i % columns;
                    int row = i / columns;

                    cells[i].anchorMin = new Vector2(0f, 1f);          // 以容器左上角为基准
                    cells[i].anchorMax = new Vector2(0f, 1f);
                    cells[i].pivot = new Vector2(0.5f, 0.5f);
                    cells[i].sizeDelta = cell;                         // 尺寸也写死，防止别的东西改它
                    cells[i].anchoredPosition = new Vector2(
                        cell.x * 0.5f + col * (cell.x + spacing),
                        -(cell.y * 0.5f + row * (cell.y + spacing)));
                }
            };

            writeGeometry();                                           // ① 先按我们的排布摆好

            // ★★ 再调**游戏自己的窗口自适应**：游戏在 Redraw 里也调它（源码 209048），但那次在我们改尺寸
            //   之前 —— 窗口框/遮罩还按旧内容算，第一次打开就会被裁得"排版错位"。
            //   ⚠ 但它会（顺手）把布局组重新打开 —— 游戏里它俩是配套的
            //   （EnableLayoutGroupsAndRefreshContentFitter / RefreshContentFitterAndDisable），
            //   所以调用之后必须**再摆一遍**，否则整排会被布局组拉回"一行"。
            RectTransform windowRect = window.transform as RectTransform;
            if (!RefreshWindowFitter(windowRect) && windowRect != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(windowRect);
            }

            writeGeometry();                                           // ② ★ 最后一步必须是我们说了算

            // 父级链重建一次（Content 的 VerticalLayoutGroup + ContentSizeFitter 需要重跑）——
            // 只影响容器在父级里的位置/尺寸，不会动我们摆好的槽位（容器内部已无布局控制器）。
            LayoutRebuilder.ForceRebuildLayoutImmediate(container);

            // 再约一次"下一帧补排版"：Unity 把 ContentSizeFitter 与 LayoutGroup 叠在同一节点上时，
            // 一次重建往往不够（要第二遍才稳定），引擎自己的布局又是帧末才跑 —— 下一帧再补一遍兜底。
            if (deferAgain) pendingLayoutPass = true;

            // 日志：每次应用都打一行（带来源与次数，便于看出"第一次打开"和后续有什么不同）；
            // 几何发生变化时再多打一行明细。
            layoutPassCount++;

            // 签名只用**稳定量**（容器尺寸 / 槽位数 / 行列 / 首末格坐标）：
            // rect 与窗口尺寸每帧都会因父级重算而微动，放进签名会让"只在变化时打"失效、日志翻倍。
            string signature = string.Concat(
                containerSize.x.ToString("0"), "x", containerSize.y.ToString("0"),
                "|cells=", cells.Count, "|cols=", columns,
                "|first=", cells[0].anchoredPosition.x.ToString("0"), ",", cells[0].anchoredPosition.y.ToString("0"),
                "|last=", cells[cells.Count - 1].anchoredPosition.x.ToString("0"), ",", cells[cells.Count - 1].anchoredPosition.y.ToString("0"));

            if (VerboseLog.Value)
            {
                bool changed = signature != lastLayoutSignature;

                // ★ 只在几何真的变化时打：这段窗口开着时每轮（默认 2 秒）都会跑，每轮一条会把日志刷爆
                if (changed)
                {
                    lastLayoutSignature = signature;

                    Instance.Logger.LogInfo(
                        $"订单槽位排版 [{source} #{layoutPassCount}] " +
                        $"每行 {columns} 个 · {cells.Count} 槽位 → {rows} 行 · " +
                        $"容器 {container.name} {containerSize.x:0}×{containerSize.y:0} " +
                        $"pos=({container.anchoredPosition.x:0},{container.anchoredPosition.y:0}) · " +
                        $"rect={container.rect.size.x:0}×{container.rect.size.y:0} · " +
                        $"窗口 {(windowRect != null ? windowRect.rect.size.x.ToString("0") + "×" + windowRect.rect.size.y.ToString("0") : "?")}");

                    StringBuilder after = new StringBuilder();
                    for (int i = 0; i < cells.Count && i < 14; i++)
                    {
                        after.Append(" #").Append(i)
                             .Append("=(").Append(cells[i].anchoredPosition.x.ToString("0"))
                             .Append(',').Append(cells[i].anchoredPosition.y.ToString("0"))
                             .Append('/').Append(cells[i].rect.size.x.ToString("0"))
                             .Append('x').Append(cells[i].rect.size.y.ToString("0")).Append(')');
                    }

                    Instance.Logger.LogInfo(
                        $"订单槽位明细：格子 {cell.x:0}×{cell.y:0}，容器 sizeDelta=" +
                        $"{container.sizeDelta.x:0}×{container.sizeDelta.y:0} rect={container.rect.size.x:0}×{container.rect.size.y:0}" +
                        $" 位置/实测尺寸:{after}");
                }
            }

            laidOutContainer = container;
            laidOutColumns = columns;

            // 保险：容器上还可能有别的布局控制器（父级链上的布局组只管"容器自己在父级里的位置"，
            // 不会动容器内部；这里只把容器内部的布局控制器彻底清干净，确保没人再动这些槽位）。
            LayoutGroup[] groups = container.GetComponents<LayoutGroup>();
            for (int i = 0; i < groups.Length; i++)
            {
                if (groups[i] != null && groups[i].enabled) groups[i].enabled = false;
            }
        }

        private static bool loggedChalkBoardForced;

        /// <summary>
        /// ★「vendor 天天可接订单」的**第一道闸门**：游戏的"粉笔板"开关 `chalk_board_enabled`。
        ///
        /// 原版用两条**按天触发**的 GameLogic 卡死接单时机（`gameLogicsDefs.tsv`）：
        ///     enable_chalk_board   → day_pride 当天 1%  时 SetPPar("chalk_board_enabled", 1)
        ///     disable_chalk_board  → day_pride 当天 99% 时 SetPPar("chalk_board_enabled", 0)
        /// 而接单判定（源码 208885）要求 `chalk_board_enabled > 0`
        ///   ⇒ 原版实际效果就是【只有 day_pride 那一天能接单】。
        ///
        /// 这里按游戏自己的写法把它一直保持为 1（游戏自己就是这么设的，源码 248618：
        /// `MainGame.PlayerData.SetRes("chalk_board_enabled", 1f)`）—— 于是那道日期限制被直接解除，
        /// 哪天都能接单。值已经是 1 就不写（避免每轮无效写入）。
        /// </summary>
        private static void KeepChalkBoardEnabled()
        {
            if (!OrdersEveryDay.Value) return;

            try
            {
                if (MainGame.PlayerData == null) return;
                if (MainGame.PlayerData.GetResInt("chalk_board_enabled") > 0) return;   // 开着就不写

                MainGame.PlayerData.SetRes("chalk_board_enabled", 1f);

                if (!loggedChalkBoardForced)
                {
                    loggedChalkBoardForced = true;
                    Instance.Logger.LogInfo(
                        "[orders] 已解除\"只有 day_pride 那天能接单\"的限制：粉笔板保持开启（OrdersEveryDay）" +
                        " / chalk board kept enabled - orders can be accepted any day");
                }
            }
            catch (Exception e)
            {
                LogErrorThrottled(e);
            }
        }

        /// <summary>
        /// 订单系统本身的两件事（与交付站开关无关）：
        ///   1) 每周限制 —— 把还带着"本周已完成"标记的订单清零。正常情况下 `Patch_NoWeeklyLock`
        ///      已经在结算那一刻清掉了，这里是兜底：mod 加载之前就打上标记的订单、或者补丁因为
        ///      游戏更新失效时，照样能解除限制（最多晚 `UpdateIntervalSeconds` 一拍）。
        ///   2) 槽位 —— 把 `currentOrders` 补到配置的数量。只增不减，绝不动已经挂着的订单。
        ///
        /// `RepeatNonRenewableOrders = true` 时再多做一件事：把"一次性"订单（isRenewable = 0）
        /// 在**奖励领完、槽位也空出来之后**重置回可接状态。
        /// 注意必须先确认它不在槽位上 —— 槽里还挂着就重置状态，会把"领取奖励"按钮一起吞掉。
        /// </summary>
        private static void MaintainOrderSystem(VendorSystem vendorSystem)
        {
            bool resetWeekly = ResetWeeklyOrderLock.Value;
            bool repeatOnce = RepeatNonRenewableOrders.Value;
            bool everyDay = OrdersEveryDay.Value;      // vendor 天天可接订单：把"本周已完成"标记直接清掉

            List<Vendor> vendors = vendorSystem.vendors;
            if (vendors != null && (resetWeekly || repeatOnce || everyDay))
            {
                int clearedWeekly = 0;
                int reopened = 0;

                for (int v = 0; v < vendors.Count; v++)
                {
                    Vendor vendor = vendors[v];
                    if (vendor == null || vendor.Orders == null) continue;

                    for (int o = 0; o < vendor.Orders.Count; o++)
                    {
                        VendorOrderData order = vendor.Orders[o];
                        if (order == null) continue;

                        if ((resetWeekly || everyDay) && order.IsFinishedThisWeek)
                        {
                            order.ResetFinishedThisWeek();
                            clearedWeekly++;
                        }

                        if (repeatOnce
                            && order.State == VendorOrderState.Finished
                            && order.Definition != null
                            && !order.Definition.isRenewable
                            && !IsOrderInSlots(vendorSystem, order))
                        {
                            order.State = VendorOrderState.Default;
                            order.Count = 0;
                            reopened++;
                        }
                    }
                }

                if (clearedWeekly > 0 || reopened > 0)
                {
                    Instance.Logger.LogInfo(
                        $"订单可接状态已刷新（ResetWeeklyOrderLock / OrdersEveryDay）：清除本周标记 {clearedWeekly} 个、重新开放一次性订单 {reopened} 个" +
                        $" / weekly flags cleared {clearedWeekly}, one-off orders reopened {reopened}");
                }
            }

            MaintainOrderSlots(vendorSystem);
        }

        /// <summary>
        /// 槽位数量维护：
        ///   `OrderSlotsEnabled = true`  → 目标 = `OrderSlots`（多于目标就加空槽，少于目标就收回）；
        ///   `OrderSlotsEnabled = false` → 目标 = **原版数量**（常量 `start_orders_count`，通常是 2）
        ///                                 —— 也就是"关掉就把多出来的槽收回"，这是 12→2 的回退路径。
        ///
        /// 收回槽位时，若那一格挂着订单，就把订单**取消**：清空槽位引用即可 ——
        ///   订单回到可再接状态（若它已交付未领奖，会明确记一行日志，不静默吞掉奖励）；
        ///   已经送到小镇托盘上的货不受影响，下次接单照样计入交付量。
        /// 这套做法与游戏自己"在槽位上换一单"的行为一致：界面重选订单就是直接覆盖 `currentOrders[i]`（源码 209072）。
        /// </summary>
        private static void MaintainOrderSlots(VendorSystem vendorSystem)
        {
            List<SGuid> slots = vendorSystem.currentOrders;
            if (slots == null) return;

            int target = OrderSlotsEnabled.Value
                ? Mathf.Clamp(OrderSlots.Value, 2, MaxOrderSlots)
                : VanillaOrderSlotCount();

            // 只加空槽，绝不碰已挂着的订单
            if (slots.Count < target)
            {
                int added = 0;
                while (slots.Count < target)
                {
                    slots.Add(SGuid.Empty);
                    added++;
                }

                Instance.Logger.LogInfo(
                    $"订单槽位已扩充 +{added}，现在 {slots.Count} 个 / order slots expanded to {slots.Count}");
                return;
            }

            if (slots.Count <= target) return;

            // 收回多余槽位：从末尾开始；遇到挂着订单的，先把订单取消掉
            int removed = 0;
            int cancelled = 0;
            while (slots.Count > target)
            {
                int last = slots.Count - 1;
                SGuid guid = slots[last];

                if (!guid.IsEmpty && CancelOrderInSlot(vendorSystem, guid.Guid)) cancelled++;

                slots.RemoveAt(last);
                removed++;
            }

            Instance.Logger.LogInfo(
                $"订单槽位已收回 {removed} 个（现在 {slots.Count} 个，目标 " +
                $"{(OrderSlotsEnabled.Value ? OrderSlots.Value.ToString() : "原版 " + target)}），" +
                $"其中取消订单 {cancelled} 个" +
                $" / {removed} slot(s) removed, {cancelled} order(s) cancelled (now {slots.Count})");
        }

        /// <summary>原版槽位数：读常量 `start_orders_count`（源码 97433 / 88520），读不到就退回 2。</summary>
        private static int VanillaOrderSlotCount()
        {
            try
            {
                ConstDef def = ConstDef.Get("start_orders_count");
                if (def != null && def.IntValue > 0) return def.IntValue;
            }
            catch (Exception e)
            {
                LogErrorThrottled(e);
            }
            return 2;
        }

        /// <summary>
        /// 取消挂在被收回槽位上的那个订单：槽位引用由调用方清掉，这里只负责让订单本身回到可再接状态。
        /// 已经结算成 `Finished`（货已交、奖励未领）的订单无法"恢复成未交付"，会明确记一行日志提示奖励作废，
        /// 而不是静默吞掉。
        /// </summary>
        private static bool CancelOrderInSlot(VendorSystem vendorSystem, System.Guid orderGuid)
        {
            List<Vendor> vendors = vendorSystem.vendors;
            if (vendors == null) return false;

            for (int v = 0; v < vendors.Count; v++)
            {
                Vendor vendor = vendors[v];
                if (vendor == null || vendor.Orders == null) continue;

                for (int o = 0; o < vendor.Orders.Count; o++)
                {
                    VendorOrderData order = vendor.Orders[o];
                    if (order == null || order.Guid.Guid != orderGuid) continue;

                    if (order.State == VendorOrderState.Finished)
                    {
                        Instance.Logger.LogInfo(
                            $"⚠ 收回槽位时取消了一个已交付但尚未领奖的订单 {order.id} —— 它的好感奖励将无法领取" +
                            $" / cancelled an already-delivered order {order.id}: its reward is forfeited");
                    }
                    else
                    {
                        order.State = VendorOrderState.Default;      // 回到可再接（已交付的进度保留）
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>该订单现在是否挂在某个槽位上（挂着的不能重置状态，否则领奖按钮会消失）。</summary>
        private static bool IsOrderInSlots(VendorSystem vendorSystem, VendorOrderData order)
        {
            List<SGuid> slots = vendorSystem.currentOrders;
            if (slots == null || order == null) return false;

            for (int i = 0; i < slots.Count; i++)
            {
                if (!slots[i].IsEmpty && slots[i].Guid == order.Guid.Guid) return true;
            }
            return false;
        }

        /// <summary>
        /// 把搬运工的背包换大（原版是硬编码 4 格，源码 119598 —— 这是"一趟只能搬 2 箱"的唯一原因）。
        ///
        /// 为什么不去改箱子尺寸（ItemSize）：<c>ItemSize.Big</c> 被约 40 处逻辑依赖 ——
        /// 掉落物用哪个 prefab、掉落物的物理碰撞、玩家是"举过头顶"还是"塞进背包"、
        /// 容器白名单判定、传送带相关搬运… 改它等于动半个物品系统。
        /// 而容量只在一处硬编码，改这里副作用面最小，效果完全相同（一趟能搬的箱数）。
        ///
        /// 只替换"比目标小"的背包，并把里面的货（含 fake_porter_slot_filler）原样搬过去，不会丢货。
        ///
        /// ★ 作用范围：**只限本 mod 管辖的交付站**（"往小镇托盘送货"的那台，默认就是补给仓库那台）
        ///   上挂着的搬运工 —— 矿场 / 采石场 / 葡萄园 / 伐木场等站的搬运工一概不碰。
        /// </summary>
        private static void EnsurePorterInventory(HashSet<System.Guid> managedStations)
        {
            if (PorterInventoryField == null) return;
            if (managedStations == null || managedStations.Count == 0) return;   // 没有管辖的站就什么都不做

            int target = Mathf.Clamp(PorterInventorySize.Value, 4, 200);
            if (target <= 4) return;                                   // 原版大小，不用管

            ZombieSystemData zombieSystem = MainGame.ZombieSystemData;
            if (zombieSystem == null) return;

            Dictionary<System.Guid, ZombieWgoData> cache = zombieSystem.Cache;
            if (cache == null || cache.Count == 0) return;

            List<ZombieWgoData> zombies = new List<ZombieWgoData>(cache.Values);   // 先拷出来，别边遍历边被改

            int replaced = 0;
            int seenPorters = 0;                                        // 管辖站上找到的搬运工数（用于日志核对）
            int alreadyBig = 0;
            for (int i = 0; i < zombies.Count; i++)
            {
                ZombieWgoData zombie = zombies[i];
                if (zombie == null) continue;

                // ★ 只管挂在本 mod 管辖的交付站上的搬运工 —— 矿场 / 采石场 / 葡萄园等站的
                //   搬运工一律不碰（用户要求：改动只限定在补给仓库这条线里）。
                WgoData attached = zombie.AttachedWgoData;
                if (attached == null || !managedStations.Contains(attached.UniqueId.Guid)) continue;

                Inventory current = PorterInventoryField.GetValue(zombie) as Inventory;
                if (current == null) continue;                          // 不是搬运工（只有搬运工有这个背包）

                seenPorters++;

                if (current.Data == null || current.Data.InventorySize >= target)
                {
                    alreadyBig++;
                    continue;
                }

                Inventory bigger = Inventory.Create(target);
                List<Item> carried = new List<Item>(current.Data.Inventory);
                for (int k = 0; k < carried.Count; k++)
                {
                    Item carriedItem = carried[k];
                    if (carriedItem == null || string.IsNullOrEmpty(carriedItem.id) || carriedItem.Count <= 0) continue;
                    bigger.AddItemToInventory(new Item(carriedItem.id, carriedItem.Count));
                }

                PorterInventoryField.SetValue(zombie, bigger);
                replaced++;
            }

            if (replaced > 0)
            {
                Instance.Logger.LogInfo(
                    $"已把补给仓库交付站上的 {replaced} 个搬运工背包扩到 {target} 格（一趟最多 {target / 2} 箱）/ " +
                    $"{replaced} delivery-station porter inventory(ies) resized to {target} slots " +
                    $"(up to {target / 2} boxes per trip).");
            }
            else if (VerboseLog.Value && seenPorters > 0 && !loggedBagSummary)
            {
                // 已经够大时不会有任何改动，这里给一行明细，方便核对"管辖站上的搬运工是不是都管到了"。
                // ★ 每次会话只打一行：这段每轮（默认 2 秒）都会跑，之前每轮都打会把日志刷爆。
                loggedBagSummary = true;
                Instance.Logger.LogInfo(
                    $"[bag] 管辖站上共找到 {seenPorters} 个搬运工（{alreadyBig} 个背包已是 {target} 格，无需替换）" +
                    $" / {seenPorters} porter(s) on managed stations, {alreadyBig} already at {target} slots" +
                    $"（注：只统计**当前场景**里的僵尸；没上场的搬运工上场后会被下一轮处理）");
            }
        }

        /// <summary>
        /// 库存：把「游戏自己认定的基础仓储容器」（`OpenInMultiInventory`，也就是搬运工取货的那个名单，
        /// 源码 121859 / 39718）里的对应物品加起来。
        ///
        /// 默认（StockScope = StationZone）只数**该交付站所在区域** —— 也就是补给仓库，
        /// 与搬运工实际装货的口径完全一致：它只搬本区池里的货。想让"凑齐整批"看整个基地
        /// （货可能堆在别的区）就把它改成 WholeBase。
        ///
        /// 小镇托盘（TownPalette）一律不算：那些是「已交付」，已经在缺口里扣过了。
        /// 结果按区域缓存，同一轮里多个站共用。
        /// </summary>
        private static Dictionary<string, int> StockFor(WorldZoneData stationZone,
            Dictionary<string, int> needed, Dictionary<WorldZoneData, Dictionary<string, int>> cache)
        {
            if (cache == null || needed.Count == 0 || stationZone == null)
            {
                return new Dictionary<string, int>(StringComparer.Ordinal);
            }

            Dictionary<string, int> stock;
            if (cache.TryGetValue(stationZone, out stock)) return stock;

            stock = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string itemId in needed.Keys)
            {
                stock[itemId] = 0;
            }

            if (string.Equals(StockScope.Value, "WholeBase", StringComparison.OrdinalIgnoreCase))
            {
                WorldData world = MainGame.WorldData;
                List<GameSceneData> scenes = world != null ? world.gameSceneDataList : null;
                if (scenes != null)
                {
                    for (int s = 0; s < scenes.Count; s++)
                    {
                        GameSceneData scene = scenes[s];
                        if (scene == null || scene.worldZones == null) continue;

                        for (int z = 0; z < scene.worldZones.Count; z++)
                        {
                            WorldZoneData zone = scene.worldZones[z];
                            if (zone == null) continue;
                            AddContainersToStock(zone.MultiInventoryWgoDatas, needed, stock);
                        }
                    }
                }
            }
            else
            {
                AddContainersToStock(stationZone.MultiInventoryWgoDatas, needed, stock);   // 只数这个区（补给仓库）
            }

            cache[stationZone] = stock;
            return stock;
        }

        /// <summary>日志里用哪种口径的库存（方便对着日志判断）。</summary>
        private static string StockLabel()
        {
            return string.Equals(StockScope.Value, "WholeBase", StringComparison.OrdinalIgnoreCase)
                ? "基地库存 stock(base)"
                : "本区库存 stock(zone)";
        }

        private static void AddContainersToStock(List<WgoData> containers,
            Dictionary<string, int> needed, Dictionary<string, int> stock)
        {
            if (containers == null) return;                 // 该区的容器名单还没建好，下一轮再来

            for (int c = 0; c < containers.Count; c++)
            {
                WgoData container = containers[c];
                if (container == null || container.Inventory == null) continue;
                if (IsTownPalette(container)) continue;        // 小镇托盘算「已交付」，不重复计

                foreach (string itemId in needed.Keys)
                {
                    if (needed[itemId] <= 0) continue;
                    int count = container.Inventory.Data.GetTotalCountInInventory(itemId);
                    if (count > 0) stock[itemId] += count;
                }
            }
        }

        /// <summary>订单目录里出现过的物品 id（结果缓存；平衡表没准备好时下一轮重试）。</summary>
        private static void BuildOrderableItemSet(GameBalance balance)
        {
            List<VendorOrderDef> defs = balance.vendorOrderDefs;
            if (defs == null || defs.Count == 0) return;

            for (int i = 0; i < defs.Count; i++)
            {
                VendorOrderDef def = defs[i];
                if (def != null && !string.IsNullOrEmpty(def.itemId)) OrderableItems.Add(def.itemId);
            }

            orderableItemsReady = true;
            Instance.Logger.LogInfo(
                $"订单目录收录 {OrderableItems.Count} 种物品，只有这些箱种的开关会被接管 / " +
                $"{OrderableItems.Count} orderable item id(s) registered; only these toggles are managed.");
        }

        /// <summary>该搬运站是不是「往小镇送货」的那一种（目的地里有 TownPalette 物件）。</summary>
        private static bool IsTownDeliveryStation(WorldData world, PorterStationDef def)
        {
            bool cached;
            if (TownStationCache.TryGetValue(def.id, out cached)) return cached;

            bool result = false;
            bool looked = false;             // 是否真的看到了可判断的数据（容器名单已建好）

            List<string> customTargets = def.customTargets;
            if (customTargets != null && customTargets.Count > 0)
            {
                for (int i = 0; i < customTargets.Count && !result; i++)
                {
                    WgoData wgo = world.GetWgoData(customTargets[i]);
                    if (wgo != null) looked = true;
                    result = IsTownPalette(wgo);
                }
            }

            if (!result && !string.IsNullOrEmpty(def.targetWorldZone))
            {
                WorldZoneData zone = world.GetWorldZoneDataById(def.targetWorldZone);
                if (zone != null && zone.wgoDataList != null && zone.wgoDataList.Count > 0)
                {
                    looked = true;
                    for (int i = 0; i < zone.wgoDataList.Count && !result; i++)
                    {
                        result = IsTownPalette(world.GetWgoData(zone.wgoDataList[i]));
                    }
                }
            }

            // ★ 只在"确实看到过数据"时缓存 —— 否则会把"世界还没建好"的 false 永久记住，
            //   之后这局的站点就全被当成非交付站、开关再也不被接管（2026-10-05 实测踩到过）。
            if (result || looked) TownStationCache[def.id] = result;
            return result;
        }

        private static bool IsTownPalette(WgoData wgo)
        {
            return wgo != null && wgo.Definition != null &&
                   wgo.Definition.interactionType == WGODef.InteractionType.TownPalette;
        }

        private static bool dumpedStationSources;

        /// <summary>
        /// 详细日志下打一次：每个"往小镇送货"的交付站，它的**取货容器**（补给仓库里那批货架）——
        /// 也就是 `WorldZoneData.MultiInventoryWgoDatas`（搬运工装货时遍历的那张名单，源码 121859 / 39718）。
        ///
        /// 为什么关键：本 mod 的"凑齐整批"判据数的就是这批容器（`StockScope = StationZone` 时走 `StockFor`）。
        /// 如果它们每个只有几格（玩家看到的"很多货架、每架 4 格"），那么"整批同时备齐"就受限于它们的
        /// **空闲格数** —— 多张订单一起排队时谁都不够格数 ⇒ 开关永不打开 ⇒ 补给仓被撑满、上游被顶停。
        /// 这一行把容器数 / 合计格数 / 已用 / 空闲量出来，正好对得上 N 网反馈里"每个托盘只能放 4 个"的说法。
        /// </summary>
        private static void DumpStationSources(WorldData world, GameBalance balance)
        {
            if (!VerboseLog.Value || dumpedStationSources || world == null || balance == null) return;

            List<PorterStationDef> defs = balance.porterStationDefs;
            if (defs == null) return;

            List<string> report = new List<string>();
            bool saw = false;

            for (int d = 0; d < defs.Count; d++)
            {
                PorterStationDef def = defs[d];
                if (def == null || string.IsNullOrEmpty(def.id) || def.items == null) continue;
                if (!IsTownDeliveryStation(world, def)) continue;

                List<WgoData> stations = world.GetWgoDataList(def.id);
                if (stations == null || stations.Count == 0) continue;

                for (int s = 0; s < stations.Count; s++)
                {
                    WgoData station = stations[s];
                    if (station == null) continue;

                    WorldZoneData zone = station.WorldZoneData;
                    List<WgoData> containers = zone != null ? zone.MultiInventoryWgoDatas : null;
                    if (containers == null) continue;

                    saw = true;

                    int slots = 0, used = 0, listed = 0;
                    List<string> lines = new List<string>();

                    for (int c = 0; c < containers.Count; c++)
                    {
                        WgoData wgo = containers[c];
                        if (wgo == null || wgo.Inventory == null || wgo.Inventory.Data == null) continue;

                        int size = wgo.Inventory.Data.InventorySize;
                        int fill = wgo.Inventory.Data.InventoryFillSize;
                        slots += size;
                        used += fill;

                        if (listed < 20)
                        {
                            lines.Add((wgo.Definition != null ? wgo.Definition.id : "?") + " " + fill + "/" + size);
                            listed++;
                        }
                    }

                    report.Add(def.id + "#" + station.UniqueId.Guid.ToString("N").Substring(0, 4) +
                               "@" + (zone != null ? zone.id : "?") + "：取货容器 " + containers.Count +
                               " 个、合计 " + slots + " 格（已用 " + used + "、空闲 " + Mathf.Max(0, slots - used) + "）" +
                               (containers.Count > listed ? "（只列前 " + listed + " 个）" : "") +
                               "：" + string.Join("、", lines.ToArray()));
                }
            }

            if (!saw) return;                        // 世界 / 站点还没建好，下一轮再来
            dumpedStationSources = true;

            for (int i = 0; i < report.Count; i++)
            {
                Instance.Logger.LogInfo("[stock] 交付站取货容器 / loading sources: " + report[i]);
            }
        }

        private static bool dumpedStationTargets;

        /// <summary>
        /// 详细日志下一次：每个"往小镇送货"的交付站，货到底会倒进哪些容器。
        ///
        /// 为什么关键：搬运工卸货走的是 `TryAddItemPreferringSameItem(multiInventory.inventoryList, item)`
        /// （源码 121689 / 121869）—— 它会在目标区 / `customTargets` 里**任意**有空位的容器中挑一个
        /// （只有"优先放已有同种物品的容器"这一条软偏好），而游戏结算只数
        /// warehouse + warehouse_cellar 里的 **TownPalette**（97488）。
        /// 两者一旦不是同一批容器，就会出现"货明明送出去了、订单却永远凑不齐"的假死锁 —— 正是要排查的情况。
        /// 输出里 `★托盘` 标记结算认的容器；只列托盘与**非空**容器，避免刷屏。
        /// </summary>
        private static void DumpStationTargets(WorldData world, GameBalance balance)
        {
            if (!VerboseLog.Value || dumpedStationTargets || world == null || balance == null) return;

            List<PorterStationDef> defs = balance.porterStationDefs;
            if (defs == null) return;

            List<string> report = new List<string>();
            bool sawStation = false;

            for (int d = 0; d < defs.Count; d++)
            {
                PorterStationDef def = defs[d];
                if (def == null || string.IsNullOrEmpty(def.id) || def.items == null) continue;
                if (!IsTownDeliveryStation(world, def)) continue;

                sawStation = true;

                List<string> containers = new List<string>();
                List<string> custom = def.customTargets;

                if (custom != null)
                {
                    for (int i = 0; i < custom.Count; i++)
                    {
                        AddContainerLine(containers, world.GetWgoData(custom[i]));
                    }
                }

                WorldZoneData zone = !string.IsNullOrEmpty(def.targetWorldZone)
                    ? world.GetWorldZoneDataById(def.targetWorldZone) : null;
                if (zone != null && zone.wgoDataList != null)
                {
                    for (int i = 0; i < zone.wgoDataList.Count; i++)
                    {
                        AddContainerLine(containers, world.GetWgoData(zone.wgoDataList[i]));
                    }
                }

                string where = (custom != null && custom.Count > 0 ? "customTargets" : "targetWorldZone") +
                               "=" + (!string.IsNullOrEmpty(def.targetWorldZone) ? def.targetWorldZone : "?");

                report.Add(def.id + "（" + where + "）：" +
                           (containers.Count > 0 ? string.Join("、", containers.ToArray()) : "（没有可列出的容器）"));
            }

            if (!sawStation) return;                 // 世界 / 站点还没建好，下一轮再来
            dumpedStationTargets = true;

            for (int i = 0; i < report.Count; i++)
            {
                Instance.Logger.LogInfo("[palette] 交付站收货容器 / delivery targets: " + report[i]);
            }
        }

        /// <summary>把容器描述加进列表：只收托盘与**非空**容器（其余没信息量，只会刷屏）。</summary>
        private static void AddContainerLine(List<string> lines, WgoData wgo)
        {
            if (wgo == null || wgo.Inventory == null || wgo.Inventory.Data == null) return;

            int size = wgo.Inventory.Data.InventorySize;
            int fill = wgo.Inventory.Data.InventoryFillSize;
            bool palette = IsTownPalette(wgo);
            if (!palette && fill <= 0) return;                        // 非托盘且空 → 跳过

            WorldZoneData zone = wgo.WorldZoneData;
            lines.Add((wgo.Definition != null ? wgo.Definition.id : "?") +
                      "@" + (zone != null ? zone.id : "?") + " " + fill + "/" + size +
                      (palette ? " ★托盘" : ""));
        }

        /// <summary>给详细日志用的一行摘要：每单还差多少、基地里备了多少。</summary>
        private static string DescribeNeeds(Dictionary<string, int> needed, Dictionary<string, int> stock)
        {
            if (needed.Count == 0) return "[orders] 当前没有已接订单的缺口 / no accepted order gaps";

            var sb = new System.Text.StringBuilder("[orders] 缺口/库存 need(have): ");
            bool first = true;
            foreach (KeyValuePair<string, int> pair in needed)
            {
                int have;
                stock.TryGetValue(pair.Key, out have);
                if (!first) sb.Append(", ");
                sb.Append(pair.Key).Append('=').Append(pair.Value)
                  .Append(have >= pair.Value ? "(够 have " : "(差 have ").Append(have).Append(')');
                first = false;
            }
            return sb.ToString();
        }

        private static void LogErrorThrottled(Exception e)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastErrorTime < 30f) return;
            lastErrorTime = now;

            if (Instance != null)
            {
                Instance.Logger.LogError(
                    $"OrderOptimizer 出错（30 秒内不再重复）/ error: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- 订单每周限制

        /// <summary>
        /// 解除「同一个订单每周只能交一次」：原版在你点"领取奖励"时给订单打上"本周已完成"标记
        /// （`OnGetOrderRewardPress` → `MarkFinishedThisWeek()`，源码 209081），之后这个订单在
        /// 订单列表里就是灰的、点不了，要等到 day_pride（每周固定的一天）才统一清零
        /// （`ResetOrdersFinishedThisWeek`，源码 97546 / 97554）。
        /// 这里把清零提前到"完成的那一刻"—— 也就是你要求的"每次完成订单之后把计数清零"。
        ///
        /// 为什么挂在这个方法上：它是那份标记**唯一**的写入点（全源码只有 209081 一处调用），
        /// 挂在这里就是"完成即清零"，既不用轮询也没有延迟。
        /// 万一以后游戏改了方法名，Tick 里还有一遍扫描兜底（见 `MaintainOrderSystem`）。
        /// </summary>
        [HarmonyPatch(typeof(VendorOrderData), "MarkFinishedThisWeek")]
        public static class Patch_NoWeeklyLock
        {
            private static void Postfix(VendorOrderData __instance)
            {
                try
                {
                    if (!ModEnabled.Value) return;
                    // 两个开关任一打开，都在"领奖那一刻"就把标记清掉：
                    //   ResetWeeklyOrderLock = 解禁每周限制（交完可再交）
                    //   OrdersEveryDay       = vendor 天天可接订单（交完当天可再接）
                    // 两者的闸门是同一个标记（源码 208885），所以共用这段清除逻辑。
                    if (!ResetWeeklyOrderLock.Value && !OrdersEveryDay.Value) return;
                    __instance.ResetFinishedThisWeek();

                    // 此刻界面里那份"订单列表"还是旧状态（刚画的、灰的），下一帧强制重绘一次，
                    // 于是"刚交完的单"当场就能再接 —— 不必关掉窗口再打开。
                    // 不在这里直接重绘：现在还在按钮的点击回调里，重绘会把正在处理的那个控件一起刷新掉。
                    pendingUiRefresh = true;
                }
                catch (Exception e)
                {
                    LogErrorThrottled(e);
                }
            }
        }

        // ---------------------------------------------------------------- 订单窗口槽位排版

        /// <summary>
        /// 订单窗口每次重绘后重排槽位：
        /// 重绘会把槽位按"显示/隐藏"重新算一遍（源码 208984-209046），所以在这里跟一手最稳 ——
        /// 玩家开关窗口、槽位增减、领奖后重绘（我们自己也调 Redraw）都会走到这里。
        /// </summary>
        [HarmonyPatch(typeof(UIVendorOrdersWindow), "Redraw")]
        public static class Patch_OrdersWindowLayout
        {
            private static void Postfix(UIVendorOrdersWindow __instance)
            {
                try
                {
                    if (!ModEnabled.Value) return;
                    ApplySlotLayout(__instance, true, "redraw");      // 下一帧再补一遍（等引擎的布局跑完）
                }
                catch (Exception e)
                {
                    LogErrorThrottled(e);
                }
            }
        }

        // ---------------------------------------------------------------- 装货上限

        /// <summary>
        /// 只装缺口量：给搬运工装货加一个"总量上限"。
        ///
        /// 为什么需要：原版装货的最小单元是 <c>PorterTryTakeItemFromInventory</c>，它会把能装的**全都**装上
        /// （一箱补给是 Big、占 2 格，背包 64 格 ⇒ 一次能装 32 箱）。所以"凑齐整批"放行之后，
        /// 若基地存货远多于订单缺口，就会多送几箱到小镇（多余的堆在托盘上，清仓时按单价折钱，不算亏，
        /// 但确实多搬了）。
        ///
        /// 做法：挂**同一个方法的头尾** ——
        ///   Prefix  —— 缺口已经够了就直接不装；否则记下这一趟上限 = 缺口 − 已装；
        ///   Postfix —— 让游戏按它自己的逻辑装完，我们只把**超出上限的部分退还原容器**。
        /// ⇒ 不复制任何游戏逻辑，只是给装货加了个"别超过订单缺口"的闸门。
        ///
        /// 覆盖范围：本 mod 管辖的交付站上的搬运工 ✓ —— 原版装货与跨区物流（Zombie Cross-Zone
        /// Logistics）的装货调用的都是这一个方法（它的 `PorterTake` 就是它），而订单箱全是 Big、
        /// 只走这一条路径（另一条 <c>PorterTryFillEmptySlotsFromInventory</c> 明确排除 Big）。
        /// 其它搬运站一路放行，什么都不做。
        /// </summary>
        [HarmonyPatch(typeof(ZombieWgoData), "PorterTryTakeItemFromInventory")]
        public static class Patch_PorterTakeCapped
        {
            private static bool budgetActive;
            private static string budgetItemId;

            /// <summary>这一趟"总共该带多少"（= 订单缺口）。注意不是"还能再装多少"：
            /// 那个数只用来决定要不要继续装；裁的时候必须按总量算，否则会把已经装好的也裁掉。</summary>
            private static int budgetTotalAllowed;

            private static bool Prefix(ZombieWgoData __instance, Inventory inventory, NeedItemData cur)
            {
                budgetActive = false;

                try
                {
                    if (!ModEnabled.Value || !RequireFullBatch.Value) return true;
                    if (cur == null || string.IsNullOrEmpty(cur.id)) return true;
                    if (!OrderableItems.Contains(cur.id)) return true;
                    if (!IsManagedPorter(__instance)) return true;        // 只管补给仓库这条线

                    int need = LiveNeedFor(cur.id);                       // 订单还差多少（实时算，不用缓存）
                    int carried = CarriedByManagedPorters(cur.id);        // 已经装在这些搬运工手上的
                    int canTakeMore = need - carried;
                    if (canTakeMore <= 0) return false;                   // 早就够了，这一趟别再装

                    // ★ 整批没齐也不装：堵住"手动把开关点开 → 零散发货"这条路。
                    // 装不上货 ⇒ 原版 121597 的「背包非空才发车」不成立 ⇒ 依旧不发车。
                    if (!BatchReady(__instance, cur.id, need, carried)) return false;

                    // ★ 托盘空间闸门（第三道）：这一单装不进托盘空位就先别装 —— 装了也是搬运工背着货
                    //   卸不下去（原版 121689：托盘放不进就留在背包里），还白占补给仓、把上游顶停。
                    if (!PaletteAllowed(cur.id)) return false;

                    budgetItemId = cur.id;
                    budgetTotalAllowed = need;                            // ★ 裁的时候按"总共该带"算
                    budgetActive = true;
                    return true;
                }
                catch
                {
                    budgetActive = false;
                    return true;
                }
            }

            private static void Postfix(ZombieWgoData __instance, Inventory inventory)
            {
                if (!budgetActive) return;
                budgetActive = false;

                try
                {
                    Inventory porter = PorterInventoryField.GetValue(__instance) as Inventory;
                    if (porter == null || porter.Data == null) return;

                    int have = porter.Data.GetTotalCountInInventory(budgetItemId);
                    int excess = have - budgetTotalAllowed;      // 按"总共该带多少"裁，不是按"还能再装多少"
                    if (excess <= 0) return;

                    List<Item> removed = porter.RemoveItemById(budgetItemId, excess);
                    if (removed == null || removed.Count == 0) return;

                    int returned = 0;
                    for (int i = 0; i < removed.Count; i++)
                    {
                        Item taken = removed[i];
                        if (taken == null || taken.Count <= 0) continue;

                        // 退回原容器；万一退不回去（容器满等），放回自己背包 —— 绝不丢货
                        if (inventory != null && inventory.AddItemToInventory(new Item(taken.id, taken.Count)))
                        {
                            returned += taken.Count;
                        }
                        else
                        {
                            porter.AddItemToInventory(new Item(taken.id, taken.Count));
                        }
                    }

                    RebalancePorterFillers(porter);

                    if (returned > 0 && VerboseLog.Value)
                    {
                        Instance.Logger.LogInfo(
                            $"[cap] 只装缺口量：{budgetItemId} 退回 {returned} 箱（本趟应带 {budgetTotalAllowed}，" +
                            $"退回后共 {porter.Data.GetTotalCountInInventory(budgetItemId)}）");
                    }
                }
                catch (Exception e)
                {
                    LogErrorThrottled(e);
                }
            }

            /// <summary>该搬运工是否挂在本 mod 管辖的交付站上。</summary>
            private static bool IsManagedPorter(ZombieWgoData zombie)
            {
                if (zombie == null || ManagedStationIds.Count == 0) return false;

                WgoData attached = zombie.AttachedWgoData;
                return attached != null && ManagedStationIds.Contains(attached.UniqueId.Guid);
            }

            /// <summary>管辖站上的搬运工一共已经装了多少个该物品（含正在路上的）。</summary>
            internal static int CarriedByManagedPorters(string itemId)
            {
                ZombieSystemData zombieSystem = MainGame.ZombieSystemData;
                if (zombieSystem == null) return 0;

                Dictionary<System.Guid, ZombieWgoData> cache = zombieSystem.Cache;
                if (cache == null || cache.Count == 0) return 0;

                List<ZombieWgoData> zombies = new List<ZombieWgoData>(cache.Values);
                int total = 0;
                for (int i = 0; i < zombies.Count; i++)
                {
                    ZombieWgoData zombie = zombies[i];
                    if (!IsManagedPorter(zombie)) continue;

                    Inventory carried = PorterInventoryField.GetValue(zombie) as Inventory;
                    if (carried == null || carried.Data == null) continue;
                    total += carried.Data.GetTotalCountInInventory(itemId);
                }
                return total;
            }

            /// <summary>
            /// ★ 「凑齐整批」的第二道闸门（下沉到装货这一层）。
            ///
            /// 为什么需要：开关那一层只能挡住 mod 自己写开关的情形 —— 玩家在站点界面手动把开关点开
            /// （界面写的就是同一个 GameRes，200002 行），原版就会照装照发，出现"零散发货"。
            /// 这里在装货前再判一次：**这批备齐了没**，没齐就不让装。
            /// 装不上货 ⇒ 原版 121597 的「porterInventory.Data.InventoryFillSize > 0 才 StartPath」
            /// 不成立 ⇒ 依旧不发车 ✓（订单箱全是 Big，只走这一条装货路径，见 121836 对 Big 的排除）。
            ///
            /// 判据与 Tick 里开开关的口径**完全一致**（同一个 StockScope）：
            ///     本区库存 + 已在途 ≥ 缺口
            /// 用「+ 在途」而不是「≥ 还差」，是为了让它在**装货过程中**保持恒定：每装走一箱，
            /// 库存 −1、在途 +1，两边同步移动，判据不会自己变假（否则同一趟里第二个容器会被误拦）。
            /// 已交付到小镇托盘的部分由 LiveNeedFor 从缺口里扣掉，两边口径同样是自洽的。
            /// </summary>
            private static bool BatchReady(ZombieWgoData zombie, string itemId, int need, int carried)
            {
                int available = StockCountForStationZone(zombie != null ? zombie.AttachedWgoData : null, itemId);
                if (available + carried >= need) return true;

                LogBatchBlocked(itemId, need, carried, available);
                return false;
            }

            /// <summary>该站口径下的可用库存（与 Tick 的 StockScope 一致；小镇托盘算「已交付」，不计）。</summary>
            private static int StockCountForStationZone(WgoData station, string itemId)
            {
                if (station == null) return 0;

                if (!string.Equals(StockScope.Value, "WholeBase", StringComparison.OrdinalIgnoreCase))
                {
                    WorldZoneData zone = station.WorldZoneData;
                    return zone != null ? CountInContainers(zone.MultiInventoryWgoDatas, itemId) : 0;
                }

                WorldData world = MainGame.WorldData;
                List<GameSceneData> scenes = world != null ? world.gameSceneDataList : null;
                if (scenes == null) return 0;

                int total = 0;
                for (int s = 0; s < scenes.Count; s++)
                {
                    GameSceneData scene = scenes[s];
                    if (scene == null || scene.worldZones == null) continue;

                    for (int z = 0; z < scene.worldZones.Count; z++)
                    {
                        WorldZoneData zone = scene.worldZones[z];
                        if (zone == null) continue;
                        total += CountInContainers(zone.MultiInventoryWgoDatas, itemId);
                    }
                }
                return total;
            }

            private static int CountInContainers(List<WgoData> containers, string itemId)
            {
                if (containers == null) return 0;

                int total = 0;
                for (int c = 0; c < containers.Count; c++)
                {
                    WgoData container = containers[c];
                    if (container == null || container.Inventory == null) continue;
                    if (IsTownPalette(container)) continue;        // 小镇托盘算「已交付」，不重复计
                    total += container.Inventory.Data.GetTotalCountInInventory(itemId);
                }
                return total;
            }

            private static float lastBatchBlockLogTime = -999f;
            private static string lastBatchBlockItem;

            /// <summary>装货层的拦下日志（限频：同一箱种 5 秒最多一行 —— 轮询是每帧级的）。</summary>
            private static void LogBatchBlocked(string itemId, int need, int carried, int available)
            {
                if (!VerboseLog.Value) return;

                float now = Time.realtimeSinceStartup;
                if (itemId == lastBatchBlockItem && now - lastBatchBlockLogTime < 5f) return;
                lastBatchBlockLogTime = now;
                lastBatchBlockItem = itemId;

                string where = string.Equals(StockScope.Value, "WholeBase", StringComparison.OrdinalIgnoreCase)
                    ? "基地库存"
                    : "本区库存";

                Instance.Logger.LogInfo(
                    $"[batch] 整批未齐，拦下这一趟：{itemId} 缺口 {need}（在途 {carried}），{where}只有 {available}" +
                    $" —— 凑齐才装车 / blocked, batch of {need} not ready (carried {carried}, stock {available})");
            }

            /// <summary>订单里该物品还差多少（实时：已接单 − 已交付 − 小镇托盘已有）。</summary>
            private static int LiveNeedFor(string itemId)
            {
                if (MainGame.Instance == null || MainGame.Instance.GameSave == null) return 0;

                WorldData world = MainGame.WorldData;
                if (world == null) return 0;

                VendorSystem vendorSystem = MainGame.Instance.GameSave.vendorSystem;
                if (vendorSystem == null) return 0;

                Dictionary<string, int> needed = CollectNeededByOrders(vendorSystem);
                if (needed.Count == 0) return 0;

                SubtractStockOnTownPalettes(world, needed);

                int need;
                return needed.TryGetValue(itemId, out need) ? need : 0;
            }

            /// <summary>
            /// 退掉多装的箱子后，同步 <c>fake_porter_slot_filler</c> 的数量 ——
            /// 原版每装 1 箱 Big 物品就加 1 个占位符、每交付 1 箱就减 1 个，这里保持同样的比例。
            /// </summary>
            private static void RebalancePorterFillers(Inventory porter)
            {
                if (porter == null || porter.Data == null) return;

                GameBalance balance = GameBalance.Me;
                if (balance == null) return;

                List<Item> items = porter.Data.Inventory;
                if (items == null) return;

                int bigCount = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    Item it = items[i];
                    if (it == null || string.IsNullOrEmpty(it.id)) continue;
                    if (it.id == "fake_porter_slot_filler") continue;

                    ItemDef def = balance.GetData<ItemDef>(it.id);
                    if (def != null && def.itemSize == ItemSize.Big) bigCount += it.Count;
                }

                int fillers = porter.Data.GetTotalCountInInventory("fake_porter_slot_filler");
                while (fillers > bigCount)
                {
                    if (porter.RemoveItemById("fake_porter_slot_filler", 1) == null) break;
                    fillers--;
                }
                while (fillers < bigCount)
                {
                    if (!porter.AddItemToInventory(new Item("fake_porter_slot_filler"))) break;
                    fillers++;
                }
            }
        }
    }
}
