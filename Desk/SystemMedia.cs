using System;
using System.Runtime.InteropServices;

namespace desk;

/// <summary>
/// 系统级媒体与音量控制（供 Web 控制台“系统控制”调用）：
///   · 暂停 / 恢复其它正在播放的媒体（浏览器、播放器、Spotify 等）
///     —— 通过发送全局媒体键 VK_MEDIA_PLAY_PAUSE，与具体播放软件无关；
///   · 读取 / 设置系统主音量、静音 —— 通过 Core Audio (IAudioEndpointVolume)。
///
/// 所有方法都吞掉异常并尽量回退，绝不影响画板主流程。
/// </summary>
internal static class SystemMedia
{
    public static Action<string>? Log;

    // ---- 全局媒体键（与具体播放器无关，系统会把按键发给当前活动的媒体会话） ----
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_MUTE = 0xAD;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void SendMediaKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception ex) { Log?.Invoke("[SystemMedia] 发送媒体键失败：" + ex.Message); }
    }

    /// <summary>切换系统媒体播放 / 暂停（暂停正在播放的其它媒体，或恢复已暂停的）。</summary>
    public static void ToggleMediaPlayPause() => SendMediaKey(VK_MEDIA_PLAY_PAUSE);

    // ---- Core Audio 主音量 ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        // vtable 在 IUnknown 之后第一个方法即为 Activate
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IAudioEndpointVolume ppInterface);
    }

    // 必须严格按 endpointvolume.h 的 IAudioEndpointVolume vtable 顺序声明（IUnknown 之后）。
    // 旧声明漏掉 GetMasterVolumeLevel / GetChannelVolumeLevel 两个占位方法，导致后面的
    // GetMasterVolumeLevelScalar / SetMute / GetMute 全部错位调用到别的 COM 方法上：
    // 音量读成 dB 负值、静音无效，甚至以错误签名写入非法的 out 参数。
    // eventContext 在 SDK 中是 LPCGUID（指针），必须用 ref Guid，不能按值传 16 字节结构体。
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out int pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevel);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float level, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevel);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    private const int CLSCTX_ALL = 0x17; // INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER
    private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static Guid _eventContext = Guid.Empty;   // 只读使用，多线程共享安全

    /// <summary>对默认渲染设备的音量接口执行操作；失败返回 false（调用方据此回退）。
    /// 每个 COM 方法都显式检查 HRESULT，避免失败被当成成功。</summary>
    private static bool WithVolume(Func<IAudioEndpointVolume, bool> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? vol = null;
        try
        {
            enumerator = new MMDeviceEnumeratorCom() as IMMDeviceEnumerator;
            if (enumerator == null) return false;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device);
            if (hr < 0 || device == null) return false;
            Guid iid = IID_IAudioEndpointVolume;
            hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out vol);
            if (hr < 0 || vol == null) return false;
            return action(vol);
        }
        catch (Exception ex)
        {
            Log?.Invoke("[SystemMedia] 音频操作失败：" + ex.Message);
            return false;
        }
        finally
        {
            if (vol != null) try { Marshal.ReleaseComObject(vol); } catch { }
            if (device != null) try { Marshal.ReleaseComObject(device); } catch { }
            if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch { }
        }
    }

    private static bool Succeeded(int hr) => hr >= 0;

    /// <summary>返回 (主音量百分比 0..100, 是否静音)；音频 API 不可用时音量返回 -1。</summary>
    public static (int volumePercent, bool muted) GetState()
    {
        int pct = -1;
        bool muted = false;
        bool ok = WithVolume(v =>
        {
            if (!Succeeded(v.GetMasterVolumeLevelScalar(out float level))) return false;
            if (!Succeeded(v.GetMute(out bool m))) return false;
            muted = m;
            pct = Math.Max(0, Math.Min(100, (int)Math.Round(level * 100.0)));
            return true;
        });
        return (ok ? pct : -1, muted);
    }

    /// <summary>设置系统主音量（0..100）。</summary>
    public static void SetVolumePercent(int percent)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        WithVolume(v => Succeeded(v.SetMasterVolumeLevelScalar(percent / 100.0f, ref _eventContext)));
    }

    /// <summary>在当前音量基础上增减 deltaPercent（如 +5 / -5）。音频 API 不可用时回退到硬件音量键。</summary>
    public static void AdjustVolume(int deltaPercent)
    {
        bool ok = WithVolume(v =>
        {
            if (!Succeeded(v.GetMasterVolumeLevelScalar(out float level))) return false;
            int next = Math.Max(0, Math.Min(100, (int)Math.Round(level * 100.0) + deltaPercent));
            return Succeeded(v.SetMasterVolumeLevelScalar(next / 100.0f, ref _eventContext));
        });
        if (!ok) SendMediaKey(deltaPercent >= 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN);
    }

    /// <summary>设置静音 / 取消静音。</summary>
    public static void SetMute(bool mute)
    {
        WithVolume(v => Succeeded(v.SetMute(mute, ref _eventContext)));
    }

    /// <summary>切换静音状态。音频 API 不可用时回退到硬件静音键。</summary>
    public static void ToggleMute()
    {
        bool ok = WithVolume(v =>
        {
            if (!Succeeded(v.GetMute(out bool m))) return false;
            return Succeeded(v.SetMute(!m, ref _eventContext));
        });
        if (!ok) SendMediaKey(VK_VOLUME_MUTE);
    }
}
