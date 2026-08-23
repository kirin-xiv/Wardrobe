using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Vestiary.Services;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Vestiary.Windows;

/// <summary>
/// Camera overlay with a 4:5 movable, resizable viewfinder.
/// Hold SHIFT to temporarily hide the overlay and rotate the camera with right-click.
/// </summary>
public class CameraWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly UtilityService utility;
    private Action<string>? onImageCaptured;
    private bool isActive;

    private Vector2 framePos;
    private Vector2 frameSize;
    private bool isDragging;
    private Vector2 dragOffset;
    private int resizeCorner = -1;
    private Vector2 resizeAnchor;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_SHIFT = 0x10;
    private bool ShiftHeld => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    private const float HandleR = 14f, MinW = 120f, MinH = 150f;
    private const float Ratio = 4f / 5f, Inset = 8f;

    public CameraWindow(Plugin plugin, UtilityService utility) : base("Vestiary Camera##CameraOverlay")
    {
        this.plugin = plugin;
        this.utility = utility;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoBackground;
        IsOpen = false;
        RespectCloseHotkey = false;
    }

    public void Open(Action<string> callback)
    {
        onImageCaptured = callback;
        isActive = true; IsOpen = true;
        isDragging = false; resizeCorner = -1;
        var vp = ImGui.GetMainViewport();
        float h = vp.Size.Y * 0.6f;
        frameSize = new Vector2(h * Ratio, h);
        framePos = vp.Pos + (vp.Size - frameSize) / 2f;
    }

    public override void PreDraw()
    {
        // Hold Shift → hide overlay so user can rotate camera
        if (ShiftHeld)
        {
            Position = new Vector2(-9999, -9999);
            Size = new Vector2(1, 1);
            PositionCondition = ImGuiCond.Always;
            return;
        }

        var vp = ImGui.GetMainViewport();
        Position = vp.Pos;
        Size = vp.Size;
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        if (!isActive || ShiftHeld) return;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var vp = ImGui.GetMainViewport();
        var dl = ImGui.GetWindowDrawList();

        // ── Corner detection ──
        static bool Near(Vector2 c, Vector2 m, float r) =>
            Math.Abs(m.X - c.X) <= r && Math.Abs(m.Y - c.Y) <= r;

        int hovered = -1;
        if (!isDragging && resizeCorner == -1)
        {
            Vector2[] crn = { framePos,
                new(framePos.X + frameSize.X, framePos.Y),
                new(framePos.X, framePos.Y + frameSize.Y), framePos + frameSize };
            for (int i = 0; i < 4; i++)
                if (Near(crn[i], mouse, HandleR)) { hovered = i; break; }
        }

        // ── Drag / resize ──
        if (resizeCorner == -1 && !isDragging && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hovered >= 0) { resizeCorner = hovered; resizeAnchor = Opposite(hovered); }
            else if (InDragZone(mouse)) { isDragging = true; dragOffset = framePos - mouse; }
        }
        if (resizeCorner >= 0)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) ResizeFrom(resizeAnchor, mouse, vp);
            else resizeCorner = -1;
        }
        if (isDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) framePos = mouse + dragOffset;
            else isDragging = false;
        }

        // Clamp
        framePos.X = Math.Clamp(framePos.X, vp.Pos.X + 10, vp.Pos.X + vp.Size.X - frameSize.X - 10);
        framePos.Y = Math.Clamp(framePos.Y, vp.Pos.Y + 35, Math.Max(vp.Pos.Y + 35, vp.Pos.Y + vp.Size.Y - frameSize.Y - 80));

        // Cursor
        if (resizeCorner >= 0 || hovered >= 0 || isDragging || InDragZone(mouse))
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

        // ═══════════ VIGNETTE ═══════════
        uint vig = ImGui.GetColorU32(ThemeManager.Current.CameraVignette);
        dl.AddRectFilled(vp.Pos, new(vp.Pos.X + vp.Size.X, framePos.Y), vig);
        dl.AddRectFilled(new(vp.Pos.X, framePos.Y + frameSize.Y), new(vp.Pos.X + vp.Size.X, vp.Pos.Y + vp.Size.Y), vig);
        dl.AddRectFilled(new(vp.Pos.X, framePos.Y), new(framePos.X, framePos.Y + frameSize.Y), vig);
        dl.AddRectFilled(new(framePos.X + frameSize.X, framePos.Y), new(vp.Pos.X + vp.Size.X, framePos.Y + frameSize.Y), vig);

        // ═══════════ FRAME ═══════════
        dl.AddRect(framePos, framePos + frameSize,
            ImGui.GetColorU32(ThemeManager.Current.CameraBorder), 0f, 0, 1.5f);

        Vector2 ip = framePos + new Vector2(Inset);
        dl.AddRect(ip, framePos + frameSize - new Vector2(Inset),
            ImGui.GetColorU32(ThemeManager.Current.CameraGrid), 2f, 0, 1f);

        // Corner dots
        bool hov = hovered >= 0 || resizeCorner >= 0;
        uint dCol = ImGui.GetColorU32(hov
            ? ThemeManager.Current.CameraTextHov : ThemeManager.Current.CameraText);
        float dR = hov ? 6f : 5f;
        void D(Vector2 c) => dl.AddCircleFilled(c, dR, dCol, 8);
        D(framePos); D(new(framePos.X + frameSize.X, framePos.Y));
        D(new(framePos.X, framePos.Y + frameSize.Y)); D(framePos + frameSize);

        // ── Hint text (single line, centered) ──
        uint hintCol = ImGui.GetColorU32(ThemeManager.Current.CameraText);

        string t;
        if (resizeCorner >= 0)
            t = Strings.CameraDimensions(frameSize.X - Inset * 2, frameSize.Y - Inset * 2);
        else if (isDragging)
            t = Strings.CameraReleaseToPlace;
        else
            t = Strings.CameraHint;

        var ts = ImGui.CalcTextSize(t);
        dl.AddText(new(framePos.X + (frameSize.X - ts.X) / 2f, framePos.Y - 22f), hintCol, t);

        // ═══════════ BUTTONS ═══════════
        const float bw = 130f, bh = 36f, gap = 20f;
        float totalW = bw * 2 + gap;
        float btnX = framePos.X + (frameSize.X - totalW) / 2f;
        float btnY = framePos.Y + frameSize.Y + 15f;
        ImGui.SetCursorScreenPos(new Vector2(btnX, btnY));

        ImGui.PushStyleColor(ImGuiCol.Button, ThemeManager.Current.CamCaptureBtn);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ThemeManager.Current.CamCaptureHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ThemeManager.Current.CamCaptureAct);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        if (ImGui.Button(Strings.CameraCapture, new Vector2(bw, bh))) Capture();
        ImGui.PopStyleVar(); ImGui.PopStyleColor(3);

        ImGui.SameLine(0, gap);
        ImGui.PushStyleColor(ImGuiCol.Button, ThemeManager.Current.CamCancelBtn);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ThemeManager.Current.CamCancelHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ThemeManager.Current.CamCancelAct);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        if (ImGui.Button(Strings.CameraCancel, new Vector2(bw, bh))) Close();
        ImGui.PopStyleVar(); ImGui.PopStyleColor(3);

        // Keyboard
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) Close();
        if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) Capture();
        float nudge = io.KeyShift ? 10f : 1f;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) framePos.X -= nudge;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) framePos.X += nudge;
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) framePos.Y -= nudge;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) framePos.Y += nudge;
    }

    bool InDragZone(Vector2 m) => m.X >= framePos.X - 10 && m.X <= framePos.X + frameSize.X + 10
        && m.Y >= framePos.Y - 10 && m.Y <= framePos.Y + frameSize.Y + 10;

    Vector2 Opposite(int i) => i switch
    {
        0 => framePos + frameSize, 1 => new(framePos.X, framePos.Y + frameSize.Y),
        2 => new(framePos.X + frameSize.X, framePos.Y), _ => framePos
    };

    void ResizeFrom(Vector2 anchor, Vector2 mouse, ImGuiViewportPtr vp)
    {
        float rw = Math.Abs(mouse.X - anchor.X), rh = Math.Abs(mouse.Y - anchor.Y);
        float nw, nh;
        if (rw / rh > Ratio) { nh = rh; nw = nh * Ratio; }
        else { nw = rw; nh = nw / Ratio; }
        nw = Math.Clamp(nw, MinW, vp.Size.X - 20f);
        nh = Math.Clamp(nh, MinH, vp.Size.Y - 115f);
        if (nw / nh > Ratio) nh = nw / Ratio; else nw = nh * Ratio;
        // Re-clamp: ratio adjustment can push dimensions past bounds
        nw = Math.Clamp(nw, MinW, vp.Size.X - 20f);
        nh = Math.Clamp(nh, MinH, vp.Size.Y - 115f);
        framePos = new Vector2(mouse.X > anchor.X ? anchor.X : anchor.X - nw,
                               mouse.Y > anchor.Y ? anchor.Y : anchor.Y - nh);
        frameSize = new Vector2(nw, nh);
    }

    void Capture()
    {
        try
        {
            int sx = (int)(framePos.X + Inset), sy = (int)(framePos.Y + Inset);
            int sw = (int)(frameSize.X - Inset * 2), sh = (int)(frameSize.Y - Inset * 2);
            if (sw <= 0 || sh <= 0)
                return;

            // Read the game's DX11 swap-chain back buffer directly. Unlike GDI
            // CopyFromScreen, this works on Linux/Wine + DXVK where the frame is
            // rendered through Vulkan and the X11 desktop read returns black.
            if (!TryCaptureRegion(sx, sy, sw, sh, out var pixels, out var width, out var height))
            {
                Plugin.Log.Warning("[Camera] capture failed (swap chain unavailable)");
                return;
            }

            var dir = utility.ThumbnailsDirectory;
            Directory.CreateDirectory(dir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var tempPath = Path.Combine(dir, $"camera_{timestamp}_temp.png");
            var destBase = Path.Combine(dir, $"camera_{timestamp}");
            var finalPath = destBase + ".jpg";

            var cb = onImageCaptured;
            Close();

            // Encode/resize off the game thread. The GPU readback above must stay
            // on the render thread, but the PNG/JPEG encoding is the expensive part
            // that was hitching the frame; do it on a worker thread and marshal the
            // callback back to the framework thread once the file is ready.
            Task.Run(() =>
            {
                try
                {
                    SavePixelsAsPng(pixels, width, height, tempPath);
                    utility.ResizeThumbnail(tempPath, destBase);
                    try { File.Delete(tempPath); } catch { }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Capture save failed");
                    return;
                }

                Plugin.Framework.RunOnFrameworkThread(() => cb?.Invoke(finalPath));
            });
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Capture failed"); }
    }

    private static void SavePixelsAsPng(byte[] pixels, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width * 4, IntPtr.Add(data.Scan0, y * stride), width * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Captures a region of the game's DX11 back buffer into a BGRA byte array
    /// (System.Drawing Format32bppArgb order). Mirrors Aetherphone's
    /// PhotoCaptureService so snapshots work on Windows and Linux alike.
    /// </summary>
    private static unsafe bool TryCaptureRegion(
        int left, int top, int width, int height,
        out byte[] pixels, out int outWidth, out int outHeight)
    {
        pixels = Array.Empty<byte>();
        outWidth = 0;
        outHeight = 0;

        var device = KernelDevice.Instance();
        if (device == null || device->SwapChain == null)
            return false;

        var swapChainPtr = (nint)device->SwapChain->DXGISwapChain;
        if (swapChainPtr == 0)
            return false;

        using var swapChain = new IDXGISwapChain(swapChainPtr);
        swapChain.AddRef();
        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var sourceDesc = backBuffer.Description;
        if (!IsSupported(sourceDesc.Format))
            return false;

        int right = Math.Clamp(left + width, 0, (int)sourceDesc.Width);
        int bottom = Math.Clamp(top + height, 0, (int)sourceDesc.Height);
        left = Math.Clamp(left, 0, (int)sourceDesc.Width);
        top = Math.Clamp(top, 0, (int)sourceDesc.Height);
        int regionWidth = right - left;
        int regionHeight = bottom - top;
        if (regionWidth <= 0 || regionHeight <= 0)
            return false;

        using var d3dDevice = backBuffer.Device;
        using var context = d3dDevice.ImmediateContext;

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)regionWidth,
            Height = (uint)regionHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = sourceDesc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = d3dDevice.CreateTexture2D(stagingDesc);
        var sourceBox = new Box(left, top, 0, right, bottom, 1);
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, backBuffer, 0, sourceBox);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            pixels = ReadPixels(mapped, regionWidth, regionHeight, IsRgba(sourceDesc.Format));
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        outWidth = regionWidth;
        outHeight = regionHeight;
        return true;
    }

    private static byte[] ReadPixels(MappedSubresource mapped, int width, int height, bool sourceIsRgba)
    {
        var result = new byte[width * height * 4];
        var rowBuffer = new byte[width * 4];
        for (int row = 0; row < height; row++)
        {
            Marshal.Copy(IntPtr.Add(mapped.DataPointer, row * (int)mapped.RowPitch), rowBuffer, 0, rowBuffer.Length);
            int destinationOffset = row * width * 4;
            for (int column = 0; column < width; column++)
            {
                int index = column * 4;
                if (sourceIsRgba)
                {
                    // source R,G,B,A -> target B,G,R,A
                    result[destinationOffset + index + 0] = rowBuffer[index + 2];
                    result[destinationOffset + index + 1] = rowBuffer[index + 1];
                    result[destinationOffset + index + 2] = rowBuffer[index + 0];
                }
                else
                {
                    // source B,G,R,A -> target B,G,R,A (already in the right order)
                    result[destinationOffset + index + 0] = rowBuffer[index + 0];
                    result[destinationOffset + index + 1] = rowBuffer[index + 1];
                    result[destinationOffset + index + 2] = rowBuffer[index + 2];
                }

                result[destinationOffset + index + 3] = 255;
            }
        }

        return result;
    }

    private static bool IsSupported(Format format) =>
        IsBgra(format) || format == Format.R8G8B8A8_UNorm || format == Format.R8G8B8A8_UNorm_SRgb;

    private static bool IsBgra(Format format) =>
        format == Format.B8G8R8A8_UNorm || format == Format.B8G8R8A8_UNorm_SRgb;

    private static bool IsRgba(Format format) =>
        format == Format.R8G8B8A8_UNorm || format == Format.R8G8B8A8_UNorm_SRgb;

    void Close()
    {
        isActive = false; IsOpen = false; onImageCaptured = null;
        isDragging = false; resizeCorner = -1;
        plugin.OnCameraClosed();
    }

    public void Dispose() { }
}
