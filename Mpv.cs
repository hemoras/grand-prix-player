using System.Globalization;
using System.Runtime.InteropServices;

namespace GrandPrixPlayer;

/// <summary>P/Invoke minimal vers libmpv-2.dll.</summary>
internal static class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    public const int FormatString = 1;
    public const int FormatFlag = 3;
    public const int FormatInt64 = 4;
    public const int FormatDouble = 5;

    public const int EventNone = 0;
    public const int EventShutdown = 1;
    public const int EventStartFile = 6;
    public const int EventEndFile = 7;
    public const int EventFileLoaded = 8;

    public const int EndFileReasonError = 4;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);
}

/// <summary>Une instance mpv (un "lecteur"). Toutes les méthodes s'appellent depuis le thread UI.</summary>
internal sealed class MpvPlayer : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private IntPtr _h;

    public bool FileLoaded { get; private set; }
    public Action? FileLoadedCallback { get; set; }
    public Action<string>? LoadFailedCallback { get; set; }

    public MpvPlayer(IReadOnlyDictionary<string, string> options)
    {
        _h = MpvNative.mpv_create();
        if (_h == IntPtr.Zero) throw new InvalidOperationException("mpv_create a échoué");
        foreach (var (k, v) in options)
            MpvNative.mpv_set_option_string(_h, k, v); // options inconnues ignorées volontairement
        int r = MpvNative.mpv_initialize(_h);
        if (r < 0)
        {
            MpvNative.mpv_terminate_destroy(_h);
            _h = IntPtr.Zero;
            throw new InvalidOperationException("mpv_initialize a échoué : " + ErrorString(r));
        }
    }

    public static string ErrorString(int code) =>
        Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(code)) ?? code.ToString(Inv);

    public int Command(params string[] args)
    {
        if (_h == IntPtr.Zero) return -1;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            return MpvNative.mpv_command(_h, ptrs);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    public void ResetLoaded() => FileLoaded = false;

    public void Set(string name, string value)
    {
        if (_h != IntPtr.Zero) MpvNative.mpv_set_property_string(_h, name, value);
    }

    public string? GetString(string name)
    {
        if (_h == IntPtr.Zero) return null;
        var p = MpvNative.mpv_get_property_string(_h, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double? GetDouble(string name)
    {
        if (_h == IntPtr.Zero) return null;
        return MpvNative.mpv_get_property(_h, name, MpvNative.FormatDouble, out double d) >= 0 ? d : null;
    }

    public long? GetLong(string name)
    {
        if (_h == IntPtr.Zero) return null;
        return MpvNative.mpv_get_property(_h, name, MpvNative.FormatInt64, out long d) >= 0 ? d : null;
    }

    public bool GetFlag(string name) => GetString(name) == "yes";

    public void SeekAbsolute(double t) =>
        Command("seek", t.ToString("0.###", Inv), "absolute+exact");

    public void ShowText(string text, int ms = 1500) =>
        Command("show-text", text, ms.ToString(Inv));

    /// <summary>Vide la file d'événements mpv (non bloquant).</summary>
    public void PumpEvents()
    {
        while (_h != IntPtr.Zero)
        {
            var ev = MpvNative.mpv_wait_event(_h, 0);
            if (ev == IntPtr.Zero) return;
            int id = Marshal.ReadInt32(ev);
            switch (id)
            {
                case MpvNative.EventNone:
                case MpvNative.EventShutdown:
                    return;
                case MpvNative.EventStartFile:
                    FileLoaded = false;
                    break;
                case MpvNative.EventFileLoaded:
                    FileLoaded = true;
                    FileLoadedCallback?.Invoke();
                    break;
                case MpvNative.EventEndFile:
                    // struct mpv_event { int event_id; int error; uint64 reply_userdata; void* data; }
                    var data = Marshal.ReadIntPtr(ev, 16);
                    if (data != IntPtr.Zero)
                    {
                        // struct mpv_event_end_file { int reason; int error; ... }
                        int reason = Marshal.ReadInt32(data);
                        int err = Marshal.ReadInt32(data, 4);
                        if (reason == MpvNative.EndFileReasonError)
                            LoadFailedCallback?.Invoke(ErrorString(err));
                    }
                    break;
            }
        }
    }

    public List<TrackInfo> ReadTracks()
    {
        var list = new List<TrackInfo>();
        long n = GetLong("track-list/count") ?? 0;
        for (int i = 0; i < n; i++)
        {
            string p = $"track-list/{i}/";
            list.Add(new TrackInfo
            {
                Type = GetString(p + "type") ?? "",
                Id = (int)(GetLong(p + "id") ?? 0),
                Title = GetString(p + "title"),
                Lang = GetString(p + "lang"),
                Codec = GetString(p + "codec"),
                Channels = GetLong(p + "demux-channel-count"),
                Width = GetLong(p + "demux-w"),
                Height = GetLong(p + "demux-h"),
                AlbumArt = GetString(p + "albumart") == "yes",
            });
        }
        return list;
    }

    public void Dispose()
    {
        var h = _h;
        _h = IntPtr.Zero;
        if (h != IntPtr.Zero) MpvNative.mpv_terminate_destroy(h);
    }
}

internal sealed class TrackInfo
{
    public int Id { get; init; }
    public string Type { get; init; } = "";
    public string? Title { get; init; }
    public string? Lang { get; init; }
    public string? Codec { get; init; }
    public long? Channels { get; init; }
    public long? Width { get; init; }
    public long? Height { get; init; }
    public bool AlbumArt { get; init; }

    public string Label =>
        !string.IsNullOrWhiteSpace(Title) ? Title!.Trim()
        : !string.IsNullOrWhiteSpace(Lang) ? Lang!.ToUpperInvariant()
        : Type == "audio" ? $"Piste audio {Id}" : $"Piste vidéo {Id}";

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Lang)) parts.Add(Lang!);
            if (!string.IsNullOrWhiteSpace(Codec)) parts.Add(Codec!);
            if (Type == "audio" && Channels is > 0) parts.Add(Channels switch { 1 => "mono", 2 => "stéréo", 6 => "5.1", 8 => "7.1", _ => $"{Channels} canaux" });
            if (Type == "video" && Width is > 0 && Height is > 0) parts.Add($"{Width}×{Height}");
            return string.Join(" · ", parts);
        }
    }
}
