using System.Collections.Generic;
using Streamer.bot.Common.Events;
using Streamer.bot.Plugin.Interface.Enums;
using Streamer.bot.Plugin.Interface.Model;

namespace Streamer.bot.Plugin.Interface;

/// <summary>
/// The <c>CPH</c> object available to C# scripts. This is CherBot's subset of Streamer.bot's script API, using the same
/// names and signatures so existing scripts compile. Methods outside the subset are not declared, so scripts that use them
/// fail at compile time with a clear error instead of misbehaving at run time.
/// </summary>
public interface IInlineInvokeProxy
{
    // ── arguments ────────────────────────────────────────────────────────────
    bool TryGetArg(string argName, out object value);
    bool TryGetArg<T>(string argName, out T value);
    void SetArgument(string variableName, object value);
    EventType GetEventType();

    // ── globals ──────────────────────────────────────────────────────────────
    T GetGlobalVar<T>(string varName, bool persisted = true);
    void SetGlobalVar(string varName, object value, bool persisted = true);
    void UnsetGlobalVar(string varName, bool persisted = true);
    List<GlobalVariableValue> GetGlobalVarValues(bool persisted = true);
    T GetTwitchUserVar<T>(string userLogin, string varName, bool persisted = true);
    void SetTwitchUserVar(string userLogin, string varName, object value, bool persisted = true);
    T GetTwitchUserVarById<T>(string userId, string varName, bool persisted = true);
    void SetTwitchUserVarById(string userId, string varName, object value, bool persisted = true);
    void UnsetTwitchUserVar(string userLogin, string varName, bool persisted = true);

    // ── actions, queues, commands, timers ───────────────────────────────────
    bool ActionExists(string actionName);
    List<ActionData> GetActions();
    bool RunAction(string actionName, bool runImmediately = true);
    bool RunActionById(string actionId, bool runImmediately = true);
    void EnableAction(string actionName);
    void DisableAction(string actionName);
    void EnableActionById(string actionId);
    void DisableActionById(string actionId);
    void PauseActionQueue(string queueName);
    void ResumeActionQueue(string queueName, bool clear = false);
    void PauseAllActionQueues();
    void ResumeAllActionQueues(bool clear = false);
    void EnableCommand(string commandId);
    void DisableCommand(string commandId);
    void EnableTimer(string timerName);
    void DisableTimer(string timerName);
    void EnableTimerById(string timerId);
    void DisableTimerById(string timerId);
    void SetTimerInterval(string timerName, int interval);
    bool ExecuteMethod(string executeCode, string methodName);

    // ── triggers and events ──────────────────────────────────────────────────
    bool RegisterCustomTrigger(string triggerName, string eventName, string[] categories);
    void TriggerCodeEvent(string eventName, bool useArgs = true);
    void TriggerCodeEvent(string eventName, Dictionary<string, object> args);
    void TriggerCodeEvent(string eventName, string json);
    void WebsocketBroadcastJson(string data);
    void WebsocketBroadcastString(string data);

    // ── logging and utilities ────────────────────────────────────────────────
    void LogInfo(string logLine);
    void LogWarn(string logLine);
    void LogDebug(string logLine);
    void LogError(string logLine);
    void LogVerbose(string logLine);
    void Wait(int milliseconds);
    int Between(int min, int max);
    double NextDouble();
    double PlaySound(string fileName, float volume = 1f, bool finishBeforeContinuing = false, string? name = null, bool useFileName = true);
    double PlaySoundFromFolder(string path, float volume = 1f, bool recursive = false, bool finishBeforeContinuing = false, string? name = null, bool useFileName = true);

    // ── users, groups, quotes ────────────────────────────────────────────────
    bool AddUserToGroup(string userName, Platform platform, string groupName);
    bool RemoveUserFromGroup(string userName, Platform platform, string groupName);
    bool UserInGroup(string userName, Platform platform, string groupName);
    QuoteData? GetQuote(int quoteId);
    int AddQuoteForTwitch(string userId, string quote, bool captureGame = false);

    // ── Twitch ───────────────────────────────────────────────────────────────
    void SendMessage(string message, bool bot = true, bool fallback = true);
    /// <summary>Streamer.bot's "/me" message. Twitch's Helix chat API has no /me, so this sends the text as a normal message.</summary>
    void SendAction(string action, bool bot = true, bool fallback = true);
    void TwitchReplyToMessage(string message, string replyId, bool useBot = true, bool fallback = true);
    void TwitchAnnounce(string message, bool bot = false, string? color = null, bool fallback = false);
    bool TwitchTimeoutUser(string username, int duration, string? reason = null, bool bot = false);
    TwitchUserInfoEx? TwitchGetUserInfoByLogin(string userLogin);
    bool TwitchIsUserSubscribed(string userId, out string tier);
    bool TwitchRedemptionFulfill(string rewardId, string redemptionId);
    bool TwitchRedemptionCancel(string rewardId, string redemptionId);
    bool SetChannelTitle(string title);
    GameInfo? SetChannelGame(string game);
    StreamMarker? CreateStreamMarker(string description);

    // ── OBS ──────────────────────────────────────────────────────────────────
    bool ObsIsConnected(int connection = 0);
    string ObsGetCurrentScene(int connection = 0);
    void ObsSetScene(string sceneName, int connection = 0);
    void ObsSetSourceVisibility(string scene, string source, bool visible, int connection = 0);
    void ObsShowSource(string scene, string source, int connection = 0);
    void ObsHideSource(string scene, string source, int connection = 0);
    void ObsSetFilterState(string source, string filterName, int state, int connection = 0);
    void ObsSetFilterState(string scene, string source, string filterName, int state, int connection = 0);
    void ObsShowFilter(string source, string filterName, int connection = 0);
    void ObsShowFilter(string scene, string source, string filterName, int connection = 0);
    void ObsHideFilter(string source, string filterName, int connection = 0);
    void ObsHideFilter(string scene, string source, string filterName, int connection = 0);
    void ObsSetGdiText(string scene, string source, string text, int connection = 0);
    string ObsSendRaw(string requestType, string data, int connection = 0);
}
