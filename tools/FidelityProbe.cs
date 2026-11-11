using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using libmsstyle;
using libmsstyle.Import;

// x64 .NET Framework diagnostic. Never applies a theme or changes shell configuration.
// Arguments: output-directory previous.msstyles current.msstyles source-xp.msstyles [source-scheme] [--offline-only].
// Raw theme draws prove API behavior, not that an Explorer control actually consumes that part.
class FidelityProbe
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L,T,R,B; public override string ToString() { return L+","+T+","+R+","+B; } }
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L,R,T,B; public override string ToString() { return L+","+T+","+R+","+B; } }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)] static extern IntPtr OpenThemeDataForDpi(IntPtr h,string c,uint dpi);
    [DllImport("uxtheme.dll")] static extern int CloseThemeData(IntPtr h);
    [DllImport("uxtheme.dll")] static extern int DrawThemeBackground(IntPtr h,IntPtr dc,int p,int s,ref RECT r,IntPtr clip);
    [DllImport("uxtheme.dll")] static extern int GetThemeMargins(IntPtr h,IntPtr dc,int p,int s,int prop,IntPtr r,out MARGINS m);
    [DllImport("uxtheme.dll")] static extern int GetThemeInt(IntPtr h,int p,int s,int prop,out int v);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)] static extern int GetThemeFont(IntPtr h,IntPtr dc,int p,int s,int prop,IntPtr font);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)] static extern int GetCurrentThemeName(StringBuilder f,int a,StringBuilder c,int b,StringBuilder s,int d);
    [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,int attr,out RECT r,int n);
    static string output;
    static TextWriter report;
    static string Class(IntPtr h) { var s=new StringBuilder(256); GetClassName(h,s,s.Capacity); return s.ToString(); }
    static string Title(IntPtr h) { var s=new StringBuilder(512); GetWindowText(h,s,s.Capacity); return s.ToString(); }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length<4) { Console.Error.WriteLine("output previous current source [scheme] [--offline-only]"); return 2; }
        SetProcessDPIAware(); output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        using(report=new StreamWriter(Path.Combine(output,"evidence.txt"))) {
            Offline(args[1],args[2],args[3],args.Length>4?args[4]:"NormalColor");
            if(args.Contains("--offline-only")) return 0;
            var f=new StringBuilder(1024); var c=new StringBuilder(256); var z=new StringBuilder(256); int hr=GetCurrentThemeName(f,f.Capacity,c,c.Capacity,z,z.Capacity);
            report.WriteLine("active-theme hr={0:X8} file={1} color={2} size={3}",hr,f,c,z);
            bool composition; hr=DwmIsCompositionEnabled(out composition); report.WriteLine("DWM composition={0} hr={1:X8}",composition,hr);
            Application.EnableVisualStyles();
            using(var form=new Form { Text="XP fidelity native diagnostic",StartPosition=FormStartPosition.Manual,Location=new Point(160,160),ClientSize=new Size(640,330) }) {
                form.Controls.Add(new Label { Text="Native drawing and control-tree diagnostic; no theme configuration changes.",AutoSize=true,Location=new Point(20,20) });
                form.Controls.Add(new Button { Text="Push button",Location=new Point(20,55),Size=new Size(120,28) });
                form.Controls.Add(new RadioButton { Text="Radio",Location=new Point(160,55),Checked=true });
                form.Controls.Add(new CheckBox { Text="Checkbox",Location=new Point(280,55),Checked=true });
                var combo=new ComboBox { Location=new Point(20,100),Width=220,DropDownStyle=ComboBoxStyle.DropDown }; combo.Items.Add("Editable combo source face"); combo.SelectedIndex=0; form.Controls.Add(combo);
                form.Controls.Add(new NumericUpDown { Location=new Point(280,100),Width=120,Value=42 });
                form.Controls.Add(new TrackBar { Location=new Point(20,150),Width=300,Value=5 });
                var status=new StatusStrip { SizingGrip=true }; status.Items.Add("Native status size grip"); form.Controls.Add(status);
                form.Show(); Application.DoEvents();
                RECT frame; hr=DwmGetWindowAttribute(form.Handle,9,out frame,16); report.WriteLine("diagnostic DWM frame={0} hr={1:X8}",frame,hr);
                Describe(form.Handle,"diagnostic-window"); EnumChildWindows(form.Handle,(h,p)=> { Describe(h,"diagnostic-child"); return true; },IntPtr.Zero);
                Native(form.Handle); Capture(form.Handle,"native-window-active.png");
                using(var other=new Form { Text="Inactive-frame diagnostic",StartPosition=FormStartPosition.Manual,Location=new Point(850,160),ClientSize=new Size(280,100) }) {
                    other.Show(); other.Activate(); Application.DoEvents(); System.Threading.Thread.Sleep(100); Application.DoEvents();
                    Capture(form.Handle,"native-window-inactive.png"); Capture(other.Handle,"native-second-active.png"); other.Close();
                }
                Shell(); form.Close();
            }
        }
        return 0;
    }
    static StyleProperty Find(VisualStyle style,string cls,int part,int state,IDENTIFIER prop) {
        var cl=style.Classes.Values.FirstOrDefault(x=>x.ClassName.Equals(cls,StringComparison.OrdinalIgnoreCase));
        if(cl==null) return null;
        return SemanticModel.FindEffective(style,cl,part,state,prop);
    }
    static string Value(VisualStyle style,string cls,int part,int state,IDENTIFIER prop) {
        var p=Find(style,cls,part,state,prop); if(p==null) return "(absent)";
        var resource=SemanticModel.ResourceRef(p,style.Platform);
        if(resource!=null && resource.Item1=="IMAGE") {
            var image=ImageIdentity.Decode(style.GetRawResource(resource.Item1,resource.Item2)); return image.Width+"x"+image.Height+"/"+image.PixelSha1();
        }
        if(p.Header.typeID==(int)IDENTIFIER.FONT && style.PreferredStringTable.TryGetValue(p.Header.shortFlag,out string font)) return font;
        return p.GetValue().ToString();
    }
    static void Offline(string oldPath,string newPath,string sourcePath,string scheme) {
        using(var old=new VisualStyle()) using(var current=new VisualStyle()) using(var source=new VisualStyle()) {
            old.Load(oldPath); current.Load(newPath); source.Load(sourcePath);
            source.LoadXpWithScheme(source.XpColorSchemes.Single(x=>x.InternalName.Equals(scheme,StringComparison.OrdinalIgnoreCase)));
            foreach(var item in new[]{Tuple.Create("Rebar",0),Tuple.Create("Rebar",3),Tuple.Create("Menu",7),Tuple.Create("Header",0),Tuple.Create("Header",1),Tuple.Create("ItemsView::Header",0),Tuple.Create("ItemsView::Header",1),Tuple.Create("CommandModule",1),Tuple.Create("CPLCommandModule::CommandModule",1)}) {
                foreach(var prop in new[]{IDENTIFIER.IMAGEFILE,IDENTIFIER.SIZINGMARGINS,IDENTIFIER.IMAGECOUNT,IDENTIFIER.IMAGELAYOUT}) {
                    string a=Value(old,item.Item1,item.Item2,0,prop),b=Value(current,item.Item1,item.Item2,0,prop);
                    report.WriteLine("preserved {0}|{1}|{2} equal={3} previous={4} current={5}",item.Item1,item.Item2,prop,a==b,a,b);
                }
            }
            foreach(int p in new[]{0,1,2,4,6,8}) report.WriteLine("StartPanel p{0} previous={1} current={2} source={3}",p,Value(old,"StartPanel",p,0,IDENTIFIER.DEFAULTPANESIZE),Value(current,"StartPanel",p,0,IDENTIFIER.DEFAULTPANESIZE),Value(source,"StartPanel",p,0,IDENTIFIER.DEFAULTPANESIZE));
            report.WriteLine("StartPanel places CONTENTMARGINS previous={0} current={1} source={2}; negative source inset intentionally widens the child without widening its host",Value(old,"StartPanel",6,0,IDENTIFIER.CONTENTMARGINS),Value(current,"StartPanel",6,0,IDENTIFIER.CONTENTMARGINS),Value(source,"StartPanel",6,0,IDENTIFIER.CONTENTMARGINS));
            foreach(int p in new[]{5,8,9,12}) foreach(var prop in new[]{IDENTIFIER.IMAGEFILE,IDENTIFIER.FONT,IDENTIFIER.CONTENTMARGINS}) report.WriteLine("ExplorerBar p{0} {1} current={2} source={3}",p,prop,Value(current,"ExplorerBar",p,0,prop),Value(source,"ExplorerBar",p,0,prop));
            report.WriteLine("ExplorerBar::Toolbar classes={0}",current.Classes.Values.Count(x=>x.ClassName.Equals("ExplorerBar::Toolbar",StringComparison.OrdinalIgnoreCase)));
            foreach(var cls in new[]{"ExplorerBar::Rebar","ExplorerBar::Toolbar","TaskBar::Toolbar","TaskBarVert::Toolbar","TaskBand::Scrollbar","Progress","Spin","Tab","Tooltip","TrackBar","TrayNotify::Button","Status","ComboBox"}) {
                var s=source.Classes.Values.FirstOrDefault(x=>x.ClassName.Equals(cls,StringComparison.OrdinalIgnoreCase));
                var t=current.Classes.Values.FirstOrDefault(x=>x.ClassName.Equals(cls,StringComparison.OrdinalIgnoreCase));
                report.WriteLine("audit-class {0} source={1} output={2}",cls,s!=null,t!=null);
                if(s==null) continue;
                foreach(var part in s.Parts.Values) foreach(var state in part.States.Values) foreach(var prop in state.Properties) {
                    if(!new[]{IDENTIFIER.FONT,IDENTIFIER.CONTENTMARGINS,IDENTIFIER.SIZINGMARGINS,IDENTIFIER.FILLTYPE,IDENTIFIER.IMAGEFILE,IDENTIFIER.IMAGEFILE1,IDENTIFIER.IMAGEFILE2,IDENTIFIER.IMAGEFILE3,IDENTIFIER.GLYPHIMAGEFILE}.Contains((IDENTIFIER)prop.Header.nameID)) continue;
                    report.WriteLine(" audit-property {0} p{1} s{2} {3} source={4} output={5}",cls,part.PartId,state.StateId,(IDENTIFIER)prop.Header.nameID,Value(source,cls,part.PartId,state.StateId,(IDENTIFIER)prop.Header.nameID),Value(current,cls,part.PartId,state.StateId,(IDENTIFIER)prop.Header.nameID));
                }
            }
        }
    }
    static void Native(IntPtr window) {
        foreach(uint dpi in new uint[]{96,144,192}) foreach(var spec in new[]{Tuple.Create("CommandModule",1),Tuple.Create("CPLCommandModule::CommandModule",1),Tuple.Create("Menu",7),Tuple.Create("Rebar",0),Tuple.Create("Rebar",3),Tuple.Create("Header",1),Tuple.Create("ExplorerBar",5),Tuple.Create("ExplorerBar",8),Tuple.Create("ExplorerBar",9),Tuple.Create("ExplorerBar",12),Tuple.Create("ExplorerBar::Toolbar",1),Tuple.Create("TaskBar::Toolbar",1),Tuple.Create("Button",1),Tuple.Create("Button",2),Tuple.Create("Button",3),Tuple.Create("ScrollBar",10),Tuple.Create("TrackBar",4)}) {
            IntPtr theme=OpenThemeDataForDpi(window,spec.Item1,dpi); report.WriteLine("native {0} p{1} dpi={2} handle={3}",spec.Item1,spec.Item2,dpi,theme!=IntPtr.Zero);
            if(theme==IntPtr.Zero) continue;
            try {
                MARGINS m; int hr=GetThemeMargins(theme,IntPtr.Zero,spec.Item2,0,3601,IntPtr.Zero,out m); int count,layout; int countHr=GetThemeInt(theme,spec.Item2,0,2401,out count),layoutHr=GetThemeInt(theme,spec.Item2,0,4011,out layout);
                report.WriteLine(" geometry margins={0} hr={1:X8} count={2}/{3:X8} layout={4}/{5:X8}",m,hr,count,countHr,layout,layoutHr);
                IntPtr font=Marshal.AllocHGlobal(92); try { hr=GetThemeFont(theme,IntPtr.Zero,spec.Item2,0,210,font); report.WriteLine(" font hr={0:X8} face={1} height={2} weight={3}",hr,hr==0?Marshal.PtrToStringUni(IntPtr.Add(font,28)):"",hr==0?Marshal.ReadInt32(font):0,hr==0?Marshal.ReadInt32(font,16):0); } finally { Marshal.FreeHGlobal(font); }
                foreach(int state in new[]{1,2,3,4,5,6}) using(var image=new Bitmap(200,30,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(image)) {
                    g.Clear(Color.Magenta); IntPtr dc=g.GetHdc(); var r=new RECT {R=200,B=30}; hr=DrawThemeBackground(theme,dc,spec.Item2,state,ref r,IntPtr.Zero); g.ReleaseHdc(dc);
                    int untouched=0; for(int y=0;y<image.Height;y++) for(int x=0;x<image.Width;x++) if(image.GetPixel(x,y).ToArgb()==Color.Magenta.ToArgb()) untouched++;
                    string name=spec.Item1.Replace(':','_')+"-p"+spec.Item2+"-s"+state+"-dpi"+dpi;
                    image.Save(Path.Combine(output,name+".png")); report.WriteLine(" draw {0} hr={1:X8} unpainted={2}/6000 center={3:X8}",name,hr,untouched,image.GetPixel(100,15).ToArgb());
                }
            } finally { CloseThemeData(theme); }
        }
    }
    static void Describe(IntPtr h,string prefix) { RECT r; GetWindowRect(h,out r); report.WriteLine("{0} {1} visible={2} rect={3} width={4} height={5} title={6}",prefix,Class(h),IsWindowVisible(h),r,r.R-r.L,r.B-r.T,Title(h)); }
    static void Shell() {
        int n=0;
        EnumWindows((h,p)=>{ string cls=Class(h); if(cls=="CabinetWClass" || cls=="ExploreWClass") { Describe(h,"Explorer"); EnumChildWindows(h,(child,q)=>{Describe(child," child"); return true;},IntPtr.Zero); if(!IsIconic(h)) Capture(h,"explorer-"+(n++)+".png"); } return true; },IntPtr.Zero);
        IntPtr tray=FindWindow("Shell_TrayWnd",null),result;
        if(tray==IntPtr.Zero) return;
        IntPtr existing=FindWindow("DV2ControlHost",null);
        bool opened=existing==IntPtr.Zero || !IsWindowVisible(existing);
        if(opened) { SendMessageTimeout(tray,0x112,new IntPtr(0xF130),IntPtr.Zero,2,2000,out result); System.Threading.Thread.Sleep(350); }
        EnumWindows((h,p)=>{ if(Class(h)=="DV2ControlHost" && IsWindowVisible(h)) { Describe(h,"Start"); EnumChildWindows(h,(child,q)=>{Describe(child," Start child"); return true;},IntPtr.Zero); Capture(h,"start.png"); if(opened) SendMessageTimeout(h,0x100,new IntPtr(27),IntPtr.Zero,2,1000,out result); } return true; },IntPtr.Zero);
    }
    static void Capture(IntPtr h,string file) {
        RECT r; GetWindowRect(h,out r); int w=r.R-r.L,hh=r.B-r.T; if(w<=0 || hh<=0 || w>8192 || hh>8192) return;
        using(var image=new Bitmap(w,hh,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(image)) { IntPtr dc=g.GetHdc(); bool ok=PrintWindow(h,dc,2); g.ReleaseHdc(dc); image.Save(Path.Combine(output,file)); report.WriteLine("capture {0} PrintWindow={1} iconic={2} (not visual fidelity proof)",file,ok,IsIconic(h)); }
    }
}
