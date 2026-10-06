// ============================================================================
//  KbmShare —— 一套键盘鼠标控制两台 Windows（自研软件 KVM）
//
//  原理: 两台机器各跑一个实例, 通过 TCP 直连。
//    - 持有键鼠的机器用低级钩子(WH_MOUSE_LL/WH_KEYBOARD_LL)捕获输入,
//      用 Raw Input(WM_INPUT) 取未钳制的鼠标增量(保证冲出屏幕边缘后仍能拿到位移)
//    - 光标冲到屏幕边缘继续外推 -> 控制权交给对方, 对方用 SendInput 注入
//    - 对方屏幕反向边缘外推 -> 归还控制权
//    - 任一机器的物理键鼠一动, 立即从对方手里接管
//    - 文本剪贴板双向自动同步
//
//  用法:
//    左边那台(通常接着键鼠):  KbmShare.exe left
//    右边那台:                KbmShare.exe right <左边机器IP>
//    可选: --port 4123   --speed 1.0   --nohooks(仅调试网络层)
//    强制把控制权拉回本机:  按住右Ctrl 再按 F12
//    退出: Ctrl+C
//
//  编译: build.bat (用系统自带 .NET Framework csc, 产物为单个 exe, 拷走即用)
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace KbmShare
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool isLeft = true;
            string host = null;
            string role = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "left") { if (role == null) role = "left"; continue; }
                if (a == "right") { if (role == null) role = "right"; continue; }
                if (a.StartsWith("--")) continue;
                if (role == "right" && host == null) { host = args[i]; continue; }
                if (role == null) role = a;
            }

            if (role != null && role != "left" && role != "right")
            {
                Console.WriteLine("参数无法识别: " + string.Join(" ", args));
                PrintUsage();
                Pause();
                return 1;
            }
            if (role == null)
            {
                // 未指定角色(双击运行/只给了可选参数): 进入交互菜单
                if (!InteractiveSetup(out isLeft, out host))
                {
                    Console.WriteLine("未指定角色且无法交互, 请用命令行运行: KbmShare.exe left  或  KbmShare.exe right <对方IP>");
                    Pause();
                    return 1;
                }
            }
            else
            {
                isLeft = role == "left";
                if (!isLeft && host == null)
                {
                    Console.WriteLine("right 模式需要对方(左边机器)的 IP, 例如: KbmShare.exe right 192.168.1.10");
                    Pause();
                    return 1;
                }
            }

            int port = 4123;
            double speed = 1.0;
            bool noHooks = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--nohooks") { noHooks = true; continue; }
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int p; if (int.TryParse(args[i + 1], out p) && p > 0 && p < 65536) port = p;
                }
                else if (args[i] == "--speed" && i + 1 < args.Length)
                {
                    double s;
                    if (double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out s)
                        && s >= 0.2 && s <= 8.0) speed = s;
                }
            }

            App app = new App(isLeft, host, port, speed, noHooks);
            return app.Run();
        }

        static void PrintUsage()
        {
            Console.WriteLine("KbmShare —— 一套键鼠管两台 Windows");
            Console.WriteLine();
            Console.WriteLine("  接键鼠、放在左边的机器:   KbmShare.exe left");
            Console.WriteLine("  右边的机器:               KbmShare.exe right <左边机器的IP>");
            Console.WriteLine();
            Console.WriteLine("可选参数: --port 4123 | --speed 1.0 | --nohooks");
            Console.WriteLine("右Ctrl+F12 = 强制把控制权拉回本机;  Ctrl+C 退出");
        }

        static bool InteractiveSetup(out bool isLeft, out string host)
        {
            isLeft = true;
            host = null;
            PrintUsage();
            Console.WriteLine();
            Console.WriteLine("---------- 交互模式(未指定 left/right 时) ----------");
            while (true)
            {
                Console.Write("本机是哪台? [1]=左边(通常接键鼠, 等对方连) [2]=右边(主动连对方): ");
                string sel = ReadLineSafe();
                if (sel == null) return false;
                if (sel == "1") { isLeft = true; return true; }
                if (sel == "2")
                {
                    Console.Write("对方(左边那台)的 IP 是: ");
                    string ip = ReadLineSafe();
                    if (ip == null) return false;
                    if (ip.Length > 0) { host = ip; isLeft = false; return true; }
                }
            }
        }

        static string ReadLineSafe()
        {
            try
            {
                string s = Console.ReadLine();
                return s == null ? null : s.Trim();
            }
            catch { return null; }
        }

        static void Pause()
        {
            try
            {
                Console.Write("按回车键退出 ...");
                Console.ReadLine();
            }
            catch { }
        }
    }

    // 消息类型
    static class M
    {
        public const byte Hello = 1;    // 名字+角色
        public const byte Transfer = 2; // 我方光标冲出边缘 -> 对方接管 (带归一化 y)
        public const byte Return = 3;   // 对方光标冲回 -> 归还我方 (带归一化 y)
        public const byte TakeOver = 4; // 任一方物理输入抢回本机
        public const byte MDelta = 5;   // 鼠标增量
        public const byte MBtn = 6;     // 鼠标按键/滚轮
        public const byte Key = 7;      // 键盘事件
        public const byte ClipText = 8; // 剪贴板文本
        public const byte Ping = 9;     // 心跳
        public const byte Bye = 10;     // 主动退出
        public const byte ClipImage = 11; // 剪贴板图片(DIB)
    }

    class App
    {
        // ---- 配置 ----
        readonly bool isLeft;
        readonly string host;
        readonly int port;
        readonly double speed;
        readonly bool noHooks;

        // ---- 共享状态(锁 st) ----
        readonly object st = new object();
        bool forwarding;       // 我的物理输入正在转发给对方(光标在对方屏上)
        bool remoteHere;       // 对方光标正在我屏上(我注入对方发来的输入)
        int lx, ly;            // 对方光标在我屏上的逻辑坐标
        double remX, remY;     // speed 缩放的取整残差
        int lastX = -999999, lastY;
        int edgeStreak;        // 连续外推计数, 防止只是路过边缘
        string lastPeerClip;   // 剪贴板回环抑制
        byte[] lastPeerImage;  // 图片剪贴板回环抑制
        int cx, cy;            // 本机主屏尺寸
        const int MaxClipImage = 48 * 1024 * 1024;  // 图片同步上限 48MB

        // ---- 窗口/钩子 ----
        Native.WndProcDelegate wndProc;      // 持引用防 GC
        Native.HookProcDelegate mouseProc;
        Native.HookProcDelegate keyProc;
        IntPtr hwnd = IntPtr.Zero;
        IntPtr hMouseHook = IntPtr.Zero;
        IntPtr hKeyHook = IntPtr.Zero;

        // Raw Input 缓冲(固定, 避免每事件分配)
        readonly byte[] rawBuf = new byte[128];
        GCHandle rawPin;

        // ---- 网络 ----
        Link link;                 // 当前连接
        volatile bool exiting;

        public App(bool isLeft, string host, int port, double speed, bool noHooks)
        {
            this.isLeft = isLeft;
            this.host = host;
            this.port = port;
            this.speed = speed;
            this.noHooks = noHooks;
        }

        public int Run()
        {
            Native.SetProcessDPIAware();
            RefreshMetrics();

            Console.WriteLine("=====================================================");
            Console.WriteLine(" KbmShare - 一套键鼠管两台 Windows");
            Console.WriteLine("=====================================================");
            Console.WriteLine("角色: " + (isLeft ? "left  (本机在左, 监听 TCP " + port + ")" : "right (本机在右, 主动连接对方)"));
            if (host != null) Console.WriteLine("对方地址: " + host + ":" + port);
            Console.WriteLine("机器名: " + Environment.MachineName);
            string myIp = null;
            int ipCount = 0;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        ipCount++;
                        bool virt = IsVirtualNic(nic);
                        Console.WriteLine("本机 IP: " + ua.Address + (virt ? "    [虚拟网卡, 一般不用这个] " : "    [可用] ")
                            + "网卡: " + nic.Name + " - " + nic.Description);
                        if (myIp == null && !virt) myIp = ua.Address.ToString();
                    }
                }
            }
            catch { }
            if (ipCount > 1)
                Console.WriteLine("注意: 出现多个 IP 时, 对方应填与它自己同网段(前 3 段相同)的那个, 可用 ping 验证。");
            bool admin = false;
            try { admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { }
            if (admin) Console.WriteLine("管理员权限: 是 (可以控制 UAC/管理员窗口)");
            else Console.WriteLine("管理员权限: 否 (无法控制管理员窗口, 建议右键->以管理员身份运行)");
            if (isLeft && myIp != null)
                Console.WriteLine("提示: 对方机器上应执行  KbmShare.exe right " + myIp);
            Console.WriteLine("-----------------------------------------------------");

            Console.CancelKeyPress += OnCancel;

            Thread net = new Thread(isLeft ? (ThreadStart)ListenLoop : (ThreadStart)ConnectLoop);
            net.IsBackground = true;
            net.Start();

            if (noHooks)
            {
                Console.WriteLine("[nohooks] 仅网络层运行, 未安装输入钩子, Ctrl+C 退出。");
                while (!exiting) Thread.Sleep(500);
                return 0;
            }

            CreateWindowAndPump();
            return 0;
        }

        // ============================ 网络角色 ============================

        void ListenLoop()
        {
            TcpListener listener;
            try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); }
            catch (Exception e)
            {
                Log("监听端口 " + port + " 失败: " + e.Message);
                int others = CountOtherKbmShare();
                if (others > 0)
                {
                    Log(">>> 检测到本机还有 " + others + " 个 KbmShare 进程在运行, 多半是它占了端口!");
                    Log(">>> 解决: 任务管理器结束所有 KbmShare.exe, 或管理员 CMD 执行:  taskkill /f /im KbmShare.exe");
                    Log(">>> 然后只启动一个实例。");
                }
                else
                {
                    Log(">>> 可能是其他软件占了这个端口, 两边都加 --port 4026 换个端口试试。");
                }
                return;
            }
            Log("等待对方连接 ...");
            while (!exiting)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { break; }
                Link old = link;
                link = new Link(this, c);
                if (old != null) old.Dispose("被新连接替换");
                link.Run();          // 阻塞直到这条连接断开
                if (link != null && !link.Connected) link = null;
            }
        }

        void ConnectLoop()
        {
            int fails = 0;
            while (!exiting)
            {
                try
                {
                    TcpClient c = new TcpClient();
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000))
                    {
                        try { c.Close(); } catch { }
                        throw new SocketException();
                    }
                    c.EndConnect(ar);
                    fails = 0;
                    link = new Link(this, c);
                    link.Run();
                }
                catch { }
                if (exiting) break;
                fails++;
                if (fails == 3)
                {
                    Log("一直连不上? 按顺序检查:");
                    Log("  1. 对方程序在运行, 且选的是 [1](left/监听方)");
                    Log("  2. 对方控制台有没有报'监听失败' (多半是重复开了多个, 结束多余进程)");
                    Log("  3. 对方防火墙放行: 管理员 CMD 执行");
                    Log("     netsh advfirewall firewall add rule name=\"KbmShare\" dir=in action=allow protocol=TCP localport=" + port);
                    Log("  4. IP 填的是对方当前打印的那个 (重新看对方窗口)");
                }
                Log("连接不可用, 2 秒后重试 ...");
                Thread.Sleep(2000);
            }
        }

        static int CountOtherKbmShare()
        {
            try
            {
                int n = 0;
                int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("KbmShare"))
                    if (p.Id != self) n++;
                return n;
            }
            catch { return 0; }
        }

        // ============================ 输入钩子 ============================

        void CreateWindowAndPump()
        {
            rawPin = GCHandle.Alloc(rawBuf, GCHandleType.Pinned);
            wndProc = new Native.WndProcDelegate(WndProc);
            Native.WNDCLASSEX wc = new Native.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX));
            wc.lpfnWndProc = wndProc;
            wc.hInstance = Native.GetModuleHandle(null);
            wc.lpszClassName = "KbmShareWnd";
            if (Native.RegisterClassEx(ref wc) == 0) { Log("RegisterClassEx 失败: " + Marshal.GetLastWin32Error()); return; }

            hwnd = Native.CreateWindowEx(0, "KbmShareWnd", "KbmShare", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Log("CreateWindow 失败"); return; }

            // Raw Input: 拿未钳制的鼠标增量(光标钉在边缘时 LL 钩子的坐标不再变化, 增量只能从这里来)
            Native.RAWINPUTDEVICE rid = new Native.RAWINPUTDEVICE();
            rid.usUsagePage = 1; rid.usUsage = 2;
            rid.dwFlags = Native.RIDEV_INPUTSINK;   // 无焦点也接收
            rid.hwndTarget = hwnd;
            if (!Native.RegisterRawInputDevices(new Native.RAWINPUTDEVICE[] { rid }, 1,
                    (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                Log("注册 Raw Input 失败: " + Marshal.GetLastWin32Error());

            Native.AddClipboardFormatListener(hwnd);

            mouseProc = new Native.HookProcDelegate(MouseHook);
            keyProc = new Native.HookProcDelegate(KeyHook);
            hMouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
            hKeyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, Native.GetModuleHandle(null), 0);
            if (hMouseHook == IntPtr.Zero || hKeyHook == IntPtr.Zero)
            {
                Log("输入钩子安装失败! err=" + Marshal.GetLastWin32Error());
                return;
            }
            Log("输入钩子就绪。鼠标冲向" + (isLeft ? "右" : "左") + "边缘继续外推 -> 控制对方; 右Ctrl+F12 强制回本机。");

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_INPUT: OnRawInput(lParam); return IntPtr.Zero;
                case Native.WM_CLIPBOARDUPDATE: OnClipboardChanged(); return IntPtr.Zero;
                case Native.WM_DISPLAYCHANGE: RefreshMetrics(); return IntPtr.Zero;
                case Native.WM_DESTROY: Native.PostQuitMessage(0); return IntPtr.Zero;
                default: return Native.DefWindowProc(h, msg, wParam, lParam);
            }
        }

        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return Native.CallNextHookEx(hMouseHook, code, wParam, lParam);
            try
            {
                Native.MSLLHOOKSTRUCT m =
                    (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                if ((m.flags & Native.LLMHF_INJECTED) != 0)
                    return Native.CallNextHookEx(hMouseHook, code, wParam, lParam);

                uint msg = (uint)wParam.ToInt64();
                bool swallow = false;
                lock (st)
                {
                    if (remoteHere)
                    {
                        // 我的屏上正显示对方光标, 但本机物理键鼠动了 -> 接管
                        remoteHere = false;
                        if (link != null) link.Send(M.TakeOver, null);
                        Log("本机鼠标接管");
                    }
                    else if (forwarding)
                    {
                        // 一切鼠标事件转发给对方; 移动事件丢弃(增量由 Raw Input 通道发送)
                        if (msg != Native.WM_MOUSEMOVE) SendBtnWheel(msg, m);
                        swallow = true;
                    }
                    else if (msg == Native.WM_MOUSEMOVE && link != null && link.Connected)
                    {
                        // 本地模式: 检测冲边。连续 2 次仍向外推才算, 防止贴边误触
                        bool outward = isLeft
                            ? (m.pt.X >= cx - 1 && m.pt.X > lastX)
                            : (m.pt.X <= 0 && m.pt.X < lastX);
                        if (outward) edgeStreak++; else edgeStreak = 0;
                        if (edgeStreak >= 2)
                        {
                            edgeStreak = 0;
                            forwarding = true;
                            link.Send(M.Transfer, BitConverter.GetBytes(NormY(m.pt.Y)));
                            Log("光标冲出边缘 -> 控制权交给对方");
                            swallow = true;
                        }
                    }
                    if (msg == Native.WM_MOUSEMOVE) { lastX = m.pt.X; lastY = m.pt.Y; }
                }
                if (swallow) return (IntPtr)1;
            }
            catch { }
            return Native.CallNextHookEx(hMouseHook, code, wParam, lParam);
        }

        IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return Native.CallNextHookEx(hKeyHook, code, wParam, lParam);
            try
            {
                uint msg = (uint)wParam.ToInt64();
                if (msg != Native.WM_KEYDOWN && msg != Native.WM_KEYUP &&
                    msg != Native.WM_SYSKEYDOWN && msg != Native.WM_SYSKEYUP)
                    return Native.CallNextHookEx(hKeyHook, code, wParam, lParam);

                Native.KBDLLHOOKSTRUCT k =
                    (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if ((k.flags & Native.LLKHF_INJECTED) != 0)
                    return Native.CallNextHookEx(hKeyHook, code, wParam, lParam);

                lock (st)
                {
                    if (remoteHere)
                    {
                        remoteHere = false;
                        if (link != null) link.Send(M.TakeOver, null);
                        Log("本机键盘接管");
                    }
                    else if (forwarding)
                    {
                        // 逃生舱: 右Ctrl+F12 强制回本机(否则 Ctrl+C 会被转发, 程序没法退出)
                        if (k.vkCode == Native.VK_F12 && (Native.GetAsyncKeyState(Native.VK_RCONTROL) & 0x8000) != 0)
                        {
                            forwarding = false;
                            if (link != null) link.Send(M.Return, BitConverter.GetBytes(NormY(lastY)));
                            Log("右Ctrl+F12 -> 强制回本机");
                            return (IntPtr)1;
                        }
                        byte[] p = new byte[9];
                        Buffer.BlockCopy(BitConverter.GetBytes(k.vkCode), 0, p, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(k.scanCode), 0, p, 4, 4);
                        p[8] = (byte)((((k.flags & Native.LLKHF_EXTENDED) != 0) ? 1 : 0)
                                    | ((((k.flags & Native.LLKHF_UP) != 0) ? 1 : 0) << 1));
                        if (link != null) link.Send(M.Key, p);
                        return (IntPtr)1;
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(hKeyHook, code, wParam, lParam);
        }

        void OnRawInput(IntPtr hRawInput)
        {
            try
            {
                uint size = (uint)rawBuf.Length;
                uint got = Native.GetRawInputData(hRawInput, Native.RID_INPUT, rawPin.AddrOfPinnedObject(),
                    ref size, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER)));
                if (got == 0 || got > (uint)rawBuf.Length) return;

                long basePtr = rawPin.AddrOfPinnedObject().ToInt64();
                Native.RAWMOUSE rm = (Native.RAWMOUSE)Marshal.PtrToStructure(
                    new IntPtr(basePtr + Marshal.SizeOf(typeof(Native.RAWINPUTHEADER))), typeof(Native.RAWMOUSE));
                if ((rm.usFlags & 1) != 0) return;   // 绝对坐标设备(触屏/数位板), 忽略

                lock (st)
                {
                    if (!forwarding) return;
                    double nx = rm.lLastX * speed + remX;
                    double ny = rm.lLastY * speed + remY;
                    int dx = (int)Math.Floor(nx), dy = (int)Math.Floor(ny);
                    remX = nx - dx; remY = ny - dy;
                    if (dx == 0 && dy == 0) return;
                    byte[] p = new byte[8];
                    Buffer.BlockCopy(BitConverter.GetBytes(dx), 0, p, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(dy), 0, p, 4, 4);
                    if (link != null) link.Send(M.MDelta, p);
                }
            }
            catch { }
        }

        void SendBtnWheel(uint msg, Native.MSLLHOOKSTRUCT m)
        {
            byte act; int data = 0;
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN: act = 0; break;
                case Native.WM_LBUTTONUP: act = 1; break;
                case Native.WM_RBUTTONDOWN: act = 2; break;
                case Native.WM_RBUTTONUP: act = 3; break;
                case Native.WM_MBUTTONDOWN: act = 4; break;
                case Native.WM_MBUTTONUP: act = 5; break;
                case Native.WM_XBUTTONDOWN: act = 6; data = (short)(m.mouseData >> 16); break;
                case Native.WM_XBUTTONUP: act = 7; data = (short)(m.mouseData >> 16); break;
                case Native.WM_MOUSEWHEEL: act = 8; data = (short)(m.mouseData >> 16); break;
                case Native.WM_MOUSEHWHEEL: act = 9; data = (short)(m.mouseData >> 16); break;
                default: return;
            }
            byte[] p = new byte[5];
            p[0] = act;
            Buffer.BlockCopy(BitConverter.GetBytes(data), 0, p, 1, 4);
            if (link != null) link.Send(M.MBtn, p);
        }

        // ============================ 消息处理(读线程) ============================

        public void OnMessage(byte type, byte[] payload)
        {
            switch (type)
            {
                case M.Hello:
                {
                    if (payload.Length < 2) break;
                    int nameLen = Math.Min(payload[0], payload.Length - 2);
                    string name = Encoding.UTF8.GetString(payload, 1, nameLen);
                    byte side = payload[1 + nameLen];
                    Log("已连接到: " + name + " (" + (side == 0 ? "left" : "right") + ")");
                    break;
                }
                case M.Transfer:
                {
                    ushort ny = BitConverter.ToUInt16(payload, 0);
                    int ex, ey;
                    lock (st)
                    {
                        remoteHere = true;
                        lx = isLeft ? cx - 1 : 0;   // 我在左: 对方从我右边缘进入; 我在右: 从左边缘进入
                        ly = DenormY(ny);
                        ex = lx; ey = ly;
                    }
                    InjectAbsMove(ex, ey);
                    Log("对方光标进入本机屏幕");
                    break;
                }
                case M.MDelta:
                {
                    int dx = BitConverter.ToInt32(payload, 0);
                    int dy = BitConverter.ToInt32(payload, 4);
                    bool apply = false, exited = false, echoed = false;
                    int ex = 0, ey = 0; ushort outY = 0;
                    lock (st)
                    {
                        if (remoteHere)
                        {
                            echoed = true;
                            lx += dx; ly += dy;
                            if (ly < 0) ly = 0;
                            if (ly > cy - 1) ly = cy - 1;
                            if (lx < 0) { lx = 0; if (!isLeft) exited = true; }
                            else if (lx > cx - 1) { lx = cx - 1; if (isLeft) exited = true; }
                            if (exited) remoteHere = false;
                            apply = !exited;
                            ex = lx; ey = ly; outY = NormY(ly);
                        }
                    }
                    if (echoed && apply) InjectAbsMove(ex, ey);
                    if (exited && link != null)
                    {
                        link.Send(M.Return, BitConverter.GetBytes(outY));
                        Log("对方光标冲回边缘 -> 归还控制权");
                    }
                    break;
                }
                case M.Return:
                {
                    ushort ry = BitConverter.ToUInt16(payload, 0);
                    lock (st) { forwarding = false; }
                    Native.SetCursorPos(isLeft ? cx - 1 : 0, DenormY(ry));
                    Log("光标回到本机, 恢复本地控制");
                    break;
                }
                case M.TakeOver:
                {
                    lock (st) { forwarding = false; }
                    Log("对方用本机物理键鼠接管, 我方停止转发");
                    break;
                }
                case M.MBtn:
                {
                    InjectBtn(payload[0], BitConverter.ToInt32(payload, 1));
                    break;
                }
                case M.Key:
                {
                    uint scan = BitConverter.ToUInt32(payload, 4);
                    InjectKey(scan, payload[8]);
                    break;
                }
                case M.ClipText:
                {
                    int bl = BitConverter.ToInt32(payload, 0);
                    if (bl <= 0 || bl > 4 * 1024 * 1024) break;
                    string text = Encoding.Unicode.GetString(payload, 4, bl);
                    lock (st) { lastPeerClip = text; }
                    SetClipText(text);
                    Log("剪贴板已同步 (" + text.Length + " 字符)");
                    break;
                }
                case M.ClipImage:
                {
                    if (payload.Length < 5) break;
                    int bl = BitConverter.ToInt32(payload, 1);
                    if (bl <= 0 || bl > MaxClipImage || 5 + bl != payload.Length) break;
                    byte[] img = new byte[bl];
                    Buffer.BlockCopy(payload, 5, img, 0, bl);
                    uint fmt = payload[0] == 0 ? Native.CF_DIB : Native.CF_DIBV5;
                    lock (st) { lastPeerImage = img; }
                    SetClipImage(fmt, img);
                    Log("剪贴板图片已同步 (" + (bl / 1024) + " KB)");
                    break;
                }
                case M.Ping: break;
                case M.Bye:
                {
                    if (link != null) link.Die("对方退出");
                    break;
                }
            }
        }

        public void OnLinkDead(Link dead, string reason)
        {
            lock (st)
            {
                if (link == dead) link = null;
                forwarding = false;
                remoteHere = false;
            }
            if (reason != null)
                Log("连接断开: " + reason + (host != null ? ", 自动重连中" : ", 继续等待对方"));
        }

        // ============================ 注入 ============================

        void InjectAbsMove(int x, int y)
        {
            Native.INPUT[] inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.dx = (int)((long)x * 65535 / Math.Max(1, cx - 1));
            inp[0].u.mi.dy = (int)((long)y * 65535 / Math.Max(1, cy - 1));
            inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE;
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        void InjectBtn(byte act, int data)
        {
            Native.INPUT[] inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.mouseData = unchecked((uint)data);
            switch (act)
            {
                case 0: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_LEFTDOWN; break;
                case 1: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_LEFTUP; break;
                case 2: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_RIGHTDOWN; break;
                case 3: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_RIGHTUP; break;
                case 4: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_MIDDLEDOWN; break;
                case 5: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_MIDDLEUP; break;
                case 6: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_XDOWN; break;
                case 7: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_XUP; break;
                case 8: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_WHEEL; break;
                case 9: inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_HWHEEL; break;
                default: return;
            }
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        void InjectKey(uint scanCode, byte flags)
        {
            Native.INPUT[] inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].u.ki.wVk = 0;
            inp[0].u.ki.wScan = (ushort)scanCode;
            inp[0].u.ki.dwFlags = Native.KEYEVENTF_SCANCODE;
            if ((flags & 1) != 0) inp[0].u.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
            if ((flags & 2) != 0) inp[0].u.ki.dwFlags |= Native.KEYEVENTF_KEYUP;
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // ============================ 剪贴板 ============================

        void OnClipboardChanged()
        {
            if (link == null || !link.Connected) return;
            string text = null;
            byte[] img = null;
            uint imgFmt = 0;
            if (!OpenClipboardRetry()) return;
            try
            {
                if (Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT))
                {
                    text = ReadTextLocked();
                }
                else if (Native.IsClipboardFormatAvailable(Native.CF_DIB))
                {
                    img = ReadBytesLocked(Native.CF_DIB);
                    imgFmt = Native.CF_DIB;
                }
                else if (Native.IsClipboardFormatAvailable(Native.CF_DIBV5))
                {
                    img = ReadBytesLocked(Native.CF_DIBV5);
                    imgFmt = Native.CF_DIBV5;
                }
            }
            finally { Native.CloseClipboard(); }

            if (!String.IsNullOrEmpty(text) && text.Length <= 2000000)
            {
                bool echo;
                lock (st)
                {
                    echo = (lastPeerClip != null && lastPeerClip == text);
                    if (echo) lastPeerClip = null;   // 只吃一次, 之后用户再复制同样内容也会同步
                }
                if (echo) return;
                byte[] b = Encoding.Unicode.GetBytes(text);
                byte[] p = new byte[4 + b.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(b.Length), 0, p, 0, 4);
                Buffer.BlockCopy(b, 0, p, 4, b.Length);
                link.Send(M.ClipText, p);
                return;
            }

            if (img != null && img.Length > 0 && img.Length <= MaxClipImage)
            {
                bool echo;
                lock (st)
                {
                    echo = SameBytes(lastPeerImage, img);
                    if (echo) lastPeerImage = null;
                }
                if (echo) return;
                byte[] p = new byte[5 + img.Length];
                p[0] = (byte)(imgFmt == Native.CF_DIB ? 0 : 1);
                Buffer.BlockCopy(BitConverter.GetBytes(img.Length), 0, p, 1, 4);
                Buffer.BlockCopy(img, 0, p, 5, img.Length);
                link.Send(M.ClipImage, p);
                Log("剪贴板图片已发送 (" + (img.Length / 1024) + " KB)");
            }
        }

        // 下面两个 Read* 方法要求调用方已打开剪贴板
        string ReadTextLocked()
        {
            IntPtr h = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = Native.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                int len = Native.lstrlenW(p);
                if (len <= 0 || len > 2000000) return null;
                return Marshal.PtrToStringUni(p, len);
            }
            finally { Native.GlobalUnlock(h); }
        }

        byte[] ReadBytesLocked(uint fmt)
        {
            IntPtr h = Native.GetClipboardData(fmt);
            if (h == IntPtr.Zero) return null;
            long sz = (long)Native.GlobalSize(h);
            if (sz <= 0 || sz > MaxClipImage) return null;
            int len = (int)sz;
            IntPtr p = Native.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                byte[] b = new byte[len];
                Marshal.Copy(p, b, 0, len);
                return b;
            }
            finally { Native.GlobalUnlock(h); }
        }

        void SetClipImage(uint fmt, byte[] img)
        {
            if (!OpenClipboardRetry()) return;
            try
            {
                Native.EmptyClipboard();
                IntPtr h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)img.Length);
                IntPtr p = Native.GlobalLock(h);
                Marshal.Copy(img, 0, p, img.Length);
                Native.GlobalUnlock(h);
                if (Native.SetClipboardData(fmt, h) == IntPtr.Zero)
                    Native.GlobalFree(h);
            }
            catch { }
            finally { Native.CloseClipboard(); }
        }

        static bool IsVirtualNic(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return true;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
            string d = (nic.Name + " " + nic.Description).ToLowerInvariant();
            string[] kw = new string[] {
                "vmware", "virtualbox", "virtual ethernet", "hyper-v", "vethernet",
                "wsl", "tap-", "tap ", "openvpn", "tailscale", "zerotier",
                "bluestacks", "isatap", "teredo", "回环"
            };
            foreach (string k in kw) if (d.Contains(k)) return true;
            return false;
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        void SetClipText(string text)
        {
            if (!OpenClipboardRetry()) return;
            try
            {
                Native.EmptyClipboard();
                byte[] b = Encoding.Unicode.GetBytes(text);
                IntPtr h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)(b.Length + 2));
                IntPtr p = Native.GlobalLock(h);
                Marshal.Copy(b, 0, p, b.Length);
                Marshal.WriteInt16(p, b.Length, 0);
                Native.GlobalUnlock(h);
                if (Native.SetClipboardData(Native.CF_UNICODETEXT, h) == IntPtr.Zero)
                    Native.GlobalFree(h);
            }
            catch { }
            finally { Native.CloseClipboard(); }
        }

        bool OpenClipboardRetry()
        {
            for (int i = 0; i < 20; i++)
            {
                if (Native.OpenClipboard(hwnd)) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        // ============================ 杂项 ============================

        internal byte[] BuildHello()
        {
            byte[] n = Encoding.UTF8.GetBytes(Environment.MachineName);
            if (n.Length > 200) Array.Resize(ref n, 200);
            byte[] p = new byte[2 + n.Length];
            p[0] = (byte)n.Length;
            Buffer.BlockCopy(n, 0, p, 1, n.Length);
            p[1 + n.Length] = (byte)(isLeft ? 0 : 1);
            return p;
        }

        void RefreshMetrics()
        {
            cx = Native.GetSystemMetrics(0);
            cy = Native.GetSystemMetrics(1);
        }

        ushort NormY(int y) { return (ushort)((long)y * 65535 / Math.Max(1, cy - 1)); }
        int DenormY(ushort n) { return (int)((int)n * Math.Max(1, cy - 1) / 65535); }

        public static void Log(string s)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s);
        }

        void OnCancel(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            exiting = true;
            try { if (link != null) { link.Send(M.Bye, null); link.Die(null); } } catch { }
            try { if (hMouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(hMouseHook); } catch { }
            try { if (hKeyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(hKeyHook); } catch { }
            try { if (hwnd != IntPtr.Zero) Native.RemoveClipboardFormatListener(hwnd); } catch { }
            Log("已退出。");
            Environment.Exit(0);
        }
    }

    // ================================ TCP 连接 ================================

    class Link
    {
        readonly App app;
        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly BlockingCollection<byte[]> sendQ = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly object dieLock = new object();
        volatile bool dead;
        bool overflowWarned;

        public Link(App app, TcpClient client)
        {
            this.app = app;
            this.client = client;
            client.NoDelay = true;              // 关 Nagle: 流畅度第一关键
            stream = client.GetStream();
            stream.ReadTimeout = 20000;         // 心跳超时兜底
        }

        public bool Connected { get { return !dead; } }

        public void Send(byte type, byte[] payload)
        {
            if (dead) return;
            int pl = payload == null ? 0 : payload.Length;
            byte[] frame = new byte[5 + pl];
            Buffer.BlockCopy(BitConverter.GetBytes((int)(1 + pl)), 0, frame, 0, 4);
            frame[4] = type;
            if (pl > 0) Buffer.BlockCopy(payload, 0, frame, 5, pl);
            if (sendQ.Count > 8192)
            {
                if (!overflowWarned) { overflowWarned = true; App.Log("发送队列积压, 丢帧(对方卡死?)"); }
                return;
            }
            try { sendQ.Add(frame); } catch { }
        }

        public void Run()
        {
            Thread w = new Thread(WriterLoop);
            w.IsBackground = true;
            w.Start();
            Send(M.Hello, app.BuildHello());
            try
            {
                byte[] lenBuf = new byte[4];
                while (!dead)
                {
                    ReadExact(lenBuf, 4);
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len < 1 || len > 64 * 1024 * 1024) throw new IOException("帧头异常");
                    byte[] body = new byte[len];
                    ReadExact(body, len);
                    byte[] payload = new byte[len - 1];
                    Buffer.BlockCopy(body, 1, payload, 0, len - 1);
                    app.OnMessage(body[0], payload);
                }
            }
            catch (Exception e)
            {
                Die("读取中断: " + e.Message);
                return;
            }
            Die(null);
        }

        void WriterLoop()
        {
            try
            {
                List<byte[]> batch = new List<byte[]>(128);
                byte[] ping = BuildFrame(M.Ping, null);
                while (!dead)
                {
                    byte[] first;
                    if (!sendQ.TryTake(out first, 5000, cts.Token))
                    {
                        stream.Write(ping, 0, ping.Length);   // 心跳保活 + 检测死连接
                        stream.Flush();
                        continue;
                    }
                    batch.Clear();
                    batch.Add(first);
                    byte[] more;
                    while (batch.Count < 128 && sendQ.TryTake(out more)) batch.Add(more);
                    int total = 0;
                    for (int i = 0; i < batch.Count; i++) total += batch[i].Length;
                    byte[] buf = new byte[total];
                    int off = 0;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        Buffer.BlockCopy(batch[i], 0, buf, off, batch[i].Length);
                        off += batch[i].Length;
                    }
                    stream.Write(buf, 0, buf.Length);        // 合并批量一次写出, 减少 syscall
                    stream.Flush();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Die("发送中断: " + e.Message); }
        }

        static byte[] BuildFrame(byte type, byte[] payload)
        {
            int pl = payload == null ? 0 : payload.Length;
            byte[] frame = new byte[5 + pl];
            Buffer.BlockCopy(BitConverter.GetBytes((int)(1 + pl)), 0, frame, 0, 4);
            frame[4] = type;
            if (pl > 0) Buffer.BlockCopy(payload, 0, frame, 5, pl);
            return frame;
        }

        void ReadExact(byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = stream.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException("对方关闭连接");
                off += r;
            }
        }

        public void Dispose(string reason)
        {
            try { Send(M.Bye, null); Thread.Sleep(100); } catch { }
            Die(reason);
        }

        public void Die(string reason)
        {
            lock (dieLock)
            {
                if (dead) return;
                dead = true;
            }
            try { cts.Cancel(); } catch { }
            try { sendQ.CompleteAdding(); } catch { }
            try { stream.Close(); } catch { }
            try { client.Close(); } catch { }
            app.OnLinkDead(this, reason);
        }
    }

    // ================================ Win32 ================================

    static class Native
    {
        // 常量
        public const int WH_MOUSE_LL = 14;
        public const int WH_KEYBOARD_LL = 13;
        public const uint LLMHF_INJECTED = 0x0001;
        public const uint LLKHF_INJECTED = 0x0010;
        public const uint LLKHF_UP = 0x0080;
        public const uint LLKHF_EXTENDED = 0x0001;
        public const int VK_F12 = 0x7B;
        public const int VK_RCONTROL = 0xA3;

        public const uint WM_INPUT = 0x00FF;
        public const uint WM_CLIPBOARDUPDATE = 0x031D;
        public const uint WM_DISPLAYCHANGE = 0x007E;
        public const uint WM_DESTROY = 0x0002;

        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_RBUTTONDOWN = 0x0204;
        public const uint WM_RBUTTONUP = 0x0205;
        public const uint WM_MBUTTONDOWN = 0x0207;
        public const uint WM_MBUTTONUP = 0x0208;
        public const uint WM_MOUSEWHEEL = 0x020A;
        public const uint WM_XBUTTONDOWN = 0x020B;
        public const uint WM_XBUTTONUP = 0x020C;
        public const uint WM_MOUSEHWHEEL = 0x020E;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_SYSKEYDOWN = 0x0104;
        public const uint WM_SYSKEYUP = 0x0105;

        public const uint CF_UNICODETEXT = 13;
        public const uint CF_DIB = 8;
        public const uint CF_DIBV5 = 17;
        public const uint GMEM_MOVEABLE = 0x0002;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDEV_INPUTSINK = 0x00000100;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080;
        public const uint MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        // 委托
        public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr HookProcDelegate(int code, IntPtr wParam, IntPtr lParam);

        // 结构
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        // P/Invoke
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern int UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
        [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr hMem);
        [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr hMem);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int lstrlenW(IntPtr lpString);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
