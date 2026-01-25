using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;

namespace GatherBuddy.AutoGather.Helpers
{
    public static class UiCloser
    {
        private static readonly HashSet<string> AlwaysKeepOpen = new()
        {
            "NamePlate", "ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3",
            "SelectString", "SelectYesno", "SelectOk", "Talk", "Dialogue", "CutSceneSelectString",
            "JournalDetail", "JournalResult", "ContentsFinderConfirm", "ContentsFinderReady",
            "RetainerTaskAsk", "RetainerTaskResult", "ShopExchangeItem", "ShopExchangeItemDialog",
            "Request", "SystemMessageDialog", "FateProgress", "ContextMenu"
        };

        private static bool ShouldSkipClosing(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.StartsWith("_")) return true;
            if (AlwaysKeepOpen.Contains(name)) return true;
            return false;
        }

        /// <summary>
        /// 關閉所有可能阻擋傳送的 UI 視窗。
        /// 安全做法：先收集名稱，再以名稱重新 lookup 後關閉，避免懸空指標。
        /// </summary>
        public static unsafe void CloseBlockingUi()
        {
            try
            {
                var atkStage = AtkStage.Instance();
                if (atkStage == null)
                {
                    GatherBuddy.Log.Warning("AtkStage 為 null，無法關閉視窗");
                    return;
                }

                var raptureAtkUnitManager = atkStage->RaptureAtkUnitManager;
                if (raptureAtkUnitManager == null)
                {
                    GatherBuddy.Log.Warning("RaptureAtkUnitManager 為 null，無法關閉視窗");
                    return;
                }

                // 第一階段：收集需要關閉的 UI 名稱（不保存裸指標）
                var namesToClose = new List<string>();
                var unitList = raptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
                for (int i = 0; i < unitList.Count; i++)
                {
                    try
                    {
                        var unit = unitList.Entries[i].Value;
                        if (unit == null || !unit->IsVisible) continue;

                        var name = unit->NameString;
                        if (ShouldSkipClosing(name)) continue;

                        namesToClose.Add(name);
                    }
                    catch
                    {
                        // 忽略單一 unit 讀取失敗
                    }
                }

                if (namesToClose.Count == 0)
                    return;

                // 第二階段：以名稱重新 lookup 後關閉（避免懸空指標）
                int closedCount = 0;
                foreach (var name in namesToClose)
                {
                    try
                    {
                        var unit = (AtkUnitBase*)Dalamud.GameGui.GetAddonByName(name);
                        if (unit != null && unit->IsVisible)
                        {
                            unit->FireCloseCallback();
                            closedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        GatherBuddy.Log.Debug($"關閉視窗 {name} 失敗: {ex.Message}");
                    }
                }

                if (closedCount > 0)
                    GatherBuddy.Log.Information($"CloseBlockingUi: 已關閉 {closedCount} 個阻擋視窗");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"關閉視窗失敗: {ex.Message}");
            }
        }
    }
}
