using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Minecraft;

public sealed class MinecraftWindowPlacementService
{
    private const int SchemaVersion = 1;
    private const int WindowStyleIndex = -16;
    private const uint GetOwner = 4;
    private const uint ShowNormal = 1;
    private const uint ShowMaximized = 3;
    private const uint RestoreToMaximized = 0x0002;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const long WindowStyleCaption = 0x00C00000L;
    private const long WindowStyleThickFrame = 0x00040000L;
    private const long WindowStylePopup = 0x80000000L;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly string _placementFile;
    private readonly Logger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public MinecraftWindowPlacementService(AppPaths paths, Logger logger)
    {
        _placementFile = paths.MinecraftWindowPlacementFile;
        _logger = logger;
    }

    public async Task TrackAsync(int processId, CancellationToken token)
    {
        var saved = TryRead();
        SavedMinecraftWindowPlacement? latest = null;
        var lastPersisted = saved;
        DateTimeOffset? changedAt = null;
        var restoredWindows = new HashSet<nint>();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var window = FindMinecraftWindow(processId);
                if (window != nint.Zero)
                {
                    if (saved is not null &&
                        !restoredWindows.Contains(window) &&
                        IsWindowed(window) &&
                        Apply(window, saved))
                    {
                        restoredWindows.Add(window);
                    }

                    var current = Capture(window);
                    if (current is not null)
                    {
                        if (!Equivalent(current, latest))
                        {
                            latest = current;
                            changedAt = DateTimeOffset.UtcNow;
                        }
                        else if (changedAt is not null && DateTimeOffset.UtcNow - changedAt >= SaveDelay)
                        {
                            if (!Equivalent(current, lastPersisted) && TryWrite(current))
                            {
                                lastPersisted = current;
                            }
                            changedAt = null;
                        }
                    }
                }

                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not monitor the Minecraft window placement: {ex.Message}");
        }
        finally
        {
            if (latest is not null && !Equivalent(latest, lastPersisted))
            {
                TryWrite(latest);
            }
        }
    }

    private bool Apply(nint window, SavedMinecraftWindowPlacement saved)
    {
        var bounds = ClampToNearestWorkArea(new NativeRect
        {
            Left = saved.Left,
            Top = saved.Top,
            Right = saved.Right,
            Bottom = saved.Bottom
        });
        if (!IsValid(bounds))
        {
            return false;
        }

        var placement = WindowPlacement.Create();
        if (!GetWindowPlacement(window, ref placement))
        {
            return false;
        }

        placement.Flags = 0;
        placement.ShowCommand = saved.Maximized ? ShowMaximized : ShowNormal;
        placement.NormalPosition = bounds;
        return SetWindowPlacement(window, ref placement);
    }

    private static SavedMinecraftWindowPlacement? Capture(nint window)
    {
        if (!IsWindowed(window))
        {
            return null;
        }

        var placement = WindowPlacement.Create();
        if (!GetWindowPlacement(window, ref placement) || !IsValid(placement.NormalPosition))
        {
            return null;
        }

        var maximized = placement.ShowCommand == ShowMaximized ||
                        (IsMinimized(placement.ShowCommand) &&
                         (placement.Flags & RestoreToMaximized) != 0);
        return new SavedMinecraftWindowPlacement
        {
            SchemaVersion = SchemaVersion,
            Left = placement.NormalPosition.Left,
            Top = placement.NormalPosition.Top,
            Right = placement.NormalPosition.Right,
            Bottom = placement.NormalPosition.Bottom,
            Maximized = maximized
        };
    }

    private SavedMinecraftWindowPlacement? TryRead()
    {
        try
        {
            if (!File.Exists(_placementFile))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<SavedMinecraftWindowPlacement>(
                File.ReadAllText(_placementFile),
                _jsonOptions);
            if (saved is null || saved.SchemaVersion != SchemaVersion)
            {
                return null;
            }

            var bounds = new NativeRect
            {
                Left = saved.Left,
                Top = saved.Top,
                Right = saved.Right,
                Bottom = saved.Bottom
            };
            return IsValid(bounds) ? saved : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TryWrite(SavedMinecraftWindowPlacement placement)
    {
        try
        {
            AtomicFile.WriteAllText(_placementFile, JsonSerializer.Serialize(placement, _jsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not save the Minecraft window placement: {ex.Message}");
            return false;
        }
    }

    private static nint FindMinecraftWindow(int processId)
    {
        nint bestWindow = nint.Zero;
        long bestScore = long.MinValue;
        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, GetOwner) != nint.Zero)
            {
                return true;
            }

            var ownerThreadId = GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerThreadId == 0 || ownerProcessId != processId ||
                !GetWindowRect(window, out var bounds) || !IsValid(bounds))
            {
                return true;
            }

            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width < 320 || height < 200)
            {
                return true;
            }

            var title = GetWindowTitle(window);
            var className = GetWindowClass(window);
            var isGlfw = string.Equals(className, "GLFW30", StringComparison.OrdinalIgnoreCase);
            var hasMinecraftTitle = title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase);
            if (!isGlfw && !hasMinecraftTitle)
            {
                return true;
            }

            var score = (long)width * height;
            if (isGlfw) score += 2_000_000_000L;
            if (hasMinecraftTitle) score += 1_000_000_000L;
            if (IsWindowed(window)) score += 500_000_000L;
            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = window;
            }
            return true;
        }, nint.Zero);
        return bestWindow;
    }

    private static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var value = new char[length + 1];
        var copied = GetWindowText(window, value, value.Length);
        return copied > 0 ? new string(value, 0, copied) : string.Empty;
    }

    private static string GetWindowClass(nint window)
    {
        var value = new char[256];
        var copied = GetClassName(window, value, value.Length);
        return copied > 0 ? new string(value, 0, copied) : string.Empty;
    }

    private static bool IsWindowed(nint window)
    {
        var style = GetWindowLongPtr(window, WindowStyleIndex).ToInt64();
        return (style & WindowStylePopup) == 0 &&
               ((style & WindowStyleCaption) != 0 || (style & WindowStyleThickFrame) != 0);
    }

    private static NativeRect ClampToNearestWorkArea(NativeRect bounds)
    {
        var monitor = MonitorFromRect(ref bounds, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return bounds;
        }

        var monitorInfo = MonitorInfo.Create();
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return bounds;
        }

        var workWidth = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var workHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        if (workWidth <= 0 || workHeight <= 0)
        {
            return bounds;
        }

        var width = Math.Clamp(bounds.Right - bounds.Left, Math.Min(320, workWidth), workWidth);
        var height = Math.Clamp(bounds.Bottom - bounds.Top, Math.Min(200, workHeight), workHeight);
        var left = Math.Clamp(bounds.Left, monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Right - width);
        var top = Math.Clamp(bounds.Top, monitorInfo.WorkArea.Top, monitorInfo.WorkArea.Bottom - height);
        return new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
    }

    private static bool Equivalent(SavedMinecraftWindowPlacement? left, SavedMinecraftWindowPlacement? right) =>
        left is null ? right is null : right is not null &&
        left.Left == right.Left &&
        left.Top == right.Top &&
        left.Right == right.Right &&
        left.Bottom == right.Bottom &&
        left.Maximized == right.Maximized;

    private static bool IsValid(NativeRect bounds) =>
        bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

    private static bool IsMinimized(uint showCommand) => showCommand is 2 or 6 or 7 or 11;

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, [Out] char[] value, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;

        public static WindowPlacement Create() => new()
        {
            Length = (uint)Marshal.SizeOf<WindowPlacement>()
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
    }

    private sealed class SavedMinecraftWindowPlacement
    {
        public int SchemaVersion { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public bool Maximized { get; set; }
    }
}
