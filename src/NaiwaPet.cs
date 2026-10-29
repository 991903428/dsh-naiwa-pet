using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NaiwaDesktop
{
    internal static class Native
    {
        internal const string Title="NaiwaPet.Plus.v1";
        internal const int Wake=0x8001, Status=0x8002, Command=0x8004, Info=0x8006;
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X,Y; internal Point(int x,int y){X=x;Y=y;} }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int W,H; internal Size(int w,int h){W=w;H=h;} }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential,Pack=1)] internal struct Blend { internal byte Op,Flags,Alpha,Format; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
        {
            internal uint Size; internal int Width,Height; internal ushort Planes,BitCount;
            internal uint Compression,SizeImage; internal int XPels,YPels; internal uint Used,Important;
        }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls,string text);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h,int m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr h,StringBuilder name,int count);
        [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] internal static extern int DwmRect(IntPtr h,int attr,out Rect value,int size);
        [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] internal static extern int DwmInt(IntPtr h,int attr,out int value,int size);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h,out Rect r);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr h,int index);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h,IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr h,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool UpdateLayeredWindow(IntPtr h,IntPtr dest,ref Point position,ref Size size,IntPtr src,ref Point origin,uint key,ref Blend blend,uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern int GetGuiResources(IntPtr process,int flags);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll",SetLastError=true)] internal static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)] internal static extern bool GetTokenInformation(IntPtr token,int info,IntPtr data,int length,out int needed);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] internal static extern bool ConvertSidToStringSid(IntPtr sid,out IntPtr text);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        internal static string Integrity()
        {
            IntPtr token=IntPtr.Zero;
            try
            {
                if(!OpenProcessToken(GetCurrentProcess(),0x0008,out token))return "unknown";
                int needed;
                GetTokenInformation(token,25,IntPtr.Zero,0,out needed);
                if(needed<=0)return "unknown";
                IntPtr buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(needed);
                try
                {
                    if(!GetTokenInformation(token,25,buffer,needed,out needed))return "unknown";
                    IntPtr sid=System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer);
                    IntPtr text;if(!ConvertSidToStringSid(sid,out text))return "unknown";
                    try
                    {
                        string value=System.Runtime.InteropServices.Marshal.PtrToStringUni(text);
                        if(value=="S-1-16-4096")return "low";
                        if(value=="S-1-16-8192")return "medium";
                        if(value=="S-1-16-12288")return "high";
                        if(value=="S-1-16-16384")return "system";
                        return value;
                    }
                    finally{LocalFree(text);}
                }
                finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
            }
            catch{return "unknown";}
            finally{if(token!=IntPtr.Zero)CloseHandle(token);}
        }
    }

    internal static class Program
    {
        // Launcher pid when the pet is started by the DSH plugin, so the pet can leave when DSH does.
        internal static int ParentPid;
        // Debug diary for the watchdog, written only when the launcher asks for it.
        internal static string WatchLog;
        // Heartbeat file that the launcher keeps touching; when it goes stale the pet leaves.
        internal static string HeartbeatPath;
        internal static double HeartbeatTimeout=8;

        /// <summary>读 --parent-pid 1234 与 --parent-pid=1234 两种写法；没有就返回 0。</summary>
        internal static int ParseParentPid(string[] args)
        {
            if(args==null)return 0;
            for(int i=0;i<args.Length;i++)
            {
                string value=null;
                if(args[i]=="--parent-pid"&&i+1<args.Length)value=args[i+1];
                else if(args[i]!=null&&args[i].StartsWith("--parent-pid=",StringComparison.Ordinal))value=args[i].Substring(13);
                int pid;
                if(value!=null&&int.TryParse(value,out pid)&&pid>0)return pid;
            }
            return 0;
        }

        /// <summary>读 --name value 形式的命令行参数；没有就返回 null。</summary>
        internal static string ParseValue(string[] args,string name)
        {
            if(args==null||name==null)return null;
            for(int i=0;i<args.Length;i++)
            {
                if(args[i]==name&&i+1<args.Length)return args[i+1];
                if(args[i]!=null&&args[i].StartsWith(name+"=",StringComparison.Ordinal))return args[i].Substring(name.Length+1);
            }
            return null;
        }

        /// <summary>父进程是否还在；能读到启动时刻时顺便排除 PID 复用，读不到就只看它是否还存在。</summary>
        internal static bool ParentAlive(int pid,DateTime startedAt)
        {
            if(pid<=0)return false;
            try
            {
                using(Process parent=Process.GetProcessById(pid))
                {
                    if(parent.HasExited)return false;
                    if(startedAt==DateTime.MinValue)return true;
                    try{if(parent.StartTime!=startedAt)return false;}catch{return true;}
                    return true;
                }
            }
            catch{return false;}
        }

        /// <summary>心跳文件多久没被写过；文件不存在时返回 +∞。</summary>
        internal static double HeartbeatAge(string path)
        {
            if(path==null)return double.PositiveInfinity;
            try
            {
                if(!File.Exists(path))return double.PositiveInfinity;
                return (DateTime.UtcNow-File.GetLastWriteTimeUtc(path)).TotalSeconds;
            }
            catch{return double.PositiveInfinity;}
        }

        /// <summary>把一次性结果写到文件；目标写 "-" 时直接写标准输出（给插件免临时文件用）。</summary>
        private static void WriteResult(string target,string text)
        {
            if(target=="-")
            {
                byte[] bytes=Encoding.UTF8.GetBytes(text);
                using(Stream stdout=Console.OpenStandardOutput()){stdout.Write(bytes,0,bytes.Length);stdout.Flush();}
                return;
            }
            File.WriteAllText(target,text,Encoding.UTF8);
        }

        [STAThread] private static void Main(string[] args)
        {
            Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            ParentPid=ParseParentPid(args);
            WatchLog=ParseValue(args,"--parent-log");
            HeartbeatPath=ParseValue(args,"--heartbeat");
            string timeout=ParseValue(args,"--heartbeat-timeout");
            double seconds;
            if(timeout!=null&&double.TryParse(timeout,out seconds)&&seconds>=2)HeartbeatTimeout=seconds;
            if(args.Length>=2 && args[0]=="--self-test") { Tests.Run(args[1]); return; }
            if(args.Length>=2 && args[0]=="--demo") { Tests.Demo(args[1]); return; }
            if(args.Length>=2 && args[0]=="--render-card") { Tests.RenderCard(args[1]); return; }
            if(args.Length>=2 && args[0]=="--probe")
            {
                IntPtr h=Native.FindWindow(null,Native.Title); Native.Rect r; Native.GetWindowRect(h,out r);
                StringBuilder probe=new StringBuilder();
                probe.AppendLine("Found="+(h!=IntPtr.Zero));
                probe.AppendLine("Visible="+Native.IsWindowVisible(h));
                probe.AppendLine("Bounds="+r.Left+","+r.Top+","+r.Right+","+r.Bottom);
                probe.AppendLine("State="+Native.SendMessage(h,Native.Status,IntPtr.Zero,IntPtr.Zero));
                WriteResult(args[1],probe.ToString()); return;
            }
            if(args.Length>=2 && args[0]=="--fetch-balance")
            {
                BalanceConfig config=BalanceConfig.Load();
                BalanceResult result=BalanceService.FetchSync(config);
                StringBuilder text=new StringBuilder();
                text.AppendLine(result.Ok?"OK":"FAIL");
                text.AppendLine("Mode="+config.Mode);
                text.AppendLine("Account="+result.Account);
                text.AppendLine("Currency="+result.Currency);
                text.AppendLine("Total="+result.Total.ToString("F2"));
                text.AppendLine("Granted="+result.Granted.ToString("F2"));
                text.AppendLine("ToppedUp="+result.ToppedUp.ToString("F2"));
                text.AppendLine("Error="+result.Error);
                text.AppendLine("Integrity="+Native.Integrity());
                WriteResult(args[1],text.ToString());
                return;
            }
            if(args.Length>=2 && args[0]=="--do")
            {
                int command=0;
                if(args[1]=="balance")command=1;
                else if(args[1]=="laugh")command=2;
                else if(args[1]=="poke")command=3;
                else if(args[1]=="hide-card")command=4;
                else if(args[1]=="stand")command=5;
                else if(args[1]=="calm")command=6;
                if(command==0)return;
                IntPtr handle=Native.FindWindow(null,Native.Title);
                if(handle==IntPtr.Zero)Environment.Exit(3);
                Native.SendMessage(handle,Native.Command,(IntPtr)command,IntPtr.Zero);
                return;
            }
            if(args.Length==1 && args[0]=="--quit") { IntPtr running=Native.FindWindow(null,Native.Title); if(running==IntPtr.Zero)Environment.Exit(3); Native.PostMessage(running,0x10,IntPtr.Zero,IntPtr.Zero); return; }
            bool fresh;
            using(Mutex mutex=new Mutex(true,"Local\\NaiwaPetPlus.v1",out fresh))
            {
                if(!fresh){Native.PostMessage(Native.FindWindow(null,Native.Title),Native.Wake,IntPtr.Zero,IntPtr.Zero);return;}
                try{BalanceConfig.EnsureTemplate();}catch{}
                Application.ThreadException+=delegate(object s,ThreadExceptionEventArgs e){Report(e.Exception); Application.Exit();};
                try { using(Assets assets=new Assets(AppDomain.CurrentDomain.BaseDirectory)) using(PetWindow pet=new PetWindow(assets,false)){Application.Run(pet);} }
                catch(Exception e){Report(e);}
            }
        }
        private static void Report(Exception e)
        {
            try{Directory.CreateDirectory(Settings.Folder);File.AppendAllText(Path.Combine(Settings.Folder,"error.log"),DateTime.Now.ToString("s")+" "+e+Environment.NewLine);}catch{}
            MessageBox.Show("奶蛙暂时没能启动。请确认程序旁边的 assets 素材文件夹和 原版笑声.wav 完整。\n\n"+e.Message,"奶蛙桌宠",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
    }

    internal sealed class Assets:IDisposable
    {
        internal readonly int Width,Height,Count,EyeCount,BlinkCount;
        internal readonly double Fps,Duration,BlinkFps;
        private readonly byte[][] encoded;
        private readonly Bitmap[] eyes;
        private readonly Bitmap[][] blinks;
        private readonly Dictionary<int,Bitmap> cache=new Dictionary<int,Bitmap>();
        private readonly Queue<int> order=new Queue<int>();
        internal readonly string AudioPath;
        internal int CacheCount{get{return cache.Count;}}
        internal Assets(string folder)
        {
            // Asset sources, in order: the original 素材.nwp archive, then an extracted assets/ folder.
            string archive=Path.Combine(folder,"素材.nwp");
            string unpacked=Path.Combine(folder,"assets");
            AudioPath=Path.Combine(folder,"原版笑声.wav");
            if(!File.Exists(AudioPath))AudioPath=Path.Combine(unpacked,"laugh.wav");
            using(ZipArchive zip=File.Exists(archive)?ZipFile.OpenRead(archive):null)
            {
                Func<string,byte[]> read=delegate(string name)
                {
                    if(zip!=null)return Read(zip,name);
                    string path=Path.Combine(unpacked,name.Replace('/',Path.DirectorySeparatorChar));
                    if(!File.Exists(path))throw new InvalidDataException("缺少动作素材："+name);
                    return File.ReadAllBytes(path);
                };
                Dictionary<string,object> meta;
                using(StreamReader reader=new StreamReader(new MemoryStream(read("metadata.json")))) meta=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                Width=Convert.ToInt32(meta["width"]);Height=Convert.ToInt32(meta["height"]);Count=Convert.ToInt32(meta["frames"]);Fps=Convert.ToDouble(meta["fps"]);EyeCount=Convert.ToInt32(meta["eyeFrames"]);Duration=Count/Fps;
                if(Width<1||Height<1||Width>1024||Height>1024||Count<1||Count>2000||Fps<1||Fps>120||EyeCount<2||EyeCount>60)throw new InvalidDataException("素材信息无效");
                eyes=new Bitmap[EyeCount];
                for(int i=0;i<EyeCount;i++)eyes[i]=Decode(read("look/"+i.ToString("00")+".png"));
                BlinkCount=Convert.ToInt32(meta["blinkFrames"]);BlinkFps=Convert.ToDouble(meta["blinkFps"]);
                if(BlinkCount<2||BlinkCount>60||BlinkFps<1||BlinkFps>120)throw new InvalidDataException("眨眼素材信息无效");
                blinks=new Bitmap[2][];
                for(int mode=0;mode<2;mode++){blinks[mode]=new Bitmap[BlinkCount];for(int i=0;i<BlinkCount;i++)blinks[mode][i]=Decode(read("blink/"+mode+"/"+i.ToString("00")+".png"));}
                encoded=new byte[Count][];
                for(int i=0;i<Count;i++)encoded[i]=read("laugh/"+i.ToString("0000")+".png");
            }
            // Warm up the first frames so the first left click starts instantly.
            Frame(0);Frame(1);Frame(2);
        }
        private static byte[] Read(ZipArchive zip,string name)
        { ZipArchiveEntry entry=zip.GetEntry(name);if(entry==null)throw new InvalidDataException("缺少动作素材："+name);using(Stream s=entry.Open())using(MemoryStream m=new MemoryStream()){s.CopyTo(m);return m.ToArray();} }
        private Bitmap Decode(byte[] data)
        {
            using(MemoryStream stream=new MemoryStream(data)) using(Image image=Image.FromStream(stream))
            {
                if(image.Width!=Width||image.Height!=Height)throw new InvalidDataException("动作尺寸不一致");
                Bitmap b=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
                using(Graphics g=Graphics.FromImage(b)){g.CompositingMode=CompositingMode.SourceCopy;g.DrawImageUnscaled(image,0,0);}return b;
            }
        }
        internal Bitmap Eye(double value){return eyes[Math.Max(0,Math.Min(EyeCount-1,(int)Math.Round(value*(EyeCount-1))))];}
        internal Bitmap Blink(double look,int frame){return blinks[look>.5?1:0][Math.Max(0,Math.Min(BlinkCount-1,frame))];}
        internal Bitmap Frame(int index)
        {
            index=Math.Max(0,Math.Min(Count-1,index));Bitmap b;
            if(cache.TryGetValue(index,out b))return b;
            b=Decode(encoded[index]);cache.Add(index,b);order.Enqueue(index);
            while(order.Count>12){int old=order.Dequeue();cache[old].Dispose();cache.Remove(old);}return b;
        }
        public void Dispose(){if(eyes!=null)foreach(Bitmap b in eyes)if(b!=null)b.Dispose();if(blinks!=null)foreach(Bitmap[] bank in blinks)foreach(Bitmap b in bank)if(b!=null)b.Dispose();foreach(Bitmap b in cache.Values)b.Dispose();cache.Clear();}
    }

    internal sealed class Motion
    {
        private const double PokeDuration=.95;
        internal double Look,Start=-1,Elapsed,Clock,BlinkStart=-1,NextBlink=4,WakeStart=-1;
        internal double PokeStart=-1,PokeX=144,PokeY=205,LandingStart=-1,DragAngle;
        internal bool Resting,Holding;
        internal bool Playing{get{return Start>=0;}}
        internal bool Blinking{get{return BlinkStart>=0;}}
        internal bool Poking{get{return PokeStart>=0;}}
        internal bool Landing{get{return LandingStart>=0;}}
        internal bool Waking{get{return WakeStart>=0;}}
        internal bool Moving{get{return Holding||Landing||Math.Abs(DragAngle)>.02;}}
        internal double PokeAmount{get{double t=Clock-PokeStart;if(!Poking||t<0||t>=PokeDuration)return 0;return t<.12?.60*Math.Sin(t/.12*Math.PI/2):.60*Math.Exp(-4.8*(t-.12))*Math.Cos(18*(t-.12));}}
        internal double Squash{get{double t=Clock-LandingStart;return Landing&&t>=0?.055*Math.Exp(-6*t)*Math.Sin(19*t):0;}}
        internal int PlayCount;
        private double previous=-1,dragTarget,dragVelocity,lastDragTime;
        private readonly Random random=new Random();
        internal void Update(double now,bool over,double duration,double blinkDuration)
        {
            Clock=now;double dt=previous<0?0:Math.Min(.05,Math.Max(0,now-previous));previous=now;
            double goal=over?1:0,step=dt/.18;Look+=Math.Max(-step,Math.Min(step,goal-Look));
            if(Playing){Elapsed=Math.Max(0,now-Start);if(Elapsed>=duration){Start=-1;Elapsed=duration;Resting=true;}}
            if(Waking&&now-WakeStart>=.24)WakeStart=-1;
            if(Poking&&now-PokeStart>=PokeDuration)PokeStart=-1;
            if(Landing&&now-LandingStart>=.9)LandingStart=-1;
            if(Blinking&&now-BlinkStart>=blinkDuration){BlinkStart=-1;ScheduleBlink(now);}
            if(!Playing&&!Resting&&!Holding&&!Landing&&!Poking&&!Waking&&!Blinking&&now>=NextBlink)BlinkStart=now;
            if(!Holding||now-lastDragTime>.10)dragTarget=0;
            // Critically damped tilt; bounded steps keep long timer gaps stable.
            int steps=Math.Max(1,(int)Math.Ceiling(dt/.008));double stepTime=dt/steps;
            for(int i=0;i<steps;i++){dragVelocity+=(160*(dragTarget-DragAngle)-25*dragVelocity)*stepTime;DragAngle+=dragVelocity*stepTime;}
            DragAngle=Math.Max(-7,Math.Min(7,DragAngle));
            if(!Holding&&Math.Abs(DragAngle)<.02&&Math.Abs(dragVelocity)<.05){DragAngle=0;dragVelocity=0;}
        }
        private void ScheduleBlink(double now){NextBlink=now+4+random.NextDouble()*4;}
        private void ClearEffects(double now){Clock=now;BlinkStart=PokeStart=WakeStart=LandingStart=-1;Holding=false;DragAngle=dragTarget=dragVelocity=0;ScheduleBlink(now);}
        internal bool Laugh(double now){if(Playing)return false;ClearEffects(now);Resting=false;Start=now;Elapsed=0;PlayCount++;return true;}
        internal void Stop(){Start=-1;Elapsed=0;Resting=false;ClearEffects(Clock);}
        internal void Stand(double now){if(!Resting)return;Resting=false;ClearEffects(now);WakeStart=now;}
        internal static bool InBelly(double x,double y){double dx=(x-144)/69,dy=(y-211)/68;return dx*dx+dy*dy<=1;}
        internal bool Poke(double now,double x,double y){if(Playing||Resting||Holding||!InBelly(x,y))return false;ClearEffects(now);PokeStart=now;PokeX=x;PokeY=y;return true;}
        internal void BeginDrag(double now){Start=-1;Elapsed=0;ClearEffects(now);Holding=true;lastDragTime=now;}
        internal void Drag(double now,double dx){if(!Holding)BeginDrag(now);double dt=Math.Max(1.0/120,now-lastDragTime);lastDragTime=now;dragTarget=Math.Max(-7,Math.Min(7,dx/dt*.013));Clock=now;}
        internal void EndDrag(double now){if(!Holding)return;Holding=false;dragTarget=0;Clock=now;LandingStart=now;ScheduleBlink(now);}
        internal void Suspend(double now){Start=-1;Elapsed=0;Look=0;ClearEffects(now);}
    }

    internal static class Trace
    {
        internal static readonly bool Enabled=File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"trace-on"));
        internal static void Line(string text){if(!Enabled)return;try{File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"trace.log"),DateTime.Now.ToString("HH:mm:ss.fff")+" "+text+Environment.NewLine);}catch{}}
    }

    internal static class Fullscreen
    {
        internal static bool Covers(Rectangle window,Rectangle screen,int style,string name)
        {
            if((style&0x00c00000)!=0||name=="Progman"||name=="WorkerW"||name=="Shell_TrayWnd"||name=="Shell_SecondaryTrayWnd")return false;
            const int tolerance=2;
            return window.Left<=screen.Left+tolerance&&window.Top<=screen.Top+tolerance&&window.Right>=screen.Right-tolerance&&window.Bottom>=screen.Bottom-tolerance;
        }
        internal static bool IsActive(IntPtr window,IntPtr pet,Rectangle screen)
        {
            if(window==IntPtr.Zero||window==pet||!Native.IsWindowVisible(window)||Native.IsIconic(window))return false;
            Native.Rect r;if(!Native.GetWindowRect(window,out r))return false;
            try{int cloaked;if(Native.DwmInt(window,14,out cloaked,4)==0&&cloaked!=0)return false;Native.Rect frame;if(Native.DwmRect(window,9,out frame,16)==0)r=frame;}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
            StringBuilder cls=new StringBuilder(256);Native.GetClassName(window,cls,cls.Capacity);
            return Covers(Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom),screen,Native.GetWindowLong(window,-16),cls.ToString());
        }
    }

    internal sealed class FullscreenGate
    {
        internal bool Hidden;
        private bool candidate;
        private double since;
        internal bool Update(bool detected,double now)
        {
            if(candidate!=detected){candidate=detected;since=now;}
            if(candidate!=Hidden&&now-since>=(candidate?.15:.25)){Hidden=candidate;return true;}return false;
        }
        internal void Reset(){Hidden=candidate=false;since=0;}
        internal void Set(bool value,double now){Hidden=candidate=value;since=now;}
    }

    internal sealed class Gesture
    {
        internal bool Pressed,Dragged;
        internal Point Origin;
        private Point lastClickPoint;
        private double lastClick=-100;
        internal void Down(Point p){Pressed=true;Dragged=false;Origin=p;}
        internal void Move(Point p){if(Pressed&&(Math.Abs(p.X-Origin.X)>5||Math.Abs(p.Y-Origin.Y)>5))Dragged=true;}
        internal bool Up(Point p,double now)
        {
            if(!Pressed)return false;Pressed=false;
            if(Dragged){lastClick=-100;return false;}
            bool twice=now-lastClick<=SystemInformation.DoubleClickTime/1000.0&&Math.Abs(p.X-lastClickPoint.X)<=SystemInformation.DoubleClickSize.Width&&Math.Abs(p.Y-lastClickPoint.Y)<=SystemInformation.DoubleClickSize.Height;
            lastClick=twice?-100:now;lastClickPoint=p;return twice;
        }
        internal void Cancel(){Pressed=false;Dragged=false;lastClick=-100;}
    }

    internal sealed class Settings
    {
        internal int Scale=85,X,Y;internal bool Position,Sound=true,OnTop=true,AutoHide=true;
        internal string LeftAction="laugh";
        internal static string Folder{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NaiwaPetPlus");}}
        internal static Settings Load()
        {
            Settings s=new Settings();try{
                string file=Path.Combine(Folder,"settings.ini");if(!File.Exists(file))return s;
                foreach(string line in File.ReadAllLines(file)){string[] p=line.Split('=');int n;if(p.Length!=2)continue;
                    if(p[0]=="scale"&&int.TryParse(p[1],out n))s.Scale=Math.Max(50,Math.Min(150,n));
                    if(p[0]=="x"&&int.TryParse(p[1],out n)){s.X=n;s.Position=true;}
                    if(p[0]=="y"&&int.TryParse(p[1],out n))s.Y=n;
                    if(p[0]=="sound")s.Sound=p[1]=="True";
                    if(p[0]=="top")s.OnTop=p[1]!="False";
                    if(p[0]=="fullscreen")s.AutoHide=p[1]!="False";
                    if(p[0]=="left")s.LeftAction=p[1]=="poke"?"poke":"laugh";
                }
            }catch{}return s;
        }
        internal void Save(){try{Directory.CreateDirectory(Folder);File.WriteAllLines(Path.Combine(Folder,"settings.ini"),new string[]{"scale="+Scale,"x="+X,"y="+Y,"sound="+Sound,"top="+OnTop,"fullscreen="+AutoHide,"left="+LeftAction});}catch{}}
    }

    internal sealed class LayeredSurface:IDisposable
    {
        private IntPtr dc,bmp,old,bits;
        private byte[] buffer;
        private int width,height;
        internal void Update(IntPtr window,Bitmap image,Point location)
        {
            if(width!=image.Width||height!=image.Height)Create(image.Width,image.Height);
            BitmapData locked=image.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{Marshal.Copy(locked.Scan0,buffer,0,buffer.Length);Marshal.Copy(buffer,0,bits,buffer.Length);}finally{image.UnlockBits(locked);}
            Native.Point destination=new Native.Point(location.X,location.Y),origin=new Native.Point(0,0);Native.Size size=new Native.Size(width,height);
            Native.Blend blend=new Native.Blend{Op=0,Flags=0,Alpha=255,Format=1};
            if(!Native.UpdateLayeredWindow(window,IntPtr.Zero,ref destination,ref size,dc,ref origin,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private void Create(int w,int h)
        {
            Dispose();width=w;height=h;buffer=new byte[w*h*4];dc=Native.CreateCompatibleDC(IntPtr.Zero);
            Native.BitmapInfo info=new Native.BitmapInfo{Size=40,Width=w,Height=-h,Planes=1,BitCount=32,Compression=0,SizeImage=(uint)(w*h*4)};
            bmp=Native.CreateDIBSection(dc,ref info,0,out bits,IntPtr.Zero,0);
            if(bmp==IntPtr.Zero)throw new InvalidOperationException("无法创建透明窗口");old=Native.SelectObject(dc,bmp);
        }
        internal bool Hit(int x,int y){return buffer!=null&&x>=0&&y>=0&&x<width&&y<height&&buffer[(y*width+x)*4+3]>24;}
        public void Dispose(){if(dc!=IntPtr.Zero){if(old!=IntPtr.Zero)Native.SelectObject(dc,old);if(bmp!=IntPtr.Zero)Native.DeleteObject(bmp);Native.DeleteDC(dc);}dc=bmp=old=bits=IntPtr.Zero;width=height=0;buffer=null;}
    }

    internal static class Renderer
    {
        internal static Icon CreateIcon(Bitmap image)
        {
            using(Bitmap small=new Bitmap(64,64,PixelFormat.Format32bppArgb))
            {
                using(Graphics g=Graphics.FromImage(small)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(image,new Rectangle(6,0,52,64),new Rectangle(60,40,175,225),GraphicsUnit.Pixel);}
                IntPtr h=small.GetHicon();try{using(Icon borrowed=Icon.FromHandle(h))return (Icon)borrowed.Clone();}finally{Native.DestroyIcon(h);}
            }
        }
        internal static Bitmap Render(Assets assets,Motion m,int scale)
        {
            int w=assets.Width*scale/100,h=assets.Height*scale/100;
            Bitmap canvas=new Bitmap(w,h,PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                // Transforms move the supplied artwork; no replacement body or expression is drawn.
                if(m.Moving)
                {
                    float px=w*.5f,py=h*(m.Holding?.56f:.9565f),s=(float)m.Squash;
                    g.TranslateTransform(px,py-(m.Holding?2f*scale/100:0));g.RotateTransform((float)m.DragAngle);
                    g.ScaleTransform(m.Holding?1.008f:1+s,m.Holding?1.018f:1-s);g.TranslateTransform(-px,-py);
                }
                Rectangle rect=new Rectangle(0,0,w,h);
                if(m.Resting)Draw(g,assets.Frame(assets.Count-1),rect,1);
                else if(m.Waking){float t=(float)Math.Min(1,(m.Clock-m.WakeStart)/.24);Draw(g,assets.Frame(assets.Count-1),rect,1-t);Draw(g,assets.Eye(m.Look),rect,t);}
                else if(!m.Playing)
                {
                    Bitmap idle=m.Blinking?assets.Blink(m.Look,(int)((m.Clock-m.BlinkStart)*assets.BlinkFps)):assets.Eye(m.Look);
                    if(m.Poking)using(Bitmap poke=WarpBelly(idle,m.PokeX,m.PokeY,m.PokeAmount))Draw(g,poke,rect,1);else Draw(g,idle,rect,1);
                }
                else if(m.Elapsed<.10){Draw(g,assets.Eye(m.Look),rect,1);Draw(g,assets.Frame((int)(m.Elapsed*assets.Fps)),rect,(float)(m.Elapsed/.10));}
                else Draw(g,assets.Frame((int)(m.Elapsed*assets.Fps)),rect,1);
            }return canvas;
        }
        internal static Bitmap WarpBelly(Bitmap original,double cx,double cy,double amount)
        {
            int w=original.Width,h=original.Height;byte[] source=new byte[w*h*4];
            BitmapData locked=original.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{Marshal.Copy(locked.Scan0,source,0,source.Length);}finally{original.UnlockBits(locked);}
            byte[] result=(byte[])source.Clone();
            const double radiusX=96,radiusY=72;
            int left=Math.Max(0,(int)(cx-radiusX)),right=Math.Min(w-2,(int)(cx+radiusX)),top=Math.Max(105,(int)(cy-radiusY)),bottom=Math.Min(Math.Min(300,h-2),(int)(cy+radiusY));
            for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
            {
                double dx=x-cx,dy=y-cy,r=dx*dx/(radiusX*radiusX)+dy*dy/(radiusY*radiusY);if(r>=1)continue;
                // Include the belly outline, fading out before the face and feet.
                double neck=Math.Min(1,(y-105)/30.0),legs=Math.Min(1,(300-y)/20.0);
                double fade=neck*neck*(3-2*neck)*legs*legs*(3-2*legs);
                double fall=(1-r)*(1-r)*amount*fade;
                double sx=Math.Max(0,Math.Min(w-1.001,x+dx*fall)),sy=Math.Max(0,Math.Min(h-1.001,y+dy*fall*.6));
                int ix=Math.Max(0,Math.Min(w-2,(int)sx)),iy=Math.Max(0,Math.Min(h-2,(int)sy));double fx=sx-ix,fy=sy-iy;
                int dest=(y*w+x)*4,a=(iy*w+ix)*4,b=a+4,c=a+w*4,d=c+4;
                for(int channel=0;channel<4;channel++)result[dest+channel]=(byte)Math.Max(0,Math.Min(255,(source[a+channel]*(1-fx)+source[b+channel]*fx)*(1-fy)+(source[c+channel]*(1-fx)+source[d+channel]*fx)*fy));
            }
            Bitmap warped=new Bitmap(w,h,PixelFormat.Format32bppPArgb);locked=warped.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
            try{Marshal.Copy(result,0,locked.Scan0,result.Length);}finally{warped.UnlockBits(locked);}return warped;
        }
        private static void Draw(Graphics g,Bitmap b,Rectangle rect,float alpha)
        {
            if(alpha<=0)return;
            using(ImageAttributes a=new ImageAttributes())
            {
                a.SetWrapMode(WrapMode.TileFlipXY);if(alpha<1){ColorMatrix c=new ColorMatrix();c.Matrix33=alpha;a.SetColorMatrix(c);}
                g.DrawImage(b,rect,0,0,b.Width,b.Height,GraphicsUnit.Pixel,a);
            }
        }
    }

    internal sealed class PetWindow:Form
    {
        private readonly Assets assets;
        private readonly bool testing;
        private readonly Settings settings;
        internal readonly Motion Motion=new Motion();
        private readonly Gesture gesture=new Gesture();
        private readonly Stopwatch watch=Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly LayeredSurface surface=new LayeredSurface();
        private readonly SoundPlayer sound;
        private readonly Icon petIcon;
        private readonly ContextMenuStrip menu;
        internal ContextMenuStrip PetMenu{get{return menu;}}
        internal ToolStripItemCollection LeftActionMenu{get{return menu.Items;}}
        private readonly NotifyIcon tray;
        private ToolStripMenuItem soundItem,topItem,stopItem,fullscreenItem,leftActionItem;
        private Point dragWindow,lastDragPointer;
        private readonly FullscreenGate fullscreenGate=new FullscreenGate();
        private double nextFullscreenCheck;
        private readonly int parentPid;
        private readonly DateTime parentStart;
        private double nextParentCheck;
        private string lastVisual="";
        private bool disposing,menuOpen,ready;
        private IntPtr menuForeground;
        private long tickStamp;
        internal bool HiddenForFullscreen{get;private set;}
        internal int ScalePercent{get{return settings.Scale;}}
        internal bool SoundEnabled{get{return settings.Sound;}}
        internal string LeftAction{get{return settings.LeftAction;}}
        private double Now{get{return watch.Elapsed.TotalSeconds;}}

        internal PetWindow(Assets a,bool test)
        {
            assets=a;testing=test;settings=testing?new Settings():Settings.Load();
            parentPid=testing?0:Program.ParentPid;
            parentStart=ParentStartOf(parentPid);
            Text=Native.Title;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.StandardClick|ControlStyles.StandardDoubleClick,true);
            ClientSize=SizeFor(settings.Scale);TopMost=settings.OnTop;Cursor=Cursors.Hand;
            if(settings.Position)Location=new Point(settings.X,settings.Y);else Home();
            KeepVisible();
            petIcon=Renderer.CreateIcon(assets.Eye(0));
            Icon=petIcon;
            if(File.Exists(assets.AudioPath)){sound=new SoundPlayer(assets.AudioPath);sound.Load();}
            menu=BuildMenu();
            if(!testing){tray=new NotifyIcon{Icon=petIcon,Text="奶蛙 · 左键笑 / 右键菜单 / 拖动",ContextMenuStrip=menu,Visible=true};tray.DoubleClick+=delegate{ReturnHome();};}
            timer.Interval=16;timer.Tick+=delegate{Tick();};
            Shown+=delegate{ready=true;RenderNow(true);if(!testing){CheckFullscreen(true);timer.Start();}};
        }
        protected override bool ShowWithoutActivation{get{return true;}}
        private static DateTime ParentStartOf(int pid)
        {
            if(pid<=0)return DateTime.MinValue;
            try{using(Process parent=Process.GetProcessById(pid))return parent.StartTime;}catch{return DateTime.MinValue;}
        }
        protected override CreateParams CreateParams{get{CreateParams p=base.CreateParams;p.ExStyle|=0x80000|0x80|0x08000000;p.ClassStyle|=8;return p;}}
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==Native.Wake){ReturnHome();return;}
            if(m.Msg==Native.Command)
            {
                int command=m.WParam.ToInt32();
                if(command==1)ShowBalance();
                else if(command==2)Laugh();
                else if(command==3){Motion.Poke(Now,144,205);RenderNow(true);}
                else if(command==4)HideBalance();
                else if(command==5){if(Motion.Resting){Motion.Stand(Now);RenderNow(true);}}
                else if(command==6){StopLaugh();HideBalance();}
                m.Result=IntPtr.Zero;
                return;
            }
            if(m.Msg==Native.Info){m.Result=(IntPtr)(int)(tickStamp&0x3fffffff);return;}
            if(m.Msg==Native.Status){m.Result=(IntPtr)(HiddenForFullscreen?4:Motion.Resting?3:Motion.Playing?2:Motion.Look>.5?1:0);return;}
            if(m.Msg==0x21){m.Result=(IntPtr)3;return;}
            if(m.Msg==0x84 && ready && !gesture.Pressed)
            {
                long value=m.LParam.ToInt64();Point p=PointToClient(new Point((short)(value&0xffff),(short)((value>>16)&0xffff)));
                m.Result=surface.Hit(p.X,p.Y)?(IntPtr)1:new IntPtr(-1);return;
            }
            base.WndProc(ref m);
            if(m.Msg==0x7e && ready){KeepVisible();RenderNow(true);Save();}
        }
        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip result=new ContextMenuStrip{Font=new Font("Microsoft YaHei UI",10f)};
            leftActionItem=new ToolStripMenuItem("左键动作 · 捧腹大笑",null,delegate{ToggleLeftAction();});result.Items.Add(leftActionItem);
            stopItem=new ToolStripMenuItem("停止大笑 / 站起来",null,delegate{if(Motion.Resting){Motion.Stand(Now);RenderNow(true);}else StopLaugh();});result.Items.Add(stopItem);
            result.Items.Add(new ToolStripSeparator());
            result.Items.Add("查看余额",null,delegate{ShowBalance();});
            result.Items.Add(new ToolStripSeparator());
            soundItem=new ToolStripMenuItem("原版笑声",null,delegate{SetSound(!settings.Sound);});result.Items.Add(soundItem);
            ToolStripMenuItem sizes=new ToolStripMenuItem("大小");
            int[] values={65,85,110};string[] labels={"小号","标准","大号"};
            for(int i=0;i<values.Length;i++){int v=values[i];ToolStripMenuItem entry=new ToolStripMenuItem(labels[i],null,delegate{SetSize(v);});entry.Tag=v;sizes.DropDownItems.Add(entry);}result.Items.Add(sizes);
            topItem=new ToolStripMenuItem("总在最上面",null,delegate{TopMost=!TopMost;settings.OnTop=TopMost;Save();UpdateMenu();});result.Items.Add(topItem);
            fullscreenItem=new ToolStripMenuItem("全屏时自动隐藏",null,delegate{SetAutoHide(!settings.AutoHide);});result.Items.Add(fullscreenItem);
            result.Items.Add(new ToolStripSeparator());
            result.Items.Add("关于奶蛙",null,delegate{ShowAbout();});
            result.Items.Add("取消桌宠（退出）",null,delegate{Close();});
            result.Opening+=delegate{menuOpen=true;menuForeground=Native.GetForegroundWindow();UpdateMenu();};result.Closed+=delegate{menuOpen=false;};PetMenuTheme.Style(result);return result;
        }
        private void ShowAbout()
        {
            string text=string.Join(Environment.NewLine,new string[]{
                "奶蛙桌宠 · 增强版","",
                "形象与音效来自开源项目 Naiwa，作者 yugh6（抖音：握石竹）。",
                "https://github.com/yugh6/Naiwa","",
                "本改造供个人非商业使用，遵循 PolyForm Noncommercial 1.0.0 许可，",
                "请保留原作者署名与原项目地址。","",
                "左键：默认笑一个，可在右键菜单「左键动作」里改成戳肚子",
                "右键：更多互动（查看余额 / 左键动作 / 大小 / 退出）"});
            MessageBox.Show(text,"关于奶蛙",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        internal void SetLeftAction(string action)
        {
            settings.LeftAction=action=="poke"?"poke":"laugh";
            Save();UpdateMenu();
        }
        // One row that flips between the two left-click behaviours: no hidden submenu state.
        internal void ToggleLeftAction()
        {
            SetLeftAction(settings.LeftAction=="poke"?"laugh":"poke");
        }
        private void UpdateMenu()
        {
            if(menu==null)return;SetToggleMark(soundItem,settings.Sound);soundItem.Enabled=sound!=null;SetToggleMark(topItem,TopMost);stopItem.Enabled=Motion.Playing||Motion.Resting;SetToggleMark(fullscreenItem,settings.AutoHide);
            if(leftActionItem!=null)leftActionItem.Text=settings.LeftAction=="poke"?"左键动作 · 戳肚子":"左键动作 · 捧腹大笑";
            if(tray!=null)tray.Text=settings.LeftAction=="poke"?"奶蛙 · 左键戳肚子 / 右键菜单 / 拖动":"奶蛙 · 左键笑 / 右键菜单 / 拖动";
            foreach(ToolStripItem entry in menu.Items){ToolStripMenuItem p=entry as ToolStripMenuItem;if(p==null)continue;foreach(ToolStripItem child in p.DropDownItems){ToolStripMenuItem c=child as ToolStripMenuItem;if(c!=null&&c.Tag is int)c.Checked=(int)c.Tag==settings.Scale;}}
        }
        private static void SetToggleMark(ToolStripMenuItem item,bool selected)
        {
            // The theme draws the tick itself, so only the state has to be tracked here.
            item.Checked=selected;
        }
        internal void SetSound(bool enabled)
        {
            settings.Sound=enabled&&sound!=null;
            if(!settings.Sound&&sound!=null)sound.Stop();
            // Enabling sound takes effect at the next laugh, so audio never starts out of sync.
            UpdateMenu();Save();
        }
        internal bool Laugh()
        {
            if(HiddenForFullscreen)return false;
            if(!Motion.Laugh(Now))return false;
            if(sound!=null&&settings.Sound&&!testing)sound.Play();RenderNow(true);return true;
        }
        internal void StopLaugh(){Motion.Stop();if(sound!=null)sound.Stop();RenderNow(true);}

        private BalanceCard card;
        private double cardHideAt;
        internal void ShowBalance()
        {
            if(HiddenForFullscreen)return;
            BalanceConfig.EnsureTemplate();
            if(card==null||card.IsDisposed)
            {
                card=new BalanceCard(assets);
                card.RefreshRequested+=delegate{RefreshBalance();};
                card.CloseRequested+=delegate{HideBalance();};
            }
            card.ShowNear(Bounds);
            cardHideAt=Now+30;
            RefreshBalance();
        }
        private void HideBalance(){if(card!=null&&!card.IsDisposed)card.Close();}
        private void RefreshBalance()
        {
            if(card==null||card.IsDisposed)return;
            BalanceConfig config=BalanceConfig.Load();
            card.ProviderLabel=config.Label;
            card.SetResult(BalanceResult.Loading());
            cardHideAt=Now+30;
            BalanceService.Fetch(config,delegate(BalanceResult result)
            {
                BalanceCard target=card;
                if(target==null||target.IsDisposed)return;
                try{target.BeginInvoke((MethodInvoker)delegate{if(!target.IsDisposed)target.SetResult(result);});}catch{}
            });
        }
        internal void Tick()
        { TickWithForeground(Native.GetForegroundWindow()); }
        // The pet window never activates, so the popup cannot rely on focus loss to close.
        // Close it ourselves as soon as any mouse button goes down outside the menu.
        internal void DismissMenuWhenClickingElsewhere()
        {
            if(Control.MouseButtons==MouseButtons.None)return;
            DismissMenuAt(Cursor.Position);
        }
        // Clicking another application also closes the menu, even if the poll misses the press.
        internal void DismissMenuWhenForegroundChanged(IntPtr foreground)
        {
            if(!menuOpen||menu==null||menu.IsDisposed||!menu.Visible)return;
            if(foreground==IntPtr.Zero||foreground==menuForeground)return;
            uint id;Native.GetWindowThreadProcessId(foreground,out id);
            if(id==Native.GetCurrentProcessId())return;
            menu.Close();
        }
        internal void DismissMenuAt(Point screenPoint)
        {
            if(!menuOpen||menu==null||menu.IsDisposed||!menu.Visible)return;
            if(menu.Bounds.Contains(screenPoint))return;
            menu.Close();
        }
        internal void TickWithForeground(IntPtr foreground)
        {
            if(Now>=nextFullscreenCheck){nextFullscreenCheck=Now+.12;CheckFullscreenWindow(foreground,false);}
            // Started by the DSH plugin: leave the desktop when DSH goes away.
            if((parentPid>0||Program.HeartbeatPath!=null)&&Now>=nextParentCheck)
            {
                nextParentCheck=Now+1;
                double age=Program.HeartbeatAge(Program.HeartbeatPath);
                // The heartbeat file is the permission-free signal: it also works under a sandboxed token.
                bool alive=Program.HeartbeatPath!=null?age<Program.HeartbeatTimeout:Program.ParentAlive(parentPid,parentStart);
                if(Program.WatchLog!=null){try{File.AppendAllText(Program.WatchLog,"t="+Now.ToString("F1")+" pid="+parentPid+" pidAlive="+Program.ParentAlive(parentPid,parentStart)+" beat="+age.ToString("F1")+" leave="+(!alive)+Environment.NewLine);}catch{}}
                if(!alive){Close();return;}
            }
            if(HiddenForFullscreen)return;
            tickStamp++;
            DismissMenuWhenClickingElsewhere();
            DismissMenuWhenForegroundChanged(foreground);
            if(card!=null&&!card.IsDisposed&&Now>=cardHideAt)HideBalance();
            Point p=PointToClient(Cursor.Position);bool over=!menuOpen&&surface.Hit(p.X,p.Y);
            bool wasPlaying=Motion.Playing;Motion.Update(Now,over,assets.Duration,assets.BlinkCount/assets.BlinkFps);
            if(wasPlaying&&!Motion.Playing&&sound!=null)sound.Stop();
            RenderNow(false);
        }
        internal void RenderNow(bool force)
        {
            if(!IsHandleCreated||disposing||HiddenForFullscreen)return;
            string key=Motion.Resting?"R":Motion.Playing?"L"+(int)(Motion.Elapsed*assets.Fps):Motion.Blinking?"B"+(Motion.Look>.5?1:0)+"/"+(int)((Motion.Clock-Motion.BlinkStart)*assets.BlinkFps):"E"+(int)Math.Round(Motion.Look*(assets.EyeCount-1));
            if(Motion.Poking||Motion.Moving||Motion.Waking)key+="/M"+(int)(Motion.Clock*60);
            key+="/"+settings.Scale;
            if(!force&&key==lastVisual)return;
            using(Bitmap frame=Renderer.Render(assets,Motion,settings.Scale))surface.Update(Handle,frame,Location);
            lastVisual=key;
        }
        internal void SetAutoHide(bool value)
        {
            settings.AutoHide=value;fullscreenGate.Reset();
            if(!value)ApplyFullscreen(false);else if(!testing)CheckFullscreen(true);
            Save();UpdateMenu();
        }
        private void CheckFullscreen(bool immediate)
        {CheckFullscreenWindow(Native.GetForegroundWindow(),immediate);}
        private void CheckFullscreenWindow(IntPtr foreground,bool immediate)
        {
            if(!ready||menuOpen||gesture.Pressed)return;
            bool detected=settings.AutoHide&&Fullscreen.IsActive(foreground,Handle,Screen.FromRectangle(Bounds).Bounds);
            if(immediate){fullscreenGate.Set(detected,Now);ApplyFullscreen(detected);}
            else if(fullscreenGate.Update(detected,Now))ApplyFullscreen(fullscreenGate.Hidden);
        }
        internal void ApplyFullscreen(bool hidden)
        {
            if(hidden==HiddenForFullscreen)return;
            HiddenForFullscreen=hidden;
            if(hidden)
            {
                gesture.Cancel();Capture=false;Motion.Suspend(Now);if(sound!=null)sound.Stop();HideBalance();
                Hide();timer.Interval=150;
            }
            else
            {
                Motion.Suspend(Now);timer.Interval=16;Show();RenderNow(true);
            }
        }
        private void ReturnHome(){Home();if(!testing)CheckFullscreen(true);if(!HiddenForFullscreen){Show();RenderNow(true);}Save();}
        private Size SizeFor(int scale){return new Size(assets.Width*scale/100,assets.Height*scale/100);}
        internal void SetSize(int scale)
        {
            int center=Left+Width/2,bottom=Top+Height;settings.Scale=Math.Max(50,Math.Min(150,scale));ClientSize=SizeFor(settings.Scale);
            Location=new Point(center-Width/2,bottom-Height);KeepVisible();RenderNow(true);Save();UpdateMenu();
        }
        internal void Home()
        {
            Rectangle area=Screen.FromPoint(Cursor.Position).WorkingArea;
            Location=new Point(area.Right-Width-24,area.Bottom-Height+(int)(12*settings.Scale/100.0));KeepVisible();
        }
        internal static Point Clamp(Point p,Size size,Rectangle area,int allowance)
        {return new Point(Math.Max(area.Left,Math.Min(p.X,area.Right-size.Width)),Math.Max(area.Top,Math.Min(p.Y,area.Bottom-size.Height+allowance)));}
        private void KeepVisible(){Location=Clamp(Location,Size,Screen.FromRectangle(Bounds).WorkingArea,(int)(12*settings.Scale/100.0));}
        private void Save(){if(testing)return;settings.X=Left;settings.Y=Top;settings.Save();}
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);Trace.Line("down "+e.Button+" "+e.Location);
            if(e.Button==MouseButtons.Right){Motion.EndDrag(Now);gesture.Cancel();Capture=false;if(menu.Visible){menu.Close();return;}menu.Show(this,e.Location);return;}
            if(e.Button!=MouseButtons.Left)return;
            gesture.Down(PointToScreen(e.Location));lastDragPointer=gesture.Origin;dragWindow=Location;Capture=true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(!gesture.Pressed)return;Point p=PointToScreen(e.Location);gesture.Move(p);
            if(gesture.Dragged)
            {
                if(!Motion.Holding){Motion.BeginDrag(Now);if(sound!=null)sound.Stop();}
                Motion.Drag(Now,p.X-lastDragPointer.X);lastDragPointer=p;
                Location=new Point(dragWindow.X+p.X-gesture.Origin.X,dragWindow.Y+p.Y-gesture.Origin.Y);RenderNow(true);
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);Trace.Line("up "+e.Button+" "+e.Location);
            if(e.Button!=MouseButtons.Left||!gesture.Pressed)return;
            bool moved=gesture.Dragged;gesture.Up(PointToScreen(e.Location),Now);Capture=false;
            if(moved){Motion.EndDrag(Now);KeepVisible();RenderNow(true);Save();if(card!=null&&!card.IsDisposed&&card.Visible)card.ShowNear(Bounds);}
            else if(Motion.Resting){Motion.Stand(Now);RenderNow(true);}
            else if(settings.LeftAction=="poke")
            {
                HideBalance();
                // Client pixels map back to asset pixels, so the belly test still works at every size.
                double scaleX=assets.Width/(double)Math.Max(1,Width),scaleY=assets.Height/(double)Math.Max(1,Height);
                double x=e.X*scaleX,y=e.Y*scaleY;
                if(!Motion.InBelly(x,y)){x=144;y=205;}
                if(Motion.Poke(Now,x,y))RenderNow(true);
            }
            else{HideBalance();Laugh();}
        }
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture&&gesture.Pressed){Motion.EndDrag(Now);gesture.Cancel();KeepVisible();RenderNow(true);Save();}}
        protected override void OnFormClosing(FormClosingEventArgs e){Save();timer.Stop();if(sound!=null)sound.Stop();HideBalance();base.OnFormClosing(e);}
        protected override void Dispose(bool disposingNow)
        {
            if(disposingNow&&!disposing){disposing=true;timer.Dispose();if(card!=null){card.Dispose();card=null;}if(tray!=null){tray.Visible=false;tray.Dispose();}if(menu!=null){Font f=menu.Font;menu.Dispose();f.Dispose();}if(sound!=null){sound.Stop();sound.Dispose();}surface.Dispose();if(petIcon!=null)petIcon.Dispose();}
            base.Dispose(disposingNow);
        }
    }

    internal static class Tests
    {
        private static IntPtr Pack(Point screen)
        {
            long value=((long)(short)screen.Y<<16)&0xffff0000L;
            value|=(long)(ushort)(short)screen.X;
            return new IntPtr((int)value);
        }
        private static void SendClick(IntPtr window,Point client)
        {
            IntPtr where=new IntPtr((client.Y<<16)|(client.X&0xffff));
            Native.SendMessage(window,0x0201,new IntPtr(1),where);
            Native.SendMessage(window,0x0202,IntPtr.Zero,where);
        }
        private static List<string> ItemTexts(ToolStripItemCollection items){List<string> text=new List<string>();foreach(ToolStripItem item in items)text.Add(item.Text);return text;}
        private static void ClickItem(ToolStripItemCollection items,string text)
        {
            foreach(ToolStripItem item in items){ToolStripMenuItem entry=item as ToolStripMenuItem;if(entry!=null&&entry.Text==text){entry.PerformClick();return;}}
        }
        private static List<string> ItemTexts(ContextMenuStrip strip){return ItemTexts(strip.Items);}
        private static void Check(bool value,string name,List<string> log){if(!value)throw new Exception("FAIL: "+name);log.Add("PASS: "+name);}
        private static void Advance(Motion m,double begin,double end,bool over,Assets a)
        {for(double t=begin;t<=end+.00001;t+=1.0/60)m.Update(t,over,a.Duration,a.BlinkCount/a.BlinkFps);}
        private static bool Equal(Bitmap a,Bitmap b,int fromY,int toY)
        {for(int y=fromY;y<toY;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;return true;}
        internal static void Run(string folder)
        {
            Directory.CreateDirectory(folder);List<string> log=new List<string>();
            // The plugin starts the pet with --parent-pid so the pet leaves the desktop when DSH does.
            Check(Program.ParseParentPid(new string[]{"--parent-pid","1234"})==1234,"--parent-pid value is read",log);
            Check(Program.ParseParentPid(new string[]{"--parent-pid=987"})==987,"--parent-pid=value is read",log);
            Check(Program.ParseParentPid(new string[]{"--parent-pid","oops"})==0&&Program.ParseParentPid(new string[0])==0,"missing or invalid parent pid is ignored",log);
            Check(!Program.ParentAlive(0,DateTime.MinValue),"no parent pid never keeps the pet alive",log);
            int selfPid=(int)Native.GetCurrentProcessId();DateTime selfStart=Process.GetCurrentProcess().StartTime;
            Check(Program.ParentAlive(selfPid,selfStart),"a live launcher keeps the pet alive",log);
            Check(Program.ParentAlive(selfPid,DateTime.MinValue),"an unreadable start time still means alive",log);
            Check(!Program.ParentAlive(selfPid,selfStart.AddMinutes(-5)),"a reused pid with another start time counts as gone",log);
            Check(!Program.ParentAlive(-1,DateTime.MinValue)&&!Program.ParentAlive(int.MaxValue,DateTime.MinValue),"a dead launcher counts as gone",log);
            string beat=Path.Combine(folder,"heartbeat.txt");
            try{File.Delete(beat);}catch{}
            Check(double.IsPositiveInfinity(Program.HeartbeatAge(beat)),"a missing heartbeat reads as ancient",log);
            File.WriteAllText(beat,"beat");
            Check(Program.HeartbeatAge(beat)<Program.HeartbeatTimeout,"a fresh heartbeat keeps the pet alive",log);
            File.SetLastWriteTimeUtc(beat,DateTime.UtcNow.AddSeconds(-30));
            Check(Program.HeartbeatAge(beat)>Program.HeartbeatTimeout,"a stale heartbeat asks the pet to leave",log);
            Check(double.IsPositiveInfinity(Program.HeartbeatAge(null)),"no heartbeat path means no heartbeat rule",log);
            try
            {
                using(Assets a=new Assets(AppDomain.CurrentDomain.BaseDirectory))
                {
                    Check(a.Count==422&&Math.Abs(a.Fps-30)<.01,"original 422-frame clip at 30 fps",log);
                    Check(a.Eye(0).GetPixel(0,0).A==0&&a.Eye(1).GetPixel(0,0).A==0,"transparent idle and hover backgrounds",log);
                    int differences=0,outsideEyes=0;
                    for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.Eye(0).GetPixel(x,y)!=a.Eye(1).GetPixel(x,y)){differences++;if(y>92)outsideEyes++;}
                    Check(differences>10&&outsideEyes==0,"hover changes only eye area, body is identical",log);
                    byte[] wav=File.ReadAllBytes(a.AudioPath);Check(wav.Length>1000&&Encoding.ASCII.GetString(wav,0,4)=="RIFF","original soundtrack decodes as WAV",log);
                    double blinkDuration=a.BlinkCount/a.BlinkFps;
                    Check(a.BlinkCount==7&&a.BlinkFps==30,"blink uses seven original video frames",log);
                    for(int bank=0;bank<2;bank++)for(int f=0;f<a.BlinkCount;f++)Check(Equal(a.Eye(bank),a.Blink(bank,f),92,a.Height),"blink body preserved, bank "+bank+" frame "+f,log);
                    Check(!Equal(a.Eye(0),a.Blink(0,3),0,92),"closed eye differs from open eye",log);
                    Motion m=new Motion();Advance(m,0,.3,true,a);Check(m.Look>.999,"hover turns eyes toward pointer",log);
                    Advance(m,.32,.62,false,a);Check(m.Look<.001,"mouse leave restores original eyes",log);
                    Check(m.Laugh(1)&&!m.Laugh(1.01)&&m.PlayCount==1,"laugh playback does not overlap or restart",log);
                    m.Update(1+a.Duration-.05,false,a.Duration,blinkDuration);Check(m.Playing,"complete source clip plays through ending",log);
                    m.Update(1+a.Duration+.01,false,a.Duration,blinkDuration);Check(!m.Playing&&m.Resting,"laugh ends in resting pose",log);
                    m.Update(100,true,a.Duration,blinkDuration);Check(m.Resting&&!m.Blinking,"resting pose stays until clicked",log);
                    m.Stand(100);Check(!m.Resting&&m.Waking,"single click starts standing up",log);Advance(m,100,100.3,false,a);Check(!m.Waking&&!m.Playing,"standing settles to original idle",log);
                    m.Laugh(101);m.Update(101+a.Duration+.001,false,a.Duration,blinkDuration);Check(m.Laugh(120)&&!m.Resting,"can laugh again from resting pose",log);m.Stop();Check(!m.Playing&&!m.Resting,"stop action cancels playback",log);
                    m=new Motion();m.Update(4,false,a.Duration,blinkDuration);Check(m.Blinking,"idle schedules a blink",log);
                    m.Update(4+blinkDuration+.01,false,a.Duration,blinkDuration);Check(!m.Blinking&&m.NextBlink>=m.Clock+4&&m.NextBlink<=m.Clock+8,"blink ends and schedules random 4-8 second interval",log);
                    m.Poke(5,143,211);Check(m.Poking&&!m.Blinking,"poke interrupts blink",log);m.Update(5.1,false,a.Duration,blinkDuration);
                    using(Bitmap warped=Renderer.WarpBelly(a.Eye(0),m.PokeX,m.PokeY,m.PokeAmount))
                    {Check(Equal(warped,a.Eye(0),0,105),"belly feedback preserves face",log);Check(!Equal(warped,a.Eye(0),160,260),"belly feedback visibly deforms source pixels",log);}
                    Advance(m,5.11,6,false,a);Check(!m.Poking&&Math.Abs(m.PokeAmount)<.0001,"belly rebound settles",log);
                    Check(!m.Poke(6,125,67),"clicking face does not poke belly",log);
                    m=new Motion();
                    Check(m.Poke(6,144,205),"poke starts from idle",log);m.Update(6.1,false,a.Duration,blinkDuration);
                    Check(m.Poke(6.15,144,205),"poke can be clicked again while it plays",log);m.Update(6.2,false,a.Duration,blinkDuration);
                    Check(m.Poking&&Math.Abs(m.PokeAmount)>.01,"repeated poke restarts the squash instead of being dropped",log);
                    m=new Motion();
                    Check(m.Laugh(40)&&!m.Laugh(40.4)&&m.PlayCount==1,"laugh ignores clicks while the clip plays",log);
                    m.Update(40+a.Duration+.01,false,a.Duration,blinkDuration);Check(!m.Playing&&m.Resting,"single laugh plays through to resting",log);
                    m.Poke(6,144,210);m.Laugh(6.1);Check(!m.Poking&&m.Playing,"laugh cancels poke before original clip starts",log);
                    m.BeginDrag(7);Check(m.Holding&&!m.Playing,"drag interrupts playback",log);
                    for(int i=1;i<=30;i++){m.Drag(7+i/60.0,12);m.Update(7+i/60.0,true,a.Duration,blinkDuration);}
                    Check(m.DragAngle>2&&m.DragAngle<=7,"drag tilts toward movement and stays bounded",log);
                    m.EndDrag(7.5);m.Update(7.57,true,a.Duration,blinkDuration);Check(!m.Holding&&m.Landing&&m.Squash>.01,"release starts landing bounce",log);
                    Advance(m,7.58,9,false,a);Check(!m.Landing&&Math.Abs(m.DragAngle)<.05,"drag and landing settle cleanly",log);
                    Gesture gesture=new Gesture();Point p=new Point(100,100);gesture.Down(p);Check(!gesture.Up(p,1),"first click is a single click",log);gesture.Down(p);Check(gesture.Up(p,1.12),"rapid second click reports a double click",log);
                    gesture.Down(p);gesture.Move(new Point(140,160));Check(!gesture.Up(new Point(140,160),2),"drag does not trigger laugh",log);
                    gesture.Down(p);Check(!gesture.Up(p,2.1),"drag resets double-click sequence",log);gesture.Cancel();Check(!gesture.Pressed,"cancelled pointer capture releases drag",log);
                    Point clamped=PetWindow.Clamp(new Point(-3000,-50),new Size(245,312),new Rectangle(-1920,0,1920,1040),10);Check(clamped.X==-1920&&clamped.Y==0,"negative-coordinate monitor bounds",log);
                    Rectangle monitor=new Rectangle(0,0,1920,1080);
                    Check(Fullscreen.Covers(monitor,monitor,0,"FullscreenPlayer"),"borderless fullscreen is detected",log);
                    Check(!Fullscreen.Covers(monitor,monitor,0x00c00000,"NormalWindow"),"ordinary maximized window is ignored",log);
                    Check(!Fullscreen.Covers(new Rectangle(0,0,1920,1040),monitor,0,"Video"),"window that leaves taskbar visible is not fullscreen",log);
                    Check(!Fullscreen.Covers(new Rectangle(-1920,0,1920,1080),monitor,0,"Video"),"fullscreen on another monitor is ignored",log);
                    Check(Fullscreen.Covers(new Rectangle(-1920,0,1920,1080),new Rectangle(-1920,0,1920,1080),0,"Video"),"fullscreen works on negative-coordinate monitor",log);
                    Check(!Fullscreen.Covers(monitor,monitor,0,"WorkerW"),"desktop background is not fullscreen app",log);
                    FullscreenGate gate=new FullscreenGate();Check(!gate.Update(true,0)&&!gate.Update(false,.08)&&!gate.Hidden,"brief fullscreen switch does not flash pet",log);
                    gate.Update(true,1);Check(gate.Update(true,1.2)&&gate.Hidden,"stable fullscreen hides pet",log);gate.Update(false,2);Check(gate.Update(false,2.3)&&!gate.Hidden,"leaving fullscreen restores pet",log);
                    using(PetWindow form=new PetWindow(a,true))
                    {
                        form.Show();Application.DoEvents();form.RenderNow(true);
                        Check(Native.IsWindowVisible(form.Handle),"native pet window is visible",log);
                        int style=Native.GetWindowLong(form.Handle,-20);Check((style&0x80000)!=0&&(style&0x80)!=0&&(style&0x08000000)!=0,"alpha transparency, no taskbar entry, no focus stealing",log);
                        form.SetSize(65);Check(form.Width==a.Width*65/100,"small size renders",log);form.SetSize(110);Check(form.Width==a.Width*110/100,"large size renders",log);form.SetSize(85);
                        form.Location=new Point(-50000,-50000);form.SetSize(85);Check(Screen.FromRectangle(form.Bounds).WorkingArea.IntersectsWith(form.Bounds),"lost window position recovers",log);
                        form.SetSound(true);Check(form.SoundEnabled,"original audio switch can be enabled",log);form.SetSound(false);Check(!form.SoundEnabled,"sound switch can be muted",log);
                        IntPtr mousePoint=(IntPtr)((160<<16)|120);
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);
                        Check(form.Motion.Playing&&!form.Motion.Poking,"native left click starts the original laugh clip",log);form.StopLaugh();
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);
                        Check(form.Motion.Playing,"left click never needs a double click",log);form.StopLaugh();
                        Check(form.PetMenu!=null&&form.PetMenu.Items.Count>=10,"context menu is built",log);
                        List<string> menuText=ItemTexts(form.PetMenu);
                        Check(menuText.Contains("查看余额"),"context menu offers balance lookup",log);
                        Check(menuText.Contains("取消桌宠（退出）"),"context menu can dismiss the pet",log);
                        Check(!menuText.Contains("回到屏幕右下角"),"context menu no longer sends the pet home",log);
                        Check(!menuText.Contains("打开使用说明")&&!menuText.Contains("打开程序文件夹"),"context menu no longer opens files",log);
                        Check(menuText.Exists(delegate(string t){return t.StartsWith("左键动作");}),"left click behaviour is configurable",log);
                        Check(menuText.Count>0&&menuText[0].StartsWith("左键动作"),"menu opens straight into the left action entry",log);
                        form.Location=new Point(500,500);
                        form.PetMenu.Show(form,new Point(40,40));
                        Check(form.PetMenu.Visible,"context menu can be opened",log);
                        Point inside=form.PetMenu.Bounds.Location;
                        inside.Offset(12,12);
                        form.DismissMenuAt(inside);
                        Check(form.PetMenu.Visible,"clicking inside the menu keeps it open",log);
                        form.DismissMenuAt(new Point(inside.X-Screen.PrimaryScreen.Bounds.Width-400,inside.Y));
                        Check(!form.PetMenu.Visible,"clicking elsewhere closes the menu",log);
                        form.PetMenu.Show(form,new Point(40,40));
                        form.DismissMenuWhenForegroundChanged(IntPtr.Zero);
                        Check(form.PetMenu.Visible,"an unknown foreground keeps the menu open",log);
                        form.DismissMenuWhenForegroundChanged(Native.FindWindow("Shell_TrayWnd",null));
                        Check(!form.PetMenu.Visible,"clicking another window closes the menu",log);
                        List<string> leftTexts=ItemTexts(form.LeftActionMenu);
                        Check(leftTexts.Contains("左键动作 · 捧腹大笑"),"left click row shows the current action",log);
                        ClickItem(form.LeftActionMenu,"左键动作 · 捧腹大笑");
                        Check(form.LeftAction=="poke","the row switches the left click to poke",log);
                        Check(ItemTexts(form.LeftActionMenu).Contains("左键动作 · 戳肚子"),"the row label follows the new action",log);
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);
                        Check(form.Motion.Poking&&!form.Motion.Playing,"poke mode makes the left click squash the belly",log);
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);
                        Check(form.Motion.Poking,"poke accepts a second click without waiting",log);
                        form.StopLaugh();
                        ClickItem(form.LeftActionMenu,"左键动作 · 戳肚子");
                        Check(form.LeftAction=="laugh","the row switches the left click back to laughing",log);
                        int clips=form.Motion.PlayCount;
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);
                        Check(form.Motion.Playing&&form.Motion.PlayCount==clips+1,"laugh mode makes the left click play the clip",log);
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);
                        Check(form.Motion.Playing&&form.Motion.PlayCount==clips+1,"clicking again does not restart the clip",log);
                        form.StopLaugh();
                        Native.SendMessage(form.Handle,Native.Command,(IntPtr)3,IntPtr.Zero);
                        Check(form.Motion.Poking,"command message pokes the belly",log);form.StopLaugh();
                        Native.SendMessage(form.Handle,Native.Command,(IntPtr)2,IntPtr.Zero);
                        Check(form.Motion.Playing,"command message plays the laugh",log);form.StopLaugh();
                        form.Motion.Laugh(0);form.Motion.Update(a.Duration,false,a.Duration,blinkDuration);Check(form.Motion.Resting,"native window reaches resting state",log);
                        IntPtr footPoint=(IntPtr)((240<<16)|170);
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,footPoint);Native.SendMessage(form.Handle,0x202,IntPtr.Zero,footPoint);
                        Check(!form.Motion.Resting&&form.Motion.Waking,"native click wakes resting pet",log);form.StopLaugh();
                        Native.SendMessage(form.Handle,0x201,(IntPtr)1,mousePoint);
                        Native.SendMessage(form.Handle,0x200,(IntPtr)1,(IntPtr)((180<<16)|155));Check(form.Motion.Holding,"native drag starts feedback",log);
                        Native.SendMessage(form.Handle,0x202,IntPtr.Zero,mousePoint);Check(!form.Motion.Holding&&form.Motion.Landing&&!form.Motion.Playing,"native drag release bounces without laughing",log);form.StopLaugh();
                        using(Form fullscreen=new Form())
                        {
                            fullscreen.FormBorderStyle=FormBorderStyle.None;fullscreen.StartPosition=FormStartPosition.Manual;fullscreen.ShowInTaskbar=false;fullscreen.Opacity=.01;
                            fullscreen.Bounds=Screen.FromRectangle(form.Bounds).Bounds;fullscreen.Show();Application.DoEvents();
                            Check(Fullscreen.IsActive(fullscreen.Handle,form.Handle,fullscreen.Bounds),"native borderless fullscreen detection",log);
                            Check(!Fullscreen.IsActive(form.Handle,form.Handle,fullscreen.Bounds),"pet window is excluded from fullscreen detection",log);
                            // Supply the actual test HWND; sandbox desktops cannot take foreground focus.
                            form.TickWithForeground(fullscreen.Handle);Thread.Sleep(220);form.TickWithForeground(fullscreen.Handle);Check(form.HiddenForFullscreen&&!Native.IsWindowVisible(form.Handle),"native fullscreen detection automatically hides pet through timer",log);
                            fullscreen.Hide();Check(!Fullscreen.IsActive(fullscreen.Handle,form.Handle,fullscreen.Bounds),"hidden window is excluded from fullscreen detection",log);
                            Thread.Sleep(180);form.TickWithForeground(fullscreen.Handle);Thread.Sleep(300);form.TickWithForeground(fullscreen.Handle);
                            Check(!form.HiddenForFullscreen&&Native.IsWindowVisible(form.Handle),"leaving fullscreen automatically restores pet through timer",log);fullscreen.Close();
                        }
                        Point savedLocation=form.Location;form.Laugh();form.ApplyFullscreen(true);
                        Check(form.HiddenForFullscreen&&!Native.IsWindowVisible(form.Handle)&&!form.Motion.Playing,"fullscreen hides native pet and interrupts playback",log);
                        Check(!form.Laugh(),"hidden pet cannot play audio or laugh",log);
                        form.ApplyFullscreen(false);Check(!form.HiddenForFullscreen&&Native.IsWindowVisible(form.Handle)&&form.Location==savedLocation,"fullscreen exit restores same position",log);
                        form.Motion.Laugh(0);form.Motion.Update(a.Duration,false,a.Duration,blinkDuration);form.ApplyFullscreen(true);form.ApplyFullscreen(false);
                        Check(form.Motion.Resting,"resting pose survives fullscreen hide and restore",log);
                        form.ApplyFullscreen(true);form.SetAutoHide(false);Check(Native.IsWindowVisible(form.Handle)&&!form.HiddenForFullscreen,"disabling automatic hide reveals pet",log);form.StopLaugh();
                        // Fill the bounded cache before measuring steady-state resource use.
                        for(int i=0;i<12;i++)a.Frame(i);
                        int before=Native.GetGuiResources(Native.GetCurrentProcess(),0);Stopwatch sw=Stopwatch.StartNew();
                        form.Motion.Laugh(0);
                        for(int i=0;i<a.Count;i++){form.Motion.Elapsed=i/a.Fps;form.RenderNow(true);}
                        sw.Stop();int after=Native.GetGuiResources(Native.GetCurrentProcess(),0);
                        log.Add("METRIC: all-frame decode/render average "+(sw.Elapsed.TotalMilliseconds/a.Count).ToString("F2")+" ms; GDI delta "+(after-before));
                        // GDI+ lazily initializes renderer resources on its first complete playback.
                        for(int i=0;i<a.Count;i++){form.Motion.Elapsed=i/a.Fps;form.RenderNow(true);}
                        int stable=Native.GetGuiResources(Native.GetCurrentProcess(),0);
                        log.Add("METRIC: repeat-playback GDI delta "+(stable-after));
                        Check(stable-after<10,"repeat playback does not leak GDI handles",log);Check(a.CacheCount<=12,"decoded frame cache stays bounded",log);
                        form.Motion.Stop();form.RenderNow(true);form.Close();Check(form.IsDisposed,"window closes cleanly",log);
                    }
                    BalanceConfig settingsCheck=new BalanceConfig();settingsCheck.ApiKey="sk-test";
                    BalanceResult parsed=BalanceService.Parse(settingsCheck,"{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"110.00\",\"granted_balance\":\"10.00\",\"topped_up_balance\":\"100.00\"}]}");
                    Check(parsed.Ok&&Math.Abs(parsed.Total-110)<.001&&Math.Abs(parsed.Granted-10)<.001&&Math.Abs(parsed.ToppedUp-100)<.001,"DeepSeek balance response parses",log);
                    BalanceConfig custom=new BalanceConfig();custom.Provider="custom";custom.CustomJsonPath="data.balance";
                    parsed=BalanceService.Parse(custom,"{\"data\":{\"balance\":12.5}}");
                    Check(parsed.Ok&&Math.Abs(parsed.Total-12.5)<.001,"custom JSON path balance parses",log);
                    BalanceConfig keyed=new BalanceConfig();keyed.ApiKey="sk-test";
                    parsed=BalanceService.Parse(keyed,"<html>oops</html>");
                    Check(!parsed.Ok&&parsed.Error.Length>0,"non-JSON balance response reports an error",log);
                    parsed=BalanceService.Parse(keyed,"{\"is_available\":false}");
                    Check(!parsed.Ok&&parsed.Error.Length>0,"response without balance field reports an error",log);
                    Check(new BalanceConfig().Mode=="dsh"||new BalanceConfig().Mode=="none","bare config follows the stored Harness account when present",log);
                    string[] store=new string[]{"version: 1","records:","  deepseek-account-platform/device:","    kind: grant","    payload:","      id: device-1","  deepseek-account-platform/default:","    kind: grant","    payload:","      version: 1","      token: tok-123","      issuer: https://platform.deepseek.com","  other/record:","    kind: grant",""};
                    DshCredential credential=DshAccount.Parse(string.Join(Environment.NewLine,store),"fixture");
                    Check(credential.Ready&&credential.Token=="tok-123"&&credential.Issuer=="https://platform.deepseek.com","Harness credential store yields the Platform grant",log);
                    DshCredential signedOut=DshAccount.Parse(string.Join(Environment.NewLine,new string[]{"version: 1","records:","  deepseek-account-platform/device:","    kind: grant",""}),"fixture");
                    Check(!signedOut.Ready,"store without a Platform login reads as signed out",log);
                    BalanceConfig dsh=new BalanceConfig();dsh.Provider="dsh";
                    parsed=BalanceService.ParseDsh(dsh,"{\"code\":0,\"data\":{\"biz_code\":0,\"biz_data\":{\"normal_wallets\":[{\"currency\":\"CNY\",\"balance\":\"10.95\"}],\"bonus_wallets\":[{\"currency\":\"CNY\",\"balance\":\"2.05\"}]}}}");
                    Check(parsed.Ok&&Math.Abs(parsed.Total-13)<.001&&Math.Abs(parsed.ToppedUp-10.95)<.001&&Math.Abs(parsed.Granted-2.05)<.001,"Harness wallet envelope parses into a balance",log);
                    parsed=BalanceService.ParseDsh(dsh,"{\"code\":0,\"data\":{\"biz_code\":0,\"biz_data\":{\"normal_wallets\":[{\"currency\":\"USD\",\"balance\":\"3.00\"}],\"bonus_wallets\":[]}}}");
                    Check(parsed.Ok&&parsed.Currency=="USD"&&Math.Abs(parsed.Total-3)<.001,"wallet currency overrides the configured currency",log);
                    parsed=BalanceService.ParseDsh(dsh,"{\"code\":40003,\"msg\":\"invalid token\"}");
                    Check(!parsed.Ok&&parsed.Error.IndexOf("登录")>=0,"rejected Harness login asks for a fresh sign-in",log);
                    parsed=BalanceService.ParseDsh(dsh,"{\"code\":0,\"data\":{\"biz_code\":7,\"biz_msg\":\"boom\",\"biz_data\":{}}}");
                    Check(!parsed.Ok&&parsed.Error.IndexOf("boom")>=0,"platform business error surfaces its message",log);
                    string livePayload=Path.Combine(folder,"real-balance.json");
                    if(File.Exists(livePayload))
                    {
                        BalanceConfig liveConfig=new BalanceConfig();liveConfig.Provider="dsh";
                        BalanceResult live=BalanceService.ParseDsh(liveConfig,File.ReadAllText(livePayload));
                        Check(live.Ok&&live.Total>=0&&(live.Currency=="CNY"||live.Currency.Length>0),"captured platform payload parses into a balance",log);
                        log.Add("NOTE: captured payload total "+(live.Ok?live.Total.ToString("F2"):"-")+" "+live.Currency+" account "+(live.Account.Length>0?live.Account:"(none)"));
                    }
                    using(BalanceCard card=new BalanceCard(a))
                    {
                        card.ShowNear(new Rectangle(400,400,245,312));Application.DoEvents();
                        Check(Native.IsWindowVisible(card.Handle),"balance card window is visible",log);
                        int cardStyle=Native.GetWindowLong(card.Handle,-20);Check((cardStyle&0x80000)!=0&&(cardStyle&0x80)!=0,"balance card is transparent and keeps out of the taskbar",log);
                        BalanceResult loaded=new BalanceResult();loaded.Ok=true;loaded.Total=110;loaded.Granted=10;loaded.ToppedUp=100;loaded.HasBreakdown=true;
                        card.SetResult(loaded);
                        Check(card.Surface.Hit(card.Width/2,card.Height/2)&&!card.Surface.Hit(1,1),"balance card only takes clicks on its painted body",log);
                        using(Bitmap shot=card.Draw())shot.Save(Path.Combine(folder,"card-native.png"),ImageFormat.Png);
                        // The card hit-tests in screen space: a client-space lookup made every card click fall through.
                        Point closeScreen=card.PointToScreen(new Point(card.CloseButton.X+card.CloseButton.Width/2,card.CloseButton.Y+card.CloseButton.Height/2));
                        Point refreshScreen=card.PointToScreen(new Point(card.RefreshButton.X+card.RefreshButton.Width/2,card.RefreshButton.Y+card.RefreshButton.Height/2));
                        Point cornerScreen=card.PointToScreen(new Point(1,1));
                        Check(Native.SendMessage(card.Handle,0x84,IntPtr.Zero,Pack(closeScreen))==(IntPtr)1,"balance card close button accepts clicks",log);
                        Check(Native.SendMessage(card.Handle,0x84,IntPtr.Zero,Pack(refreshScreen))==(IntPtr)1,"balance card refresh button accepts clicks",log);
                        Check(Native.SendMessage(card.Handle,0x84,IntPtr.Zero,Pack(cornerScreen))==(IntPtr)(-1),"balance card corner stays click through",log);
                        bool askedRefresh=false,askedClose=false;
                        card.RefreshRequested+=delegate{askedRefresh=true;};
                        card.CloseRequested+=delegate{askedClose=true;};
                        SendClick(card.Handle,new Point(card.RefreshButton.X+card.RefreshButton.Width/2,card.RefreshButton.Y+card.RefreshButton.Height/2));
                        Check(askedRefresh&&!askedClose,"clicking refresh asks for a new balance",log);
                        SendClick(card.Handle,new Point(card.CloseButton.X+card.CloseButton.Width/2,card.CloseButton.Y+card.CloseButton.Height/2));
                        Check(askedClose,"clicking close dismisses the balance card",log);
                        card.Close();
                    }
                    using(Icon icon=Renderer.CreateIcon(a.Eye(0)))using(FileStream f=File.Create(Path.Combine(folder,"naiwa.ico")))icon.Save(f);
                    m=new Motion();
                    using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-idle.png"),ImageFormat.Png);
                    m.Look=1;using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-hover.png"),ImageFormat.Png);
                    m.Laugh(0);m.Elapsed=1;using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-laugh.png"),ImageFormat.Png);
                    m=new Motion();m.Poke(0,144,211);m.Update(.10,false,a.Duration,blinkDuration);using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-poke.png"),ImageFormat.Png);
                    m.BeginDrag(1);m.DragAngle=6;using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-drag.png"),ImageFormat.Png);
                    m=new Motion();m.BlinkStart=0;m.Clock=.1;using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-blink.png"),ImageFormat.Png);
                    m.Stop();m.Resting=true;using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,"native-rest.png"),ImageFormat.Png);
                }
                log.Add("RESULT: PASS");
            }
            catch(Exception e){log.Add(e.ToString());log.Add("RESULT: FAIL");Environment.ExitCode=1;}
            File.WriteAllLines(Path.Combine(folder,"self-test.txt"),log.ToArray(),Encoding.UTF8);
        }
        internal static void RenderCard(string folder)
        {
            using(Assets a=new Assets(AppDomain.CurrentDomain.BaseDirectory)) BalanceCard.RenderPreviews(a,folder);
        }
        internal static void Demo(string folder)
        {
            Directory.CreateDirectory(folder);using(Assets a=new Assets(AppDomain.CurrentDomain.BaseDirectory))
            {
                Motion m=new Motion();m.NextBlink=.6;
                for(int i=0;i<720;i++)
                {
                    double t=i/30.0;
                    if(i==60)m.Poke(t,143,205);
                    if(i==90)m.BeginDrag(t);
                    if(i>=90&&i<126)m.Drag(t,i<108?5:-5);
                    if(i==126)m.EndDrag(t);
                    if(i==174)m.Laugh(t);
                    if(i==666)m.Stand(t);
                    m.Update(t,t>=1.1&&t<5.8,a.Duration,a.BlinkCount/a.BlinkFps);
                    using(Bitmap b=Renderer.Render(a,m,100))b.Save(Path.Combine(folder,i.ToString("0000")+".png"),ImageFormat.Png);
                }
            }
        }
    }
}
