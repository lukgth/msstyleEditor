using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Compile as an x64 .NET Framework executable with System.Drawing and System.Windows.Forms.
// Owns only its two diagnostic windows; does not alter or close any user windows.
class CaptionProbe
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; public override string ToString() { return $"{Left},{Top},{Right},{Bottom}"; } }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; public override string ToString() { return $"{L},{T},{R},{B}"; } }
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
    [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] static extern IntPtr OpenThemeData(IntPtr h, string c);
    [DllImport("uxtheme.dll")] static extern int CloseThemeData(IntPtr h);
    [DllImport("uxtheme.dll")] static extern int DrawThemeBackground(IntPtr h, IntPtr dc, int p, int s, ref RECT r, IntPtr clip);
    [DllImport("uxtheme.dll")] static extern int GetThemePartSize(IntPtr h, IntPtr dc, int p, int s, IntPtr r, int kind, out SIZE z);
    [DllImport("uxtheme.dll")] static extern int GetThemeMargins(IntPtr h, IntPtr dc, int p, int s, int prop, IntPtr r, out MARGINS m);
    [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] static extern int GetCurrentThemeName(StringBuilder file, int a, StringBuilder color, int b, StringBuilder size, int c);
    static string dir;
    [STAThread] static void Main(string[] args)
    {
        SetProcessDPIAware();
        dir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("caption-probe");
        Directory.CreateDirectory(dir);
        Application.EnableVisualStyles();
        var f = new Form { Text = "Maximized caption coverage diagnostic", BackColor = Color.White, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(180,180,800,500) };
        var backdrop = new Form { Text = "Caption diagnostic backdrop", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds, BackColor = Color.Magenta, ShowInTaskbar=false };
        backdrop.Show(); f.Show(); f.Activate();
        var timer = new Timer { Interval = 900 };
        int phase=0; Bitmap first=null;
        timer.Tick += (sender,e) => {
            if (phase==0) { f.WindowState=FormWindowState.Maximized; phase++; return; }
            if (phase==1) { first=Capture("magenta.png"); backdrop.BackColor=Color.Lime; backdrop.Refresh(); f.Activate(); phase++; return; }
            timer.Stop();
            using(var second=Capture("green.png")) using(first) using(var report=new StreamWriter(Path.Combine(dir,"measurement.txt"))) {
                var file=new StringBuilder(1024); var color=new StringBuilder(256); var size=new StringBuilder(256); GetCurrentThemeName(file,file.Capacity,color,color.Capacity,size,size.Capacity);
                report.WriteLine($"theme={file}; color={color}; size={size}");
                RECT wr,cr,dr; GetWindowRect(f.Handle,out wr); GetClientRect(f.Handle,out cr); DwmGetWindowAttribute(f.Handle,9,out dr,16); POINT cp=new POINT(); ClientToScreen(f.Handle,ref cp);
                report.WriteLine($"window={wr}; client={cr}; client-origin={cp.X},{cp.Y}; dwm-frame={dr}; workarea={Screen.PrimaryScreen.WorkingArea}");
                report.WriteLine($"SM_CYCAPTION={GetSystemMetrics(4)}; SM_CYFRAME={GetSystemMetrics(33)}; SM_CXPADDEDBORDER={GetSystemMetrics(92)}");
                int left=100,right=first.Width-150;
                for(int y=0;y<50;y++) { int changed=0,magenta=0,green=0; for(int x=left;x<right;x++) { if(first.GetPixel(x,y)!=second.GetPixel(x,y)) changed++; if(first.GetPixel(x,y).ToArgb()==Color.Magenta.ToArgb()) magenta++; if(second.GetPixel(x,y).ToArgb()==Color.Lime.ToArgb()) green++; } report.WriteLine($"screen row {y}: backdrop-dependent={changed}/{right-left}; magenta={magenta}; green={green}; center={second.GetPixel(second.Width/2,y)}"); }
                int top=Screen.PrimaryScreen.WorkingArea.Top;
                for(int y=top;y<top+4;y++) { int changed=0,exposed=0; for(int x=0;x<first.Width;x++) { if(first.GetPixel(x,y)!=second.GetPixel(x,y)) changed++; if(second.GetPixel(x,y).ToArgb()==Color.Lime.ToArgb()) exposed++; } report.WriteLine($"workarea top+{y-top}: backdrop-dependent={changed}/{first.Width}; exposed-green={exposed}/{first.Width}"); }
                using(var crop=second.Clone(new Rectangle(0,top,second.Width,Math.Min(80,second.Height-top)),PixelFormat.Format32bppArgb)) crop.Save(Path.Combine(dir,"caption.png"));
                Native(f,report);
            }
            f.Close(); backdrop.Close(); Application.ExitThread();
        };
        timer.Start(); Application.Run();
    }
    static Bitmap Capture(string name) { var b=Screen.PrimaryScreen.Bounds; var image=new Bitmap(b.Width,b.Height,PixelFormat.Format32bppArgb); using(var g=Graphics.FromImage(image)) g.CopyFromScreen(b.Location,Point.Empty,b.Size); image.Save(Path.Combine(dir,name)); return image; }
    static void Native(Form f,TextWriter report)
    {
        IntPtr theme=OpenThemeData(f.Handle,"Window");
        try { foreach(int part in new[]{1,2,3,4,5,6,7,8,9}) foreach(int state in new[]{1,2}) {
            SIZE size; GetThemePartSize(theme,IntPtr.Zero,part,state,IntPtr.Zero,1,out size); MARGINS m; GetThemeMargins(theme,IntPtr.Zero,part,state,3601,IntPtr.Zero,out m); report.WriteLine($"native part={part} state={state} size={size.X}x{size.Y} sizing={m}");
            if(part>6) continue;
            foreach(int height in new[]{19,23,27,40}) using(var image=new Bitmap(500,height,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(image)) {
                g.Clear(Color.Magenta); IntPtr dc=g.GetHdc(); var r=new RECT { Right=500,Bottom=height }; int hr=DrawThemeBackground(theme,dc,part,state,ref r,IntPtr.Zero); g.ReleaseHdc(dc);
                image.Save(Path.Combine(dir,$"native-p{part}-s{state}-h{height}.png"));
                report.Write($"draw part={part} state={state} height={height} hr={hr:X8} center rows:"); for(int y=0;y<height;y++) report.Write($" {image.GetPixel(250,y).ToArgb():X8}"); report.WriteLine();
            }
        }} finally { CloseThemeData(theme); }
    }
}
