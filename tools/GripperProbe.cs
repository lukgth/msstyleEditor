using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Compile as an x64 .NET Framework executable with System.Drawing and System.Windows.Forms.
// Creates and closes only its own diagnostic window; never closes user applications.
class GripperProbe
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int X, Y; }
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string text, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] static extern IntPtr OpenThemeData(IntPtr h, string c);
    [DllImport("uxtheme.dll")] static extern int CloseThemeData(IntPtr h);
    [DllImport("uxtheme.dll")] static extern int DrawThemeBackground(IntPtr h, IntPtr dc, int p, int s, ref RECT r, IntPtr clip);
    [DllImport("uxtheme.dll")] static extern int GetThemePartSize(IntPtr h, IntPtr dc, int p, int s, IntPtr r, int kind, out SIZE z);
    [DllImport("uxtheme.dll")] static extern int GetThemeInt(IntPtr h, int p, int s, int prop, out int value);
    [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] static extern int GetCurrentThemeName(StringBuilder file, int a, StringBuilder color, int b, StringBuilder size, int c);
    [STAThread] static void Main(string[] args)
    {
        SetProcessDPIAware();
        string dir=Path.GetFullPath(args.Length>0 ? args[0] : "gripper-probe"); Directory.CreateDirectory(dir);
        Application.EnableVisualStyles();
        using(var f=new Form { Text="Native XP resize gripper diagnostic", StartPosition=FormStartPosition.Manual, Location=new Point(180,180), ClientSize=new Size(480,230), BackColor=SystemColors.Control })
        {
            f.Controls.Add(new Label { Text="Native scrollbar SBS_SIZEGRIP (left) and status bar (bottom).\nRaw Status GRIPPER and ScrollBar SIZEBOX images saved separately.", AutoSize=true, Location=new Point(20,20) });
            f.Show();
            CreateWindowEx(0,"ScrollBar","",unchecked((int)0x50000018),40,100,17,17,f.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            CreateWindowEx(0,"msctls_statusbar32","Native status gripper",unchecked((int)0x50000100),0,210,480,20,f.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            var timer=new Timer { Interval=900 };
            timer.Tick+=(sender,e)=> {
                timer.Stop();
                using(var report=new StreamWriter(Path.Combine(dir,"measurement.txt"))) {
                    var file=new StringBuilder(1024); var color=new StringBuilder(256); var size=new StringBuilder(256); GetCurrentThemeName(file,file.Capacity,color,color.Capacity,size,size.Capacity);
                    report.WriteLine($"theme={file}; color={color}; size={size}");
                    foreach(string cls in new[]{"Status","ScrollBar"}) {
                        int part=cls=="Status" ? 3 : 10; IntPtr theme=OpenThemeData(f.Handle,cls);
                        try { foreach(int state in new[]{1,2,3,4,5,6,7,8}) {
                            SIZE z; int hr=GetThemePartSize(theme,IntPtr.Zero,part,state,IntPtr.Zero,1,out z); int count,layout; GetThemeInt(theme,part,state,2401,out count); GetThemeInt(theme,part,state,4011,out layout);
                            report.WriteLine($"class={cls} part={part} state={state} size={z.X}x{z.Y} hr={hr:X8} count={count} layout={layout}");
                            using(var image=new Bitmap(17,17,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(image)) {
                                g.Clear(Color.Magenta); IntPtr dc=g.GetHdc(); var r=new RECT { Right=17,Bottom=17 }; hr=DrawThemeBackground(theme,dc,part,state,ref r,IntPtr.Zero); g.ReleaseHdc(dc);
                                image.Save(Path.Combine(dir,$"{cls}-s{state}.png")); report.WriteLine($"draw={hr:X8}");
                            }
                        }} finally { CloseThemeData(theme); }
                    }
                }
                using(var image=new Bitmap(f.Width,f.Height,PixelFormat.Format32bppArgb)) { using(var g=Graphics.FromImage(image)) g.CopyFromScreen(f.Location,Point.Empty,image.Size); image.Save(Path.Combine(dir,"native-controls.png")); }
                f.Close();
            };
            timer.Start(); Application.Run(f); timer.Dispose();
        }
    }
}
