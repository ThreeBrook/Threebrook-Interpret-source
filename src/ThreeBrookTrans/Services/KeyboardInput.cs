using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ThreeBrookTrans.Services;

/// <summary>模拟按键（划词时发送 Ctrl+C、剪贴板历史选中后发送 Ctrl+V）。</summary>
public static class KeyboardInput
{
    public const ushort VK_C = 0x43, VK_V = 0x56;
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private static readonly ushort[] Modifiers = { VK_MENU, VK_SHIFT, VK_CONTROL, VK_LWIN, VK_RWIN };

    /// <summary>
    /// 用户还按着快捷键的修饰键（如 Alt）时直接发 Ctrl+C，目标程序收到的会是 Ctrl+Alt+C。
    /// 先等用户松开，超时仍按着就主动发送抬起事件。
    /// </summary>
    public static async Task ReleaseModifiersAsync()
    {
        for (var i = 0; i < 15 && Modifiers.Any(IsDown); i++)
            await Task.Delay(20);

        var held = Modifiers.Where(IsDown).ToArray();
        if (held.Length > 0)
            Send(held.Select(vk => KeyInput(vk, up: true)).ToArray());
    }

    public static void SendCtrl(ushort key) => Send(new[]
    {
        KeyInput(VK_CONTROL, up: false),
        KeyInput(key, up: false),
        KeyInput(key, up: true),
        KeyInput(VK_CONTROL, up: true),
    });

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x0002u : 0u } },
    };

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
