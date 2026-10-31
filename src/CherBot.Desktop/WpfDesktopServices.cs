using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CherBot.Core.Platform;
using WinForms = System.Windows.Forms;

namespace CherBot.Desktop;

/// <summary>Real implementations of the desktop-only sub-actions for Local mode: Toast Notification, Get Input and Keyboard Press.</summary>
public sealed class WpfDesktopServices : IDesktopServices
{
    public Func<string, string, Task>? ShowBalloon { get; set; }

    public bool IsAvailable => true;

    public Task ShowToastAsync(string title, string text, string? iconPath, CancellationToken ct) =>
        ShowBalloon?.Invoke(title, text) ?? Task.CompletedTask;

    public async Task<string?> GetInputAsync(string title, string prompt, string defaultText, CancellationToken ct) =>
        await Application.Current.Dispatcher.InvokeAsync(() => InputDialog.Ask(title, prompt, defaultText));

    public Task KeyPressAsync(JsonElement? key, JsonElement? modifiers, CancellationToken ct)
    {
        var mods = ParseKeys(modifiers);
        var keys = ParseKeys(key);
        if (keys.Count == 0) return Task.CompletedTask;
        foreach (var m in mods) Send(m, up: false);
        foreach (var k in keys)
        {
            Send(k, up: false);
            Send(k, up: true);
        }
        for (var i = mods.Count - 1; i >= 0; i--) Send(mods[i], up: true);
        return Task.CompletedTask;
    }

    /// <summary>Accepts a virtual-key number, a key name ("F13", "Control"), an array of either, or an object with a key/value field.</summary>
    internal static List<byte> ParseKeys(JsonElement? element)
    {
        var result = new List<byte>();
        if (element is not { } e) return result;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number when e.TryGetInt32(out var vk) && vk is > 0 and < 256:
                result.Add((byte)vk);
                break;
            case JsonValueKind.String:
                foreach (var part in (e.GetString() ?? "").Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Enum.TryParse<WinForms.Keys>(part, true, out var k) && (int)(k & WinForms.Keys.KeyCode) is > 0 and < 256)
                        result.Add((byte)(k & WinForms.Keys.KeyCode));
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) result.AddRange(ParseKeys(item));
                break;
            case JsonValueKind.Object:
                foreach (var name in new[] { "key", "keyCode", "virtualKey", "value", "Key" })
                    if (e.TryGetProperty(name, out var inner)) { result.AddRange(ParseKeys(inner)); break; }
                break;
        }
        return result;
    }

    private const uint KeyEventKeyUp = 0x0002;

    private static void Send(byte vk, bool up) => keybd_event(vk, (byte)MapVirtualKey(vk, 0), up ? KeyEventKeyUp : 0, UIntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
