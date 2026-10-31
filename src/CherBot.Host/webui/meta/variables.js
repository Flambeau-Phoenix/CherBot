// Arguments each event provides to the actions it triggers, with a short description and an example.
// Pure data (also loaded by the Node coverage test). The variable helper merges these with the live
// keys of the latest real event (/v1/events/sample).

const v = (name, desc, example) => ({ name, desc, example });

const USER = [
  v('user', 'Display name', 'CoolViewer'),
  v('userName', 'Login name (lower case)', 'coolviewer'),
  v('userId', 'Twitch user id', '123456789'),
  v('userType', 'Platform', 'twitch'),
];
const CHAT = [
  ...USER,
  v('message', 'The full message', 'hello chat!'),
  v('messageStripped', 'The message without emotes', 'hello chat!'),
  v('rawInput', 'The full message (or text after the command)', 'hello chat!'),
  v('msgId', 'Message id (for replies and deletes)', 'abc-123'),
  v('emotes', 'Emote names used', '[Kappa]'),
  v('emoteCount', 'How many emotes', '1'),
  v('isSubscribed', 'Subscriber?', 'True'),
  v('isModerator', 'Moderator?', 'False'),
  v('isVip', 'VIP?', 'False'),
  v('isBroadcaster', 'The broadcaster?', 'False'),
  v('role', '1 viewer, 2 VIP, 3 mod, 4 broadcaster', '1'),
  v('color', 'Chat colour', '#FF0000'),
  v('badges', 'Badge set ids', '[subscriber]'),
  v('firstWordOfStream', 'Their first message this stream?', 'False'),
  v('isReply', 'A reply to another message?', 'False'),
  v('bits', 'Bits cheered in the message', '0'),
];
const COMMAND = [
  ...CHAT,
  v('command', 'The command word that matched', '!so'),
  v('commandId', 'Command id', ''),
  v('commandName', 'Command name', '!so'),
  v('rawInput', 'Text after the command', '@friend thanks'),
  v('input0', 'First word after the command', '@friend'),
  v('input1', 'Second word after the command', 'thanks'),
  v('inputEscaped0', 'input0, HTML-escaped', '@friend'),
  v('inputUrlEncoded0', 'input0, URL-encoded', '%40friend'),
  v('counter', 'Times the command has run', '42'),
  v('userCounter', 'Times this user has run it', '3'),
];
const TIER = [v('tier', 'tier 1, tier 2, tier 3 or prime', 'tier 1'), v('subTier', '1000, 2000 or 3000', '1000')];
const OBS = [v('obsId', 'OBS connection id', ''), v('connectionId', 'OBS connection id', ''), v('obsConnectionName', 'OBS connection name', 'OBS')];
const GLOBAL = [v('variableName', 'Variable name', 'deaths'), v('name', 'Variable name', 'deaths'), v('newValue', 'New value', '5'), v('oldValue', 'Previous value', '4'), v('persisted', 'Persisted?', 'True')];

/** Variables per event type id. */
export const eventVariables = {
  1: [v('eventName', 'Custom event name', 'myEvent')],
  101: [...USER, v('followDate', 'When they followed', '2026-01-01T00:00:00Z')],
  102: [...USER, v('bits', 'Bits cheered', '500'), v('message', 'Cheer message', 'Cheer500 gg'), v('messageStripped', 'Message without cheermotes', 'gg'), v('anonymous', 'Anonymous cheer?', 'False')],
  103: [...USER, ...TIER, v('isPrime', 'Prime sub?', 'False'), v('isMultiMonth', 'Multi-month?', 'False'), v('multiMonthDuration', 'Months bought', '1'), v('message', 'Message', ''), v('systemMessage', 'Twitch system text', '')],
  104: [...USER, ...TIER, v('cumulativeMonths', 'Total months', '12'), v('monthStreak', 'Streak months (if shared)', '6'), v('streakShared', 'Streak shared?', 'True'), v('isPrime', 'Prime?', 'False'), v('rawInput', 'Resub message', 'still here!'), v('message', 'Resub message', 'still here!')],
  105: [...USER, ...TIER, v('recipientUser', 'Recipient display name', 'LuckyViewer'), v('recipientUserName', 'Recipient login', 'luckyviewer'), v('recipientId', 'Recipient id', ''), v('monthsGifted', 'Months gifted', '1'), v('totalSubsGifted', 'Gifter\'s total', '25'), v('fromGiftBomb', 'Part of a gift bomb?', 'False'), v('anonymous', 'Anonymous gifter?', 'False')],
  106: [...USER, ...TIER, v('gifts', 'Subs in this bomb', '10'), v('totalGifts', 'Gifter\'s total', '50'), v('anonymous', 'Anonymous gifter?', 'False')],
  107: [...USER, v('viewers', 'Raiding viewers', '42')],
  112: [...USER, v('rewardId', 'Reward id', ''), v('rewardName', 'Reward title', 'Hydrate'), v('rewardCost', 'Cost', '500'), v('rewardPrompt', 'Reward prompt', ''), v('redemptionId', 'Redemption id', ''), v('rawInput', 'Text the viewer entered', ''), v('counter', 'Times redeemed', '7'), v('userCounter', 'Times this user redeemed', '1')],
  118: [v('title', 'Stream title', 'Chill stream'), v('status', 'Stream title', 'Chill stream'), v('gameId', 'Category id', '509658'), v('gameName', 'Category', 'Just Chatting'), v('language', 'Language', 'en')],
  120: CHAT,
  124: [v('users', 'Viewers (id, login, display, role, subscribed)', '[…]'), v('count', 'How many', '12')],
  133: CHAT,
  135: [v('targetMessageId', 'Deleted message id', ''), v('msgId', 'Deleted message id', ''), v('targetUser', 'Author display name', ''), v('targetUserName', 'Author login', ''), v('targetUserId', 'Author id', '')],
  138: [...CHAT, v('announcementColor', 'Announcement colour', 'PRIMARY')],
  139: [v('adLength', 'Ad length (seconds)', '90'), v('isAutomatic', 'Scheduled ad?', 'True'), v('startedAt', 'Start time', '')],
  144: [...USER, v('viewerCount', 'Viewers shown the shoutout', '100')],
  154: [v('broadcastUser', 'Your display name', 'Streamer'), v('broadcastUserName', 'Your login', 'streamer'), v('broadcastUserId', 'Your id', ''), v('startedAt', 'Went live at', ''), v('type', 'live', 'live')],
  155: [v('broadcastUserName', 'Your login', 'streamer'), v('broadcastUserId', 'Your id', '')],
  156: [...USER, v('viewers', 'Their viewer count', '1200'), v('viewerCount', 'Their viewer count', '1200')],
  173: [v('broadcastUser', 'Linked broadcaster', 'Streamer')],
  174: [],
  175: [],
  401: COMMAND,
  402: [...COMMAND, v('cooldownLeft', 'Seconds left (largest)', '12'), v('globalCooldownLeft', 'Global seconds left', '0'), v('userCooldownLeft', 'User seconds left', '12')],
  601: [v('quoteId', 'Quote id', '12'), v('quote', 'Quote text', 'a classic'), v('quoteUser', 'Added by', '')],
  602: [v('quoteId', 'Quote id', '12'), v('quote', 'Quote text', 'a classic')],
  701: [v('timerName', 'Timer name', 'Promo'), v('timerId', 'Timer id', ''), v('timerInterval', 'Interval (seconds)', '900')],
  702: [v('isTest', 'Always true', 'True')],
  706: [],
  707: [],
  709: GLOBAL,
  710: [...GLOBAL, v('userId', 'User id', ''), v('userName', 'User login', ''), v('user', 'User display name', ''), v('platform', 'Platform', 'twitch')],
  711: GLOBAL,
  712: GLOBAL,
  801: [v('actionName', 'Action that started', ''), v('actionId', 'Its id', '')],
  806: [v('actionName', 'Action that finished', ''), v('actionId', 'Its id', ''), v('status', 'ok, error or break', 'ok')],
  14001: OBS,
  14002: OBS,
  14003: [...OBS, v('eventName', 'obs-websocket event type', 'InputMuteStateChanged'), v('eventType', 'Same as eventName', ''), v('eventData', 'Event data (JSON)', '{}')],
  14004: [...OBS, v('sceneName', 'New scene', 'Gameplay'), v('oldSceneName', 'Previous scene', 'Starting')],
  14005: OBS, 14006: OBS, 14007: OBS, 14008: OBS,
  14009: [...OBS, v('vendorName', 'Plugin (vendor) name', 'move'), v('eventName', 'Vendor event type', 'MoveEnded'), v('eventData', 'Event data (JSON)', '{}')],
  18001: [v('eventName', 'Custom event name', 'giveaway.winner'), v('args', 'Arguments sent with it', '{…}'),
    v('giveawayName', 'Giveaway events: giveaway name', 'Steam key'), v('prize', 'Giveaway events: prize', ''), v('entrants', 'Giveaway events: entrants', '12'),
    v('emote', 'Pyramid events: the emote', 'Kappa'), v('width', 'Pyramid events: width', '3'), v('breaker', 'pyramid.broken: who broke it', ''),
    v('filterName', 'chat.filtered: which filter', 'links'), v('filterReason', 'chat.filtered: the reason', '')],
  18002: [v('name', 'Code trigger name', ''), v('eventName', 'Event name', '')],
  23001: [], 23002: [],
};

/** Arguments every run has. */
export const alwaysAvailable = [
  v('actionName', 'This action\'s name', 'My action'),
  v('actionId', 'This action\'s id', ''),
  v('runningActionId', 'Id of this run', ''),
  v('actionQueuedAt', 'When the run was queued', ''),
  v('triggerName', 'What started the run', 'Command Triggered'),
  v('triggerCategory', 'Trigger category', 'Core / Commands'),
  v('triggerId', 'Trigger id', ''),
  v('eventSource', 'twitch, obs, command, core…', 'twitch'),
  v('__source', 'Internal event name', 'CommandTriggered'),
  v('isTest', 'Set when run from the builder\'s Test', 'True'),
];

/** Format examples for the helper's last group. */
export const formats = [
  v('%count:N0%', 'Number with thousands separators', '1,234'),
  v('%price:0.00%', 'Two decimals', '3.50'),
  v('%actionQueuedAt:HH:mm%', 'Time of day', '21:07'),
  v('%actionQueuedAt:yyyy-MM-dd%', 'Date', '2026-10-31'),
  v('~myGlobal~', 'A persisted global variable', ''),
];
