using System;

namespace ElosWin.Models;

public class CaptureTargetItem
{
    public string Title { get; set; } = string.Empty;
    public IntPtr Hwnd { get; set; } = IntPtr.Zero;
    public bool IsFullScreen { get; set; }
    public string DisplaySubtitle => IsFullScreen ? "Tela inteira" : "Janela de aplicativo";
}