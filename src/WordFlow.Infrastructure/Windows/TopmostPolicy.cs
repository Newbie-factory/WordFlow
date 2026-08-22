using System.Runtime.InteropServices;

namespace WordFlow.Infrastructure.Windows;

public interface IWindowZOrder
{
    void SetTopmost(nint hwnd, bool noActivate);
    void SetNotTopmost(nint hwnd, bool noActivate);
}

public sealed class TopmostPolicy : IWindowZOrder
{
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private readonly ISetWindowPosNative native;

    public TopmostPolicy() : this(new SetWindowPosNative()) { }

    internal TopmostPolicy(ISetWindowPosNative native) =>
        this.native = native ?? throw new ArgumentNullException(nameof(native));

    public void SetTopmost(nint hwnd, bool noActivate) => Apply(hwnd, HwndTopmost, noActivate);

    public void SetNotTopmost(nint hwnd, bool noActivate) => Apply(hwnd, HwndNotTopmost, noActivate);

    private void Apply(nint hwnd, nint insertAfter, bool noActivate)
    {
        if (hwnd == 0) return;
        uint flags = NoMove | NoSize;
        if (noActivate) flags |= NoActivate;
        _ = native.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
    }
}

internal interface ISetWindowPosNative
{
    bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}

internal sealed class SetWindowPosNative : ISetWindowPosNative
{
    public bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags) =>
        NativeMethods.SetWindowPos(hwnd, insertAfter, x, y, width, height, flags);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            nint hwnd,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
