using System;
using System.Runtime.InteropServices;

namespace DetPS2.Present;

/// <summary>
/// OpenGL host presenter (Windows WGL + opengl32): Soft-GS BGRA → textured fullscreen blit → SwapBuffers.
/// Soft-GS remains determinism truth; this only displays.
/// <see cref="DeviceReady"/> is true after successful <see cref="AttachWindow"/>.
/// Never throws from Present/Attach to callers.
/// </summary>
public sealed unsafe class OpenGLSwapPresenter : IHostSwapPresenter
{
    private readonly object _lock = new();
    private nint _hwnd;
    private nint _hdc;
    private nint _hglrc;
    private uint _texture;
    private int _texW;
    private int _texH;
    private int _outW;
    private int _outH;
    private bool _ready;
    private bool _disposed;
    private string? _renderer;
    private string? _version;

    // GL entry points resolved after context is current
    private delegate* unmanaged[Stdcall]<uint, void> _glEnable;
    private delegate* unmanaged[Stdcall]<uint, void> _glDisable;
    private delegate* unmanaged[Stdcall]<int, int, int, int, void> _glViewport;
    private delegate* unmanaged[Stdcall]<float, float, float, float, void> _glClearColor;
    private delegate* unmanaged[Stdcall]<uint, void> _glClear;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glGenTextures;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glDeleteTextures;
    private delegate* unmanaged[Stdcall]<uint, uint, void> _glBindTexture;
    private delegate* unmanaged[Stdcall]<uint, uint, int, void> _glTexParameteri;
    private delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void> _glTexImage2D;
    private delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void> _glTexSubImage2D;
    private delegate* unmanaged[Stdcall]<int, int, void> _glPixelStorei;
    private delegate* unmanaged[Stdcall]<uint, void> _glMatrixMode;
    private delegate* unmanaged[Stdcall]<void> _glLoadIdentity;
    private delegate* unmanaged[Stdcall]<double, double, double, double, double, double, void> _glOrtho;
    private delegate* unmanaged[Stdcall]<uint, void> _glBegin;
    private delegate* unmanaged[Stdcall]<void> _glEnd;
    private delegate* unmanaged[Stdcall]<float, float, void> _glTexCoord2f;
    private delegate* unmanaged[Stdcall]<float, float, void> _glVertex2f;
    private delegate* unmanaged[Stdcall]<uint, byte*> _glGetString;

    public PresentBackend Backend => PresentBackend.OpenGL;
    public string Name => DeviceReady ? "OpenGL" : "OpenGL(not-ready)";
    public bool DeviceReady => _ready;
    public bool WindowAttached => _hwnd != 0 && _hglrc != 0;
    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.Nearest;
    public PresentStats Stats { get; } = new() { BackendName = "OpenGL", DeviceReady = false };
    public string? Renderer => _renderer;
    public string? Version => _version;

    public bool AttachWindow(nint hwnd, int width, int height)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            Stats.LastError = "OpenGL: invalid hwnd/size";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            Stats.LastError = "OpenGL: WGL path is Windows-only in v1";
            Stats.DeviceReady = false;
            return false;
        }

        lock (_lock)
        {
            if (_disposed) return false;

            try
            {
                DestroyContext_NoLock();

                nint hdc = GetDC(hwnd);
                if (hdc == 0)
                {
                    Stats.LastError = "OpenGL: GetDC failed";
                    return false;
                }

                var pfd = new PIXELFORMATDESCRIPTOR
                {
                    nSize = (ushort)sizeof(PIXELFORMATDESCRIPTOR),
                    nVersion = 1,
                    dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
                    iPixelType = PFD_TYPE_RGBA,
                    cColorBits = 32,
                    cDepthBits = 24,
                    cStencilBits = 8,
                    iLayerType = PFD_MAIN_PLANE,
                };

                int pf = ChoosePixelFormat(hdc, ref pfd);
                if (pf == 0 || !SetPixelFormat(hdc, pf, ref pfd))
                {
                    ReleaseDC(hwnd, hdc);
                    Stats.LastError = "OpenGL: Choose/SetPixelFormat failed";
                    return false;
                }

                nint hglrc = wglCreateContext(hdc);
                if (hglrc == 0)
                {
                    ReleaseDC(hwnd, hdc);
                    Stats.LastError = "OpenGL: wglCreateContext failed";
                    return false;
                }

                if (!wglMakeCurrent(hdc, hglrc))
                {
                    wglDeleteContext(hglrc);
                    ReleaseDC(hwnd, hdc);
                    Stats.LastError = "OpenGL: wglMakeCurrent failed";
                    return false;
                }

                if (!ResolveGl_NoLock())
                {
                    wglMakeCurrent(nint.Zero, nint.Zero);
                    wglDeleteContext(hglrc);
                    ReleaseDC(hwnd, hdc);
                    Stats.LastError = "OpenGL: failed to resolve GL entry points";
                    return false;
                }

                _hwnd = hwnd;
                _hdc = hdc;
                _hglrc = hglrc;
                _outW = Math.Max(1, width);
                _outH = Math.Max(1, height);

                if (_glGetString != null)
                {
                    try
                    {
                        _renderer = Marshal.PtrToStringAnsi((nint)_glGetString(GL_RENDERER));
                        _version = Marshal.PtrToStringAnsi((nint)_glGetString(GL_VERSION));
                    }
                    catch { /* ignore */ }
                }

                _glDisable(GL_DEPTH_TEST);
                _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
                _glViewport(0, 0, _outW, _outH);

                uint tex = 0;
                _glGenTextures(1, &tex);
                _texture = tex;
                _glBindTexture(GL_TEXTURE_2D, _texture);
                int filter = UpscaleMode == UpscaleMode.Nearest ? GL_NEAREST : GL_LINEAR;
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

                _ready = true;
                Stats.DeviceReady = true;
                Stats.OutputWidth = _outW;
                Stats.OutputHeight = _outH;
                Stats.LastError = "";
                Stats.BackendName = "OpenGL";
                return true;
            }
            catch (Exception ex)
            {
                DestroyContext_NoLock();
                _ready = false;
                Stats.DeviceReady = false;
                Stats.LastError = "OpenGL AttachWindow: " + ex.Message;
                return false;
            }
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_lock)
        {
            if (_disposed || !_ready) return;
            try
            {
                if (!MakeCurrent_NoLock()) return;
                _outW = width;
                _outH = height;
                _glViewport(0, 0, width, height);
                Stats.OutputWidth = width;
                Stats.OutputHeight = height;
            }
            catch (Exception ex)
            {
                Stats.LastError = "OpenGL Resize: " + ex.Message;
            }
        }
    }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        if (!_ready) return;
        if (width <= 0 || height <= 0 || framebuffer.Length < width * height) return;

        lock (_lock)
        {
            if (_disposed || !_ready || _hdc == 0) return;
            try
            {
                if (!MakeCurrent_NoLock()) return;

                _glBindTexture(GL_TEXTURE_2D, _texture);
                fixed (uint* ptr = framebuffer)
                {
                    if (_texW == width && _texH == height)
                    {
                        _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height,
                            GL_BGRA, GL_UNSIGNED_BYTE, ptr);
                    }
                    else
                    {
                        _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0,
                            GL_BGRA, GL_UNSIGNED_BYTE, ptr);
                        _texW = width;
                        _texH = height;
                    }
                }

                int filter = UpscaleMode == UpscaleMode.Nearest ? GL_NEAREST : GL_LINEAR;
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);

                _glViewport(0, 0, _outW, _outH);
                _glClearColor(0f, 0f, 0f, 1f);
                _glClear(GL_COLOR_BUFFER_BIT);

                // Compatibility-profile textured quad (V-flipped UV for top-left Soft-GS origin).
                if (_glEnable != null && _glBegin != null)
                {
                    _glEnable(GL_TEXTURE_2D);
                    _glBindTexture(GL_TEXTURE_2D, _texture);
                    _glMatrixMode(GL_PROJECTION);
                    _glLoadIdentity();
                    _glOrtho(0, 1, 0, 1, -1, 1);
                    _glMatrixMode(GL_MODELVIEW);
                    _glLoadIdentity();
                    _glBegin(GL_QUADS);
                    _glTexCoord2f(0f, 1f); _glVertex2f(0f, 0f);
                    _glTexCoord2f(1f, 1f); _glVertex2f(1f, 0f);
                    _glTexCoord2f(1f, 0f); _glVertex2f(1f, 1f);
                    _glTexCoord2f(0f, 0f); _glVertex2f(0f, 1f);
                    _glEnd();
                }

                if (!SwapBuffers(_hdc))
                    Stats.LastError = "OpenGL: SwapBuffers failed";

                int n = width * height;
                Stats.SourceWidth = width;
                Stats.SourceHeight = height;
                Stats.BytesUploaded += (ulong)n * 4;
                Stats.UploadCount++;
                Stats.PresentCount++;
                Stats.DeviceReady = true;
            }
            catch (Exception ex)
            {
                Stats.LastError = "OpenGL Present: " + ex.Message;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Stats.ClearCounters();
            Stats.DeviceReady = DeviceReady;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DestroyContext_NoLock();
            Stats.DeviceReady = false;
        }
        GC.SuppressFinalize(this);
    }

    private bool MakeCurrent_NoLock()
    {
        if (_hdc == 0 || _hglrc == 0) return false;
        if (!wglMakeCurrent(_hdc, _hglrc))
        {
            Stats.LastError = "OpenGL: wglMakeCurrent failed";
            return false;
        }
        return true;
    }

    private bool ResolveGl_NoLock()
    {
        _glEnable = (delegate* unmanaged[Stdcall]<uint, void>)Load("glEnable");
        _glDisable = (delegate* unmanaged[Stdcall]<uint, void>)Load("glDisable");
        _glViewport = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)Load("glViewport");
        _glClearColor = (delegate* unmanaged[Stdcall]<float, float, float, float, void>)Load("glClearColor");
        _glClear = (delegate* unmanaged[Stdcall]<uint, void>)Load("glClear");
        _glGenTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)Load("glGenTextures");
        _glDeleteTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)Load("glDeleteTextures");
        _glBindTexture = (delegate* unmanaged[Stdcall]<uint, uint, void>)Load("glBindTexture");
        _glTexParameteri = (delegate* unmanaged[Stdcall]<uint, uint, int, void>)Load("glTexParameteri");
        _glTexImage2D = (delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void>)Load("glTexImage2D");
        _glTexSubImage2D = (delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void>)Load("glTexSubImage2D");
        _glPixelStorei = (delegate* unmanaged[Stdcall]<int, int, void>)Load("glPixelStorei");
        _glMatrixMode = (delegate* unmanaged[Stdcall]<uint, void>)Load("glMatrixMode");
        _glLoadIdentity = (delegate* unmanaged[Stdcall]<void>)Load("glLoadIdentity");
        _glOrtho = (delegate* unmanaged[Stdcall]<double, double, double, double, double, double, void>)Load("glOrtho");
        _glBegin = (delegate* unmanaged[Stdcall]<uint, void>)Load("glBegin");
        _glEnd = (delegate* unmanaged[Stdcall]<void>)Load("glEnd");
        _glTexCoord2f = (delegate* unmanaged[Stdcall]<float, float, void>)Load("glTexCoord2f");
        _glVertex2f = (delegate* unmanaged[Stdcall]<float, float, void>)Load("glVertex2f");
        _glGetString = (delegate* unmanaged[Stdcall]<uint, byte*>)Load("glGetString");

        return _glViewport != null && _glTexImage2D != null && _glClear != null && _glBindTexture != null;
    }

    private static nint Load(string name)
    {
        nint p = wglGetProcAddress(name);
        if (p == 0 || p == 1 || p == 2 || p == 3)
        {
            nint mod = GetModuleHandle("opengl32.dll");
            if (mod != 0)
                p = GetProcAddress(mod, name);
        }
        return p;
    }

    private void DestroyContext_NoLock()
    {
        try
        {
            if (_texture != 0 && _glDeleteTextures != null && _hdc != 0 && _hglrc != 0)
            {
                if (wglMakeCurrent(_hdc, _hglrc))
                {
                    uint t = _texture;
                    _glDeleteTextures(1, &t);
                }
            }
        }
        catch { /* ignore */ }
        _texture = 0;
        _texW = _texH = 0;

        if (_hglrc != 0)
        {
            wglMakeCurrent(nint.Zero, nint.Zero);
            wglDeleteContext(_hglrc);
            _hglrc = 0;
        }

        if (_hdc != 0 && _hwnd != 0)
        {
            ReleaseDC(_hwnd, _hdc);
            _hdc = 0;
        }

        _hwnd = 0;
        _ready = false;
        Stats.DeviceReady = false;
        _glEnable = null;
        _glBegin = null;
    }

    // ── GL constants ─────────────────────────────────────────────────────
    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_DEPTH_TEST = 0x0B71;
    private const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    private const uint GL_BGRA = 0x80E1;
    private const uint GL_UNSIGNED_BYTE = 0x1401;
    private const int GL_RGBA8 = 0x8058;
    private const int GL_NEAREST = 0x2600;
    private const int GL_LINEAR = 0x2601;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S = 0x2802;
    private const uint GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const uint GL_PROJECTION = 0x1701;
    private const uint GL_MODELVIEW = 0x1700;
    private const uint GL_QUADS = 0x0007;
    private const uint GL_RENDERER = 0x1F01;
    private const uint GL_VERSION = 0x1F02;

    private const int PFD_DRAW_TO_WINDOW = 0x00000004;
    private const int PFD_SUPPORT_OPENGL = 0x00000020;
    private const int PFD_DOUBLEBUFFER = 0x00000001;
    private const byte PFD_TYPE_RGBA = 0;
    private const byte PFD_MAIN_PLANE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(nint hdc, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(nint hdc, int format, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(nint hdc);
    [DllImport("opengl32.dll")] private static extern nint wglCreateContext(nint hdc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(nint hglrc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(nint hdc, nint hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern nint wglGetProcAddress(string lpszProc);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint hModule, string procName);
}
