using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;
using ImGuiNET;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.CustomInfo;
using Newtonsoft.Json;
using OtterGui;
using OtterGui.Raii;

namespace GatherBuddy.AutoGather
{
    public static class AutoGatherUI
    {
        private static bool _gatherDebug;

        public static void DrawAutoGatherStatus()
        {
            var enabled = GatherBuddy.AutoGather.Enabled;
            if (ImGui.Checkbox("啟用", ref enabled))
            {
                GatherBuddy.AutoGather.Enabled = enabled;
            }

            ImGui.Text($"狀態: {GatherBuddy.AutoGather.AutoStatus}");
            
            var scheduledStatus = GatherBuddy.AutoGather.GetScheduledCommandStatus();
            if (!string.IsNullOrEmpty(scheduledStatus))
            {
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"[排程] {scheduledStatus}");
            }
            
            var lastNavString = GatherBuddy.AutoGather.LastNavigationResult.HasValue
                ? GatherBuddy.AutoGather.LastNavigationResult.Value
                    ? "成功"
                    : "失敗 (請嘗試重啟遊戲)"
                : "無";
            ImGui.Text($"導航狀態: {lastNavString}");
        }


        public static void DrawDebugTables()
        {
            if (ImGui.Button("從剪貼簿匯入節點偏移設定"))
            {
                var settings = new JsonSerializerSettings();
                var                          text    = ImGuiUtil.GetClipboardText();
                var vectors = JsonConvert.DeserializeObject<List<OffsetPair>>(text, settings) ?? [];
                foreach (var offset in vectors)
                {
                    WorldData.NodeOffsets[offset.Original] = offset.Offset;
                    GatherBuddy.Log.Information($"已添加偏移 {offset} 至字典");
                }
                WorldData.SaveOffsetsToFile();
                GatherBuddy.Log.Information("匯入完成");
            }
            ImGui.SameLine();
            if (ImGui.Button("匯出節點偏移設定至剪貼簿"))
            {
                var settings = new JsonSerializerSettings();
                var offsetString = JsonConvert.SerializeObject(WorldData.NodeOffsets.Select(x => new OffsetPair(x.Key, x.Value)).ToList(), Formatting.Indented, settings);
                ImGui.SetClipboardText(offsetString);
                GatherBuddy.Log.Information("節點偏移設定已匯出至剪貼簿");
            }
            // First column: Nearby nodes table
            if (ImGui.BeginTable("##nearbyNodesTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("名稱");
                ImGui.TableSetupColumn("可選中");
                ImGui.TableSetupColumn("節點 ID");
                ImGui.TableSetupColumn("位置");
                ImGui.TableSetupColumn("距離");
                ImGui.TableSetupColumn("操作");

                ImGui.TableHeadersRow();

                var playerPosition = Player.Object?.Position ?? Vector3.Zero;
                foreach (var node in Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint)
                             .OrderBy(o => Vector3.Distance(o.Position, playerPosition)))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(node.Name.ToString());
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(node.IsTargetable ? "是" : "否");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(node.DataId.ToString());
                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text(node.Position.ToString());
                    ImGui.TableSetColumnIndex(4);
                    var distance = Vector3.Distance(playerPosition, node.Position);
                    ImGui.Text(distance.ToString());
                    ImGui.TableSetColumnIndex(5);

                    var territoryId = Dalamud.ClientState.TerritoryType;
                    var isBlacklisted = GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.TryGetValue(territoryId, out var list)
                     && list.Contains(node.Position);

                    if (isBlacklisted && list != null)
                    {
                        if (ImGui.Button($"移除黑名單##{node.Position}"))
                        {
                            list.Remove(node.Position);
                            if (list.Count == 0)
                            {
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.Remove(territoryId);
                            }

                            GatherBuddy.Config.Save();
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"添加黑名單##{node.Position}"))
                        {
                            if (list == null)
                            {
                                list                                                                           = new List<Vector3>();
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId[territoryId] = list;
                            }

                            list.Add(node.Position);
                            GatherBuddy.Config.Save();
                        }
                    }

                    if (ImGui.Button($"導航至##{node.Position}"))
                    {
                        if (GatherBuddy.AutoGather.Enabled)
                        {
                            Communicator.PrintError("[GatherBuddyReborn] 已啟用自動採集, 無法使用手動導航");
                            return;
                        }
                        //VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                        VNavmesh.Path.Stop();
                        VNavmesh.SimpleMove.PathfindAndMoveTo(node.Position, GatherBuddy.AutoGather.ShouldFly(node.Position));
                    }

                    if (WorldData.NodeOffsets.TryGetValue(node.Position, out var offset))
                    {
                        if (ImGui.Button($"移除該偏移##{node.Position}"))
                        {
                            WorldData.NodeOffsets.Remove(node.Position);
                            WorldData.SaveOffsetsToFile();
                        }
                        ImGui.Text(offset.ToString());
                        if (ImGui.Button($"導航至偏移##{node.Position}"))
                        {
                            if (GatherBuddy.AutoGather.Enabled)
                            {
                                Communicator.PrintError("[GatherBuddyReborn] 已啟用自動採集, 無法使用手動導航");
                                return;
                            }
                            //VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                            VNavmesh.Path.Stop();
                            VNavmesh.SimpleMove.PathfindAndMoveTo(offset, GatherBuddy.AutoGather.ShouldFly(offset));
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"添加此偏移##{node.Position}"))
                        {
                            WorldData.AddOffset(node.Position, playerPosition);
                        }
                        ImGui.Text(playerPosition.ToString());
                    }
                    
                }

                ImGui.EndTable();
            }
        }

        public unsafe static void DrawMountSelector()
        {
            ImGui.PushItemWidth(300);
            var ps = PlayerState.Instance();
            var preview = Dalamud.GameData.GetExcelSheet<Mount>().First(x => x.RowId == GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId)
                .Singular.ToString().ToProperCase();
            if (string.IsNullOrEmpty(preview))
                preview = "隨機坐騎";
            if (ImGui.BeginCombo("選擇坐騎", preview))
            {
                if (ImGui.Selectable("隨機坐騎", GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId = 0;
                    GatherBuddy.Config.Save();
                }

                foreach (var mount in Dalamud.GameData.GetExcelSheet<Mount>().OrderBy(x => x.Singular.ToString().ToProperCase()))
                {
                    if (ps->IsMountUnlocked(mount.RowId))
                    {
                        var selected = ImGui.Selectable(mount.Singular.ToString().ToProperCase(),
                            GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId == mount.RowId);

                        if (selected)
                        {
                            GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId = mount.RowId;
                            GatherBuddy.Config.Save();
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        /// <summary>
        /// Extension method to convert the string to Proper Case.
        /// </summary>
        /// <param name="input">The string input.</param>
        /// <returns>The string in Proper Case.</returns>
        public static string ToProperCase(this string input)
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower());
        }
    }
}
