using System;

namespace ElosWin.Models;

public class CaptureTargetItem
{
    public string Title { get; set; } = string.Empty;
    public string Name => Title;
    public IntPtr Hwnd { get; set; } = IntPtr.Zero;
    public bool IsFullScreen { get; set; }
    public string DisplaySubtitle => IsFullScreen ? "Tela inteira" : "Janela de aplicativo";

    public override string ToString() => Title;
}