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
        public const string PluginVersion = "1.0.0";

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
        internal static ConfigEntry<bool> ResetWeeklyOrderLock;
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
                    "订单窗口一排放几个槽位（多于这个数就换行）。**0 = 不换行**（保持游戏原来的排列，默认）。\n" +
                    "只在 OrderSlots > 2 时才有意义；原版 2 格的外观永远不动。\n" +
                    "How many slots per row the orders window uses (extra ones wrap to the next row).\n" +
                    "0 = no wrapping at all, keep whatever arrangement the game itself does (default).\n" +
                    "Only meaningful when OrderSlots > 2; the vanilla 2-slot look is never touched.",
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

            VendorSystem vendorSystem = MainGame.Instance.GameSave.vendorSystem;
            if (vendorSystem == null) return;

            Dictionary<string, int> needed = CollectNeededByOrders(vendorSystem);
            if (needed.Count > 0)
            {
                SubtractStockOnTownPalettes(world, needed);                                // 小镇托盘上已有的算已交付
            }

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
                        bool wanted = stillToSend > 0 && (!RequireFullBatch.Value || have >= stillToSend);
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

            if (OrderSlotsPerRow.Value <= 1) return;                 // 0/1 = 不换行（不接管排版）
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

            int perRow = Mathf.Clamp(OrderSlotsPerRow.Value, 2, cells.Count);   // 上限也按实际槽位数
            int columns = Mathf.Min(perRow, cells.Count);
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

            List<Vendor> vendors = vendorSystem.vendors;
            if (vendors != null && (resetWeekly || repeatOnce))
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

                        if (resetWeekly && order.IsFinishedThisWeek)
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
                        $"订单每周限制已解除：清除本周标记 {clearedWeekly} 个、重新开放一次性订单 {reopened} 个" +
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

            List<string> customTargets = def.customTargets;
            if (customTargets != null && customTargets.Count > 0)
            {
                for (int i = 0; i < customTargets.Count && !result; i++)
                {
                    result = IsTownPalette(world.GetWgoData(customTargets[i]));
                }
            }

            if (!result && !string.IsNullOrEmpty(def.targetWorldZone))
            {
                WorldZoneData zone = world.GetWorldZoneDataById(def.targetWorldZone);
                if (zone != null && zone.wgoDataList != null)
                {
                    for (int i = 0; i < zone.wgoDataList.Count && !result; i++)
                    {
                        result = IsTownPalette(world.GetWgoData(zone.wgoDataList[i]));
                    }
                }
            }

            TownStationCache[def.id] = result;
            return result;
        }

        private static bool IsTownPalette(WgoData wgo)
        {
            return wgo != null && wgo.Definition != null &&
                   wgo.Definition.interactionType == WGODef.InteractionType.TownPalette;
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
                    if (!ModEnabled.Value || !ResetWeeklyOrderLock.Value) return;
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
