using ECommons.Automation.LegacyTaskManager;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text;
using Dalamud.Utility;
using ECommons;
using ECommons.ExcelServices;
using ECommons.Automation;
using ECommons.MathHelpers;
using GatherBuddy.Data;
using NodeType = GatherBuddy.Enums.NodeType;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using Lumina.Excel.Sheets;
using Fish = GatherBuddy.Classes.Fish;
using GatheringType = GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather : IDisposable
    {
        public AutoGather(GatherBuddy plugin)
        {
            // Initialize the task manager
            TaskManager                  =  new();
            TaskManager.ShowDebug        =  false;
            _plugin                      =  plugin;
            _soundHelper                 =  new SoundHelper();
            _advancedUnstuck             =  new();
            _antiStuckManager            =  new AntiStuckManager(_advancedUnstuck);
            GatherBuddy.Config.AutoGatherConfig.MigrateAntiStuckConfig();
            _activeItemList              =  new ActiveItemList(plugin.AutoGatherListsManager);
            _listsManager                =  plugin.AutoGatherListsManager;
            ArtisanExporter              =  new Reflection.ArtisanExporter(plugin.AutoGatherListsManager);
            Svc.Chat.CheckMessageHandled += OnMessageHandled;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
            _plugin.FishRecorder.Parser.CaughtFish += OnFishCaught;

            _overlay = new AutoGatherOverlay(this);

        }

        public Fish? LastCaughtFish { get; private set; }
        public Fish? PreviouslyCaughtFish { get; private set; }
        private void OnFishCaught(Fish arg1, ushort arg2, byte arg3, bool arg4, bool arg5)
        {
            PreviouslyCaughtFish = LastCaughtFish;
            LastCaughtFish       = arg1;
        }

        private void OnGatheringFinalize(AddonEvent type, AddonArgs args)
        {
            GatherTarget gatherTarget;
            if (!_activeItemList.IsInitialized)
                // If Auto-Gather is enabled after opening the node, the active item list is not initialized.
                gatherTarget = _activeItemList.GetNextOrDefault(new List<uint>()).FirstOrDefault();
            else
                // Otherwise, we don't want the list to suddenly change while gathering.
                gatherTarget = _activeItemList.CurrentOrDefault;
            var targetNode = Svc.Targets.Target ?? Svc.Targets.PreviousTarget;
            if (targetNode != null && targetNode.ObjectKind is ObjectKind.GatheringPoint)
            {
                _activeItemList.MarkVisited(targetNode);

                if (gatherTarget.Gatherable?.NodeType is NodeType.常规 or NodeType.限时
                 && VisitedNodes.Last?.Value != targetNode.DataId
                 && gatherTarget.Node?.WorldPositions.ContainsKey(targetNode.DataId) == true)
                {
                    FarNodesSeenSoFar.Clear();
                    VisitedNodes.AddLast(targetNode.DataId);
                    while (VisitedNodes.Count > (gatherTarget.Node.WorldPositions.Count <= 4 ? 2 : 4))
                        VisitedNodes.RemoveFirst();
                }
            }
        }

        private void OnMessageHandled(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            try
            {
                if (type is (XivChatType)2243)
                {
                    var text = message.TextValue;
                    var id = Svc.Data.GetExcelSheet<LogMessage>()
                        ?.FirstOrDefault(x => x.Text.ToString() == text).RowId;

                    LureSuccess = GatherBuddy.GameData.Fishes.Values.FirstOrDefault(f => f.FishData?.Unknown_70_1 == text) != null;

                    if (LureSuccess)
                        return;

                    LureSuccess = id is 5565 or 5569;
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to handle message: {e}");
            }
        }

        private readonly GatherBuddy           _plugin;
        private readonly SoundHelper           _soundHelper;
        private readonly AdvancedUnstuck       _advancedUnstuck;
        private readonly ActiveItemList        _activeItemList;
        private readonly AutoGatherListsManager _listsManager;
        private readonly PlayerTargetTracker   _playerTargetTracker = new();
        private readonly AntiStuckManager      _antiStuckManager;

        private AutoGatherOverlay? _overlay;

        public AntiStuckManager AntiStuckManager => _antiStuckManager;
        public PlayerTargetTracker PlayerTargetTracker => _playerTargetTracker;
        public AutoGatherOverlay? Overlay => _overlay;



        public Reflection.ArtisanExporter ArtisanExporter;
        public TaskManager                TaskManager { get; }

        private           bool             _enabled { get; set; } = false;
        internal readonly GatheringTracker NodeTracker = new();

        public bool Waiting { get; private set; }

        public unsafe bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;

                if (!value)
                {
                    AutoStatus = "空閒中...";
                    TaskManager.Abort();
                    YesAlready.Unlock();

                    _activeItemList.Reset();
                    _playerTargetTracker.Reset();
                    _antiStuckManager.Reset("auto-gather disabled");
                    _wasGathering               = false;
                    Waiting                    = false;
                    ActionSequence             = null;
                    CurrentCollectableRotation = null;

                    if (VNavmesh.Enabled && IsPathGenerating)
                        VNavmesh.Nav.PathfindCancelAll();
                    StopNavigation();
                    CurrentFarNodeLocation   = null;
                    _homeWorldWarning        = false;
                    _diademQueuingInProgress = false;
                    FarNodesSeenSoFar.Clear();
                    VisitedNodes.Clear();
                }
                else
                {
                    WentHome = true; //Prevents going home right after enabling auto-gather
                    _antiStuckManager.OnSessionStart();
                    if (AutoHook.Enabled)
                        AutoHook.SetPluginState(false); //Make sure AutoHook doesn't interfere with us
                }

                _enabled = value;
                _plugin.Ipc.AutoGatherEnabledChanged(value);
            }
        }

        public bool GoHome()
        {
            StopNavigation();

            if (WentHome)
                return false;

            WentHome = true;

            if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                return false;

            if (Lifestream.Enabled && !Lifestream.IsBusy())
            {
                var command = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
                if (command.Contains("/li "))
                    command = command.Replace("/li ", "");
                Lifestream.ExecuteCommand(command);
                TaskManager.EnqueueImmediate(() => !Lifestream.IsBusy(), 120000, "Wait until Lifestream is done");
                return true;
            }
            else
            {
                GatherBuddy.Log.Warning("未安裝或啟用 Lifestream");
                return false;
            }
        }

        private class NoGatherableItemsInNodeException : Exception
        { }

        private class NoCollectableActionsException : Exception
        { }

        private bool _diademQueuingInProgress = false;
        private bool _homeWorldWarning        = false;

        public void DoAutoGather()
        {
            if (!IsGathering)
                LuckUsed = new(0); //Reset the flag even if auto-gather was disabled mid-gathering

            if (!Enabled)
            {
                return;
            }

            // AntiStuckManager: 通知採集狀態變更
            _antiStuckManager.OnGatheringStateChanged(IsGathering);

            _wasGathering = IsGathering;

            if (GatherBuddy.Config.AutoGatherConfig.EnablePlayerTargetEvasion)
            {
                // 如果躲避流程已在進行中，檢查狀態並等待完成
                if (_playerTargetTracker.IsEvasionInProgress)
                {
                    // 中止條件：副本中無法傳送
                    if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                    {
                        AutoStatus = "偵測到玩家選中 - 副本中無法傳送，已取消躲避";
                        _playerTargetTracker.Reset();
                        return;
                    }

                    // 檢查 Lifestream 是否仍在工作中
                    if (Lifestream.Enabled && Lifestream.IsBusy())
                    {
                        AutoStatus = "偵測到玩家選中 - 正在返回旅館...";
                        return; // 等待 Lifestream 完成
                    }

                    // Lifestream 不再 busy，檢查 TaskManager 是否有等待的任務
                    if (TaskManager.IsBusy)
                    {
                        AutoStatus = "偵測到玩家選中 - 正在返回旅館...";
                        return; // 等待 TaskManager 完成
                    }

                    // 如果都不 busy 但 InProgress 仍為 true，可能是異常狀態，重置
                    GatherBuddy.Log.Warning("躲避流程異常結束，正在重置狀態");
                    _playerTargetTracker.Reset();
                    return;
                }

                if (_playerTargetTracker.ShouldEvade(GatherBuddy.Config.AutoGatherConfig.PlayerTargetEvasionSeconds))
                {
                    // 如果正在採集，先關閉採集視窗（只在 TaskManager 空閒時執行，避免重複 enqueue）
                    if (IsGathering)
                    {
                        AutoStatus = "偵測到玩家選中 - 正在關閉採集視窗...";
                        if (!TaskManager.IsBusy)
                            CloseGatheringAddons();
                        return; // 等待下一 tick 確認已關閉
                    }

                    // 確認可以行動後再執行躲避
                    if (!CanAct)
                    {
                        AutoStatus = "偵測到玩家選中 - 等待可行動...";
                        return;
                    }

                    // 檢查是否在副本中（無法傳送）
                    if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                    {
                        AutoStatus = "偵測到玩家選中 - 副本中無法傳送";
                        _playerTargetTracker.Reset();
                        return;
                    }

                    // 檢查 Lifestream 狀態：busy 時等待，不要進入 fallback
                    if (Lifestream.Enabled && Lifestream.IsBusy())
                    {
                        AutoStatus = "偵測到玩家選中 - 等待傳送完成...";
                        return;
                    }

                    var message = "偵測到玩家選中 - 正在躲避返回旅館...";
                    GatherBuddy.Log.Information(message);
                    Communicator.Print($"[GatherBuddy] {message}");
                    Svc.Toasts.ShowNormal(message);

                    // 停止導航
                    StopNavigation();

                    // 標記躲避流程開始（防止重入）
                    _playerTargetTracker.MarkEvasionStarted();

                    // 使用既有的 GoHome() 機制（有完整的 Lifestream 狀態處理）
                    WentHome = false; // 確保 GoHome() 會執行
                    if (GoHome())
                    {
                        // GoHome 成功排程，等待完成後再停用
                        TaskManager.Enqueue(() =>
                        {
                            Enabled = false;
                            _playerTargetTracker.Reset();
                        });
                    }
                    else
                    {
                        // GoHome 失敗：Lifestream 未安裝/未啟用，回退到直接指令
                        // 注意：此時 Lifestream.IsBusy() 已在上方檢查過，不會是 busy 狀態
                        if (!Lifestream.Enabled)
                        {
                            var lifeStreamCmd = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand.Trim();
                            if (lifeStreamCmd.StartsWith("/li ", StringComparison.OrdinalIgnoreCase))
                                lifeStreamCmd = lifeStreamCmd[4..];
                            if (lifeStreamCmd.StartsWith("/li", StringComparison.OrdinalIgnoreCase))
                                lifeStreamCmd = lifeStreamCmd[3..].TrimStart();
                            Chat.Instance.ExecuteCommand($"/li {lifeStreamCmd}");
                        }
                        Enabled = false;
                        _playerTargetTracker.Reset();
                    }
                    return;
                }
            }

            // AntiStuckManager: 更新目的地並檢查是否需要執行強制措施
            _antiStuckManager.SetDestination(CurrentDestination);
            if (CanAct && !TaskManager.IsBusy && !IsGathering && _antiStuckManager.ShouldExecuteDrasticAction())
            {
                var action = _antiStuckManager.GetDrasticAction();
                var message = action == AutoGatherConfig.PositionUnstuckAction.TeleportAetheryte
                    ? "偵測到長時間卡住，正在傳送到最近水晶..."
                    : "偵測到長時間卡住，正在返回旅館...";

                GatherBuddy.Log.Warning(message);
                Communicator.Print($"[GatherBuddy] {message}");
                Svc.Toasts.ShowNormal(message);

                _antiStuckManager.MarkDrasticActionExecuted();

                if (action == AutoGatherConfig.PositionUnstuckAction.TeleportAetheryte)
                {
                    TeleportToNearestAetheryte();
                }
                else
                {
                    StopNavigation();
                    WentHome = false;
                    if (GoHome())
                    {
                        TaskManager.Enqueue(() => { Enabled = false; });
                    }
                    else
                    {
                        Enabled = false;
                    }
                }
                return;
            }

            try
            {
                if (!NavReady)
                {
                    AutoStatus = "等待導航中...";
                    return;
                }
            }
            catch (Exception e)
            {
                //GatherBuddy.Log.Error(e.Message);
                AutoStatus = "未安裝或啟用 vnavmesh";
                return;
            }

            if (TaskManager.IsBusy)
            {
                //GatherBuddy.Log.Verbose("TaskManager has tasks, skipping DoAutoGather");
                return;
            }

            if (!_homeWorldWarning && !Functions.OnHomeWorld())
            {
                _homeWorldWarning = true;
                Communicator.PrintError("目前不在原始伺服器, 部分物品無法採集");
            }

            if (DiscipleOfLand.NextTreasureMapAllowance == DateTime.MinValue)
            {
                //Wait for timer refresh
                AutoStatus = "重新整理採集時鐘中...";
                DiscipleOfLand.RefreshNextTreasureMapAllowance();
                return;
            }

            if (!CanAct && !_diademQueuingInProgress)
            {
                AutoStatus = Dalamud.Conditions[ConditionFlag.Gathering] ? "採集中..." : "目前無法行動, 等待中...";
                return;
            }

            YesAlready.Unlock(); // Clean up lock that may have been left behind by cancelled tasks

            if (FreeInventorySlots == 0)
            {
                if (HasReducibleItems())
                {
                    if (IsGathering)
                        CloseGatheringAddons();
                    else
                        ReduceItems(false);
                }
                else
                {
                    AbortAutoGather("背包物品已滿");
                }

                return;
            }

            if (_activeItemList.GetNextOrDefault(new List<uint>()).Any(g => g.Fish != null)
             && !GatherBuddy.Config.AutoGatherConfig.FishDataCollection)
            {
                Communicator.PrintError(
                    "自動採集清單中包含魚類, 但未開啟捕魚資料採集, 因此無法繼續自動採集, 請啟用捕魚資料收集, 或從自動採集清單中刪除所有魚類");
                AbortAutoGather();
                return;
            }

            if (IsGathering)
            {
                GatherTarget gatherTarget;
                if (!_activeItemList.IsInitialized)
                    // If Auto-Gather is enabled after opening the node, the active item list is not initialized.
                    gatherTarget = _activeItemList.GetNextOrDefault(new List<uint>()).FirstOrDefault();
                else
                    // Otherwise, we don't want the list to suddenly change while gathering.
                    gatherTarget = _activeItemList.CurrentOrDefault;

                if (!GatherBuddy.Config.AutoGatherConfig.DoGathering)
                    return;

                AutoStatus = "採集中...";
                StopNavigation();

                var fish = _activeItemList.GetNextOrDefault(new List<uint>()).Where(g => g.Fish != null);
                if (fish.Any() && Player.Job == Job.FSH)
                {
                    if (GatherBuddy.Config.AutoGatherConfig.UseNavigation)
                        DoFishMovement(fish);
                    DoFishingTasks(fish);
                    return;
                }

                if (!fish.Any() && Player.Job == Job.FSH)
                {
                    QueueQuitFishingTasks();
                }

                try
                {
                    DoActionTasks(gatherTarget);
                }
                catch (NoGatherableItemsInNodeException)
                {
                    CloseGatheringAddons();
                }
                catch (NoCollectableActionsException)
                {
                    Communicator.PrintError(
                        "目前無可用的收藏品價值上升技能, 請檢查設定中相關技能的啟用情況");
                    AbortAutoGather();
                }


                return;
            }

            ActionSequence             = null;
            CurrentCollectableRotation = null;

            //Cache IPC call results
            var isPathGenerating = IsPathGenerating;
            var isPathing        = IsPathing;

            switch (_antiStuckManager.Tick(isPathGenerating, isPathing))
            {
                case AdvancedUnstuckCheckResult.Pass: break;
                case AdvancedUnstuckCheckResult.Wait: return;
                case AdvancedUnstuckCheckResult.Fail:
                    StopNavigation();
                    AutoStatus = $"嘗試進一步脫離卡死";
                    return;
            }

            if (isPathGenerating)
            {
                AutoStatus = "正在生成路徑...";
                return;
            }

            if (Player.Job is Job.BTN or Job.MIN or Job.FSH
             && !isPathing
             && !Svc.Condition[ConditionFlag.Mounted])
            {
                if (SpiritbondMax > 0)
                {
                    if (IsGathering)
                    {
                        QueueQuitFishingTasks();
                    }

                    DoMateriaExtraction();
                    return;
                }

                if (FreeInventorySlots < 20 && HasReducibleItems())
                {
                    ReduceItems(false);
                    return;
                }
            }

            var nearbyNodes = Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable).Select(o => o.DataId);
            var next = _activeItemList.GetNextOrDefault(nearbyNodes);
            if (!next.Any())
            {
                if (!_activeItemList.HasItemsToGather)
                {
                    AbortAutoGather();
                    return;
                }

                if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle)
                    if (GoHome())
                        return;

                if (HasReducibleItems())
                {
                    ReduceItems(true);
                    return;
                }

                if (!Waiting)
                {
                    Waiting = true;
                    _plugin.Ipc.AutoGatherWaiting();
                }

                AutoStatus = "無待採集物品";
                return;
            }

            Waiting = false;

            if (next.Any(n => n.Item.ItemData.IsCollectable
                 && !CheckCollectablesUnlocked(n.Fish != null ? GatheringType.捕鱼人 : n.Gatherable!.GatheringType.ToGroup())))
            {
                AbortAutoGather();
                return;
            }

            if (RepairIfNeeded())
                return;

            if (!GatherBuddy.Config.AutoGatherConfig.UseNavigation)
            {
                AutoStatus = "Waiting for Gathering Point... (No Nav Mode)";
                return;
            }

            var territoryId = Svc.ClientState.TerritoryType;
            //Idyllshire to The Dravanian Hinterlands
            if ((territoryId == 478 && next.First().Node.Territory.Id == 399)
             || (territoryId == 418 && next.First().Node.Territory.Id is 901 or 929 or 939) && Lifestream.Enabled)
            {
                var aetheryte = Svc.Objects.Where(x => x.ObjectKind == ObjectKind.Aetheryte && x.IsTargetable)
                    .OrderBy(x => x.Position.DistanceToPlayer()).FirstOrDefault();
                if (aetheryte != null)
                {
                    if (aetheryte.Position.DistanceToPlayer() > 10)
                    {
                        AutoStatus = "向以太之光移動中...";
                        if (!isPathing && !isPathGenerating)
                            Navigate(aetheryte.Position, false);
                    }
                    else if (!Lifestream.IsBusy())
                    {
                        AutoStatus = "傳送中...";
                        StopNavigation();
                        string name = string.Empty;
                        switch (territoryId)
                        {
                            case 478:
                                var exit = next.First().Node.DefaultXCoord < 2000 ? 91u : 92u;
                                name = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRow(exit).AethernetName.Value.Name
                                    .ToString();
                                break;
                            case 418:
                                name = Dalamud.GameData.GetExcelSheet<TerritoryType>().GetRow(886).PlaceName.Value.Name.ToString()
                                    .Split(" ")[1];
                                break;
                        }

                        TaskManager.Enqueue(() => Lifestream.AethernetTeleport(name));
                        TaskManager.DelayNext(1000);
                        TaskManager.Enqueue(() => GenericHelpers.IsScreenReady());
                    }

                    return;
                }
            }

            if (territoryId == 886 && next.First().Node.Territory.Id is 901 or 929 or 939)
            {
                var dutyNpc                    = Svc.Objects.FirstOrDefault(o => o.DataId == 1031694);
                var selectStringAddon          = Dalamud.GameGui.GetAddonByName("SelectString");
                var talkAddon                  = Dalamud.GameGui.GetAddonByName("Talk");
                var selectYesNoAddon           = Dalamud.GameGui.GetAddonByName("SelectYesno");
                var contentsFinderConfirmAddon = Dalamud.GameGui.GetAddonByName("ContentsFinderConfirm");
                Svc.Log.Verbose($"Addons: {selectStringAddon}, {talkAddon}, {selectYesNoAddon}, {contentsFinderConfirmAddon}");
                if (dutyNpc != null && dutyNpc.Position.DistanceToPlayer() > 3)
                {
                    var point = VNavmesh.Query.Mesh.NearestPoint(dutyNpc.Position, 10, 10000);
                    VNavmesh.SimpleMove.PathfindAndMoveTo(point, false);
                    return;
                }
                else
                    switch (Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent])
                    {
                        case false when contentsFinderConfirmAddon > 0:
                        {
                            var contents = new AddonMaster.ContentsFinderConfirm(contentsFinderConfirmAddon);
                            TaskManager.Enqueue(contents.Commence);
                            TaskManager.Enqueue(() => _diademQueuingInProgress = false);
                            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BoundByDuty]);
                            TaskManager.Enqueue(YesAlready.Unlock);
                            return;
                        }
                        case false when contentsFinderConfirmAddon == nint.Zero
                         && selectStringAddon == nint.Zero
                         && selectYesNoAddon == nint.Zero:
                            unsafe
                            {
                                var targetSystem = TargetSystem.Instance();
                                if (targetSystem == null)
                                    return;

                                TaskManager.Enqueue(YesAlready.Lock);
                                TaskManager.Enqueue(StopNavigation);
                                TaskManager.Enqueue(()
                                    => targetSystem->OpenObjectInteraction(
                                        (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)dutyNpc.Address));
                                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]);
                                TaskManager.Enqueue(() => _diademQueuingInProgress = true);
                                return;
                            }
                        case true when selectStringAddon > 0:
                        {
                            var select = new AddonMaster.SelectString(selectStringAddon);
                            TaskManager.Enqueue(() => select.Entries[0].Select());
                            return;
                        }
                        case true when selectYesNoAddon > 0:
                        {
                            var yesNo = new AddonMaster.SelectYesno(selectYesNoAddon);
                            TaskManager.Enqueue(yesNo.Yes);
                            TaskManager.DelayNext(5000);
                            return;
                        }
                        case true when talkAddon > 0:
                        {
                            var talk = new AddonMaster.Talk(talkAddon);
                            TaskManager.Enqueue(talk.Click);
                            return;
                        }
                    }
            }

            var forcedAetheryte = ForcedAetherytes.ZonesWithoutAetherytes
                .FirstOrDefault(z => z.ZoneId == next.First().Location.Territory.Id);
            if (forcedAetheryte.ZoneId != 0
             && GatherBuddy.GameData.Aetherytes[forcedAetheryte.AetheryteId].Territory.Id == territoryId)
            {
                if (territoryId == 478 && !Lifestream.Enabled)
                    AutoStatus = $"啟用並安裝 Lifestream 或手動傳送至 {next.First().Location.Territory.Name}";
                else
                    AutoStatus = "需要手動傳送";
                return;
            }

            //At this point, we are definitely going to gather something, so we may go home after that.
            if (Lifestream.Enabled)
                Lifestream.Abort();
            WentHome = false;

            if (next.First().Location.Territory.Id != territoryId)
            {
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty] && !Functions.InTheDiadem())
                {
                    AutoStatus = "無法在副本任務中傳送";
                    return;
                }
                else if (Functions.InTheDiadem())
                {
                    LeaveTheDiadem();
                    return;
                }

                AutoStatus = "傳送中...";
                StopNavigation();

                if (!MoveToTerritory(next.First().Location))
                    AbortAutoGather();

                // Reset target to pick up closest item after teleport
                next = default;

                return;
            }

            var config = MatchConfigPreset(next.First().Gatherable);

            if (DoUseConsumablesWithoutCastTime(config))
                return;

            if (!LocationMatchesJob(next.First().Location))
            {
                if (!ChangeGearSet(next.First().Location.GatheringType.ToGroup(), 2400))
                    AbortAutoGather();
            }

            if (next.First().Fish != null)
            {
                DoFishMovement(next);
                return;
            }

            if (next.First().Gatherable != null)
            {
                DoNodeMovement(next, config);
                return;
            }

            AutoStatus = "自動採集流程意外損壞, 請回報此問題";
            return;
        }

        public readonly Dictionary<GatherTarget, (Vector3 Position, Angle Rotation, DateTime Expiration)> FishingSpotData = new();

        private void DoFishMovement(IEnumerable<GatherTarget> next)
        {
            var fish = next.First(ne => ne.Fish != null);

            if (!FishingSpotData.TryGetValue(fish, out var fishingSpotData))
            {
                var positionData = _plugin.FishRecorder.GetPositionForFishingSpot(fish!.FishingSpot);
                if (!positionData.HasValue)
                {
                    Communicator.PrintError(
                        $"No position data for fishing spot {fish.FishingSpot.Name}. Auto-Fishing cannot continue.");
                    AbortAutoGather();
                    return;
                }

                DateTime spotExpiration =
                    DateTime.Now.AddMinutes(GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes); //TODO: Make this configurable
                FishingSpotData.Add(fish, (positionData.Value.Position, positionData.Value.Rotation, spotExpiration));
                return;
            }

            if (fishingSpotData.Expiration < DateTime.Now)
            {
                Svc.Log.Debug("Time for a new fishing spot!");
                FishingSpotData.Remove(fish);
                if (IsGathering || IsFishing)
                {
                    QueueQuitFishingTasks();
                }

                return;
            }

            if (Vector3.Distance(fishingSpotData.Position, Player.Position) < 1)
            {
                if (Dalamud.Conditions[ConditionFlag.Mounted])
                    EnqueueDismount();

                var playerAngle = new Angle(Player.Rotation);
                if (playerAngle != fishingSpotData.Rotation)
                    TaskManager.Enqueue(() => SetRotation(fishingSpotData.Rotation));
                Svc.Log.Debug($"Fishing Spot is valid for {(fishingSpotData.Expiration - DateTime.Now).TotalSeconds} seconds");

                AutoStatus = "Fishing...";
                DoFishingTasks(next);
                return;
            }

            if (CurrentDestination != fishingSpotData.Position)
            {
                StopNavigation();
                AutoStatus = "Moving to fishing spot...";
                if (IsGathering || IsFishing)
                {
                    QueueQuitFishingTasks();
                }

                MoveToFishingSpot(fishingSpotData.Position, fishingSpotData.Rotation);
            }
        }

        private void DoNodeMovement(IEnumerable<GatherTarget> next, ConfigPreset config)
        {
            var allPositions = next.Where(n => n.Location.Territory.Id == Player.Territory)
                .SelectMany(ne => ne.Node?.WorldPositions
                        .ExceptBy(VisitedNodes, n => n.Key)
                        .SelectMany(w => w.Value)
                        .Where(v => !IsBlacklisted(v))
                 ?? []).Select(s => s)
                .ToHashSet();

            var visibleNodes = Svc.Objects
                .Where(o => allPositions.Contains(o.Position))
                .ToList();

            var closestTargetableNode = visibleNodes
                .Where(o => o.IsTargetable)
                .MinBy(o => Vector3.Distance(Player.Position, o.Position));

            if (ActivateGatheringBuffs(next.First().Gatherable.NodeType is NodeType.未知 or NodeType.传说))
                return;

            if (closestTargetableNode != null)
            {
                AutoStatus = "正在移動至採集點...";
                var targetItem = next.First(ti => ti.Node != null && ti.Node.WorldPositions.ContainsKey(closestTargetableNode.DataId))
                    .Gatherable;
                MoveToCloseNode(closestTargetableNode, targetItem, config);
                return;
            }

            AutoStatus = "正在移動至較遠節點...";

            if (CurrentDestination != default)
            {
                var currentNode = visibleNodes.FirstOrDefault(o => o.Position == CurrentDestination);
                if (currentNode != null && !currentNode.IsTargetable)
                    GatherBuddy.Log.Verbose($"下一節點距離較遠, 目前尚不可選中, 距離: {currentNode.Position.DistanceToPlayer()}.");

                //It takes some time (roundtrip to the server) before a node becomes targetable after it becomes visible,
                //so we need to delay excluding it. But instead of measuring time, we use distance, since character is traveling at a constant speed.
                //Value 50 was determined empirically.
                foreach (var node in allPositions.Where(o => o.DistanceToPlayer() < 50))
                    FarNodesSeenSoFar.Add(node);

                if (CurrentDestination.DistanceToPlayer() < 50)
                {
                    GatherBuddy.Log.Verbose("下一節點距離較遠, 目前尚不可選中, 已切換至另一節點");
                }
                else
                {
                    return;
                }
            }

            Vector3 selectedFarNode;

            // only Legendary and Unspoiled show marker
            var timedNode = next.FirstOrDefault(n => n.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(-8));
            if (ShouldUseFlag && timedNode != default)
            {
                var pos = TimedNodePosition;
                // marker not yet loaded on game
                if (pos == null || timedNode.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(-8))
                {
                    AutoStatus = "等待標點出現中";
                    return;
                }

                selectedFarNode = allPositions
                    .Where(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)) < 10)
                    .OrderBy(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)))
                    .FirstOrDefault();
                if (selectedFarNode == default)
                    selectedFarNode = VNavmesh.Query.Mesh.NearestPoint(new Vector3(pos.Value.X, 0, pos.Value.Y), 10, 10000);
            }
            else
            {
                //Select the closest node
                selectedFarNode = allPositions
                    .Where(fn => !visibleNodes.Select(vn => vn.Position).Contains(fn))
                    .OrderBy(v => Vector3.Distance(Player.Position, v))
                    .FirstOrDefault(n => !FarNodesSeenSoFar.Contains(n));

                if (selectedFarNode == default)
                {
                    FarNodesSeenSoFar.Clear();
                    GatherBuddy.Log.Verbose($"目前選擇節點為空, 較遠節點篩選器已被清空");
                    return;
                }
            }

            MoveToFarNode(selectedFarNode);
        }

        private unsafe void LeaveTheDiadem()
        {
            AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinderMenu)->Show();
            if (GenericHelpers.TryGetAddonByName("ContentsFinderMenu", out AtkUnitBase* addon))
            {
                TaskManager.Enqueue(YesAlready.Lock);
                TaskManager.Enqueue(() => Callback.Fire(addon, true,  0));
                TaskManager.Enqueue(() => Callback.Fire(addon, false, -2));
                TaskManager.DelayNext(1000);
                TaskManager.Enqueue(() => Callback.Fire((AtkUnitBase*)Dalamud.GameGui.GetAddonByName("SelectYesno"), true, 0));
                TaskManager.Enqueue(YesAlready.Unlock);
                return;
            }
        }

        private void AbortAutoGather(string? status = null)
        {
            if (Functions.InTheDiadem())
            {
                LeaveTheDiadem();
                return;
            }

            if (!string.IsNullOrEmpty(status))
                AutoStatus = status;
            if (GatherBuddy.Config.AutoGatherConfig.HonkMode)
                Task.Run(() => _soundHelper.StartHonkSoundTask(3));
            CloseGatheringAddons();
            if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone)
                EnqueueActionWithDelay(() => { GoHome(); });
            TaskManager.Enqueue(() =>
            {
                Enabled    = false;
                AutoStatus = status ?? AutoStatus;
            });
        }

        private unsafe void CloseGatheringAddons(bool closeGathering = true)
        {
            var masterpieceOpen = MasterpieceAddon != null;
            var gatheringOpen   = GatheringAddon != null;
            if (masterpieceOpen)
            {
                EnqueueActionWithDelay(() =>
                {
                    if (MasterpieceAddon is var addon and not null)
                    {
                        Callback.Fire(&addon->AtkUnitBase, true, -1);
                    }
                });
                TaskManager.Enqueue(() => MasterpieceAddon == null,                 "Wait until GatheringMasterpiece addon is closed");
                TaskManager.Enqueue(() => GatheringAddon is var addon and not null, "Wait until Gathering addon pops up");
                TaskManager.DelayNext(
                    300); //There is some delay after the moment the addon pops up (and is ready) before the callback can be used to close it. We wait some time and retry the callback.
            }

            if (closeGathering && (gatheringOpen || masterpieceOpen))
            {
                TaskManager.Enqueue(() =>
                {
                    if (GatheringAddon is var gathering and not null && gathering->IsReady)
                    {
                        Callback.Fire(&gathering->AtkUnitBase, true, -1);
                        TaskManager.DelayNextImmediate(100);
                        return false;
                    }

                    var addon = SelectYesnoAddon;
                    if (addon != null)
                    {
                        EnqueueActionWithDelay(() =>
                        {
                            if (SelectYesnoAddon is var addon and not null)
                            {
                                var master = new AddonMaster.SelectYesno(addon);
                                master.Yes();
                            }
                        }, true);
                        TaskManager.EnqueueImmediate(() => !IsGathering, "Wait until Gathering addon is closed");
                        return true;
                    }

                    return !IsGathering;
                }, "Wait until Gathering addon is closed or SelectYesno addon pops up");
            }
        }

        private bool CheckCollectablesUnlocked(GatheringType gatheringType)
        {
            var level = gatheringType switch
            {
                GatheringType.采矿工    => DiscipleOfLand.MinerLevel,
                GatheringType.园艺工 => DiscipleOfLand.BotanistLevel,
                GatheringType.捕鱼人   => DiscipleOfLand.FisherLevel,
                GatheringType.多职业 => Math.Max(DiscipleOfLand.MinerLevel, DiscipleOfLand.BotanistLevel),
                _                      => 0
            };
            if (level < Actions.Collect.MinLevel)
            {
                Communicator.PrintError("清單內存在目前無法採集的收藏品, 原因: 等級不足");
                return false;
            }

            var questId = gatheringType switch
            {
                GatheringType.采矿工 => Actions.Collect.QuestIds.Miner,
                GatheringType.园艺工 => Actions.Collect.QuestIds.Botanist,
                _                 => 0u
            };

            if (questId != 0 && !QuestManager.IsQuestComplete(questId))
            {
                Communicator.PrintError("清單內存在尚未解鎖的收藏品");
                var sheet      = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Quest>()!;
                var row        = sheet.GetRow(questId)!;
                var loc        = row.IssuerLocation.Value!;
                var map        = loc.Map.Value!;
                var pos        = MapUtil.WorldToMap(new Vector2(loc.X, loc.Z), map);
                var mapPayload = new MapLinkPayload(loc.Territory.RowId, loc.Map.RowId, pos.X, pos.Y);
                var text       = new SeStringBuilder();
                text.AddText("收藏品可以由 ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .AddQuestLink(questId)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(row.Name.ToString())
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(" 任務解鎖, 開始地點: ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .Add(mapPayload)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText($"{mapPayload.PlaceName} {mapPayload.CoordinateString}")
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(".");
                Communicator.Print(text.BuiltString);
                return false;
            }

            return true;
        }

        private bool ChangeGearSet(GatheringType job, int delay)
        {
            var set = job switch
            {
                GatheringType.采矿工    => GatherBuddy.Config.MinerSetName,
                GatheringType.园艺工 => GatherBuddy.Config.BotanistSetName,
                GatheringType.捕鱼人   => GatherBuddy.Config.FisherSetName,
                _                      => null,
            };
            if (string.IsNullOrEmpty(set))
            {
                Communicator.PrintError($"{job} 不存在任何關聯套裝");
                return false;
            }

            Chat.Instance.ExecuteCommand($"/gearset change \"{set}\"");
            TaskManager.DelayNext(Random.Shared.Next(delay, delay + 500)); //Add a random delay to be less suspicious
            return true;
        }

        internal void DebugClearVisited()
        {
            _activeItemList.DebugClearVisited();
        }

        internal void DebugMarkVisited(GatherTarget target)
        {
            _activeItemList.DebugMarkVisited(target);
        }

        private void TeleportToNearestAetheryte()
        {
            var player = Svc.ClientState.LocalPlayer;
            if (player == null)
            {
                GatherBuddy.Log.Warning("無法取得玩家位置");
                return;
            }

            if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
            {
                GatherBuddy.Log.Warning("副本中無法傳送，改為停止自動採集");
                Communicator.PrintError("[GatherBuddy] 副本中無法傳送");
                Enabled = false;
                return;
            }

            var currentTerritory = Svc.ClientState.TerritoryType;
            // Aetheryte.XCoord/YCoord are map coordinates (*100). Use the same coordinate space for sorting.
            var mapObject = Player.Object;
            var candidates = GatherBuddy.GameData.Aetherytes.Values
                .Where(a => a.Territory.Id == currentTerritory && SeFunctions.Teleporter.IsAttuned(a.Id));

            // Prefer nearest-by-map distance when possible; otherwise fall back to a stable ordering.
            var mapCoords = mapObject?.GetMapCoordinates();
            if (mapCoords != null)
            {
                var mapX = (int)(mapCoords.Value.X * 100f);
                var mapY = (int)(mapCoords.Value.Y * 100f);
                candidates = candidates.OrderBy(a => a.WorldDistance(currentTerritory, mapX, mapY));
            }
            else
            {
                candidates = candidates.OrderBy(a => a.Id);
            }

            var closest = candidates.FirstOrDefault();

            if (closest == null)
            {
                GatherBuddy.Log.Warning("未找到可用的以太水晶，改為返回旅館");
                Communicator.PrintError("[GatherBuddy] 未找到可用的以太水晶，改為返回旅館");
                StopNavigation();
                WentHome = false;
                if (GoHome())
                {
                    TaskManager.Enqueue(() => { Enabled = false; });
                }
                else
                {
                    Enabled = false;
                }
                return;
            }

            GatherBuddy.Log.Information($"正在傳送到 {closest.Name}...");
            AutoStatus = $"正在傳送到 {closest.Name}...";

            StopNavigation();

            TaskManager.Enqueue(() => SeFunctions.Teleporter.Teleport(closest.Id));
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BetweenAreas] 
                || Dalamud.Conditions[ConditionFlag.BetweenAreas51], 10000, "等待進入傳送");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.BetweenAreas] 
                && !Dalamud.Conditions[ConditionFlag.BetweenAreas51], 120000, "等待傳送完成");
            TaskManager.Enqueue(() => CanAct, 5000, "等待可行動");
        }

        public void Dispose()
        {
            _overlay?.Dispose();
            _overlay = null;

            _antiStuckManager.Dispose();
            _advancedUnstuck.Dispose();
            NodeTracker.Dispose();
            _activeItemList.Dispose();
            Svc.Chat.CheckMessageHandled -= OnMessageHandled;
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Gathering", OnGatheringFinalize);
        }
    }
}
