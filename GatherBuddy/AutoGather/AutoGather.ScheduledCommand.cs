using System;
using ECommons.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private enum ScheduledCommandState
        {
            Off,
            Armed,
            WaitingGatherEnd,
            ExecutingCommand,
            WaitingResume
        }

        private ScheduledCommandState _scheduledState = ScheduledCommandState.Off;
        private DateTime _scheduledExecuteAt;
        private DateTime _scheduledResumeAt;
        private DateTime _scheduledWaitStartedAt;
        private bool _isResumingFromSchedule;

        private const int ScheduledCommandWaitTimeoutSeconds = 120;
        private const int ScheduledCommandTaskWaitTimeoutSeconds = 30;

        private void InitializeScheduledCommand()
        {
            if (_isResumingFromSchedule)
            {
                _isResumingFromSchedule = false;
                return;
            }

            var config = GatherBuddy.Config.AutoGatherConfig;
            if (config.EnableScheduledCommand
                && !string.IsNullOrWhiteSpace(config.ScheduledCommand)
                && config.ScheduledCommandIntervalMinutes > 0)
            {
                _scheduledExecuteAt = DateTime.Now.AddMinutes(config.ScheduledCommandIntervalMinutes);
                _scheduledState = ScheduledCommandState.Armed;
                GatherBuddy.Log.Information($"排程指令已啟用，將在 {_scheduledExecuteAt:HH:mm:ss} 執行");
            }
        }

        private void ResetScheduledCommand()
        {
            _scheduledState = ScheduledCommandState.Off;
            _isResumingFromSchedule = false;
        }

        private void RearmScheduledCommand(string reason)
        {
            var config = GatherBuddy.Config.AutoGatherConfig;
            _scheduledExecuteAt = DateTime.Now.AddMinutes(config.ScheduledCommandIntervalMinutes);
            _scheduledState = ScheduledCommandState.Armed;
            GatherBuddy.Log.Warning($"{reason}，將在 {_scheduledExecuteAt:HH:mm:ss} 重試");
            Communicator.PrintError($"[GatherBuddy] {reason}，{config.ScheduledCommandIntervalMinutes} 分鐘後重試");
        }

        private bool HandleScheduledCommand()
        {
            var config = GatherBuddy.Config.AutoGatherConfig;

            if (!config.EnableScheduledCommand)
            {
                if (_scheduledState != ScheduledCommandState.Off)
                    _scheduledState = ScheduledCommandState.Off;
                return false;
            }

            switch (_scheduledState)
            {
                case ScheduledCommandState.Off:
                    if (Enabled 
                        && !string.IsNullOrWhiteSpace(config.ScheduledCommand)
                        && config.ScheduledCommandIntervalMinutes > 0)
                    {
                        _scheduledExecuteAt = DateTime.Now.AddMinutes(config.ScheduledCommandIntervalMinutes);
                        _scheduledState = ScheduledCommandState.Armed;
                        GatherBuddy.Log.Information($"排程指令已動態啟用，將在 {_scheduledExecuteAt:HH:mm:ss} 執行");
                    }
                    return false;

                case ScheduledCommandState.Armed:
                    if (DateTime.Now >= _scheduledExecuteAt)
                    {
                        GatherBuddy.Log.Information("排程時間到，檢查採集狀態...");
                        _scheduledState = ScheduledCommandState.WaitingGatherEnd;
                        _scheduledWaitStartedAt = DateTime.Now;
                    }
                    return false;

                case ScheduledCommandState.WaitingGatherEnd:
                    var gatherWaitSeconds = (DateTime.Now - _scheduledWaitStartedAt).TotalSeconds;
                    if (gatherWaitSeconds > ScheduledCommandWaitTimeoutSeconds)
                    {
                        GatherBuddy.Log.Warning($"等待採集完成超時 ({ScheduledCommandWaitTimeoutSeconds}秒)，強制繼續執行");
                        Communicator.PrintError($"[GatherBuddy] 等待採集完成超時，強制繼續執行排程指令");
                    }
                    else if (IsGathering)
                    {
                        AutoStatus = $"排程：等待採集完成... ({(int)(ScheduledCommandWaitTimeoutSeconds - gatherWaitSeconds)}秒後超時)";
                        return false;
                    }

                    GatherBuddy.Log.Information("準備執行排程指令");
                    StopNavigation();
                    Enabled = false;
                    _scheduledState = ScheduledCommandState.ExecutingCommand;
                    _scheduledWaitStartedAt = DateTime.Now;
                    return true;

                case ScheduledCommandState.ExecutingCommand:
                    var taskWaitSeconds = (DateTime.Now - _scheduledWaitStartedAt).TotalSeconds;
                    if (taskWaitSeconds > ScheduledCommandTaskWaitTimeoutSeconds)
                    {
                        GatherBuddy.Log.Warning($"等待任務完成超時 ({ScheduledCommandTaskWaitTimeoutSeconds}秒)，強制執行指令");
                        TaskManager.Abort();
                    }
                    else if (TaskManager.IsBusy)
                    {
                        AutoStatus = $"排程：等待任務完成... ({(int)(ScheduledCommandTaskWaitTimeoutSeconds - taskWaitSeconds)}秒後超時)";
                        return true;
                    }

                    if (!CanAct)
                    {
                        AutoStatus = "排程：等待可行動狀態...";
                        return true;
                    }

                    var command = config.ScheduledCommand.Trim();
                    if (!command.StartsWith("/"))
                    {
                        GatherBuddy.Log.Error($"排程指令格式錯誤，必須以 / 開頭: {command}");
                        Communicator.PrintError($"[GatherBuddy] 排程指令格式錯誤，必須以 / 開頭");
                        RearmScheduledCommand("指令格式錯誤");
                        return false;
                    }

                    try
                    {
                        GatherBuddy.Log.Information($"執行排程指令: {command}");
                        Chat.Instance.ExecuteCommand(command);

                        var delayMinutes = config.ScheduledCommandResumeDelayMinutes;
                        _scheduledResumeAt = DateTime.Now.AddMinutes(delayMinutes);
                        _scheduledState = ScheduledCommandState.WaitingResume;

                        GatherBuddy.Log.Information($"指令已執行，將在 {_scheduledResumeAt:HH:mm:ss} 恢復採集");
                        Communicator.Print($"[GatherBuddy] 排程指令已執行，{delayMinutes} 分鐘後恢復採集");
                    }
                    catch (Exception ex)
                    {
                        GatherBuddy.Log.Error($"執行排程指令失敗: {ex.Message}");
                        Communicator.PrintError($"[GatherBuddy] 排程指令執行失敗: {ex.Message}");
                        RearmScheduledCommand("指令執行失敗");
                    }
                    return true;

                case ScheduledCommandState.WaitingResume:
                    var remaining = (_scheduledResumeAt - DateTime.Now).TotalSeconds;
                    if (remaining > 0)
                    {
                        AutoStatus = $"排程：等待恢復中... ({(int)remaining} 秒)";
                        return true;
                    }

                    GatherBuddy.Log.Information("恢復時間到，重新啟用自動採集");
                    
                    _scheduledExecuteAt = DateTime.Now.AddMinutes(config.ScheduledCommandIntervalMinutes);
                    _scheduledState = ScheduledCommandState.Armed;
                    _isResumingFromSchedule = true;
                    
                    Enabled = true;

                    GatherBuddy.Log.Information($"下次排程將在 {_scheduledExecuteAt:HH:mm:ss} 執行");
                    Communicator.Print($"[GatherBuddy] 自動採集已恢復，下次排程將在 {config.ScheduledCommandIntervalMinutes} 分鐘後執行");
                    return false;
            }

            return false;
        }

        public string GetScheduledCommandStatus()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.EnableScheduledCommand)
                return string.Empty;

            return _scheduledState switch
            {
                ScheduledCommandState.Armed =>
                    $"下次執行: {Math.Max(0, (_scheduledExecuteAt - DateTime.Now).TotalMinutes):F1} 分鐘後",
                ScheduledCommandState.WaitingGatherEnd =>
                    $"等待採集完成... ({Math.Max(0, ScheduledCommandWaitTimeoutSeconds - (DateTime.Now - _scheduledWaitStartedAt).TotalSeconds):F0}秒後超時)",
                ScheduledCommandState.ExecutingCommand =>
                    "執行指令中...",
                ScheduledCommandState.WaitingResume =>
                    $"恢復倒數: {Math.Max(0, (_scheduledResumeAt - DateTime.Now).TotalSeconds):F0} 秒",
                _ => string.Empty
            };
        }
    }
}
