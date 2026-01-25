using System;
using System.Numerics;
using ECommons.DalamudServices;
using GatherBuddy.Plugin;
using ImGuiNET;

namespace GatherBuddy.AutoGather;

public class AutoGatherOverlay : IDisposable
{
    private readonly AutoGather _autoGather;
    private float _radiusToShow = 50f;
    private DateTime _radiusShowUntil = DateTime.MinValue;
    private DateTime _lastTargetCheck = DateTime.MinValue;
    private bool _cachedIsBeingTargeted = false;
    private double _cachedTargetedTime = 0;

    public AutoGatherOverlay(AutoGather autoGather)
    {
        _autoGather = autoGather;
        Dalamud.PluginInterface.UiBuilder.Draw += OnDraw;
    }

    public void Dispose()
    {
        Dalamud.PluginInterface.UiBuilder.Draw -= OnDraw;
    }

    public void SetShowRadiusCircle(bool show, float radius = 50f)
    {
        if (show)
        {
            _radiusToShow     = radius;
            // Fail-safe: auto-hide if UI stops updating this flag.
            _radiusShowUntil  = DateTime.UtcNow.AddMilliseconds(250);
        }
    }

    private void OnDraw()
    {
        var player = Svc.ClientState.LocalPlayer;
        if (player == null) return;

        var drawList = ImGui.GetBackgroundDrawList();

        if (DateTime.UtcNow <= _radiusShowUntil)
        {
            DrawWorldCircle(drawList, player.Position, _radiusToShow, 0x8000FF00);
        }

        if (GatherBuddy.Config.AutoGatherConfig.AntiStuck.EscalationEnabled
            && _autoGather.Enabled)
        {
            var manager = _autoGather.AntiStuckManager;
            var state = manager.State;

            if (state == Helpers.AntiStuckState.EscalationArmed || state == Helpers.AntiStuckState.DrasticActionReady)
            {
                var timeInRange = manager.TimeInArea;
                var threshold = GatherBuddy.Config.AutoGatherConfig.AntiStuck.AreaTimeSeconds;
                var remaining = Math.Max(0, threshold - timeInRange);
                var stateText = state switch
                {
                    Helpers.AntiStuckState.EscalationArmed => " [升級待命]",
                    Helpers.AntiStuckState.DrasticActionReady => " [準備執行]",
                    _ => ""
                };

                DrawCountdownText(drawList, $"防卡死倒數: {remaining:F0} 秒{stateText}",
                    new Vector4(1f, 1f, 0f, 1f),
                    new Vector2(10, 60));
            }
            else if (state == Helpers.AntiStuckState.Cooldown)
            {
                var cooldownRemaining = manager.CooldownRemaining;
                DrawCountdownText(drawList, $"防卡死冷卻中: {cooldownRemaining:F0} 秒",
                    new Vector4(0.5f, 0.5f, 1f, 1f),
                    new Vector2(10, 60));
            }
            else
            {
                // 顯示區域追蹤狀態
                if (manager.IsAreaTracking)
                {
                    var timeInRange = manager.TimeInArea;
                    var threshold = GatherBuddy.Config.AutoGatherConfig.AntiStuck.AreaTimeSeconds;
                    DrawCountdownText(drawList, $"區域停滯追蹤: {timeInRange:F0} / {threshold} 秒",
                        new Vector4(1f, 1f, 0f, 1f),
                        new Vector2(10, 60));
                }
                else
                {
                    DrawCountdownText(drawList, "區域停滯追蹤: 尚未啟用",
                        new Vector4(0.6f, 0.6f, 0.6f, 1f),
                        new Vector2(10, 60));
                }
            }
        }

        if (GatherBuddy.Config.AutoGatherConfig.EnablePlayerTargetEvasion && _autoGather.Enabled)
        {
            if ((DateTime.UtcNow - _lastTargetCheck).TotalMilliseconds >= 200)
            {
                _cachedIsBeingTargeted = _autoGather.PlayerTargetTracker.IsBeingTargetedByPlayer();
                _cachedTargetedTime = _autoGather.PlayerTargetTracker.GetTargetedTime();
                _lastTargetCheck = DateTime.UtcNow;
            }

            if (_cachedIsBeingTargeted)
            {
                var threshold = GatherBuddy.Config.AutoGatherConfig.PlayerTargetEvasionSeconds;
                var color = _cachedTargetedTime >= threshold 
                    ? new Vector4(1f, 0f, 0f, 1f)
                    : new Vector4(1f, 0.5f, 0f, 1f);

                DrawCountdownText(drawList, $"被玩家選中: {_cachedTargetedTime:F1} / {threshold} 秒", 
                    color,
                    new Vector2(10, 90));
            }
        }

        if (GatherBuddy.Config.AutoGatherConfig.EnableScheduledCommand)
        {
            var scheduledStatus = _autoGather.GetScheduledCommandStatus();
            if (!string.IsNullOrEmpty(scheduledStatus))
            {
                var yOffset = 120f;
                if (GatherBuddy.Config.AutoGatherConfig.EnablePlayerTargetEvasion && _cachedIsBeingTargeted)
                    yOffset = 120f;
                else if (GatherBuddy.Config.AutoGatherConfig.AntiStuck.EscalationEnabled && _autoGather.Enabled)
                    yOffset = 90f;
                else
                    yOffset = 60f;

                DrawCountdownText(drawList, $"[排程] {scheduledStatus}",
                    new Vector4(0.4f, 0.8f, 1.0f, 1f),
                    new Vector2(10, yOffset));
            }
        }
    }

    private void DrawWorldCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color)
    {
        const int segments = 64;
        Vector2? lastValidPoint = null;

        for (int i = 0; i <= segments; i++)
        {
            var angle = (float)(2 * Math.PI * i / segments);
            var worldPos = new Vector3(
                center.X + radius * MathF.Cos(angle),
                center.Y,
                center.Z + radius * MathF.Sin(angle)
            );

            if (Svc.GameGui.WorldToScreen(worldPos, out var screenPos))
            {
                if (lastValidPoint.HasValue)
                {
                    drawList.AddLine(lastValidPoint.Value, screenPos, color, 2f);
                }
                lastValidPoint = screenPos;
            }
            else
            {
                lastValidPoint = null;
            }
        }
    }

    private void DrawCountdownText(ImDrawListPtr drawList, string text, Vector4 color, Vector2 position)
    {
        var textSize = ImGui.CalcTextSize(text);
        var bgPadding = new Vector2(5, 3);
        
        drawList.AddRectFilled(
            position - bgPadding, 
            position + textSize + bgPadding, 
            0xAA000000);

        drawList.AddText(position, ImGui.ColorConvertFloat4ToU32(color), text);
    }
}
